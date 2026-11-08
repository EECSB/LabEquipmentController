using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabEquipmentController;

/// <summary>
/// The assistant's system prompt, written again at every question from what is true then: the
/// instruments open, the approval rung in force, the tools switched off.
///
/// In Core so the desktop's window and the page's say the same thing — the only differences are
/// the ones handed in. GDS Viewer's rule that the prompt is built at every send, because a model
/// told every call is vetted writes differently from one told nothing is watched, and because the
/// bench changes under a conversation: an instrument opened since the last question is one the
/// model should hear about without having to ask.
/// </summary>
public static class AssistantPrompt
{
    /// <summary>The most instruments named in the prompt itself; past that, list_instruments has the rest.</summary>
    public const int MostNamed = 12;

    /// <summary>What a rung is called where the person picks it — the same words in both builds.</summary>
    public static string RungLabel(ToolApprovalMode mode) => mode switch
    {
        ToolApprovalMode.Ask => "Ask before every step",
        ToolApprovalMode.AutoReadWrite => "Change without asking",
        _ => "Ask before changes",
    };

    /// <summary>Every rung, in the order the picker lists them: each one lets through what the one above it does, and more.</summary>
    public static IReadOnlyList<ToolApprovalMode> Rungs { get; } =
        [ToolApprovalMode.Ask, ToolApprovalMode.AutoRead, ToolApprovalMode.AutoReadWrite];

    /// <summary>The words the box opens on, and what <c>/status</c> asks: a request that works as it stands.</summary>
    public const string StatusQuestion = "What is open on the bench, and what does each instrument read right now?";

    /// <summary>What <c>/errors</c> asks.</summary>
    public const string ErrorsQuestion =
        "Read the error queue of every instrument open, and say what each error means and what probably caused it.";

    /// <summary>The start of a request that <c>/script</c> puts in the box, to be finished there.</summary>
    public const string ScriptStart = "Write a script that ";

    /// <summary>
    /// A call's arguments on one line, as its card shows them: the instrument and the command
    /// first, the rest as given, and a proposal's script left to the box under it. Cut at 300
    /// characters; what does not read as JSON is shown as it came.
    /// </summary>
    public static string CallLine(string argumentsJson)
    {
        try
        {
            if (JsonNode.Parse(argumentsJson) is not JsonObject args) return argumentsJson;
            var parts = new List<string>();
            if (args["instrument"] is JsonValue i && i.TryGetValue(out string? instrument) && instrument.Length > 0) parts.Add(instrument);
            if (args["command"] is JsonValue c && c.TryGetValue(out string? command) && command.Length > 0) parts.Add(command);
            foreach (var (name, value) in args)
            {
                if (name is "instrument" or "command" or "script") continue;
                parts.Add($"{name}: {value?.ToJsonString()}");
            }
            string line = string.Join(" · ", parts);
            return line.Length > 300 ? line[..300] + "…" : line;
        }
        catch (JsonException)
        {
            return argumentsJson;
        }
    }

    /// <summary>Whether a call's card is waiting on the person: no decision yet, and a gate still open.</summary>
    public static bool Waiting(ChatTurn turn)
        => turn.Kind == ChatTurn.Tool && turn.Decision is null && turn.Gate is not null && !turn.Gate.Task.IsCompleted;

    /// <summary>What happened to a call, in the words its card says it with, or "" while it simply runs.</summary>
    public static string CallState(ChatTurn turn) => turn.Decision switch
    {
        null when Waiting(turn) => turn.Changes ? "waits for you: this changes the instrument" : "waits for you",
        null => "",
        true when turn.Auto => "ran without asking",
        true => "approved",
        false => "declined",
    };

    /// <summary>
    /// Where a proposed script goes, from the arguments it was proposed with: a sequence, or the
    /// script of the instrument named (null when none was, for the one instrument there is).
    /// Arguments that do not read are a script for that one instrument.
    /// </summary>
    public static (bool Sequence, string? Instrument) ProposalTarget(string argumentsJson)
    {
        try
        {
            if (JsonNode.Parse(argumentsJson) is JsonObject args)
            {
                string? kind = args["kind"] is JsonValue k && k.TryGetValue(out string? said) ? said : null;
                string? named = args["instrument"] is JsonValue n && n.TryGetValue(out string? who) ? who : null;
                return (string.Equals(kind, "sequence", StringComparison.OrdinalIgnoreCase), named);
            }
        }
        catch (JsonException)
        {
        }
        return (false, null);
    }

    /// <summary>
    /// The line over the conversation saying what the model will be told about, so the person can
    /// see it before asking: the instruments open, by the names on their console tabs
    /// (<c>DS2202 (192.168.1.17)</c>), and what happens to a read and to a change.
    /// </summary>
    public static string Context(IReadOnlyList<BenchInstrument> bench)
    {
        var titles = new List<string>(bench.Count);
        foreach (BenchInstrument instrument in bench)
        {
            (_, string model) = InstrumentProfile.ParseIdentity(instrument.Identity);
            titles.Add(model.Length == 0 ? $"Instrument ({instrument.Host})" : $"{model} ({instrument.Host})");
        }
        return titles.Count switch
        {
            0 => "No instrument is open. The assistant works on the instruments open on the bench; connect one to give it something to work on.",
            1 => $"Working on the one instrument open: {titles[0]}. Reads run as the model asks; a command that changes it waits for you, unless Changes says otherwise.",
            _ => $"Working on the {titles.Count} instruments open: {string.Join(", ", titles)}. Reads run as the model asks; a command that changes one waits for you, unless Changes says otherwise.",
        };
    }

