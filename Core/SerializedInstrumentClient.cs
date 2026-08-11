using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// Lets one exchange at a time onto a connection, and queues the rest.
///
/// A connection carries one conversation at a time. VXI-11 is ONC RPC: every call writes a
/// request record and then reads its reply record off the same stream, so two overlapping
/// calls interleave their records and a reader picks up something that is not its reply —
/// "Malformed RPC reply (not a REPLY)", followed by a timeout for the reply that another
/// reader already swallowed, followed by the socket being torn down. A raw socket fails the
/// same way but quietly: there are no record boundaries, so overlapping queries simply pair
/// the wrong reply with the wrong query and the readout sits one behind for ever.
///
/// Every caller shares this — the console, the quick buttons, both script runners, the screen
/// and waveform captures, command discovery — which is why the gate belongs here rather than
/// in any one of them.
///
/// <para>
/// After a failed exchange the stream is not merely idle, it is out of step: the reply that
/// never arrived may still turn up and be read as the answer to the next question. So a
/// failure marks the connection, and the next caller reconnects before it speaks. Trading a
/// loud failure for a silently wrong reading is the one outcome worth going out of the way to
/// avoid on a bench.
/// </para>
/// </summary>
public sealed class SerializedInstrumentClient : IInstrumentClient
{
    private readonly IInstrumentClient _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// The commands in flight or waiting, oldest first — the queue as the user would draw it.
    /// Held rather than merely counted, so the UI can show what is waiting and not only how
    /// much: "three queued" says nothing about whether the one just pressed is among them.
    /// </summary>
    private readonly List<string> _queue = new();

    private volatile bool _outOfStep;

    public SerializedInstrumentClient(IInstrumentClient inner) => _inner = inner;

    /// <summary>Exchanges in flight or waiting their turn. 0 when the connection is idle.</summary>
    public int Pending { get { lock (_queue) return _queue.Count; } }

    /// <summary>Snapshot of the queue, oldest first — copied, so a caller can walk it while
    /// the connection carries on working.</summary>
    public IReadOnlyList<string> Queued
    {
        get { lock (_queue) return _queue.ToArray(); }
    }

    /// <summary>Raised whenever the queue changes. Fired off the UI thread.</summary>
    public event EventHandler? PendingChanged;

    public string Host => _inner.Host;
    public string Description => _inner.Description;
    public bool IsConnected => _inner.IsConnected;

    public int TimeoutMs
    {
        get => _inner.TimeoutMs;
        set => _inner.TimeoutMs = value;
    }

    public Task ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);

    public Task SendAsync(string command, CancellationToken ct = default)
        => RunAsync(command, c => _inner.SendAsync(command, c), ct);

    public Task<string> QueryAsync(string command, CancellationToken ct = default)
        => RunAsync(command, c => _inner.QueryAsync(command, c), ct);

    public Task<byte[]> QueryBinaryAsync(string command, CancellationToken ct = default)
        => RunAsync(command, c => _inner.QueryBinaryAsync(command, c), ct);

    /// <summary>
    /// Not queued. It runs while the window is closing, after the last command has been
    /// cancelled rather than completed, so waiting for a turn behind an abandoned exchange
    /// would just delay the close by a timeout.
    /// </summary>
    public Task ReturnToLocalAsync(CancellationToken ct = default) => _inner.ReturnToLocalAsync(ct);

    public void Close() => _inner.Close();

    public void Dispose()
    {
        _inner.Dispose();
        _gate.Dispose();
    }

    // ------------------------------------------------------------------------ the gate

    private async Task RunAsync(string command, Func<CancellationToken, Task> op, CancellationToken ct)
        => await RunAsync<bool>(command,
                                async c => { await op(c).ConfigureAwait(false); return true; }, ct)
            .ConfigureAwait(false);

    private async Task<T> RunAsync<T>(
        string command, Func<CancellationToken, Task<T>> op, CancellationToken ct)
    {
        Enter(command);
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            Leave(command);      // cancelled while queued: it never took its turn
            throw;
        }

        try
        {
            if (_outOfStep) await ResyncAsync(ct).ConfigureAwait(false);

            bool completed = false;
            try
            {
                T result = await op(ct).ConfigureAwait(false);
                completed = true;
                return result;
            }
            finally
            {
                // Any exception means the exchange did not finish — a half-written request, or
                // a reply still to come. Either way the next caller must not trust the stream.
                if (!completed) _outOfStep = true;
            }
        }
        finally
        {
            _gate.Release();
            Leave(command);
        }
    }

    /// <summary>
    /// Put the connection back in a known state by making a new one. Draining would do for a
    /// raw socket, but there is no way to tell how much of a VXI-11 record is still to come,
    /// and a wrong guess leaves exactly the silent off-by-one this class exists to prevent.
    /// </summary>
    private async Task ResyncAsync(CancellationToken ct)
    {
        _outOfStep = false;      // cleared first: a failed reconnect must not loop for ever
        try { _inner.Close(); } catch { /* already gone */ }
        await _inner.ConnectAsync(ct).ConfigureAwait(false);
    }

    private void Enter(string command)
    {
        lock (_queue) _queue.Add(command);
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Take this command back off the queue. By first match rather than by position: the same
    /// command sent twice is two identical strings, and removing the oldest is what makes the
    /// list drain in the order it filled.
    /// </summary>
    private void Leave(string command)
    {
        lock (_queue) _queue.Remove(command);
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }
}
