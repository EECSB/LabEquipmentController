using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// The assistant's tools over a bench: what the model may read from the instruments open now,
/// the one tool that changes one, and the one that puts a script forward.
///
/// Written once against <see cref="IAssistantBench"/>, so the desktop's window, the web's and
/// <c>lec mcp</c> offer the same tools with the same descriptions, refuse the same calls in the
/// same words and check the same things — GDS Viewer's rule that the hosts of one set of tools
/// cannot drift apart if there is only one set.
///
/// What is different from GDS Viewer's tools is what a change is. An edit to a layout is one press
/// of Undo away; a command to an instrument is not, and an output switched on into a circuit stays
/// on until something switches it off. So <c>send</c> is the only tool that changes anything, it
/// takes one command a call so its card says exactly what will happen, it refuses a header the
/// instrument's catalog does not document unless the model says it means it, and the error queue
/// is read before and after so the answer reports what that command — and only that command —
/// made the instrument complain about. A script, which is many changes, is only ever proposed
/// (SPEC §11c).
/// </summary>
public sealed class BenchToolRunner : ILlmToolRunner, IEditCapableToolRunner
{
    public const string ListInstruments = "list_instruments";
    public const string FindCommands = "find_commands";
    public const string Query = "query";
    public const string Send = "send";
    public const string ReadErrors = "read_errors";
    public const string LookAtScreen = "look_at_screen";
    public const string Wait = "wait";
    public const string ProposeScript = "propose_script";

    /// <summary>How long a reply is shown to the model before it is cut — long enough for a trace's settings, short of a waveform.</summary>
    public const int MostReplyCharacters = 4000;

    /// <summary>How many catalog entries one search lists, at most.</summary>
    public const int MostCommandsListed = 60;

    /// <summary>The longest pause <c>wait</c> takes in one call.</summary>
    public const double MostWaitSeconds = 60;

    /// <summary>
    /// How many times the error queue is read in one go. Ten: an instrument that has more to say
    /// than that is saying the same thing, and every read is a round trip.
    /// </summary>
    public const int MostErrorsRead = 10;

    private static readonly ToolParameter Instrument = new(
        "instrument", ToolValue.Text,
        "The instrument, by the name list_instruments gives it (dmm, psu, scope…). May be left out when only one is open.");

    private static readonly IReadOnlyList<ToolParameter> NoParameters = [];

    private static readonly IReadOnlyList<ToolParameter> FindParameters =
    [
        Instrument,
        new("words", ToolValue.Text,
            "What to look for, as words from the command or its description: \"output voltage\", \"trigger level\", \"VOLT:RANG\".",
            Required: true),
    ];

    private static readonly IReadOnlyList<ToolParameter> QueryParameters =
    [
        Instrument,
        new("command", ToolValue.Text, "One SCPI query, ending in ? before any arguments: \":MEASure:VOLTage:DC?\", \"*IDN?\".", Required: true),
    ];

    private static readonly IReadOnlyList<ToolParameter> SendParameters =
    [
        Instrument,
        new("command", ToolValue.Text, "One SCPI command, with its values: \":SOURce1:VOLTage 2.5\", \"OUTPut1 OFF\".", Required: true),
        new("undocumented", ToolValue.Flag,
            "True to send a command the instrument's catalog does not document — only when you are sure the instrument takes it, and say why. The person sees that it is undocumented."),
    ];

    private static readonly IReadOnlyList<ToolParameter> WaitParameters =
    [
        new("seconds", ToolValue.Number, $"How long to wait, up to {MostWaitSeconds:0} seconds.", Required: true),
    ];

    private static readonly IReadOnlyList<ToolParameter> ProposeParameters =
    [
        new("kind", ToolValue.Text,
            "\"instrument\" for a script one instrument's console runs, \"sequence\" for one that drives several.",
            Required: true, Choices: ["instrument", "sequence"]),
        Instrument with { Description = "For kind instrument: whose console the script is for, by its name. Not used for a sequence." },
        new("script", ToolValue.Text, "The whole script, in the language for its kind.", Required: true),
        new("summary", ToolValue.Text, "One line saying what the script does, for the person deciding whether to use it."),
    ];

    private readonly IAssistantBench _bench;

    public BenchToolRunner(IAssistantBench bench) => _bench = bench;

    /// <summary>Whether <c>send</c> is offered and honoured. Off until the approving runner turns it on.</summary>
    public bool AllowEdits { get; set; }

