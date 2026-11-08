using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// One open instrument, as the assistant's tools see it.
/// </summary>
/// <param name="Key">The build's own handle for it — a session, an address — which the tools hand back and never show.</param>
/// <param name="Identity">Its *IDN? reply, or "" if it gave none.</param>
/// <param name="Host">Its address, as the person sees it.</param>
/// <param name="Transport">How it is reached — "VXI-11", "raw socket on 5555" — as the console header says it.</param>
/// <param name="Busy">A script is driving it, and nothing else may talk to it until that ends.</param>
public sealed record BenchInstrument(string Key, string Identity, string Host, string Transport, bool Busy)
{
    /// <summary>What it was taken for — quick commands, screen capture, catalog — from its identity.</summary>
    public InstrumentProfile Profile => InstrumentProfile.ForIdentity(Identity);
}

/// <summary>
/// The bench the assistant's tools work on: the instruments open now, and the four things a tool
/// does to one.
///
/// Each build has its own, because each reaches its instruments its own way — the desktop holds
/// the sessions, the web page asks its server, <c>lec mcp</c> dials them itself — and the tools
/// in <see cref="BenchToolRunner"/> are written once against this. Everything a call says, refuses
/// or checks is the tools'; this only carries words to an instrument and back.
/// </summary>
public interface IAssistantBench
{
    /// <summary>The instruments open now, in the order the person sees them.</summary>
    Task<IReadOnlyList<BenchInstrument>> ListAsync(CancellationToken ct);

    /// <summary>
    /// The commands documented for one, or null when nothing is: the curated catalog for its
    /// family, and the commands a datasheet reader extracted for its model where the build keeps
    /// them (SPEC §11b, marked ◆).
    /// </summary>
    Task<CommandReference?> CatalogAsync(BenchInstrument instrument, CancellationToken ct);

    /// <summary>Writes one query and reads its reply, as the console would.</summary>
    Task<string> QueryAsync(BenchInstrument instrument, string command, CancellationToken ct);

    /// <summary>Writes one command that has no reply, as the console would.</summary>
    Task SendAsync(BenchInstrument instrument, string command, CancellationToken ct);

    /// <summary>
    /// The instrument's screen, as the bytes its screen dump returned — PNG or BMP, by family —
    /// with the setup the family needs sent first, as Capture Screen does it.
    /// </summary>
    Task<byte[]> CaptureScreenAsync(BenchInstrument instrument, CancellationToken ct);
}
