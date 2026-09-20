using System;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// How long one command is worth waiting for.
///
/// The timeout on the Timeout field is set for a question: ask an instrument what its
/// timebase is and it answers in milliseconds, so a few seconds is generous and a reply that
/// slow means something is wrong. A few commands are not questions about state — they tell
/// the instrument to go and <em>measure something</em>, and how long that takes is the
/// instrument's business. A Siglent SDM3065X answers <c>MEASure:RESistance?</c> in 2.7 s
/// asked on its own, and takes longer than five seconds to do it straight after a current
/// reading, because changing function moves relays and re-ranges. At the app's default of
/// three seconds, pressing the meter's own Resistance button reported a healthy instrument
/// as one that had stopped answering.
///
/// So those commands get a floor under their allowance, and every other command is left on
/// the user's own figure. The same shape the waveform path already used — raise it around
/// the transfer, put it back afterwards — expressed once instead of per window.
/// </summary>
public static class QueryTime
{
    /// <summary>
    /// The least a command that takes a reading is given, however short the user's timeout.
    /// The same 15 s a waveform transfer is allowed, and for the same reason: it is the
    /// instrument's work being waited on, not the link.
    /// </summary>
    public const int ReadingFloorMs = 15000;

    /// <summary>
    /// Does this command make the instrument go and measure?
    ///
    /// Three headers, deliberately few. <c>MEASure</c> and <c>READ</c> both start an
    /// acquisition — the first configures and reads, the second reads on the current
    /// configuration — and <c>*TST</c> runs a self-test, which on some instruments takes the
    /// best part of a minute. <c>FETCh?</c> is not here: it returns the reading already
    /// taken, and is as quick as any other question.
    /// </summary>
    public static bool TakesAReading(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        string head = command.Trim().TrimStart(':');
        int space = head.IndexOf(' ');
        if (space > 0) head = head[..space];

        return head.StartsWith("MEAS", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("READ", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("*TST", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What to allow this command, given the timeout the user set.</summary>
    public static int Allow(string? command, int userTimeoutMs)
        => TakesAReading(command) ? Math.Max(userTimeoutMs, ReadingFloorMs) : userTimeoutMs;

    /// <summary>
    /// Query, allowing for the work the command asks the instrument to do, and leaving the
    /// link's timeout as it was found. Everything that sends a command a user typed or
    /// pressed goes through here, in both builds.
    /// </summary>
    public static async Task<string> AskAsync(this IInstrumentClient client, string command,
                                              CancellationToken ct = default)
    {
        int was = client.TimeoutMs;
        int allow = Allow(command, was);
        if (allow == was) return await client.QueryAsync(command, ct).ConfigureAwait(false);

        client.TimeoutMs = allow;
        try { return await client.QueryAsync(command, ct).ConfigureAwait(false); }
        finally { client.TimeoutMs = was; }
    }
}
