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
/// One value a sequence takes from outside, as its INPUT line declared it.
///
/// A script that measures at 5 V and a script that measures at 12 V should be one script and
/// two runs, not two scripts. Before this there was no way to say so: <c>$name</c> held only
/// what a <c>-&gt;</c> capture had put there, so a value from outside meant editing the text of
/// the script, and a released procedure whose text is edited is a different procedure.
/// </summary>
/// <param name="Name">What the value is called, and what <c>$name</c> substitutes.</param>
/// <param name="Kind">number, integer or text.</param>
/// <param name="Unit">What the number is in — "V", "Hz" — for whoever fills the box. Free text.</param>
/// <param name="Default">The value used when none is given, or null when one must be.</param>
/// <param name="Min">The lowest a number may be, or null.</param>
/// <param name="Max">The highest a number may be, or null.</param>
public sealed record SequenceInput(
    string Name,
    string Kind,
    string Unit,
    string? Default,
    double? Min,
    double? Max)
{
    /// <summary>A value that is a number, whole or not, and may carry an engineering suffix.</summary>
    public const string Number = "number";

    /// <summary>A number with nothing after the point — a count, a channel.</summary>
    public const string Integer = "integer";

    /// <summary>Anything else, and what an input with no kind written is.</summary>
    public const string Text = "text";

    /// <summary>A value has to be given for an input that declared no default.</summary>
    public bool Required => Default == null;
}