    /// <summary>
    /// The bench in one line, as a kept chat records it and its row in the chats list shows it: each
    /// instrument open by the name the tools give it, with its model ("dmm 34465A, psu E36313A"),
    /// or "" with nothing open. A reopened chat compares it with the bench open then, to say so
    /// when they differ.
    /// </summary>
    public static string BenchLine(IReadOnlyList<BenchInstrument> bench)
    {
        if (bench.Count == 0) return "";

        IReadOnlyList<ScriptContextInstrument> named = ScriptContext.ForSequence(bench, b => b.Identity, b => b.Host);
        var parts = new List<string>(bench.Count);
        for (int i = 0; i < bench.Count; i++)
        {
            (_, string model) = InstrumentProfile.ParseIdentity(bench[i].Identity);
            parts.Add(model.Length > 0 ? $"{named[i].Alias} {model}" : named[i].Alias);
        }
        return string.Join(", ", parts);
    }

    /// <summary>
    /// The line a kept chat opens again with (<see cref="AiChat.Restore"/>): when, and, when the bench
    /// open now is not the one it was about, both, so neither the person nor the model takes the old
    /// names for the instruments in front of them.
    /// </summary>
    public static string ReopenedLine(string keptBench, string benchNow, DateTime now)
    {
        string line = $"This chat was opened again on {now.ToString("d MMMM yyyy 'at' HH:mm", CultureInfo.InvariantCulture)}.";
        if (keptBench == benchNow) return line;
        return line + $" It was about {(keptBench.Length > 0 ? keptBench : "no instrument")}; open now: {(benchNow.Length > 0 ? benchNow : "none")}.";
    }

    /// <param name="bench">The instruments open now, as the tools list them.</param>
    /// <param name="mode">The rung in force as the question is sent.</param>
    /// <param name="off">The tools the person switched off in this conversation.</param>
    /// <param name="where">Where the person is — "the desktop app", "the bench's web page" — for the sentences that are the host's.</param>
    /// <param name="today">The date, which a model does not otherwise know and a log line often asks about.</param>
    public static string Build(IReadOnlyList<BenchInstrument> bench, ToolApprovalMode mode,
                               IReadOnlySet<string> off, string where, DateTime today)
    {
        var text = new StringBuilder();
        text.Append("You are the AI assistant in Lab Equipment Controller, working with the person at ").Append(where).Append('.');
        text.Append(" Today is ").Append(today.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)).Append(".\n\n");
        text.Append(BenchToolRunner.Conventions).Append('\n');

        text.Append(mode switch
        {
            ToolApprovalMode.Ask =>
                "Every tool call, reads included, waits for the person to approve it on a card. Ask for what you need in as few calls as you can.",
            ToolApprovalMode.AutoReadWrite =>
                "Every call runs at once, sends included, and each is shown to the person as it happens. Nothing waits for them, so be sure of a send before you make it.",
            _ =>
                "Reads run at once. Each send waits for the person to approve or decline it on a card, so never say that you cannot change an instrument - say what you would send, and send it.",
        });
        text.Append(" A call the person declines is answered as declined: do not try another way to the same end, ask them instead.\n");

        if (off.Count > 0)
        {
            text.Append("\nSwitched off in this conversation by the person, in the window's tool picker: ")
                .Append(string.Join(", ", off.Order(StringComparer.Ordinal)))
                .Append(". Say what you would have used one for, and that it can be switched back on there.");
            if (off.Contains(BenchToolRunner.Send))
                text.Append(" With send off, this conversation only reads: say so if a change is asked for.");
            text.Append('\n');
        }

        text.Append('\n');
        if (bench.Count == 0)
        {
            text.Append("No instrument is open yet. The person connects instruments in the app; there is no tool for it.\n");
            return text.ToString();
        }

        IReadOnlyList<ScriptContextInstrument> named = ScriptContext.ForSequence(bench, b => b.Identity, b => b.Host);
        text.Append(bench.Count == 1 ? "The instrument open now:\n" : $"The {bench.Count} instruments open now:\n");
        for (int i = 0; i < Math.Min(bench.Count, MostNamed); i++)
        {
            (string maker, string model) = InstrumentProfile.ParseIdentity(bench[i].Identity);
            string what = string.Join(" ", new[] { maker, model }.Where(s => s.Length > 0));
            text.Append($"- {named[i].Alias}: {(what.Length > 0 ? what : "no identity given")} at {bench[i].Host}");
            if (bench[i].Busy) text.Append(" (a script is running on it)");
            text.Append('\n');
        }
        if (bench.Count > MostNamed)
            text.Append($"…and {bench.Count - MostNamed} more, which list_instruments names.\n");
        return text.ToString();
    }
}
