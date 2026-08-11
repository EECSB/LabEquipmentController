using System;
using System.Net.Sockets;
using System.Text;
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
/// Line-based reads are fine for identity/setting/measurement queries. Binary
/// block responses (e.g. ":WAV:DATA?") are not decoded.
/// </summary>
public sealed class ScpiClient : IInstrumentClient
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;

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

        // Swallow anything already sitting in the buffer from a prior session before the
        // first query. (The scan no longer leaves queries unread, so this is just a
        // light safety net; a badly desynced instrument may still need a power-cycle.)
        DrainInput();
    }

    /// <summary>Write a command (no response expected).</summary>
    public async Task SendAsync(string command, CancellationToken ct = default)
    {
        EnsureConnected();
        DrainInput(); // discard any late reply from a previous slow query
        byte[] payload = Encoding.ASCII.GetBytes(NormalizeCommand(command));
        await _stream!.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Non-blocking read-and-discard of anything still buffered. Prevents a late
    /// response from one command being mis-read as the answer to the next.
    /// </summary>
    private void DrainInput()
    {
        try
        {
            var tmp = new byte[512];
            while (_stream is { DataAvailable: true })
            {
                if (_stream.Read(tmp, 0, tmp.Length) <= 0) break;
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>Write a query and read one line of response.</summary>
    public async Task<string> QueryAsync(string command, CancellationToken ct = default)
    {
        await SendAsync(command, ct).ConfigureAwait(false);
        return await ReadLineAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write a query and read an IEEE 488.2 binary block. On a raw socket there is no
    /// EOI signal, so we read the definite-length header (#&lt;n&gt;&lt;length&gt;) and then
    /// read EXACTLY that many bytes — never "until timeout", which would truncate binary
    /// data that happens to contain no newline or arrives in several TCP segments.
    /// </summary>
    public async Task<byte[]> QueryBinaryAsync(string command, CancellationToken ct = default)
    {
        await SendAsync(command, ct).ConfigureAwait(false);
        EnsureConnected();

        return await Deadline.RunAsync(t => ReadBlockAsync(t), TimeoutMs,
                                       $"{Host}:{Port} did not finish answering {command.Trim()}", ct)
                             .ConfigureAwait(false);
    }

    /// <summary>The read half of <see cref="QueryBinaryAsync"/>, under the caller's deadline.</summary>
    private async Task<byte[]> ReadBlockAsync(CancellationToken t)
    {
        byte first = (await ReadExactAsync(1, t).ConfigureAwait(false))[0];
        if (first != (byte)'#')
            return await ReadToNewlineAsync(first, t).ConfigureAwait(false);   // ordinary text reply

        int digits = (await ReadExactAsync(1, t).ConfigureAwait(false))[0] - '0';
        if (digits < 0 || digits > 9)
            throw new IOException("Malformed IEEE 488.2 block: bad length-digit count.");

        if (digits == 0)
            return await ReadToNewlineAsync(null, t).ConfigureAwait(false);    // #0 indefinite

        byte[] lenBytes = await ReadExactAsync(digits, t).ConfigureAwait(false);
        if (!int.TryParse(Encoding.ASCII.GetString(lenBytes), out int length) || length < 0)
            throw new IOException("Malformed IEEE 488.2 block: bad length field.");

        byte[] payload = await ReadExactAsync(length, t).ConfigureAwait(false);

        // Swallow a trailing newline if it's already here; if not, the next query's
        // pre-send drain will clear it.
        if (_stream!.DataAvailable)
        {
            var nl = new byte[1];
            await _stream.ReadAsync(nl.AsMemory(0, 1), t).ConfigureAwait(false);
        }
        return payload;
    }

    /// <summary>Read exactly <paramref name="count"/> bytes or throw.</summary>
    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        EnsureConnected();
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await _stream!.ReadAsync(buf.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("Connection closed before the full block was read.");
            read += n;
        }
        return buf;
    }

    /// <summary>Read bytes until '\n', optionally seeded with an already-read byte.</summary>
    private async Task<byte[]> ReadToNewlineAsync(byte? seed, CancellationToken ct)
    {
        var data = new List<byte>();
        if (seed is byte s) data.Add(s);
        var one = new byte[1];
        while (true)
        {
            int n = await _stream!.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0 || one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') data.Add(one[0]);
        }
        return data.ToArray();
    }

    /// <summary>True when the command looks like a query (SCPI queries contain '?').</summary>
    public static bool IsQuery(string command) => command.Contains('?');

    /// <summary>
    /// No-op for a raw socket: these instruments tie remote state to the TCP connection,
    /// so simply closing it (in <see cref="Close"/>) returns the front panel to local.
    /// </summary>
    public Task ReturnToLocalAsync(CancellationToken ct = default) => Task.CompletedTask;

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        EnsureConnected();

        var sb = new StringBuilder();
        var buffer = new byte[1];

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeoutMs);

        while (true)
        {
            int n;
            try
            {
                n = await _stream!.ReadAsync(buffer.AsMemory(0, 1), timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // A reply that never reached its terminator is a failure, not a short answer.
                //
                // This used to break and return whatever had arrived. With nothing read that
                // is an empty string, which the console prints as "(no response)" — no
                // exception, so nothing marks the connection, and the reply that turns up
                // late is read as the answer to the next question. Read mid-number it is
                // worse: "+8.39" instead of "+8.39319298E-04" looks like a valid reading, is
                // recorded and plotted as one, and leaves the rest of the line in the buffer
                // to corrupt the read after it.
                //
                // Throwing puts it in the same shape as the VXI-11 path, so the connection is
                // marked out of step and remade before anything else is asked of it.
                throw new TimeoutException(
                    $"{Host} did not finish its reply within {TimeoutMs} ms"
                    + (sb.Length > 0 ? $" — {sb.Length} character(s) of it arrived." : "."));
            }

            // Closed before the terminator, so the reply is truncated or absent. Same story.
            if (n == 0)
                throw new IOException($"{Host} closed the connection before its reply ended.");
            char c = (char)buffer[0];
            if (c == '\n') break;   // SCPI message terminator
            sb.Append(c);
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static string NormalizeCommand(string command)
        => command.TrimEnd('\r', '\n') + "\n";

    private void EnsureConnected()
    {
        if (_stream == null || _tcp is not { Connected: true })
            throw new InvalidOperationException("Not connected to an instrument.");
    }

    public void Close()
    {
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _tcp?.Close(); } catch { /* ignore */ }
        _stream = null;
        _tcp = null;
    }

    public void Dispose() => Close();
}