/// <summary>
/// Executes a script that drives several instruments at once.
///
/// <see cref="ScriptRunner"/> talks to one instrument and is unchanged; this is its
/// multi-instrument counterpart, and the reason it is a separate runner rather than an
/// option on that one is that almost every line here has to decide *which* connection it
/// belongs to before it can do anything with it.
///
/// The language is the single-instrument one plus eight things:
///
///   INPUT vset : number V = 5   take a value from outside, used as $vset
///   TIMEOUT 5m                  give the whole run a deadline
///   FINALLY ... END             lines that run at the end, however it ended
///   DEVICE gen : SDG2042X       bind an alias to a connected instrument
///   gen: C1:OUTP ON             send this line to that instrument
///   WITH gen … END              ...or set the target for a whole block
///   FOR f = 100 TO 10000 …      sweep a value, linearly or logarithmically
///   dmm: MEAS:VOLT:AC? -> v     capture a reply, then use it as $v
///   RECORD $f, $v               append a row of results, saved as CSV
///
/// Which instrument a DEVICE line gets is the front end's to say. <see cref="SequenceBinding"/>
/// works it out for a whole script from what is connected — the desktop's strip and the web's
/// binding table both show its answer — and the RunAsync overload that resolves by alias then
/// runs on it, as it runs on the CLI's <c>--device</c> bindings. The overload that is handed
/// only the model resolves each line alone, which cannot tell two instruments of one model
/// apart.
///
/// Either way, every DEVICE line is resolved before the first line runs, wherever in the script
/// it stands, and one that finds nothing stops the run with nothing sent. The same is true of
/// INPUT: every declared value is bound and checked before the first command is sent, so a
/// measurement never stops half way because the number it was given was not a number.
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

    public const string ColumnsKeyword = "COLUMNS";

    /// <summary>A deadline for the whole run, as against one exchange's.</summary>
    public const string TimeoutKeyword = "TIMEOUT";

    /// <summary>Lines that run at the end of a run however it ended.</summary>
    public const string FinallyKeyword = "FINALLY";

    /// <summary>The same block, for whoever reaches for the other word.</summary>
    public const string AlwaysKeyword = "ALWAYS";

    /// <summary>
    /// How long the lines after FINALLY are given, once the run itself is over.
    ///
    /// They run on a token of their own, because the usual one has just been cancelled — by
    /// the Stop button, which is exactly when an output most needs switching off. Bounded all
    /// the same: Stop must mean stopped soon, and a DELAY written into a safe-state block
    /// would otherwise hold a bench for as long as it liked.
    /// </summary>
    public static readonly TimeSpan SafeStateWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The deadline the script gives itself, or null when it names none.
    ///
    /// A measurement whose author knows it takes ninety seconds can say so, and then a run
    /// that hangs — an instrument that stops answering, a DELAY written with three noughts too
    /// many — ends rather than holding the bench until somebody notices. A caller may impose
    /// its own as well, and the shorter of the two is what runs.
    /// </summary>
    public static TimeSpan? TimeLimit(string script)
    {
        foreach (string raw in Lines(script))
        {
            string line = raw.Trim();
            if (!StartsWithWord(line, TimeoutKeyword))
                continue;

            if (TryParseSpan(line[TimeoutKeyword.Length..], out TimeSpan limit, out _))
                return limit;
        }

        return null;
    }

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

    /// <summary>Column headings for the recorded rows, if the script declared any.</summary>
    public const string InputKeyword = "INPUT";

    /// <summary>
    /// The INPUT lines of a script, in order.
    ///
    /// Parsed on its own so a front end can ask for the values before anyone presses Run, and
    /// so a host that stores a script as a released procedure can store what it takes with it.
    /// A line that does not parse is left out, as an unparseable DEVICE line is: this is for
    /// showing, and the runner is where a malformed declaration stops the run.
    /// </summary>
    public static IReadOnlyList<SequenceInput> Inputs(string script)
        => ParseInputs(Lines(script), out _);

    /// <summary>
    /// Work out the value of every declared input, from what was given and what was declared.
    /// </summary>
    /// <remarks>
    /// Public because every front end wants to refuse a bad set of values before it opens a
    /// socket: the CLI before it connects, the web before it holds an instrument, the desktop
    /// before it greys out its own Run button. The runner calls it too, so a caller that skips
    /// it cannot skip the check.
    /// </remarks>
    /// <param name="given">Values by input name, or null for none. Case does not matter.</param>
    /// <param name="values">The value of each declared input, ready to be seeded into $name.</param>
    /// <param name="error">What is wrong, as a sentence for whoever is filling the boxes.</param>
    public static bool TryBindInputs(
        string script,
        IReadOnlyDictionary<string, string>? given,
        out IReadOnlyDictionary<string, string> values,
        out string? error)
    {
        var bound = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        values = bound;

        IReadOnlyList<SequenceInput> declared = ParseInputs(Lines(script), out error);
        if (error != null)
            return false;

        // A value for something nobody declared is a typo in whoever sent it, not a value.
        // Taken silently, "vlot=12" would look like it worked and the whole measurement would
        // run at the default instead — which is the kind of wrong that is only found afterward.
        foreach (string name in given?.Keys ?? Enumerable.Empty<string>())
        {
            if (declared.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (declared.Count == 0)
                error = $"This script takes no inputs, and a value was given for \"{name}\".";
            else
                error = $"This script has no input called \"{name}\". It takes: "
                      + string.Join(", ", declared.Select(d => d.Name)) + ".";

            return false;
        }

        foreach (SequenceInput input in declared)
        {
            string? text = Supplied(given, input.Name) ?? input.Default;
            if (text == null)
            {
                error = $"No value for \"{input.Name}\", and it has no default.";
                return false;
            }

            if (!TryValue(input, text, out string value, out string? why))
            {
                error = $"The value for \"{input.Name}\" {why}";
                return false;
            }

            bound[input.Name] = value;
        }

        return true;
    }

    /// <summary>What was given for one name, whatever case it was given in.</summary>
    private static string? Supplied(IReadOnlyDictionary<string, string>? given, string name)
    {
        if (given == null)
            return null;

        foreach (var (key, value) in given)
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return value;

        return null;
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
    /// Run a sequence, finding each DEVICE line's instrument by the model it names.
    /// </summary>
    /// <remarks>
    /// Each line is resolved alone, so two lines asking for one model are one question with one
    /// answer. A front end with a bench to bind against wants
    /// <see cref="SequenceBinding"/> and the overload that resolves by alias instead, which is
    /// how the desktop and the web run; the CLI's <c>--device</c> bindings use that overload too.
    /// </remarks>
    /// <param name="resolve">
    /// Finds the connection for a model named in a DEVICE line, or null if it is not
    /// connected. Kept as a callback so Core stays clear of the session list, and so a test
    /// can bind a fake instrument to any name it likes.
    /// </param>
    /// <param name="record">Called for each RECORD row.</param>
    /// <param name="inputs">Values for the script's INPUT lines, or null when it declares none.</param>
    /// <param name="limit">A deadline the caller imposes. The script's own TIMEOUT, if it has
    /// one, applies as well, and the shorter of the two is what runs.</param>
    public static Task RunAsync(
        string script,
        Func<string, IInstrumentClient?> resolve,
        Action<string, ScriptOutputKind> output,
        Action<SequenceRow> record,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? inputs = null,
        TimeSpan? limit = null)
        => RunCoreAsync(script, (_, model) => resolve(model), NotConnected, output, record, ct, inputs, limit);

    /// <summary>
    /// Run a sequence whose aliases have been bound, finding each DEVICE line's instrument by
    /// its alias.
    /// </summary>
    /// <remarks>
    /// For a front end that has already said which instrument plays which part: bound by
    /// <see cref="SequenceBinding"/>, as the desktop and the web do, or given by hand, as
    /// <c>lec seq --device gen=192.168.1.5</c> is. The alias is what tells two instruments of
    /// one model apart — <c>DEVICE left : SDM3065X</c> and <c>DEVICE right : SDM3065X</c> are two
    /// meters. The model is passed too, for a caller that wants to check a binding against it.
    /// </remarks>
    /// <param name="resolve">
    /// Given a DEVICE line's alias and then its model — <c>("gen", "SDG2042X")</c> — returns the
    /// connection bound to it, or null if nothing is.
    /// </param>
    /// <param name="record">Called for each RECORD row.</param>
    /// <param name="inputs">Values for the script's INPUT lines, or null when it declares none.</param>
    /// <param name="limit">A deadline the caller imposes. The script's own TIMEOUT, if it has
    /// one, applies as well, and the shorter of the two is what runs.</param>
    public static Task RunAsync(
        string script,
        Func<string, string, IInstrumentClient?> resolve,
        Action<string, ScriptOutputKind> output,
        Action<SequenceRow> record,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? inputs = null,
        TimeSpan? limit = null)
        => RunCoreAsync(script, resolve, NotBound, output, record, ct, inputs, limit);

    /// <summary>Why a DEVICE line looked up by model found nothing.</summary>
    private static string NotConnected(string alias, string model)
        => $"no connected instrument matches \"{model}\". "
         + "Connect it first, or edit the DEVICE line to the model you have.";

    /// <summary>...and one looked up by alias.</summary>
    private static string NotBound(string alias, string model)
        => $"no instrument is bound to \"{alias}\" ({model}).";

    /// <param name="resolve">A DEVICE line's alias and model to its connection, or null.</param>
    /// <param name="unresolved">The alias and model of a line that resolved to nothing, to why.</param>
    private static async Task RunCoreAsync(
        string script,
        Func<string, string, IInstrumentClient?> resolve,
        Func<string, string, string> unresolved,
        Action<string, ScriptOutputKind> output,
        Action<SequenceRow> record,
        CancellationToken outer,
        IReadOnlyDictionary<string, string>? inputs,
        TimeSpan? limit)
    {
        string[] lines = Lines(script);
        var devices = new Dictionary<string, SequenceDevice>(StringComparer.OrdinalIgnoreCase);
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var frames = new Stack<Frame>();

        // The values from outside, before anything else. Checked ahead of the DEVICE lines
        // because a missing value needs no bench to discover, and seeded into the same table
        // $name is read from, so an input and a captured reply are one idea inside the script.
        if (!TryBindInputs(script, inputs, out IReadOnlyDictionary<string, string> seeded,
                           out string? inputError))
        {
            output(inputError!, ScriptOutputKind.Error);
            return;
        }

        foreach (var (name, value) in seeded)
            vars[name] = value;

        // Every DEVICE line is found before anything is sent. Found as the run reached each one,
        // a meter declared on line 5 and not connected stopped the run after line 2 had turned
        // on a generator's output — which is what naming the instruments up front is for
        // preventing. The desktop and the web will not start with a part unbound, for the same
        // reason; this is the same rule for everything else that runs one.
        var declared = new Dictionary<int, SequenceDevice>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (!line.ToUpperInvariant().StartsWith("DEVICE")) continue;

            if (!TryParseDevice(line, out string alias, out string model))
            {
                output($"Line {i + 1}: expected  DEVICE <alias> : <model>", ScriptOutputKind.Error);
                return;
            }

            IInstrumentClient? client = resolve(alias, model);
            if (client == null)
            {
                output($"Line {i + 1}: {unresolved(alias, model)}", ScriptOutputKind.Error);
                return;
            }

            declared[i] = new SequenceDevice(alias, model, client);
        }

        string? target = null;          // the current WITH block's device, if any
        int pc = 0, executed = 0;

        // The deadline, the script's own and the caller's together. Linked rather than checked,
        // so a run that is waiting on an instrument's reply or inside a DELAY ends when it
        // expires rather than at the next line it reaches.
        TimeSpan? deadline = Shorter(TimeLimit(script), limit);
        using var running = CancellationTokenSource.CreateLinkedTokenSource(outer);
        if (deadline is { } span)
            running.CancelAfter(span);

        // Reassigned while the safe state runs, which needs a token the deadline has not
        // already cancelled. Captured by the two functions below rather than passed, because
        // every line they execute has to see the change at once.
        CancellationToken ct = running.Token;

        try
        {
            await ExecuteAsync(0, lines.Length).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline != null && !outer.IsCancellationRequested)
        {
            // The deadline, not the Stop button. Said as an error so a caller counting errors
            // ends the run failed, and not rethrown, because nobody asked for this to stop. A
            // cancellation from anywhere else — a transport that reports its own timeout this
            // way — is left to travel as it did before.
            output($"Timed out after {Said(deadline.Value)}, and stopped where it stood.",
                   ScriptOutputKind.Error);
        }
        finally
        {
            // However the run ended — finished, failed, timed out, stopped — the lines after
            // FINALLY run. An output left on is the reason: a generator set to 10 V by a run
            // that died on the next line stays at 10 V until somebody walks over to it.
            await SafeStateAsync().ConfigureAwait(false);
        }

        return;

        // ------------------------------------------------------------------ the loop itself

        async Task ExecuteAsync(int from, int stop)
        {
            pc = from;

            while (pc < stop)
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

                // --- DEVICE gen : SDG2042X --- found already, above; from here it is in scope
                if (upper.StartsWith("DEVICE"))
                {
                    SequenceDevice found = declared[lineNo - 1];
                    devices[found.Alias] = found;
                    output($"{found.Alias} → {found.Model}", ScriptOutputKind.Info);
                    continue;
                }

                if (upper.StartsWith(ColumnsKeyword)) continue;   // read by Columns(), not here

                if (StartsWithWord(upper, TimeoutKeyword)) continue;   // read by TimeLimit(), not here

                // --- FINALLY / ALWAYS --- stepped over here and run at the end, wherever in the
                // script it was written. Written last is how it reads; written first is how a
                // careful author puts the safe state where nobody can miss it.
                if (StartsWithWord(upper, FinallyKeyword) || StartsWithWord(upper, AlwaysKeyword))
                {
                    pc = SkipToMatchingEnd(lines, pc);
                    continue;
                }

                // --- INPUT vset : number V = 5 --- bound already, above; said here so the log of a
                // run carries what it was run with. A value is what tells two runs of one procedure
                // apart, and a log that does not name it describes neither.
                if (StartsWithWord(upper, InputKeyword))
                {
                    if (TryParseInput(raw[InputKeyword.Length..], out SequenceInput said, out _)
                        && vars.TryGetValue(said.Name, out string? bound))
                        output($"{said.Name} = {bound}{(said.Unit.Length > 0 ? " " + said.Unit : "")}",
                               ScriptOutputKind.Info);
                    continue;
                }

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
                                // A sweep over a name an INPUT declared gives it back rather than
                                // dropping it: left dropped, every later $name would be sent to an
                                // instrument as the four letters "$vset", carrying a value nobody
                                // chose — which is the one thing substitution must never do.
                                if (seeded.TryGetValue(frame.Sweep.Variable, out string? back))
                                    vars[frame.Sweep.Variable] = back;
                                else
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
                    var values = Fields(raw.Length > 6 ? raw[6..] : "", vars);
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
                        string resp = (await device.Client.AskAsync(command, ct).ConfigureAwait(false)).Trim();
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

        // ------------------------------------------------------------------ the safe state

        async Task SafeStateAsync()
        {
            var blocks = SafeStateBlocks(lines);
            if (blocks.Count == 0)
                return;

            // A token of its own: the run's has just been cancelled in the case this exists
            // for. Bounded, so Stop still means stopped.
            using var safe = new CancellationTokenSource(SafeStateWindow);
            CancellationToken was = ct;
            ct = safe.Token;

            // Every instrument the script declared, in scope again. The run may have died
            // before the DEVICE line that named the generator was reached, and the safe state
            // is precisely the lines that have to address it anyway.
            foreach (SequenceDevice found in declared.Values)
                devices[found.Alias] = found;

            try
            {
                foreach (var (from, stop) in blocks)
                {
                    frames.Clear();
                    target = null;
                    await ExecuteAsync(from, stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                output($"The lines after {FinallyKeyword} did not finish within "
                     + $"{Said(SafeStateWindow)}.", ScriptOutputKind.Error);
            }
            catch (Exception ex)
            {
                output($"The lines after {FinallyKeyword} stopped: {ex.Message}", ScriptOutputKind.Error);
            }
            finally
            {
                ct = was;
            }
        }
    }

    /// <summary>The body of every FINALLY block, in the order they are written.</summary>
    private static List<(int From, int Stop)> SafeStateBlocks(string[] lines)
    {
        var blocks = new List<(int, int)>();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (!StartsWithWord(line, FinallyKeyword) && !StartsWithWord(line, AlwaysKeyword))
                continue;

            int after = SkipToMatchingEnd(lines, i + 1);

            // SkipToMatchingEnd stops past the END that closed the block, so the body is
            // everything before it. A block nobody closed runs to the end of the script,
            // which is what an author who wrote no END plainly meant.
            int stop = after;
            if (after > i + 1 && after <= lines.Length)
            {
                string last = lines[after - 1].Trim().ToUpperInvariant();
                if (last == "END" || last == "ENDREPEAT") stop = after - 1;
            }

            blocks.Add((i + 1, stop));
            i = after - 1;
        }

        return blocks;
    }

    /// <summary>The shorter of two deadlines, or whichever one there is.</summary>
    private static TimeSpan? Shorter(TimeSpan? one, TimeSpan? other)
    {
        if (one == null) return other;
        if (other == null) return one;

        return one < other ? one : other;
    }

    /// <summary>A span as it was most likely written: "90s", "5m", "1h".</summary>
    private static string Said(TimeSpan span)
    {
        if (span.TotalHours >= 1 && span.TotalHours == Math.Floor(span.TotalHours))
            return $"{(int)span.TotalHours}h";

        if (span.TotalMinutes >= 1 && span.TotalMinutes == Math.Floor(span.TotalMinutes))
            return $"{(int)span.TotalMinutes}m";

        return $"{span.TotalSeconds:0.##}s";
    }

    /// <summary>
    /// "90s", "5m", "1h", "500ms" — the vocabulary <c>lec watch --every</c> already takes.
    /// Public because <c>lec seq --max-time</c> reads the same words from a command line.
    /// </summary>
    /// <remarks>
    /// The unit is required. DELAY's bare number is milliseconds, so a bare number here would
    /// read as milliseconds to anyone who had just written one, and TIMEOUT 90 meaning a
    /// minute and a half to the runner and a tenth of a second to its author is the kind of
    /// difference nobody finds until a measurement has been cut off.
    /// </remarks>
    public static bool TryParseSpan(string text, out TimeSpan span, out string? why)
    {
        span = TimeSpan.Zero;
        why = null;

        string said = text.Trim().ToLowerInvariant();
        string unit = "";

        foreach (string suffix in new[] { "ms", "s", "m", "h" })
        {
            if (said.EndsWith(suffix, StringComparison.Ordinal))
            {
                unit = suffix;
                said = said[..^suffix.Length].Trim();
                break;
            }
        }

        if (unit.Length == 0)
        {
            why = $"{TimeoutKeyword} needs a unit: 90s, 5m, 1h.";
            return false;
        }

        if (!double.TryParse(said, NumberStyles.Float, CultureInfo.InvariantCulture, out double howMany)
            || howMany <= 0)
        {
            why = $"{TimeoutKeyword} needs a length of time: 90s, 5m, 1h.";
            return false;
        }

        span = unit switch
        {
            "ms" => TimeSpan.FromMilliseconds(howMany),
            "s" => TimeSpan.FromSeconds(howMany),
            "m" => TimeSpan.FromMinutes(howMany),
            _ => TimeSpan.FromHours(howMany),
        };

        return true;
    }

    // ------------------------------------------------------------------------ parsing

    private static string[] Lines(string script) => script.Replace("\r\n", "\n").Split('\n');

    /// <summary>
    /// True when a line begins with a keyword and then ends or breaks for a space.
    /// </summary>
    /// <remarks>
    /// "INPUT" is also the head of a real SCPI subsystem — <c>INPut:COUPling AC</c> is a line a
    /// meter is given — so a keyword that matched any line starting with those five letters
    /// would swallow it. Every keyword here is a word, and a word ends.
    /// </remarks>
    private static bool StartsWithWord(string line, string word)
    {
        if (!line.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            return false;

        return line.Length == word.Length || char.IsWhiteSpace(line[word.Length]);
    }

    /// <summary>Every INPUT line, or what is wrong with the first one that is not.</summary>
    private static List<SequenceInput> ParseInputs(string[] lines, out string? error)
    {
        var found = new List<SequenceInput>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        error = null;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (!StartsWithWord(line, InputKeyword))
                continue;

            if (!TryParseInput(line[InputKeyword.Length..], out SequenceInput input, out string? why))
            {
                error = $"Line {i + 1}: {why}";
                return found;
            }

            if (!seen.Add(input.Name))
            {
                error = $"Line {i + 1}: \"{input.Name}\" is declared twice.";
                return found;
            }

            found.Add(input);
        }

        return found;
    }

    /// <summary>
    /// "vset : number V = 5 (0 TO 30)" → the declaration it describes.
    /// </summary>
    /// <remarks>
    /// Read from the end inward, because each piece is introduced by its own punctuation: the
    /// range in its brackets, then the default after '=', then the kind after ':', and what is
    /// left is the name. Written the other way, a text default containing a bracket or a colon
    /// would take a piece of itself off as a declaration.
    /// </remarks>
    private static bool TryParseInput(string rest, out SequenceInput input, out string? why)
    {
        input = new SequenceInput("", SequenceInput.Text, "", null, null, null);
        why = null;

        string text = rest.Trim();

        // --- (0 TO 30) ---
        double? min = null, max = null;
        if (text.EndsWith(")", StringComparison.Ordinal))
        {
            int open = text.LastIndexOf('(');
            if (open < 0)
            {
                why = "a range needs its opening bracket — (<from> TO <to>).";
                return false;
            }

            string[] ends = text[(open + 1)..^1]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (ends.Length != 3 || !ends[1].Equals("TO", StringComparison.OrdinalIgnoreCase)
                || !TryNum(ends[0], out double from) || !TryNum(ends[2], out double to))
            {
                why = "expected a range of  (<from> TO <to>).";
                return false;
            }

            if (from > to)
                (from, to) = (to, from);

            min = from;
            max = to;
            text = text[..open].TrimEnd();
        }

        // --- = 5 ---
        string? fallback = null;
        int eq = text.IndexOf('=');
        if (eq >= 0)
        {
            fallback = Unquote(text[(eq + 1)..].Trim());
            text = text[..eq].TrimEnd();
        }

        // --- : number V ---
        string kind = min.HasValue ? SequenceInput.Number : SequenceInput.Text;
        string unit = "";
        int colon = text.IndexOf(':');
        if (colon >= 0)
        {
            string[] parts = text[(colon + 1)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length == 0)
            {
                why = "expected a kind after the colon — number, integer or text.";
                return false;
            }

            kind = parts[0].ToLowerInvariant();
            if (kind != SequenceInput.Number && kind != SequenceInput.Integer && kind != SequenceInput.Text)
            {
                why = $"\"{parts[0]}\" is not a kind. Use number, integer or text.";
                return false;
            }

            unit = string.Join(" ", parts.Skip(1));
            text = text[..colon].TrimEnd();
        }

        string name = text.Trim();
        if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            why = "expected  INPUT <name> : <kind> = <default>";
            return false;
        }

        if (min.HasValue && kind == SequenceInput.Text)
        {
            why = "a range belongs to a number, not to text.";
            return false;
        }

        input = new SequenceInput(name, kind, unit, fallback, min, max);

        // A default its own declaration would refuse is a trap set for whoever runs it: the
        // run fails with nothing given and nothing wrong with what they gave.
        if (fallback != null && !TryValue(input, fallback, out _, out string? bad))
        {
            why = $"the default {bad}";
            input = new SequenceInput("", SequenceInput.Text, "", null, null, null);
            return false;
        }

        return true;
    }

    /// <summary>One value against its declaration, and what it becomes if it passes.</summary>
    private static bool TryValue(SequenceInput input, string given, out string value, out string? why)
    {
        value = given.Trim();
        why = null;

        // A line break in a value ends the command it sits inside and begins another. Nothing
        // a measurement needs carries one, and it is the only way a value could become an
        // instruction — which is why the values are seeded rather than the text templated.
        if (value.Any(char.IsControl))
        {
            why = "contains a line break or a control character.";
            return false;
        }

        if (input.Kind == SequenceInput.Text)
            return true;

        if (!TryNum(value, out double number))
        {
            why = $"has to be a number, and \"{value}\" is not.";
            return false;
        }

        if (input.Kind == SequenceInput.Integer && number != Math.Floor(number))
        {
            why = $"has to be a whole number, and \"{value}\" is not.";
            return false;
        }

        if (input.Min is { } low && number < low)
        {
            why = $"is below {Num(low)}, the lowest it may be.";
            return false;
        }

        if (input.Max is { } high && number > high)
        {
            why = $"is above {Num(high)}, the highest it may be.";
            return false;
        }

        // Written as the instrument will read it: "10k" is this language's, not SCPI's, and a
        // machine set to a decimal comma must not put one into a command.
        value = Num(number);
        return true;
    }

    /// <summary>A default written in quotes, without them.</summary>
    private static string Unquote(string text)
    {
        if (text.Length >= 2 && (text[0] == '"' || text[0] == '\'') && text[^1] == text[0])
            return text[1..^1];

        return text;
    }

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

    /// <summary>A RECORD line's fields — split before substituting. See ScriptValues.</summary>
    private static List<string> Fields(string text, Dictionary<string, string> vars)
        => ScriptValues.Fields(text, vars);

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
                || u == "WITH" || u.StartsWith("WITH ")
                || StartsWithWord(u, FinallyKeyword) || StartsWithWord(u, AlwaysKeyword)) depth++;
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
