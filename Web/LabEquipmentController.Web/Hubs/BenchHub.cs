using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LabEquipmentController.Web.Bench;
using LabEquipmentController.Web.Client.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace LabEquipmentController.Web.Hubs;

/// <summary>
/// The push channel: scan hits as they are found, and script output as it happens.
/// </summary>
/// <remarks>
/// Runs are watched by group, because a run belongs to the bench rather than to whoever
/// started it: two people can watch the same sweep, and joining is by the run id handed back
/// by the call that started it, so there is nothing to guess at.
///
/// A scan is the other shape. It belongs to the browser that asked for it — it drives that
/// page's progress bar and nothing else — so it is streamed straight back to the caller
/// instead. That also settles cancellation for free: a browser that stops listening cancels
/// the token the sweep is running under, which is exactly what the Stop button needs and is
/// also what should happen when the tab is closed mid-sweep.
/// </remarks>
public sealed class BenchHub(BenchService bench, RunService runs) : Hub
{
    /// <summary>
    /// Who is watching the bench, which is what decides whether it is still wanted.
    ///
    /// Every page holds one of these for as long as it is open - see MainLayout - so a hub
    /// connection is the nearest thing the server has to "a browser has this open". A reload
    /// drops one and makes another a moment later, which is why the bench lingers rather than
    /// closing on the spot.
    /// </summary>
    public override Task OnConnectedAsync()
    {
        bench.Watching();
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? error)
    {
        bench.Unwatching();
        return base.OnDisconnectedAsync(error);
    }

    /// <summary>
    /// Join a run's group, and tell the run that someone is listening — it holds its first
    /// line until this arrives, so a script that finishes in eight milliseconds is not
    /// talking to an empty room.
    /// </summary>
    public async Task Watch(string runId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, runId);
        runs.Listening(runId);
    }

    public Task Unwatch(string runId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, runId);

    /// <summary>
    /// Sweep for instruments, reporting as it goes: how far along it is, and every instrument
    /// the moment it answers.
    /// </summary>
    /// <remarks>
    /// The scanner reports through a callback and this method has to yield, so a channel sits
    /// between them: the sweep writes, the stream reads. Unbounded, because the writer is the
    /// scanner's own thread and it must never be made to wait on a browser — the reports are
    /// small, and the scanner already throttles the count to about two hundred for a whole
    /// sweep rather than one per address.
    /// </remarks>
    public async IAsyncEnumerable<ScanEvent> Scan(ScanRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<ScanEvent>(new UnboundedChannelOptions { SingleReader = true });

        // Not awaited here: this method has to get to its yield loop for anything to be
        // delivered at all. The sweep finishes by completing the channel, which ends the loop.
        _ = Task.Run(async () =>
        {
            try
            {
                var report = await bench.ScanAsync(req, e => channel.Writer.TryWrite(e), ct);
                channel.Writer.TryWrite(new ScanEvent(
                    ScanStage.Done, report.Scanned, report.Scanned, null, null, report.Error));
            }
            catch (OperationCanceledException)
            {
                // The browser hung up. There is nobody left to tell.
            }
            catch (Exception ex)
            {
                channel.Writer.TryWrite(new ScanEvent(ScanStage.Done, 0, 0, null, null, ex.Message));
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var e in channel.Reader.ReadAllAsync(ct)) yield return e;
    }

    /// <summary>
    /// The same, over the server's serial ports: open each one and ask who is there.
    ///
    /// A separate method rather than a flag on <see cref="Scan"/> because the two take
    /// genuinely different inputs — an interface and an address range against a port name and
    /// a list of baud rates — and a request record with half its fields ignored is a worse
    /// contract than two records.
    /// </summary>
    public async IAsyncEnumerable<ScanEvent> ScanSerial(SerialScanRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<ScanEvent>(new UnboundedChannelOptions { SingleReader = true });

        _ = Task.Run(async () =>
        {
            try
            {
                var report = await bench.ScanSerialAsync(req, e => channel.Writer.TryWrite(e), ct);
                channel.Writer.TryWrite(new ScanEvent(
                    ScanStage.Done, report.Scanned, report.Scanned, null, null, report.Error));
            }
            catch (OperationCanceledException)
            {
                // The browser hung up. There is nobody left to tell.
            }
            catch (Exception ex)
            {
                channel.Writer.TryWrite(new ScanEvent(ScanStage.Done, 0, 0, null, null, ex.Message));
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var e in channel.Reader.ReadAllAsync(ct)) yield return e;
    }

    /// <summary>
    /// Poll one measurement on this instrument and stream the readings back.
    ///
    /// The same shape as a scan and for the same reasons: it belongs to the page that asked
    /// for it, and a browser that stops listening cancels the token the polling runs under —
    /// which is what the Stop button needs, and what should happen when the pane is closed or
    /// the tab goes away mid-run. Nothing here holds a channel, because unlike a scan the work
    /// is already an async sequence.
    /// </summary>
    public IAsyncEnumerable<ReadingDto> Readout(string sessionId, string query, int intervalMs, CancellationToken ct)
        => bench.ReadoutAsync(sessionId, query, intervalMs, ct);
}
