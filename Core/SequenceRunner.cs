using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>One instrument a sequence can talk to, under the name the script gave it.</summary>
/// <param name="Alias">The name used to address it — "gen", "dmm".</param>
/// <param name="Model">The model the script asked for, as written in the DEVICE line.</param>
/// <param name="Client">The connection it resolved to.</param>
public sealed record SequenceDevice(string Alias, string Model, IInstrumentClient Client);

/// <summary>One row of measured data, as produced by RECORD.</summary>
public sealed record SequenceRow(IReadOnlyList<string> Values);

/// <summary>
/// Executes a script that drives several instruments at once.
///
/// <see cref="ScriptRunner"/> talks to one instrument and is unchanged; this is its
/// multi-instrument counterpart, and the reason it is a separate runner rather than an
/// option on that one is that almost every line here has to decide *which* connection it
/// belongs to before it can do anything with it.
///
/// The language is the single-instrument one plus five things:
///
///   DEVICE gen : SDG2042X       bind an alias to a connected instrument, by model
///   gen: C1:OUTP ON             send this line to that instrument
///   WITH gen … END              ...or set the target for a whole block
///   FOR f = 100 TO 10000 …      sweep a value, linearly or logarithmically
///   dmm: MEAS:VOLT:AC? -> v     capture a reply, then use it as $v
///   RECORD $f, $v               append a row of results, saved as CSV
///
/// A sweep is why this exists: stepping a generator and reading a meter at each step is
/// interleaved, one instrument after the other inside a loop, which is not something a
/// script-per-instrument can express at all.
///
/// Deliberately sequential. Two instruments never run concurrently here, because a sweep
/// is inherently ordered — set, settle, measure — and because a connection carries one
/// conversation at a time, which is the rule the consoles already follow.
/// </summary>
public static class SequenceRunner
{
    /// <summary>Guard against a pathological script; the Stop button is the normal exit.</summary>
    private const int MaxInstructions = 1_000_000;

    /// <summary>Column headings for the recorded rows, if the script declared any.</summary>
    public const string ColumnsKeyword = "COLUMNS";

    /// <summary>
    /// The DEVICE lines at the top of a script, in order.
    ///
    /// Parsed on its own so the editor can show what a sequence needs — and whether each
    /// one is currently connected — before anyone presses Run.
    /// </summary>
    public static IReadOnlyList<(string Alias, string Model)> Requirements(string script)
    {
        var found = new List<(string, string)>();
        foreach (string raw in Lines(script))
        {
            string line = raw.Trim();
            if (!line.StartsWith("DEVICE", StringComparison.OrdinalIgnoreCase)) continue;
            if (TryParseDevice(line, out string alias, out string model)) found.Add((alias, model));
        }
        return found;
    }

