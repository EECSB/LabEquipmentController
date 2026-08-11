using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>Outcome of a <see cref="CommandDiscovery"/> attempt.</summary>
public sealed class CommandDiscoveryResult
{
    public bool Success { get; init; }

    /// <summary>The raw header dump (empty on failure).</summary>
    public string HeaderList { get; init; } = "";

    /// <summary>The individual command headers parsed out of the dump.</summary>
    public string[] Headers { get; init; } = Array.Empty<string>();

    public int Count => Headers.Length;
}

/// <summary>
/// Best-effort "list every command" for an instrument.
///
/// There is no universal way to enumerate an instrument's command set — the
/// programming manual is ground truth. The one standard runtime query is SCPI-99's
/// <c>SYSTem:HELP:HEADers?</c>, which returns the full command-header tree as an
/// IEEE 488.2 block. Many budget instruments (notably Rigol and Siglent) don't
/// implement it, in which case this reports failure and the caller should point the
/// user at the documentation.
/// </summary>
public static class CommandDiscovery
{
    public const string Query = "SYSTem:HELP:HEADers?";

    public static async Task<CommandDiscoveryResult> DiscoverAsync(
        IInstrumentClient client, CancellationToken ct = default)
    {
        byte[] raw;
        try
        {
            raw = await client.QueryBinaryAsync(Query, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A real cancellation, and now only that: a query that simply goes unanswered
            // raises TimeoutException (see Deadline) and falls through to the line below,
            // which is where an instrument with no SYSTem:HELP:HEADers? was always meant
            // to land. Before that split it arrived here and was reported as cancelled.
            throw;
        }
        catch
        {
            return new CommandDiscoveryResult { Success = false };   // timed out / errored
        }

        string text = Encoding.ASCII.GetString(raw);
        string[] headers = text
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(h => h.Trim())
            .Where(h => h.Length > 0)
            .ToArray();

        // A genuine header dump is many lines, most of which look like SCPI headers
        // (":CHAN...", "*IDN", ...). Anything shorter/odder means the query wasn't
        // understood (an error line, a bare "0", or nothing).
        int headerLike = headers.Count(h => h.StartsWith(':') || h.StartsWith('*') || h.Contains(':'));
        bool looksValid = headers.Length >= 3 && headerLike >= headers.Length / 2;

        return looksValid
            ? new CommandDiscoveryResult { Success = true, HeaderList = text.Trim(), Headers = headers }
            : new CommandDiscoveryResult { Success = false };
    }
}
