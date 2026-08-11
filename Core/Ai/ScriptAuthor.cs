using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>One instrument a script may address, and the commands it accepts.</summary>
/// <param name="Alias">
/// How the script names it — "gen", "dmm". Empty for a single-instrument script, where
/// lines carry no prefix.
/// </param>
public sealed record ScriptContextInstrument(
    string Alias, string Model, string Identity, CommandReference? Reference);

/// <summary>What a generated script was checked against, and what did not check out.</summary>
/// <param name="Script">The script as written, ready for the editor.</param>
/// <param name="Notes">The model's own one-line summary of what it did, if it gave one.</param>
/// <param name="Undocumented">
/// Lines whose command is in no catalog. Not removed — the user may be driving an
/// instrument whose guide was never transcribed — but surfaced, because an undocumented
/// command is the one thing a model is most likely to have made up.
/// </param>
public sealed record AuthoredScript(
    string Script, string Notes, IReadOnlyList<string> Undocumented);

/// <summary>
/// Asks a model to write a script from a plain-English description.
///
/// The same problem as <see cref="CommandExtractor"/>, and the same answer: a model will
/// cheerfully invent SCPI, so it is given the catalogs to work from and its output is
/// checked back against them. What it produces is a draft in an editor that the user reads
/// and runs deliberately — never something that executes on its own.
///
/// Three things go into the prompt beyond the user's request:
///
/// <list type="bullet">
/// <item>the commands each addressed instrument actually accepts, from the shipped
///       catalogs — the model is told to use nothing else;</item>
/// <item>the script language, which is small and entirely local to this app, so a model
///       has no prior knowledge of it whatsoever;</item>
/// <item>the current script and the last run's output, so "it failed with -113, fix it"
///       is a question it can actually answer.</item>
/// </list>
/// </summary>
public sealed class ScriptAuthor
{
    private readonly IAiClient _client;

    public ScriptAuthor(IAiClient client) => _client = client;

    /// <summary>
    /// How many commands per instrument go into the prompt.
    ///
    /// The catalogs run to 2,270 commands for one R&amp;S analyzer, which is both wasteful and
    /// worse than a shorter list: a model asked to pick from everything picks less well than
    /// one given the plausible subset. Commands are ranked against the request first — see
    /// <see cref="Relevant"/> — so the ones the user is asking about survive the cut.
    /// </summary>
    public int MaxCommandsPerInstrument { get; init; } = 240;

    private const string SingleInstrumentLanguage = """
        The script language, one instruction per line:
          <SCPI>                a command. One containing '?' is a query and its reply is shown
          # text                a comment; // also works
          DELAY <ms>            pause. WAIT is the same thing
          PRINT <text>          write a message to the output. ECHO and LOG are the same
          REPEAT <n> ... END    repeat a block. May be nested
        There are no variables, no arithmetic, no conditionals. Nothing else is valid.
        """;

    private const string SequenceLanguage = """
        The script language, one instruction per line:
          DEVICE <alias> : <model>   name an instrument. Must come before it is used
          <alias>: <SCPI>            send this line to that instrument
          WITH <alias> ... END       send a whole block to it
          FOR <v> = <a> TO <b> STEP <n> ... END     sweep a value
          FOR <v> = <a> TO <b> POINTS <n> LOG ... END   log-spaced sweep, for frequency
          <alias>: <query>? -> <name>   capture the reply; use it later as $name
          RECORD <a>, <b>            append a row of results
          COLUMNS <a>, <b>           name the result columns. Put it near the top
          DELAY <ms>, PRINT <text>, REPEAT <n> ... END, # comment
        Numbers may carry an engineering suffix: 1k, 2.5M, 100m.
        There is no arithmetic and there are no conditionals. Nothing else is valid.

        Every line carrying a command must say which instrument it is for, by prefix or by
        being inside a WITH block. A line that does not is an error, not a default.
        """;

    private static readonly string Rules = """
        You are writing a script for a bench instrument control program.

        Rules, in order of importance:
        - Use ONLY commands from the list given for each instrument. If the list does not
          contain what the request needs, say so in "notes" and write what you can. Never
          write a command from your own knowledge of similar instruments: the vendor's
          spelling is not guessable, and a wrong one either errors or silently does nothing.
        - Substitute real values for the placeholders. "C<n>:BSWV FRQ,<freq>" is a template;
          "C1:BSWV FRQ,1000" is a command. Never leave <n> or <freq> in the script.
        - Anything that turns on an output, applies a voltage or sinks current gets a comment
          on the line above saying what it will do. This drives real hardware attached to
          real circuits.
        - Wait after changing something before measuring it. Instruments settle.
        - Comment the script the way an engineer would: why, not what.
        - Keep it as short as the request allows.
        """;