    /// <summary>The column headings a script declared, or an empty list.</summary>
    public static IReadOnlyList<string> Columns(string script)
    {
        foreach (string raw in Lines(script))
        {
            string line = raw.Trim();
            if (!line.StartsWith(ColumnsKeyword, StringComparison.OrdinalIgnoreCase)) continue;
            return Split(line[ColumnsKeyword.Length..]);
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Run a sequence.
    /// </summary>
    /// <param name="resolve">
    /// Finds the connection for a model named in a DEVICE line, or null if it is not
    /// connected. Kept as a callback so Core stays clear of the session list, and so a test
    /// can bind a fake instrument to any name it likes.
    /// </param>
    /// <param name="record">Called for each RECORD row.</param>
    public static async Task RunAsync(
        string script,
        Func<string, IInstrumentClient?> resolve,
        Action<string, ScriptOutputKind> output,
        Action<SequenceRow> record,
        CancellationToken ct)
    {
        string[] lines = Lines(script);
        var devices = new Dictionary<string, SequenceDevice>(StringComparer.OrdinalIgnoreCase);
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var frames = new Stack<Frame>();

        string? target = null;          // the current WITH block's device, if any
        int pc = 0, executed = 0;

        while (pc < lines.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (++executed > MaxInstructions)
            {
                output("Aborted: instruction limit reached.", ScriptOutputKind.Error);
                return;
            }

            string raw = lines[pc].Trim();
            int lineNo = pc + 1;
            pc++;

            if (raw.Length == 0 || raw.StartsWith("#") || raw.StartsWith("//")) continue;

            string upper = raw.ToUpperInvariant();

            // --- DEVICE gen : SDG2042X ---
            if (upper.StartsWith("DEVICE"))
            {
                if (!TryParseDevice(raw, out string alias, out string model))
                {
                    output($"Line {lineNo}: expected  DEVICE <alias> : <model>", ScriptOutputKind.Error);
                    return;
                }

                IInstrumentClient? client = resolve(model);
                if (client == null)
                {
                    output($"Line {lineNo}: no connected instrument matches \"{model}\". "
                         + "Connect it first, or edit the DEVICE line to the model you have.",
                           ScriptOutputKind.Error);
                    return;
                }

                devices[alias] = new SequenceDevice(alias, model, client);
                output($"{alias} → {model}", ScriptOutputKind.Info);
                continue;
            }

            if (upper.StartsWith(ColumnsKeyword)) continue;   // read by Columns(), not here

            // --- WITH gen ---
            if (upper == "WITH" || upper.StartsWith("WITH "))
            {
                string alias = raw[4..].Trim();
                if (!devices.ContainsKey(alias))
                {
                    output($"Line {lineNo}: no device called \"{alias}\". {KnownDevices(devices)}",
                           ScriptOutputKind.Error);
                    return;
                }
                frames.Push(Frame.With(target));
                target = alias;
                continue;
            }

            // --- REPEAT n ---
            if (upper == "REPEAT" || upper.StartsWith("REPEAT "))
            {
                int count = ParseInt(raw[6..], 1);
                if (count > 0) frames.Push(Frame.Repeat(pc, count));
                else pc = SkipToMatchingEnd(lines, pc);
                continue;
            }

            // --- FOR f = 100 TO 10000 STEP 100  |  POINTS 40 LOG ---
            if (upper.StartsWith("FOR "))
            {
                if (!TryParseFor(raw, out Sweep sweep, out string? why))
                {
                    output($"Line {lineNo}: {why}", ScriptOutputKind.Error);
                    return;
                }
                if (sweep.Values.Count == 0) { pc = SkipToMatchingEnd(lines, pc); continue; }

                vars[sweep.Variable] = Num(sweep.Values[0]);
                frames.Push(Frame.For(pc, sweep));
                continue;
            }

            // --- END ---
            if (upper == "END" || upper == "ENDREPEAT")
            {
                if (frames.Count == 0)
                {
                    output($"Line {lineNo}: END without REPEAT, FOR or WITH.", ScriptOutputKind.Error);
                    return;
                }

                Frame frame = frames.Peek();
                switch (frame.Kind)
                {
                    case FrameKind.With:
                        target = frame.PreviousTarget;
                        frames.Pop();
                        break;

                    case FrameKind.Repeat:
                        if (--frame.Remaining > 0) pc = frame.BodyStart;
                        else frames.Pop();
                        break;

                    case FrameKind.For:
                        if (++frame.Index < frame.Sweep!.Values.Count)
                        {
                            vars[frame.Sweep.Variable] = Num(frame.Sweep.Values[frame.Index]);
                            pc = frame.BodyStart;
                        }
                        else
                        {
                            vars.Remove(frame.Sweep.Variable);
                            frames.Pop();
                        }
                        break;
                }
                continue;
            }

            // --- DELAY / WAIT ---
            if (upper.StartsWith("DELAY") || upper.StartsWith("WAIT"))
            {
                int ms = ParseInt(Substitute(raw[(upper.StartsWith("DELAY") ? 5 : 4)..], vars), 0);
                if (ms > 0)
                {
                    output($"(wait {ms} ms)", ScriptOutputKind.Info);
                    await Task.Delay(ms, ct).ConfigureAwait(false);
                }
                continue;
            }

            // --- PRINT / ECHO / LOG ---
            if (upper.StartsWith("PRINT ") || upper.StartsWith("ECHO ") || upper.StartsWith("LOG "))
            {
                output(Substitute(raw[(raw.IndexOf(' ') + 1)..], vars), ScriptOutputKind.Info);
                continue;
            }

            // --- RECORD $f, $v ---
            if (upper == "RECORD" || upper.StartsWith("RECORD "))
            {
                var values = Split(Substitute(raw.Length > 6 ? raw[6..] : "", vars));
                record(new SequenceRow(values));
                output("recorded: " + string.Join(", ", values), ScriptOutputKind.Info);
                continue;
            }

            // --- everything else is SCPI, for one named instrument ---
            if (!TrySplitTarget(raw, target, devices, out string alias2, out string command))
            {
                output(devices.Count == 0
                    ? $"Line {lineNo}: no instruments declared. Start with  DEVICE <alias> : <model>"
                    : $"Line {lineNo}: which instrument? Prefix the line — \"gen: {raw}\" — "
                    + $"or put it in a WITH block. {KnownDevices(devices)}",
                       ScriptOutputKind.Error);
                return;
            }

            command = Substitute(command, vars).Trim();
            if (command.Length == 0) continue;

            // "MEASure:VOLTage:AC? -> vout" — capture the reply under a name.
            string? capture = null;
            int arrow = command.IndexOf("->", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                capture = command[(arrow + 2)..].Trim();
                command = command[..arrow].Trim();
                if (capture.Length == 0)
                {
                    output($"Line {lineNo}: \"->\" needs a name to store the reply under.",
                           ScriptOutputKind.Error);
                    return;
                }
            }

            SequenceDevice device = devices[alias2];
            output($"{alias2}> {command}", ScriptOutputKind.Command);
            try
            {
                if (command.Contains('?'))
                {
                    string resp = (await device.Client.QueryAsync(command, ct).ConfigureAwait(false)).Trim();
                    output(resp.Length == 0 ? "(no response)" : resp, ScriptOutputKind.Response);
                    if (capture != null) vars[capture] = resp;
                }
                else
                {
                    await device.Client.SendAsync(command, ct).ConfigureAwait(false);
                    if (capture != null)
                    {
                        output($"Line {lineNo}: \"->\" only works on a query — this line has no '?'.",
                               ScriptOutputKind.Error);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;   // Stop button — the caller reports it
            }
            catch (Exception ex)
            {
                output($"ERROR on line {lineNo} ({alias2}): {ex.Message}", ScriptOutputKind.Error);
                return;
            }
        }
    }

    // ------------------------------------------------------------------------ parsing

    private static string[] Lines(string script) => script.Replace("\r\n", "\n").Split('\n');

    /// <summary>"DEVICE gen : SDG2042X" → ("gen", "SDG2042X").</summary>
    private static bool TryParseDevice(string line, out string alias, out string model)
    {
        alias = model = "";
        string rest = line.Length > 6 ? line[6..].Trim() : "";

        int colon = rest.IndexOf(':');
        if (colon <= 0) return false;

        alias = rest[..colon].Trim();
        model = rest[(colon + 1)..].Trim();

        // An alias has to be a plain word, or "gen: …" further down is ambiguous.
        return alias.Length > 0 && model.Length > 0
            && alias.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    /// <summary>
    /// Work out which instrument a line is for.
    ///
    /// A prefix wins, then the enclosing WITH, then — only when exactly one instrument is
    /// declared — that one. With two or more declared and no prefix the line is refused
    /// rather than guessed at: sending a generator's command to a meter is the failure this
    /// whole project is built to avoid.
    /// </summary>
    private static bool TrySplitTarget(
        string raw, string? withTarget, Dictionary<string, SequenceDevice> devices,
        out string alias, out string command)
    {
        int colon = raw.IndexOf(':');
        if (colon > 0)
        {
            string head = raw[..colon].Trim();
            if (devices.ContainsKey(head))
            {
                alias = head;
                command = raw[(colon + 1)..].Trim();
                return true;
            }
        }

        if (withTarget != null) { alias = withTarget; command = raw; return true; }
        if (devices.Count == 1) { alias = devices.Keys.First(); command = raw; return true; }

        alias = command = "";
        return false;
    }

    private static string KnownDevices(Dictionary<string, SequenceDevice> devices)
        => devices.Count == 0
            ? "None are declared."
            : "Declared: " + string.Join(", ", devices.Keys) + ".";

    // Shared with ScriptRunner, which records results the same way — see ScriptValues.
    private static string Substitute(string text, Dictionary<string, string> vars)
        => ScriptValues.Substitute(text, vars);

    private static List<string> Split(string text) => ScriptValues.Split(text);

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : fallback;

    /// <summary>Format a swept value without a locale's decimal comma or exponent notation.</summary>
    private static string Num(double v)
        => v == Math.Floor(v) && Math.Abs(v) < 1e15
            ? ((long)v).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.##########", CultureInfo.InvariantCulture);

    private static int SkipToMatchingEnd(string[] lines, int pc)
    {
        int depth = 1;
        while (pc < lines.Length && depth > 0)
        {
            string u = lines[pc].Trim().ToUpperInvariant();
            if (u == "REPEAT" || u.StartsWith("REPEAT ") || u.StartsWith("FOR ")
                || u == "WITH" || u.StartsWith("WITH ")) depth++;
            else if (u == "END" || u == "ENDREPEAT") depth--;
            pc++;
        }
        return pc;
    }

    // -------------------------------------------------------------------------- sweeps

    private sealed record Sweep(string Variable, IReadOnlyList<double> Values);

    /// <summary>
    /// "FOR f = 100 TO 100000 STEP 100" or "FOR f = 100 TO 100000 POINTS 40 LOG".
    ///
    /// LOG spacing is not a nicety: a filter response is read per decade, and a linear
    /// sweep from 100 Hz to 100 kHz spends 99% of its points above 1 kHz — which is where
    /// the interesting part of a low-pass response is not.
    /// </summary>
    private static bool TryParseFor(string raw, out Sweep sweep, out string? error)
    {
        sweep = new Sweep("", Array.Empty<double>());
        error = null;

        string rest = raw[4..].Trim();
        int eq = rest.IndexOf('=');
        if (eq <= 0) { error = "expected  FOR <name> = <from> TO <to> …"; return false; }

        string name = rest[..eq].Trim();
        if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
        { error = $"\"{name}\" is not a usable variable name."; return false; }

        string[] parts = rest[(eq + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 3 || !parts[1].Equals("TO", StringComparison.OrdinalIgnoreCase))
        { error = "expected  FOR <name> = <from> TO <to> …"; return false; }

        if (!TryNum(parts[0], out double from) || !TryNum(parts[2], out double to))
        { error = "the range has to be two numbers."; return false; }

        var values = new List<double>();

        if (parts.Length == 3)
        {
            for (double v = from; from <= to ? v <= to + 1e-9 : v >= to - 1e-9; v += from <= to ? 1 : -1)
                values.Add(v);
        }
        else if (parts[3].Equals("STEP", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 5 || !TryNum(parts[4], out double step) || step == 0)
            { error = "STEP needs a non-zero number."; return false; }

            if ((to - from) * step < 0) step = -step;   // a step pointing the wrong way
            for (double v = from; step > 0 ? v <= to + 1e-9 : v >= to - 1e-9; v += step)
                values.Add(v);
        }
        else if (parts[3].Equals("POINTS", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 5 || !int.TryParse(parts[4], out int n) || n < 1)
            { error = "POINTS needs a count of 1 or more."; return false; }

            bool log = parts.Length > 5 && parts[5].Equals("LOG", StringComparison.OrdinalIgnoreCase);
            if (log && (from <= 0 || to <= 0))
            { error = "a LOG sweep cannot start or end at zero."; return false; }

            if (n == 1) values.Add(from);
            else if (log)
            {
                double a = Math.Log10(from), b = Math.Log10(to);
                for (int i = 0; i < n; i++) values.Add(Math.Pow(10, a + (b - a) * i / (n - 1)));
            }
            else
            {
                for (int i = 0; i < n; i++) values.Add(from + (to - from) * i / (n - 1));
            }
        }
        else { error = $"\"{parts[3]}\" — expected STEP or POINTS."; return false; }

        sweep = new Sweep(name, values);
        return true;
    }

    private static bool TryNum(string s, out double v)
    {
        // "10k", "1M", "2.5m" — an engineering suffix, because a sweep is written in the
        // units the instrument's front panel uses.
        s = s.Trim();
        double scale = 1;
        if (s.Length > 1)
        {
            char last = s[^1];
            scale = last switch
            {
                'k' or 'K' => 1e3,
                'M' => 1e6,
                'G' => 1e9,
                'm' => 1e-3,
                'u' or 'U' => 1e-6,
                'n' or 'N' => 1e-9,
                'p' or 'P' => 1e-12,
                _ => 1,
            };
            if (scale != 1) s = s[..^1];
        }

        bool ok = double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        v *= scale;
        return ok;
    }

    // -------------------------------------------------------------------------- frames

    private enum FrameKind { Repeat, For, With }

    private sealed class Frame
    {
        public FrameKind Kind;
        public int BodyStart;
        public int Remaining;
        public int Index;
        public Sweep? Sweep;
        public string? PreviousTarget;

        public static Frame Repeat(int start, int count)
            => new() { Kind = FrameKind.Repeat, BodyStart = start, Remaining = count };

        public static Frame For(int start, Sweep sweep)
            => new() { Kind = FrameKind.For, BodyStart = start, Sweep = sweep, Index = 0 };

        public static Frame With(string? previous)
            => new() { Kind = FrameKind.With, PreviousTarget = previous };
    }
}
