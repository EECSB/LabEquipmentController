using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// How a SCPI message is put on a byte stream and taken off it again: write a command,
/// read a line back, or read an IEEE 488.2 definite-length block.
///
/// This is the part <see cref="ScpiClient"/> and <see cref="SerialInstrumentClient"/> share,
/// and it is most of both of them — SCPI over RS-232 is line-based in exactly the way SCPI
/// over a raw socket is, which is why the serial client is a short file. What differs is
/// only how the stream is opened, how one asks it whether bytes are waiting, and what goes
/// on the end of a command; those three arrive here as constructor arguments.
///
/// The subtleties are worth keeping in one copy. A reply that never reached its terminator
/// throws rather than returning the part that arrived (a truncated "+8.39" is a plausible
/// voltage and would be recorded as one); a binary block is read by its declared length
/// rather than until a pause, because binary data contains newlines and arrives in pieces.
/// </summary>
internal sealed class ScpiFraming
{
    private readonly Stream _stream;
    private readonly Func<bool> _hasBufferedInput;
    private readonly string _label;

    /// <param name="stream">The open stream — a <c>NetworkStream</c>, or a serial port's.</param>
    /// <param name="hasBufferedInput">
    /// Whether bytes are already waiting, without blocking to find out. Sockets answer this
    /// with <c>DataAvailable</c> and serial ports with <c>BytesToRead</c>; there is no member
    /// on <see cref="Stream"/> that asks it, so the caller supplies the question.
    /// </param>
    /// <param name="label">What to call the far end in an error message.</param>
    public ScpiFraming(Stream stream, Func<bool> hasBufferedInput, string label)
    {
        _stream = stream;
        _hasBufferedInput = hasBufferedInput;
        _label = label;
    }

    /// <summary>
    /// Non-blocking read-and-discard of anything still buffered. Prevents a late response
    /// from one command being mis-read as the answer to the next.
    /// </summary>
    public void Drain()
    {
        try
        {
            var tmp = new byte[512];
            while (_hasBufferedInput())
            {
                if (_stream.Read(tmp, 0, tmp.Length) <= 0) break;
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>Write one command, terminated as this transport terminates commands.</summary>
    public async Task WriteAsync(string command, string terminator, CancellationToken ct)
    {
        byte[] payload = Encoding.ASCII.GetBytes(command.TrimEnd('\r', '\n') + terminator);
        await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read one reply, up to the line feed. A reply that never reaches its terminator
    /// within <paramref name="timeoutMs"/> is a failure, not a short answer: returning what
    /// had arrived would hand back "+8.39" for "+8.39319298E-04" — a number that looks
    /// valid, is recorded and plotted as one, and leaves the rest of the line in the buffer
    /// to corrupt the read after it. Throwing puts it in the same shape as the VXI-11 path,
    /// so the connection is marked out of step and remade before anything else is asked.
    /// </summary>
    public async Task<string> ReadLineAsync(int timeoutMs, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new byte[1];

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        while (true)
        {
            int n;
            try
            {
                n = await _stream.ReadAsync(buffer.AsMemory(0, 1), timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(TimedOut(timeoutMs, sb.Length));
            }
            catch (TimeoutException)
            {
                // A serial port enforces its own read timeout in the driver and gets there
                // first, with a message that says only "the operation has timed out". Same
                // failure, so say the same thing about it.
                throw new TimeoutException(TimedOut(timeoutMs, sb.Length));
            }

            // Closed before the terminator, so the reply is truncated or absent. Same story.
            if (n == 0)
                throw new IOException($"{_label} closed the connection before its reply ended.");

            char c = (char)buffer[0];
            if (c == '\n') break;   // SCPI message terminator
            sb.Append(c);
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    private string TimedOut(int timeoutMs, int arrived)
        => $"{_label} did not finish its reply within {timeoutMs} ms"
           + (arrived > 0 ? $" — {arrived} character(s) of it arrived." : ".");

    /// <summary>
    /// Read an IEEE 488.2 binary block. There is no EOI signal on either of these
    /// transports, so the definite-length header (#&lt;n&gt;&lt;length&gt;) is read and then
    /// exactly that many bytes — never "until timeout", which would truncate binary data
    /// that happens to contain no newline or arrives in several pieces.
    /// </summary>
    public async Task<byte[]> ReadBlockAsync(CancellationToken ct)
    {
        byte first = (await ReadExactAsync(1, ct).ConfigureAwait(false))[0];
        if (first != (byte)'#')
            return await ReadToNewlineAsync(first, ct).ConfigureAwait(false);   // ordinary text reply

        int digits = (await ReadExactAsync(1, ct).ConfigureAwait(false))[0] - '0';
        if (digits < 0 || digits > 9)
            throw new IOException("Malformed IEEE 488.2 block: bad length-digit count.");

        if (digits == 0)
            return await ReadToNewlineAsync(null, ct).ConfigureAwait(false);    // #0 indefinite

        byte[] lenBytes = await ReadExactAsync(digits, ct).ConfigureAwait(false);
        if (!int.TryParse(Encoding.ASCII.GetString(lenBytes), out int length) || length < 0)
            throw new IOException("Malformed IEEE 488.2 block: bad length field.");

        byte[] payload = await ReadExactAsync(length, ct).ConfigureAwait(false);

        // Swallow a trailing newline if it's already here; if not, the next command's
        // pre-send drain will clear it.
        if (_hasBufferedInput())
        {
            var nl = new byte[1];
            await _stream.ReadAsync(nl.AsMemory(0, 1), ct).ConfigureAwait(false);
        }
        return payload;
    }

    /// <summary>Read exactly <paramref name="count"/> bytes or throw.</summary>
    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await _stream.ReadAsync(buf.AsMemory(read, count - read), ct).ConfigureAwait(false);
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
            int n = await _stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0 || one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') data.Add(one[0]);
        }
        return data.ToArray();
    }
}
