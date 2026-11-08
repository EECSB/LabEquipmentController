using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// The assistant's bench as the sessions this process holds open — the desktop's consoles, and
/// the instruments <c>lec mcp</c> dials.
///
/// An instrument is known by its address. That is unique among open sessions by design: a
/// second session to one address is never opened (<see cref="SessionRegistry"/> — the Rigol
/// DS2202 wedges its firmware if one is), so the address is the one handle that both survives
/// the list changing around it and means something to the person reading a card.
/// </summary>
public sealed class SessionBench : IAssistantBench
{
    private readonly Func<IReadOnlyList<InstrumentSession>> _sessions;
    private readonly Func<InstrumentSession, CommandReference?> _catalog;

    /// <param name="sessions">The sessions open now, in the order the person sees them — asked at every call.</param>
    /// <param name="catalog">
    /// The commands documented for one, when the build keeps more than the curated catalog —
    /// a datasheet's extracted commands. Left out, the curated catalog for its family.
    /// </param>
    public SessionBench(Func<IReadOnlyList<InstrumentSession>> sessions,
                        Func<InstrumentSession, CommandReference?>? catalog = null)
    {
        _sessions = sessions;
        _catalog = catalog ?? (s => CommandReference.ForIdentity(s.Identity));
    }

    /// <summary>
    /// Raised after every command the assistant sends and every query it makes, with the reply
    /// where there was one — so the console it went to can write it into its log, as it does what
    /// the person types. A command the person did not type still shows where they look.
    /// </summary>
    public event Action<InstrumentSession, string, string?>? Exchanged;

    public Task<IReadOnlyList<BenchInstrument>> ListAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<BenchInstrument>>(
            [.. _sessions().Where(s => s.IsConnected).Select(Describe)]);

    public Task<CommandReference?> CatalogAsync(BenchInstrument instrument, CancellationToken ct)
        => Task.FromResult(_catalog(Find(instrument)));

    public async Task<string> QueryAsync(BenchInstrument instrument, string command, CancellationToken ct)
    {
        InstrumentSession session = Find(instrument);
        string reply = await session.Client.QueryAsync(command, ct);
        Exchanged?.Invoke(session, command, reply);
        return reply;
    }

    public async Task SendAsync(BenchInstrument instrument, string command, CancellationToken ct)
    {
        InstrumentSession session = Find(instrument);
        await session.Client.SendAsync(command, ct);
        Exchanged?.Invoke(session, command, null);
    }

    public async Task<byte[]> CaptureScreenAsync(BenchInstrument instrument, CancellationToken ct)
    {
        InstrumentSession session = Find(instrument);
        string command = session.Profile.ScreenCaptureCommand
                      ?? throw new InvalidOperationException("No screen-capture command is known for this instrument.");
        try
        {
            // A full-screen BMP is about a megabyte: the same headroom over the user's timeout
            // that Capture Screen gives it.
            session.Client.TimeoutMs = Math.Max(session.UserTimeoutMs, 15000);
            foreach (string setup in session.Profile.ScreenCaptureSetup)
            {
                await session.Client.SendAsync(setup, ct);
                Exchanged?.Invoke(session, setup, null);
            }

            byte[] data = await session.Client.QueryBinaryAsync(command, ct);
            Exchanged?.Invoke(session, command, $"({data.Length:N0} bytes of screen)");
            return data;
        }
        finally
        {
            session.Client.TimeoutMs = session.UserTimeoutMs;
        }
    }

    private static BenchInstrument Describe(InstrumentSession s)
        => new(s.Host, s.Identity, s.Host, s.Client.Description, s.IsBusy);

    // By address, every time: the session a card named may have been closed since it was listed.
    private InstrumentSession Find(BenchInstrument instrument)
        => _sessions().FirstOrDefault(s => s.IsConnected && string.Equals(s.Host, instrument.Key, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"{instrument.Host} is no longer open.");
}
