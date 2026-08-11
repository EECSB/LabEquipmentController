using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LabEquipmentController.Tests.Bench;

/// <summary>What happened when one catalog entry was sent to the instrument.</summary>
public sealed record SweepResult(string Syntax, string Sent, SweepOutcome Outcome, string Detail);

public enum SweepOutcome
{
    /// <summary>Answered with something, and the error queue stayed clean.</summary>
    Answered,

    /// <summary>The instrument reported an error — usually an undefined header.</summary>
    Rejected,

    /// <summary>No reply inside the timeout.</summary>
    TimedOut,

    /// <summary>
    /// The read returned, and returned nothing.
    ///
    /// Its own outcome because it is not confirmation of anything, and it used to be counted
    /// as <see cref="Answered"/>. On a DS2202 — which has no logic analyser — :LA:ACTive?,
    /// :LA:DIGital&lt;n&gt;:POSition? and :LA:POD&lt;n&gt;:DISPlay? all came back empty and
    /// were listed among the answers, which is a claim that the instrument supports them.
    /// Whether an empty read lands here or as a timeout is a matter of timing, which is why
    /// two runs of the same sweep disagreed about those three.
    /// </summary>
    Empty,

    /// <summary>Not sendable safely, so never tried.</summary>
    Skipped,
}

/// <summary>
/// Turns a catalog into the subset that can be sent to a live instrument without changing
/// anything about it, and decides what each reply means.
///
/// **Queries only, and only those needing no argument.** A catalog is mostly setting
/// commands, and a sweep that sent them would set a generator's output on, move a scope's
/// timebase, or arm something. The whole point is to leave the instrument exactly as it was
/// found, so anything that is not a bare query is skipped rather than guessed at.
///
/// Optional groups are dropped, since `SAMPle:COUNt? [{MIN|MAX|DEF}]` is perfectly valid
/// without its tail. Channel, marker and trace suffixes are filled with 1 — `&lt;n&gt;` in
/// `:CHANnel&lt;n&gt;:SCALe?` is a slot, not a value to invent, and every instrument here has
/// a channel 1. That takes the sendable share from 451 commands to 622.
/// </summary>
public static class CatalogSweep
{
    /// <summary>Suffixes that are an index into something the instrument already has.</summary>
    private static readonly Regex Suffix = new(@"<\s*[nmxtk]\s*>", RegexOptions.IgnoreCase);

    /// <summary>
    /// The command to send for this entry, or null when it cannot be sent without either
    /// inventing an argument or changing the instrument.
    /// </summary>
    public static string? Sendable(string syntax)
    {
        if (string.IsNullOrWhiteSpace(syntax)) return null;

        // Brackets mean two different things, and treating them alike invents commands.
        //
        //   [{MIN|MAX|DEF}]  an optional *argument* — drop it, the query stands without one
        //   [:VOLTage]       an optional *node* — keep the word, drop the brackets
        //
        // Dropping "[:VOLTage]" from "MEASure[:VOLTage]:DC?" yields "MEASure:DC?". SCPI says
        // that short form is legal; a Siglent SDM answers it by hanging, which then reads as
        // a catalog entry the instrument does not support. The long form is what the guide
        // prints and what every instrument accepts, so that is what gets sent.
        string t = syntax;
        for (int i = 0; i < 5; i++)
        {
            string next = Regex.Replace(t, @"\[([^\[\]]*)\]",
                m => m.Groups[1].Value.StartsWith(':') ? m.Groups[1].Value : "");
            if (next == t) break;
            t = next;
        }

        t = Suffix.Replace(t, "1").Trim();
        t = Regex.Replace(t, @"\s+", " ");

        // What is left must be a query with nothing to fill in.
        if (!t.EndsWith("?")) return null;
        if (t.Contains('<') || t.Contains('{') || t.Contains('|')) return null;
        if (t.Contains(' ')) return null;             // a query still wanting an argument

        return t;
    }

    /// <summary>Every entry of a catalog that can be swept, in catalog order.</summary>
    public static IReadOnlyList<(CommandRef Command, string Send)> Plan(CommandReference reference)
        => reference.Commands
            .Select(c => (Command: c, Send: Sendable(c.Syntax)))
            .Where(p => p.Send != null)
            .Select(p => (p.Command, p.Send!))
            .ToList();

    /// <summary>
    /// Does this error-queue reply mean the instrument did not understand the command?
    ///
    /// SCPI answers an unknown header with -113 "Undefined header"; -100 to -199 are all
    /// command errors. A clean queue answers 0 or "No error". Anything else is reported as
    /// the detail rather than judged, because an instrument busy or out of range is not the
    /// same as one that has never heard of the command.
    /// </summary>
    public static bool IsUndefinedHeader(string? errorReply)
    {
        if (string.IsNullOrWhiteSpace(errorReply)) return false;
        Match m = Regex.Match(errorReply, @"-?\d+");
        if (!m.Success) return false;
        int code = int.Parse(m.Value);
        return code is <= -100 and >= -199;
    }

    /// <summary>A clean error queue: 0, or a phrase saying so.</summary>
    public static bool IsClean(string? errorReply)
        => string.IsNullOrWhiteSpace(errorReply)
        || errorReply.TrimStart().StartsWith("0")
        || errorReply.Contains("No error", StringComparison.OrdinalIgnoreCase);
}
