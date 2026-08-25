using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// Minimal SCPI-over-raw-TCP client.
///
/// Most modern LAN / LXI lab instruments (oscilloscopes, power supplies,
/// function generators, DMMs from Rigol, Siglent, Keysight, Tektronix,
/// Rohde &amp; Schwarz, ...) expose a "raw socket" SCPI server, conventionally
/// on TCP port 5025. Commands and responses are plain ASCII text, newline
/// ('\n') terminated.
///
/// Convention used here:
///   * A command containing '?' is a QUERY -> we write it and read one line back.
///   * Any other command is fire-and-forget (write only).
///
/// The framing itself — write a command, read a line, read an IEEE 488.2 block — lives in
/// <see cref="ScpiFraming"/>, because <see cref="SerialInstrumentClient"/> needs the same
/// rules over a different stream. What is left here is the socket.
/// </summary>
public sealed class ScpiClient : IInstrumentClient
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ScpiFraming? _wire;

    public string Host { get; }
    public int Port { get; }

    public string Description => $"raw socket (port {Port})";

    /// <summary>Connect / read / write timeout in milliseconds.</summary>
    public int TimeoutMs { get; set; } = 5000;

    public bool IsConnected => _tcp?.Connected == true;

    public ScpiClient(string host, int port)
    {
        Host = host;
        Port = port;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Close();
        _tcp = new TcpClient { NoDelay = true };

        await Deadline.RunAsync(t => _tcp.ConnectAsync(Host, Port, t).AsTask(), TimeoutMs,
                                $"{Host}:{Port} did not answer", ct).ConfigureAwait(false);

        _stream = _tcp.GetStream();
        _stream.ReadTimeout = TimeoutMs;
        _stream.WriteTimeout = TimeoutMs;
        _wire = new ScpiFraming(_stream, () => _stream is { DataAvailable: true }, Host);

        // Swallow anything already sitting in the buffer from a prior session before the
        // first query. (The scan no longer leaves queries unread, so this is just a
        // light safety net; a badly desynced instrument may still need a power-cycle.)
        _wire.Drain();
    }

    /// <summary>Write a command (no response expected).</summary>
    public async Task SendAsync(string command, CancellationToken ct = default)
    {
        EnsureConnected();
        _wire!.Drain();   // discard any late reply from a previous slow query
        await _wire.WriteAsync(command, "\n", ct).ConfigureAwait(false);
    }

    /// <summary>Write a query and read one line of response.</summary>
    public async Task<string> QueryAsync(string command, CancellationToken ct = default)
    {
        await SendAsync(command, ct).ConfigureAwait(false);
        EnsureConnected();
        return await _wire!.ReadLineAsync(TimeoutMs, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write a query and read an IEEE 488.2 binary block response (waveforms, screenshots),
    /// returning just the data payload.
    /// </summary>
    public async Task<byte[]> QueryBinaryAsync(string command, CancellationToken ct = default)
    {
        await SendAsync(command, ct).ConfigureAwait(false);
        EnsureConnected();

        return await Deadline.RunAsync(t => _wire!.ReadBlockAsync(t), TimeoutMs,
                                       $"{Host}:{Port} did not finish answering {command.Trim()}", ct)
                             .ConfigureAwait(false);
    }

    /// <summary>True when the command looks like a query (SCPI queries contain '?').</summary>
    public static bool IsQuery(string command) => command.Contains('?');

    /// <summary>
    /// No-op for a raw socket: these instruments tie remote state to the TCP connection,
    /// so simply closing it (in <see cref="Close"/>) returns the front panel to local.
    /// </summary>
    public Task ReturnToLocalAsync(CancellationToken ct = default) => Task.CompletedTask;

    private void EnsureConnected()
    {
        if (_stream == null || _wire == null || _tcp is not { Connected: true })
            throw new InvalidOperationException("Not connected to an instrument.");
    }

    public void Close()
    {
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _tcp?.Close(); } catch { /* ignore */ }
        _wire = null;
        _stream = null;
        _tcp = null;
    }

    public void Dispose() => Close();
}
