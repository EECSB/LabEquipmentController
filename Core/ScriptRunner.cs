using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>How a line of script output should be presented.</summary>
public enum ScriptOutputKind { Command, Response, Info, Error }

/// <summary>
/// Executes a small SCPI script against a connected instrument.
///
/// One instruction per line:
///   *IDN?                 SCPI command; a query (contains '?') reads a reply
///   # comment  /  // comment
///   DELAY 500  /  WAIT 500  pause this many milliseconds
///   PRINT text / ECHO text  write a message to the output
///   REPEAT 3 ... END        repeat the enclosed block (may be nested)
///   COLUMNS Freq, Vpp       name the columns of the results table
///   MEAS:VOLT? -> v         capture a reply, then use it as $v
///   RECORD $f, $v           append a row of results, saved as CSV
///
/// The last three are the sequence language's recording statements, with the same spelling
/// and the same meaning — a script moved between this window and the multi-instrument one
/// should record the same thing. What is missing here is only what needs more than one
/// instrument: aliases, WITH blocks, and FOR sweeps.
///
/// The script stops on the first command error or when the token is cancelled
/// (the Stop button). Cancellation is honoured between every line and during waits.
/// </summary>
public static class ScriptRunner
{
    /// <summary>Guard against a pathological script; the Stop button is the normal exit.</summary>
    private const int MaxInstructions = 1_000_000;

    /// <summary>Column headings for the recorded rows, if the script declared any.</summary>
    public const string ColumnsKeyword = "COLUMNS";

    /// <summary>The column headings a script declared, or an empty list.</summary>
    public static IReadOnlyList<string> Columns(string script)
    {
        foreach (string raw in script.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith(ColumnsKeyword, StringComparison.OrdinalIgnoreCase)) continue;
            return ScriptValues.Split(line[ColumnsKeyword.Length..]);
        }
        return Array.Empty<string>();
    }

    /// <param name="record">Called for each RECORD row.</param>
    public static async Task RunAsync(
        string script,
        IInstrumentClient client,
        Action<string, ScriptOutputKind> output,
        Action<SequenceRow> record,
        CancellationToken ct)
    {
        string[] lines = script.Replace("\r\n", "\n").Split('\n');
        var loops = new Stack<LoopFrame>();
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int pc = 0;
        int executed = 0;

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

            if (raw.Length == 0 || raw.StartsWith("#") || raw.StartsWith("//"))
                continue;

            string upper = raw.ToUpperInvariant();

            if (upper == "REPEAT" || upper.StartsWith("REPEAT "))
            {
                int count = ParseArgInt(raw, 6, 1);
                if (count > 0) loops.Push(new LoopFrame { BodyStart = pc, Remaining = count });
                else pc = SkipToMatchingEnd(lines, pc);   // count <= 0: skip the whole block
                continue;
            }

            if (upper == "END" || upper == "ENDREPEAT")
            {
                if (loops.Count == 0)
                {
                    output($"Line {lineNo}: END without REPEAT.", ScriptOutputKind.Error);
                    return;
                }
                LoopFrame frame = loops.Peek();
                if (--frame.Remaining > 0) pc = frame.BodyStart;   // loop back
                else loops.Pop();
                continue;
            }

            if (upper.StartsWith("DELAY") || upper.StartsWith("WAIT"))
            {
                int ms = ParseArgInt(raw, upper.StartsWith("DELAY") ? 5 : 4, 0);
                if (ms > 0)
                {
                    output($"(wait {ms} ms)", ScriptOutputKind.Info);
                    await Task.Delay(ms, ct).ConfigureAwait(false);
                }
                continue;
            }

            if (upper.StartsWith("PRINT ") || upper.StartsWith("ECHO ") || upper.StartsWith("LOG "))
            {
                output(ScriptValues.Substitute(raw[(raw.IndexOf(' ') + 1)..], vars),
                       ScriptOutputKind.Info);
                continue;
            }

            // Read by Columns() before the run starts, so the table has its headings from the
            // first row rather than gaining them halfway through.
            if (upper.StartsWith(ColumnsKeyword)) continue;

            if (upper == "RECORD" || upper.StartsWith("RECORD "))
            {
                var values = ScriptValues.Split(
                    ScriptValues.Substitute(raw.Length > 6 ? raw[6..] : "", vars));
                record(new SequenceRow(values));
                output("recorded: " + string.Join(", ", values), ScriptOutputKind.Info);
                continue;
            }

            // Otherwise it's a SCPI command.
            string command = ScriptValues.Substitute(raw, vars).Trim();
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

            output("> " + command, ScriptOutputKind.Command);
            try
            {
                if (command.Contains('?'))
                {
                    string resp = (await client.QueryAsync(command, ct).ConfigureAwait(false)).Trim();
                    output(resp.Length == 0 ? "(no response)" : resp, ScriptOutputKind.Response);
                    if (capture != null) vars[capture] = resp;
                }
                else
                {
                    await client.SendAsync(command, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;   // Stop button — let the caller report it
            }
            catch (Exception ex)
            {
                output($"ERROR on line {lineNo}: {ex.Message}", ScriptOutputKind.Error);
                return;  // stop the script on the first failure
            }
        }
    }

    private sealed class LoopFrame
    {
        public int BodyStart;
        public int Remaining;
    }

    private static int SkipToMatchingEnd(string[] lines, int pc)
    {
        int depth = 1;
        while (pc < lines.Length && depth > 0)
        {
            string u = lines[pc].Trim().ToUpperInvariant();
            if (u == "REPEAT" || u.StartsWith("REPEAT ")) depth++;
            else if (u == "END" || u == "ENDREPEAT") depth--;
            pc++;
        }
        return pc;
    }

    /// <summary>Parse the integer argument that follows a keyword of length <paramref name="keywordLen"/>.</summary>
    private static int ParseArgInt(string line, int keywordLen, int fallback)
    {
        string rest = line.Length > keywordLen ? line[keywordLen..].Trim() : "";
        return int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
    }
}