    /// <summary>
    /// What a proposed script's answer tells the model becomes of it — the host's sentence, not
    /// the tools': a window shows it on a card with Use, and an MCP client has no editor at all.
    /// The windows' sentence to begin with.
    /// </summary>
    public string HowProposalsAreTaken { get; init; } =
        "It is shown to the person on its card, and reaches the editor only when they press Use there and runs only when they press Run - tell them what it does and what to check before running it.";

    /// <summary>
    /// The tools the person switched off for this conversation: not offered, refused in words if
    /// called anyway, and named in the system prompt — withheld without a word, a model concludes
    /// it cannot do the thing and stops.
    /// </summary>
    public IReadOnlySet<string> Off { get; set; } = new HashSet<string>();

    /// <summary>Every tool, whatever is switched off or allowed — what a tool picker lists.</summary>
    public static IReadOnlyList<LlmTool> All { get; } =
    [
        new LlmTool
        {
            Name = ListInstruments,
            Description = "Lists the instruments open on the bench now: the name to call each by, what it is, its address, how many commands its catalog documents, and whether its screen can be captured. Call it first, and again when an instrument is not found - the person may have connected or closed one since.",
            InputSchemaJson = ToolParameter.SchemaFor(NoParameters),
        },
        new LlmTool
        {
            Name = FindCommands,
            Description = "Searches an instrument's command catalog - the commands its vendor documents - and lists the matching entries with their syntax, what each does and an example. Use only commands found here: vendors spell the same setting differently, and one from another instrument either errors or does nothing.",
            InputSchemaJson = ToolParameter.SchemaFor(FindParameters),
        },
        new LlmTool
        {
            Name = Query,
            Description = "Sends one query to an instrument and returns its reply. Reads only - a setting, a measurement, the identity - and runs at once. Anything that sets something goes through send.",
            InputSchemaJson = ToolParameter.SchemaFor(QueryParameters),
        },
        new LlmTool
        {
            Name = Send,
            Description = "Sends one command that changes an instrument - a value, a mode, an output - and reports what its error queue said afterwards. There is no undo on hardware: say what it will do before calling it, one command a call. A query that acts, such as *TST?, goes here too, and its reply is returned.",
            InputSchemaJson = ToolParameter.SchemaFor(SendParameters),
            Changes = true,
        },
        new LlmTool
        {
            Name = ReadErrors,
            Description = "Reads an instrument's error queue (SYST:ERR?) until it is empty, and returns what it held. Reading clears it.",
            InputSchemaJson = ToolParameter.SchemaFor([Instrument]),
        },
        new LlmTool
        {
            Name = LookAtScreen,
            Description = "Captures an instrument's screen, as its own screen dump draws it, and shows you the picture - a scope's traces and measurements, a meter's display, an analyser's sweep.",
            InputSchemaJson = ToolParameter.SchemaFor([Instrument]),
        },
        new LlmTool
        {
            Name = Wait,
            Description = $"Waits, so an instrument can settle after a change before it is measured. Up to {MostWaitSeconds:0} seconds a call.",
            InputSchemaJson = ToolParameter.SchemaFor(WaitParameters),
        },
        new LlmTool
        {
            Name = ProposeScript,
            Description = "Puts a script to the person as a draft, for anything to be repeated, swept or kept. It is never run by you: they put it in the editor with Use and run it with Run. Every command header is checked against the catalogs and the ones no catalog documents are listed.\n\n"
                + "For kind instrument:\n" + ScriptAuthor.SingleInstrumentLanguage
                + "\nFor kind sequence:\n" + ScriptAuthor.SequenceLanguage
                + "\nDeclare each instrument of a sequence with the DEVICE line list_instruments gives for it.",
            InputSchemaJson = ToolParameter.SchemaFor(ProposeParameters),
            Proposes = true,
        },
    ];

    /// <summary>The tools offered now: <c>send</c> only while edits are allowed, and none the person switched off.</summary>
    public IReadOnlyList<LlmTool> Tools
        => [.. All.Where(t => (AllowEdits || !t.Changes) && !Off.Contains(t.Name))];

    /// <summary>
    /// What every host tells the model about working on a bench, for its system prompt. The
    /// sentences that are the host's — the approval rung, where the settings are — are the
    /// host's to add.
    /// </summary>
    public const string Conventions = """
        You are working on a lab bench through tools that reach its instruments over the network.
        - Instruments go by the names list_instruments gives them (dmm, psu, scope, gen…). Call it before anything else.
        - Use only commands from an instrument's catalog, found with find_commands. A command from your knowledge of a similar instrument is the commonest way to be wrong: vendors spell the same setting differently. If the catalog lacks what is needed, say so rather than guessing.
        - query reads and runs at once. send changes an instrument. There is no undo on hardware: an output switched on stays on until something switches it off. Before each send, say in a sentence what it will do.
        - Never switch an output on, raise a voltage, current or power, or lift a protection limit unless the person asked for exactly that. After changing something, query it back to confirm it took.
        - Each send is followed by a read of the instrument's error queue, and the answer says what it reported. read_errors reads it on demand.
        - Instruments settle: wait between a change and a measurement of its effect.
        - Replies come as the instrument writes them - 4.99876E+00 for 4.99876 V. Give values to the person in plain units.
        - Anything to be repeated, swept or kept is a script: put it forward with propose_script. The person runs it, never you.
        """;