    /// <summary>
    /// Write or revise a script.
    /// </summary>
    /// <param name="request">What the user asked for, in their own words.</param>
    /// <param name="instruments">The instruments the script may address.</param>
    /// <param name="isSequence">Sequence language (several instruments) or the plain one.</param>
    /// <param name="currentScript">What is in the editor, if the request is a revision.</param>
    /// <param name="recentOutput">
    /// The tail of the last run — errors included. This is what makes "it failed, fix it"
    /// answerable, and it is the reason the console output is worth handing over at all.
    /// </param>
    public async Task<AuthoredScript> WriteAsync(
        string request,
        IReadOnlyList<ScriptContextInstrument> instruments,
        bool isSequence,
        AiConnection connection,
        string apiKey,
        string? currentScript = null,
        string? recentOutput = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request))
            throw new AiException("Describe what the script should do.");

        string instruction = Rules + "\r\n\r\n"
                           + (isSequence ? SequenceLanguage : SingleInstrumentLanguage);

        string payload = BuildPayload(
            request, instruments, currentScript, recentOutput, MaxCommandsPerInstrument);

        string reply = await _client.CompleteAsync(
            connection, apiKey, instruction, AiPayload.FromText(payload), Schema(), ct)
            .ConfigureAwait(false);

        return Parse(reply, instruments);
    }

    /// <summary>Everything the model is told about this bench and this request.</summary>
    public static string BuildPayload(
        string request,
        IReadOnlyList<ScriptContextInstrument> instruments,
        string? currentScript,
        string? recentOutput,
        int maxCommands = 240)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## What is wanted");
        sb.AppendLine(request.Trim());
        sb.AppendLine();

        sb.AppendLine("## Instruments available");
        foreach (ScriptContextInstrument i in instruments)
        {
            sb.Append("### ");
            if (i.Alias.Length > 0) sb.Append(i.Alias).Append(" — ");
            sb.AppendLine(i.Model);
            if (i.Identity.Length > 0) sb.AppendLine("*IDN? — " + i.Identity);

            if (i.Reference == null || i.Reference.Commands.Count == 0)
            {
                sb.AppendLine("No command catalog is available for this instrument. Use only "
                            + "IEEE 488.2 common commands (*IDN?, *RST, *CLS, *OPC?) and say "
                            + "in notes that the rest could not be checked.");
                sb.AppendLine();
                continue;
            }

            IReadOnlyList<CommandRef> chosen = Relevant(i.Reference.Commands, request, maxCommands);
            sb.AppendLine($"Commands it accepts ({chosen.Count} of {i.Reference.Commands.Count} "
                        + "shown, chosen for this request):");
            foreach (CommandRef c in chosen)
                sb.AppendLine($"  {c.Syntax}  — {Shorten(c.Description)}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(currentScript))
        {
            sb.AppendLine("## The script currently in the editor");
            sb.AppendLine("Revise this rather than starting over, unless asked otherwise.");
            sb.AppendLine("```");
            sb.AppendLine(currentScript.Trim());
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(recentOutput))
        {
            sb.AppendLine("## What happened when it last ran");
            sb.AppendLine("Lines beginning '>' were sent; the rest are replies and errors.");
            sb.AppendLine("```");
            sb.AppendLine(Tail(recentOutput, 4000));
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The commands most likely to be wanted, best first.
    ///
    /// Scored on the request's own words appearing in the command or its description, then
    /// on being a short, top-level command — an instrument's everyday controls are near the
    /// root of its tree, while the deep ones are options and calibration. Falling back to
    /// the head of the catalog would give a model 240 commands all starting with ABORt.
    /// </summary>
    public static IReadOnlyList<CommandRef> Relevant(
        IReadOnlyList<CommandRef> commands, string request, int max)
    {
        if (commands.Count <= max) return commands;

        string[] words = request
            .Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ';', ':', '(', ')', '"', '\'' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3)
            .Select(w => w.ToUpperInvariant())
            .Distinct()
            .ToArray();

        int Score(CommandRef c)
        {
            string syntax = c.Syntax.ToUpperInvariant();
            string desc = c.Description.ToUpperInvariant();

            int score = 0;
            foreach (string w in words)
            {
                if (syntax.Contains(w)) score += 10;
                else if (desc.Contains(w)) score += 3;
            }

            // Shallow commands are the ones a user means; :CALibration:… and the deeper
            // option trees are not what "set the frequency to 1 kHz" is asking for.
            score -= c.Syntax.Count(ch => ch == ':');
            return score;
        }

        return commands
            .Select(c => (Command: c, Score: Score(c)))
            .OrderByDescending(x => x.Score)
            .Take(max)
            .Select(x => x.Command)
            .ToList();
    }

    private static string Shorten(string description)
    {
        string d = description.Replace("\r", " ").Replace("\n", " ").Trim();
        // One sentence is enough to choose between commands, and the catalogs carry
        // paragraphs — the R&S ones especially.
        int stop = d.IndexOf(". ", StringComparison.Ordinal);
        if (stop > 0 && stop < 160) d = d[..(stop + 1)];
        return d.Length > 160 ? d[..157] + "…" : d;
    }

    private static string Tail(string text, int chars)
        => text.Length <= chars ? text : "…" + text[^chars..];

    private static JsonNode Schema() => JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "script": { "type": "string" },
            "notes":  { "type": "string" }
          },
          "required": ["script"]
        }
        """)!;

    /// <summary>
    /// Read the reply, and check every command in it against the catalogs it was given.
    ///
    /// The check is the point. A model told to use only the listed commands mostly will,
    /// and "mostly" is the gap between a script that runs and one that puts an undefined
    /// header on the wire.
    /// </summary>
    public static AuthoredScript Parse(
        string reply, IReadOnlyList<ScriptContextInstrument> instruments)
    {
        JsonNode? node = ObjectIn(reply);
        string script = Text(node, "script");
        string notes = Text(node, "notes");

        if (string.IsNullOrWhiteSpace(script))
            throw new AiException("The model did not return a script.");

        script = script.Replace("\r\n", "\n").Replace("\n", "\r\n").Trim();

        return new AuthoredScript(script, notes.Trim(), Undocumented(script, instruments));
    }

    /// <summary>
    /// The object out of a reply. Same problem as the extractor's: models wrap JSON in prose
    /// and fences often enough that the braces are found rather than assumed.
    /// </summary>
    private static JsonNode? ObjectIn(string reply)
    {
        foreach (string candidate in new[] { reply, Between(reply, '{', '}') })
        {
            if (candidate.Length == 0) continue;
            try
            {
                if (JsonNode.Parse(candidate) is JsonObject o) return o;
            }
            catch { /* try the next candidate */ }
        }
        throw new AiException("The model's reply was not the JSON that was asked for.");
    }

    private static string Between(string text, char open, char close)
    {
        int a = text.IndexOf(open), b = text.LastIndexOf(close);
        return a >= 0 && b > a ? text[a..(b + 1)] : "";
    }

    /// <summary>A string property, whatever type the model actually put there.</summary>
    private static string Text(JsonNode? node, string name)
    {
        JsonNode? value = node?[name];
        if (value == null) return "";
        try { return value.GetValue<string>(); }
        catch (InvalidOperationException) { return value.ToString(); }
    }

    /// <summary>
    /// Lines whose command header appears in no catalog the model was given.
    ///
    /// Header, and no further. <see cref="ScpiSyntax.Matches"/> compares the part before the
    /// first space, so a documented header carrying a wrong argument passes: Siglent's guide
    /// says <c>C1:BSWV FRQ,1000</c> and a model reaching for the familiar spelling writes
    /// <c>C1:BSWV FREQ,1000</c>, which has the same header and is not caught here. The
    /// instrument catches it, on the bench.
    ///
    /// So an empty result means "every header is documented", not "this script is correct" —
    /// which is why what comes back is a draft shown for review rather than something run.
    /// </summary>
    public static IReadOnlyList<string> Undocumented(
        string script, IReadOnlyList<ScriptContextInstrument> instruments)
    {
        var byAlias = instruments.ToDictionary(i => i.Alias, i => i.Reference,
                                               StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        var withStack = new Stack<string?>();
        string? blockTarget = null;
        string? only = instruments.Count == 1 ? instruments[0].Alias : null;

        foreach (string raw in script.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;

            string upper = line.ToUpperInvariant();
            if (upper.StartsWith("WITH ")) { withStack.Push(blockTarget); blockTarget = line[4..].Trim(); continue; }
            if (upper is "END" or "ENDREPEAT") { if (withStack.Count > 0) blockTarget = withStack.Pop(); continue; }
            if (IsKeyword(upper)) continue;

            string alias = blockTarget ?? only ?? "";
            string command = line;

            int colon = line.IndexOf(':');
            if (colon > 0 && byAlias.ContainsKey(line[..colon].Trim()))
            {
                alias = line[..colon].Trim();
                command = line[(colon + 1)..].Trim();
            }

            int arrow = command.IndexOf("->", StringComparison.Ordinal);
            if (arrow >= 0) command = command[..arrow].Trim();
            if (command.Length == 0) continue;

            CommandReference? reference = byAlias.GetValueOrDefault(alias);
            if (reference == null) continue;   // no catalog to check against — not a finding

            List<string> templates = reference.Commands.Select(c => c.Syntax).ToList();
            if (!ScpiSyntax.MatchesAny(command, templates)) found.Add(line);
        }

        return found;
    }

    private static bool IsKeyword(string upper)
        => upper.StartsWith("DEVICE") || upper.StartsWith("COLUMNS")
        || upper.StartsWith("FOR ") || upper.StartsWith("REPEAT")
        || upper.StartsWith("DELAY") || upper.StartsWith("WAIT")
        || upper.StartsWith("PRINT") || upper.StartsWith("ECHO")
        || upper.StartsWith("LOG ") || upper.StartsWith("RECORD");
}