    public async Task<LlmToolOutput> RunToolAsync(string name, string argumentsJson, CancellationToken ct)
    {
        if (!All.Any(t => t.Name == name))
            return LlmToolOutput.Refused($"There is no tool called {name}. The tools are {string.Join(", ", Tools.Select(t => t.Name))}.");

        if (Off.Contains(name))
            return LlmToolOutput.Refused($"{name} is switched off in this conversation: the person turned it off in the window's tool picker. Say what you would have used it for, and that it can be switched back on there.");

        if (name == Send && !AllowEdits)
            return LlmToolOutput.Refused("send is not available here: nothing may change an instrument from this conversation.");

        try
        {
            return name switch
            {
                ListInstruments => await ListAsync(argumentsJson, ct),
                FindCommands => await FindAsync(argumentsJson, ct),
                Query => await QueryAsync(argumentsJson, ct),
                Send => await SendAsync(argumentsJson, ct),
                ReadErrors => await ReadErrorsAsync(argumentsJson, ct),
                LookAtScreen => await LookAsync(argumentsJson, ct),
                Wait => await WaitAsync(argumentsJson, ct),
                _ => await ProposeAsync(argumentsJson, ct),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsInstrumentFailure(ex))
        {
            return LlmToolOutput.Refused($"{name} failed on the instrument: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------- the tools

    private async Task<LlmToolOutput> ListAsync(string json, CancellationToken ct)
    {
        if (ToolArguments.Read(json, ListInstruments, NoParameters, out string? problem) is null)
            return LlmToolOutput.Refused(problem!);

        IReadOnlyList<BenchInstrument> bench = await _bench.ListAsync(ct);
        if (bench.Count == 0)
            return new LlmToolOutput { Text = "No instrument is open on this bench. The person connects instruments; there is no tool for it." };

        IReadOnlyList<ScriptContextInstrument> named = Names(bench);
        var text = new StringBuilder(bench.Count == 1 ? "1 instrument is open:\n" : $"{bench.Count} instruments are open:\n");
        for (int i = 0; i < bench.Count; i++)
        {
            BenchInstrument b = bench[i];
            CommandReference? catalog = await _bench.CatalogAsync(b, ct);
            InstrumentProfile profile = b.Profile;

            text.Append($"- {named[i].Alias}: {Identity(b)}, at {b.Host} over {b.Transport}. Taken for: {profile.Name}. ");
            text.Append(catalog is null || catalog.Commands.Count == 0
                ? "No catalog documents its commands. "
                : $"Its catalog documents {catalog.Commands.Count} commands{Extracted(catalog)}. ");
            text.Append(profile.ScreenCaptureCommand is null ? "Its screen cannot be captured." : "Its screen can be captured.");
            if (b.Busy) text.Append(" A script is running on it, and nothing else may talk to it until that ends.");
            text.Append('\n');
        }

        text.Append("\nIn a sequence script, declare them as:\n");
        foreach (ScriptContextInstrument n in named)
            text.Append($"DEVICE {n.Alias} : {n.Model}\n");
        return new LlmToolOutput { Text = text.ToString().TrimEnd() };
    }

    private async Task<LlmToolOutput> FindAsync(string json, CancellationToken ct)
    {
        var args = ToolArguments.Read(json, FindCommands, FindParameters, out string? problem);
        if (args is null) return LlmToolOutput.Refused(problem!);

        var (instrument, alias, refusal) = await ResolveAsync(args, ct);
        if (instrument is null) return LlmToolOutput.Refused(refusal!);

        CommandReference? catalog = await _bench.CatalogAsync(instrument, ct);
        if (catalog is null || catalog.Commands.Count == 0)
        {
            return new LlmToolOutput
            {
                Text = $"No catalog documents {alias}'s commands ({Identity(instrument)}). The IEEE 488.2 common commands - *IDN?, *RST, *CLS, *OPC?, *ESR? - and SYST:ERR? work on most SCPI instruments; anything else needs the person's word for it.",
            };
        }

        IReadOnlyList<CommandRef> found = Search(catalog, args.Text("words")!);
        if (found.Count == 0)
            return new LlmToolOutput { Text = $"Nothing in {alias}'s catalog ({catalog.Commands.Count} commands, {catalog.Instrument}) matches \"{args.Text("words")}\". Try other words - the guide's own, or a part of the command." };

        var text = new StringBuilder($"{alias}'s catalog ({catalog.Instrument}) has {found.Count} matching {(found.Count == 1 ? "entry" : "entries")}");
        text.Append(found.Count > MostCommandsListed ? $"; the first {MostCommandsListed}, the closest first:\n" : ", the closest first:\n");
        foreach (CommandRef c in found.Take(MostCommandsListed))
            text.Append(Entry(c)).Append('\n');
        if (found.Count > MostCommandsListed)
            text.Append($"…and {found.Count - MostCommandsListed} more. Narrow the words to see them.");
        return new LlmToolOutput { Text = text.ToString().TrimEnd() };
    }

    private async Task<LlmToolOutput> QueryAsync(string json, CancellationToken ct)
    {
        var args = ToolArguments.Read(json, Query, QueryParameters, out string? problem);
        if (args is null) return LlmToolOutput.Refused(problem!);

        var (instrument, alias, refusal) = await ResolveAsync(args, ct);
        if (instrument is null) return LlmToolOutput.Refused(refusal!);

        string command = args.Text("command")!;
        if (OneCommand(command, Query) is string notOne) return LlmToolOutput.Refused(notOne);
        if (!IsQuery(command))
            return LlmToolOutput.Refused($"{command} is not a query: its header does not end in ?. A command that sets something goes through send, which waits for the person.");
        if (ActingQuery(command) is string acts) return LlmToolOutput.Refused(acts);
        if (BinaryQuery(command, instrument) is string binary) return LlmToolOutput.Refused(binary);
        if (instrument.Busy) return LlmToolOutput.Refused(BusyMessage(alias));

        CommandReference? catalog = await _bench.CatalogAsync(instrument, ct);
        string reply = await _bench.QueryAsync(instrument, command, ct);
        string unlisted = Documented(command, catalog) ? "" : " (That query is not in its catalog.)";
        return new LlmToolOutput { Text = $"{alias} answered {command} with: {Shown(reply)}{unlisted}" };
    }

    private async Task<LlmToolOutput> SendAsync(string json, CancellationToken ct)
    {
        var args = ToolArguments.Read(json, Send, SendParameters, out string? problem);
        if (args is null) return LlmToolOutput.Refused(problem!);

        var (instrument, alias, refusal) = await ResolveAsync(args, ct);
        if (instrument is null) return LlmToolOutput.Refused(refusal!);

        string command = args.Text("command")!;
        if (OneCommand(command, Send) is string notOne) return LlmToolOutput.Refused(notOne);
        if (BinaryQuery(command, instrument) is string binary) return LlmToolOutput.Refused(binary);
        if (instrument.Busy) return LlmToolOutput.Refused(BusyMessage(alias));

        CommandReference? catalog = await _bench.CatalogAsync(instrument, ct);
        if (!Documented(command, catalog) && !args.Flag("undocumented"))
        {
            string where = catalog is null || catalog.Commands.Count == 0
                ? $"No catalog documents {alias}'s commands, so nothing can be checked against one."
                : $"{ScpiSyntax.HeaderOf(command)} is not in {alias}'s catalog ({catalog.Commands.Count} commands).{Near(command, catalog)}";
            return LlmToolOutput.Refused(where + " If you are sure the instrument takes it, call send again with undocumented set to true and say why - the person will see that it is undocumented.");
        }

        // Read what was already in the queue first, so what is reported afterwards is what this
        // command caused rather than whatever was left from before. An instrument that does not
        // answer the error query costs its whole timeout to find that out, so it is asked once.
        IReadOnlyList<string>? before = await DrainAsync(instrument, ct);

        string? reply = null;
        if (IsQuery(command)) reply = await _bench.QueryAsync(instrument, command, ct);
        else await _bench.SendAsync(instrument, command, ct);

        IReadOnlyList<string>? after = before is null ? null : await DrainAsync(instrument, ct);

        var text = new StringBuilder(reply is null ? $"Sent {command} to {alias}." : $"Sent {command} to {alias}, which answered: {Shown(reply)}.");
        if (after is null)
            text.Append(" Its error queue could not be read (SYST:ERR? went unanswered), so whether it took the command is unknown: query the setting to check.");
        else if (after.Count == 0)
            text.Append(" Its error queue is empty.");
        else
            text.Append($" It then reported: {string.Join("; ", after)}.");
        if (before is { Count: > 0 })
            text.Append($" (Before it was sent, the queue already held, from earlier: {string.Join("; ", before)}.)");
        return new LlmToolOutput { Text = text.ToString(), IsError = after is { Count: > 0 } };
    }

    private async Task<LlmToolOutput> ReadErrorsAsync(string json, CancellationToken ct)
    {
        var args = ToolArguments.Read(json, ReadErrors, [Instrument], out string? problem);
        if (args is null) return LlmToolOutput.Refused(problem!);

        var (instrument, alias, refusal) = await ResolveAsync(args, ct);
        if (instrument is null) return LlmToolOutput.Refused(refusal!);
        if (instrument.Busy) return LlmToolOutput.Refused(BusyMessage(alias));

        IReadOnlyList<string>? errors = await DrainAsync(instrument, ct);
        if (errors is null)
            return LlmToolOutput.Refused($"{alias} did not answer SYST:ERR?, so its error queue cannot be read this way.");
        return new LlmToolOutput
        {
            Text = errors.Count == 0
                ? $"{alias}'s error queue is empty."
                : $"{alias}'s error queue held {errors.Count}, now read and cleared: {string.Join("; ", errors)}.",
        };
    }

    private async Task<LlmToolOutput> LookAsync(string json, CancellationToken ct)
    {
        var args = ToolArguments.Read(json, LookAtScreen, [Instrument], out string? problem);
        if (args is null) return LlmToolOutput.Refused(problem!);

        var (instrument, alias, refusal) = await ResolveAsync(args, ct);
        if (instrument is null) return LlmToolOutput.Refused(refusal!);
        if (instrument.Profile.ScreenCaptureCommand is null)
            return LlmToolOutput.Refused($"{alias}'s guide documents no screen dump ({instrument.Profile.Name}), so its screen cannot be captured.");
        if (instrument.Busy) return LlmToolOutput.Refused(BusyMessage(alias));

        byte[] dump = await _bench.CaptureScreenAsync(instrument, ct);
        var (png, width, height, wrong) = ScreenPicture.ToPng(dump);
        if (png is null) return LlmToolOutput.Refused($"{alias}'s screen could not be shown: {wrong}");

        return new LlmToolOutput
        {
            Text = $"The screen of {alias} ({Identity(instrument)}), {width} × {height} pixels, as the instrument drew it.",
            Png = Convert.ToBase64String(png),
        };
    }

    private static async Task<LlmToolOutput> WaitAsync(string json, CancellationToken ct)
    {
        var args = ToolArguments.Read(json, Wait, WaitParameters, out string? problem);
        if (args is null) return LlmToolOutput.Refused(problem!);

        double seconds = args.Number("seconds")!.Value;
        if (seconds <= 0 || seconds > MostWaitSeconds)
            return LlmToolOutput.Refused($"wait takes more than 0 and up to {MostWaitSeconds:0} seconds; {seconds.ToString(CultureInfo.InvariantCulture)} was asked for. Wait again for longer.");

        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        return new LlmToolOutput { Text = $"Waited {seconds.ToString("0.###", CultureInfo.InvariantCulture)} s." };
    }

    private async Task<LlmToolOutput> ProposeAsync(string json, CancellationToken ct)
    {
        var args = ToolArguments.Read(json, ProposeScript, ProposeParameters, out string? problem);
        if (args is null) return LlmToolOutput.Refused(problem!);

        string script = (args.Text("script") ?? "").Replace("\r\n", "\n").Trim('\n');
        bool sequence = string.Equals(args.Text("kind"), "sequence", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<BenchInstrument> bench = await _bench.ListAsync(ct);

        IReadOnlyList<ScriptContextInstrument> checkedAgainst;
        string where;
        if (sequence)
        {
            var named = Names(bench, script);
            var catalogs = new List<ScriptContextInstrument>();
            for (int i = 0; i < bench.Count; i++)
                catalogs.Add(named[i] with { Reference = await _bench.CatalogAsync(bench[i], ct) });
            checkedAgainst = catalogs;
            where = "a sequence";
        }
        else
        {
            var (instrument, alias, refusal) = await ResolveAsync(args, ct);
            if (instrument is null) return LlmToolOutput.Refused(refusal!);
            checkedAgainst =
            [
                ScriptContext.ForScript(instrument.Identity, instrument.Host)
                    with { Reference = await _bench.CatalogAsync(instrument, ct) },
            ];
            where = $"{alias}'s script";
        }

        int lines = script.Split('\n').Count(l => l.Trim().Length > 0);
        IReadOnlyList<string> undocumented = ScriptAuthor.Undocumented(script, checkedAgainst);
        var text = new StringBuilder($"A draft for {where}, {lines} line{(lines == 1 ? "" : "s")}, checked against the catalogs. {HowProposalsAreTaken}");
        if (undocumented.Count > 0)
            text.Append($" These lines use headers no catalog documents, so say so: {string.Join(" | ", undocumented.Take(12))}{(undocumented.Count > 12 ? $" and {undocumented.Count - 12} more" : "")}.");
        return new LlmToolOutput { Text = text.ToString(), Proposal = script };
    }

    /// <summary>
    /// The instrument a name stands for, resolved as the tools resolve one, or null — for a
    /// window acting on a card's arguments, such as putting a proposed script in that
    /// instrument's editor.
    /// </summary>
    public async Task<BenchInstrument?> FindInstrumentAsync(string? named, CancellationToken ct)
    {
        string json = new System.Text.Json.Nodes.JsonObject { ["instrument"] = named ?? "" }.ToJsonString();
        ToolArguments? args = ToolArguments.Read(json, "find", [Instrument], out _);
        return args is null ? null : (await ResolveAsync(args, ct)).Instrument;
    }

    // ------------------------------------------------------------------- naming the instruments

    /// <summary>
    /// What each open instrument is called — the alias a sequence script would give it, by the
    /// same code the script writer uses, so a name the model learns in conversation is the name
    /// it writes in a script.
    /// </summary>
    private static IReadOnlyList<ScriptContextInstrument> Names(IReadOnlyList<BenchInstrument> bench, string? script = null)
        => ScriptContext.ForSequence(bench, b => b.Identity, b => b.Host, script);

    /// <summary>
    /// The instrument an argument names: by its alias, its address, its model where only one
    /// instrument is that model, or its serial number — or, left out, the only one open. Anything
    /// else is refused with the names that do exist.
    /// </summary>
    private async Task<(BenchInstrument? Instrument, string Alias, string? Refusal)> ResolveAsync(ToolArguments args, CancellationToken ct)
    {
        IReadOnlyList<BenchInstrument> bench = await _bench.ListAsync(ct);
        if (bench.Count == 0)
            return (null, "", "No instrument is open on this bench. The person connects instruments; there is no tool for it.");

        IReadOnlyList<ScriptContextInstrument> named = Names(bench);
        string asked = args.Text("instrument") ?? "";

        // A model echoing a whole line back — "dmm (RIGOL DM3058E at 192.168.1.17)" — means the name in front.
        int bracket = asked.IndexOf(" (", StringComparison.Ordinal);
        if (bracket > 0) asked = asked[..bracket].Trim();

        if (asked.Length == 0)
        {
            return bench.Count == 1
                ? (bench[0], named[0].Alias, null)
                : (null, "", $"Several instruments are open, so say which: {Roll(bench, named)}.");
        }

        for (int i = 0; i < bench.Count; i++)
        {
            if (string.Equals(named[i].Alias, asked, StringComparison.OrdinalIgnoreCase)
                || string.Equals(bench[i].Host, asked, StringComparison.OrdinalIgnoreCase))
                return (bench[i], named[i].Alias, null);
        }

        int[] byModel = [.. Enumerable.Range(0, bench.Count).Where(i => SequenceBinding.Same(SequenceBinding.ModelOf(bench[i].Identity), asked))];
        if (byModel.Length == 1) return (bench[byModel[0]], named[byModel[0]].Alias, null);

        int[] bySerial = [.. Enumerable.Range(0, bench.Count).Where(i => SequenceBinding.SerialOf(bench[i].Identity) is { Length: > 0 } s && SequenceBinding.Same(s, asked))];
        if (bySerial.Length == 1) return (bench[bySerial[0]], named[bySerial[0]].Alias, null);

        return byModel.Length > 1
            ? (null, "", $"More than one open instrument is a {asked}, so name it: {Roll(bench, named)}.")
            : (null, "", $"No instrument called \"{asked}\" is open. Open now: {Roll(bench, named)}.");
    }

    private static string Roll(IReadOnlyList<BenchInstrument> bench, IReadOnlyList<ScriptContextInstrument> named)
        => string.Join(", ", bench.Select((b, i) => $"{named[i].Alias} ({Identity(b)} at {b.Host})"));

    private static string Identity(BenchInstrument b)
    {
        (string maker, string model) = InstrumentProfile.ParseIdentity(b.Identity);
        string serial = SequenceBinding.SerialOf(b.Identity);
        string what = string.Join(" ", new[] { maker, model }.Where(s => s.Length > 0));
        if (what.Length == 0) return "an instrument that gave no identity";
        return serial.Length > 0 ? $"{what}, serial {serial}" : what;
    }

    private static string Extracted(CommandReference catalog)
    {
        int extracted = catalog.Commands.Count(c => c.AiExtracted);
        return extracted == 0 ? "" : $", {extracted} of them read out of a datasheet by a model (◆) and unchecked";
    }

    private static string BusyMessage(string alias)
        => $"A script is running on {alias}, and nothing else may talk to it until that ends: two conversations on one connection collide. Wait, or ask the person to stop the script.";

    // ------------------------------------------------------------------- the commands

    /// <summary>
    /// Why this is not one command, or null. One a call, on one line, with no <c>;</c> joining
    /// another on: a compound message ending in a query would pass for a read while it set a
    /// voltage, and a card that says one thing must be the whole of what is sent.
    /// </summary>
    private static string? OneCommand(string command, string tool)
    {
        if (command.Contains('\n') || command.Contains('\r'))
            return $"{tool} takes one command on one line. Send each line by itself.";

        bool quoted = false;
        char quote = '\0';
        foreach (char c in command)
        {
            if (quoted) { if (c == quote) quoted = false; continue; }
            if (c is '"' or '\'') { quoted = true; quote = c; continue; }
            if (c == ';') return $"{tool} takes one command a call, and {command} joins several with ;. Send each one by itself.";
        }
        return null;
    }

    /// <summary>Whether the command's header — the part before its arguments — ends in a question mark.</summary>
    private static bool IsQuery(string command)
    {
        string head = command.Trim();
        int space = head.IndexOfAny([' ', '\t']);
        if (space > 0) head = head[..space];
        return head.EndsWith('?');
    }

    /// <summary>
    /// Queries that do something: the self-test and calibration ones, which can switch outputs or
    /// take minutes. They go through <c>send</c>, which waits for the person.
    /// </summary>
    private static string? ActingQuery(string command)
    {
        string header = ScpiSyntax.HeaderOf(command).TrimStart(':').ToUpperInvariant();
        bool acts = header is "*TST" or "*CAL" || header.StartsWith("CAL", StringComparison.Ordinal);
        return acts
            ? $"{command} runs a self-test or a calibration, which can change outputs and take a while: it goes through send, which waits for the person."
            : null;
    }

    /// <summary>
    /// Queries whose reply is a binary block — screen dumps, traces, files. Read as text they
    /// leave the rest of the block in the connection, and every reply after them on that
    /// instrument is garbage until it is closed. The console's capture tools read them properly.
    /// </summary>
    private static string? BinaryQuery(string command, BenchInstrument instrument)
    {
        string header = ScpiSyntax.HeaderOf(command).TrimStart(':').ToUpperInvariant();
        string? screen = instrument.Profile.ScreenCaptureCommand;
        bool binary = (screen is not null && string.Equals(header, ScpiSyntax.HeaderOf(screen).TrimStart(':'), StringComparison.OrdinalIgnoreCase))
                   || header.StartsWith("HCOP", StringComparison.Ordinal)
                   || header.StartsWith("HARDC", StringComparison.Ordinal)
                   || header.StartsWith("PRIN", StringComparison.Ordinal)
                   || header.StartsWith("CURV", StringComparison.Ordinal)
                   || (header.StartsWith("DISP", StringComparison.Ordinal) && header.Contains("DATA", StringComparison.Ordinal))
                   || (header.StartsWith("MMEM", StringComparison.Ordinal) && header.Contains("DATA", StringComparison.Ordinal))
                   || (header.Contains("WAV", StringComparison.Ordinal) && header.Contains("DATA", StringComparison.Ordinal));
        return binary
            ? $"{command} answers with a binary block - a picture, a trace or a file - which would be read here as text and leave the connection out of step. look_at_screen captures the screen; the console's own capture reads traces."
            : null;
    }

    private static readonly string[] CommonCommands =
        ["*CLS", "*ESE", "*ESE?", "*ESR?", "*IDN?", "*OPC", "*OPC?", "*OPT?", "*PSC", "*PSC?", "*RCL", "*RST", "*SAV", "*SRE", "*SRE?", "*STB?", "*TRG", "*TST?", "*WAI"];

    /// <summary>
    /// Whether the catalog documents the command's header — or it is an IEEE 488.2 common command
    /// or the SCPI error query, which every instrument here takes and few catalogs repeat.
    /// </summary>
    private static bool Documented(string command, CommandReference? catalog)
    {
        string head = command.Trim();
        int space = head.IndexOfAny([' ', '\t']);
        if (space > 0) head = head[..space];
        if (CommonCommands.Contains(head, StringComparer.OrdinalIgnoreCase)) return true;
        if (ScpiSyntax.Matches(command, ":SYSTem:ERRor[:NEXT]?")) return true;
        return catalog is not null && ScpiSyntax.MatchesAny(command, catalog.Commands.Select(c => c.Syntax));
    }

    private static string Near(string command, CommandReference catalog)
    {
        string words = string.Join(" ", ScpiSyntax.HeaderOf(command).Split(':', StringSplitOptions.RemoveEmptyEntries)
                                                                     .Select(n => new string([.. n.TakeWhile(char.IsLetter)])));
        var near = Search(catalog, words).Take(5).Select(c => c.Syntax).ToList();
        return near.Count == 0 ? "" : $" The nearest it documents: {string.Join(" | ", near)}.";
    }

    /// <summary>
    /// Reads the error queue until it says there is nothing more, up to <see cref="MostErrorsRead"/>
    /// times, and hands back what it held — or null when the instrument does not answer the query
    /// at all, which some families never do. A read that fails partway keeps what came before it:
    /// after a send, the command has gone whatever the queue does next.
    /// </summary>
    private async Task<IReadOnlyList<string>?> DrainAsync(BenchInstrument instrument, CancellationToken ct)
    {
        var errors = new List<string>();
        for (int i = 0; i < MostErrorsRead; i++)
        {
            string reply;
            try
            {
                reply = (await _bench.QueryAsync(instrument, "SYST:ERR?", ct)).Trim();
            }
            catch (Exception ex) when (IsInstrumentFailure(ex) && !ct.IsCancellationRequested)
            {
                if (i == 0) return null;
                break;
            }

            if (reply.Length == 0 || NoError(reply)) break;
            errors.Add(reply);
        }
        return errors;
    }

    // "0,"No error"", "+0,"No Error"", "0, No error": the code is what says it.
    private static bool NoError(string reply)
    {
        string code = new([.. reply.TrimStart('+').TakeWhile(c => char.IsDigit(c) || c == '-')]);
        return int.TryParse(code, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) && n == 0;
    }

    private static string Shown(string reply)
    {
        string text = reply.Trim();
        if (text.StartsWith('#') && text.Length > 1 && char.IsDigit(text[1]))
            return $"a binary block of {text.Length} bytes, which is not shown";
        return text.Length <= MostReplyCharacters
            ? (text.Length == 0 ? "(nothing)" : text)
            : text[..MostReplyCharacters] + $"… and {text.Length - MostReplyCharacters} characters more";
    }

    // ------------------------------------------------------------------- the catalog search

    /// <summary>
    /// Catalog entries matching any of the words, the closest first: a word in the command itself
    /// counts three times what one in its description or category does, and the guide's order
    /// breaks ties, since a guide lists a subsystem's commands together.
    /// </summary>
    internal static IReadOnlyList<CommandRef> Search(CommandReference catalog, string words)
    {
        string[] terms = [.. words.ToLowerInvariant()
                                  .Split([' ', ',', ':', '?', '*', '[', ']', '<', '>', '(', ')', '"', '\'', '/'], StringSplitOptions.RemoveEmptyEntries)
                                  .Where(t => t.Length > 1)
                                  .Distinct()];
        if (terms.Length == 0) return [];

        return [.. catalog.Commands
            .Select((c, index) => (c, index, score: terms.Sum(t =>
                (c.Syntax.Contains(t, StringComparison.OrdinalIgnoreCase) ? 3 : 0)
              + (c.Description.Contains(t, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
              + (c.Category.Contains(t, StringComparison.OrdinalIgnoreCase) ? 1 : 0))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Select(x => x.c)];
    }

    private static string Entry(CommandRef c)
    {
        var line = new StringBuilder($"{c.Syntax} - {c.Description}");
        if (!string.IsNullOrWhiteSpace(c.Example)) line.Append($" Example: {c.Example}");
        if (c.BenchVerified) line.Append(" [checked on a real instrument]");
        if (c.AiExtracted) line.Append(" [◆ read out of a datasheet by a model, unchecked]");
        if (!string.IsNullOrWhiteSpace(c.GuideMisprint)) line.Append($" [the guide may be wrong here: {c.GuideMisprint}]");
        return line.ToString();
    }

    /// <summary>
    /// What going wrong on the instrument's side looks like — a timeout, a dropped connection, a
    /// refusal from the server in between — as opposed to a fault here, which should surface.
    /// </summary>
    private static bool IsInstrumentFailure(Exception ex)
        => ex is TimeoutException or IOException or SocketException or InvalidOperationException
              or System.Net.Http.HttpRequestException
        || (ex is OperationCanceledException);
}
