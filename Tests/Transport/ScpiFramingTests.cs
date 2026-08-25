using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// How a SCPI message goes onto a byte stream and comes off it again.
///
/// <see cref="ScpiClient"/> and <see cref="SerialInstrumentClient"/> share this, which is
/// what makes the serial client a short file — and what makes it worth testing here rather
/// than twice through two transports. It also gets at the serial path in the only way a
/// build server can: a COM port needs a COM port, a stream does not.
///
/// The cases that matter are the unhappy ones. A reply that stops halfway is not a short
/// answer, a block is read by its declared length rather than until a pause, and a
/// carriage return is not part of a reading.
/// </summary>
public class ScpiFramingTests
{
    [Fact]
    public async Task A_line_is_read_up_to_the_newline_and_no_further()
    {
        using var s = new ScriptedStream();
        var wire = new ScpiFraming(s, () => s.Available > 0, "COM3");

        s.Deliver("1.234\n5.678\n");

        Assert.Equal("1.234", await wire.ReadLineAsync(1000, default));
        Assert.Equal("5.678", await wire.ReadLineAsync(1000, default));
    }

    /// <summary>
    /// An instrument that ends its lines with CRLF is not reporting a different number.
    /// </summary>
    [Fact]
    public async Task A_carriage_return_is_not_part_of_the_reading()
    {
        using var s = new ScriptedStream();
        var wire = new ScpiFraming(s, () => s.Available > 0, "COM3");

        s.Deliver("+8.39319298E-04\r\n");

        Assert.Equal("+8.39319298E-04", await wire.ReadLineAsync(1000, default));
    }

    /// <summary>
    /// The dangerous case, and the reason this throws rather than returning what arrived:
    /// "+8.39" is a plausible voltage. Handed back, it would be recorded and plotted as a
    /// reading, with the rest of the line left in the buffer to corrupt the next one.
    /// </summary>
    [Fact]
    public async Task A_reply_cut_off_mid_number_is_a_timeout_not_a_short_answer()
    {
        using var s = new ScriptedStream();
        var wire = new ScpiFraming(s, () => s.Available > 0, "COM3");

        s.Deliver("+8.39");   // and then nothing

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => wire.ReadLineAsync(150, default));
        Assert.Contains("COM3", ex.Message);
        Assert.Contains("5 character(s)", ex.Message);
    }

    /// <summary>
    /// A serial port enforces its own read timeout in the driver and gets there first, with
    /// a message that says only that an operation timed out. Same failure, so the caller
    /// should hear the same sentence about it — including which port went quiet.
    /// </summary>
    [Fact]
    public async Task A_driver_level_timeout_is_reported_the_same_way_as_ours()
    {
        using var s = new ScriptedStream { ThrowTimeoutOnRead = true };
        var wire = new ScpiFraming(s, () => s.Available > 0, "COM7");

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => wire.ReadLineAsync(5000, default));
        Assert.Contains("COM7", ex.Message);
        Assert.Contains("5000 ms", ex.Message);
    }

    [Fact]
    public async Task A_stream_that_ends_before_the_terminator_is_an_IO_failure()
    {
        using var s = new ScriptedStream();
        var wire = new ScpiFraming(s, () => s.Available > 0, "COM3");

        s.Deliver("1.23");
        s.EndOfStream();

        await Assert.ThrowsAsync<IOException>(() => wire.ReadLineAsync(1000, default));
    }

    [Fact]
    public async Task Cancelling_a_read_is_a_cancel_and_not_a_timeout()
    {
        using var s = new ScriptedStream();
        var wire = new ScpiFraming(s, () => s.Available > 0, "COM3");
        using var cts = new CancellationTokenSource();

        Task<string> reading = wire.ReadLineAsync(30_000, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
    }

    /// <summary>
    /// The payload contains a newline and a '#'. Read "until a pause" or "up to a newline",
    /// both would truncate it — which is what the declared length is for.
    /// </summary>
    [Fact]
    public async Task A_binary_block_is_read_by_its_declared_length()
    {
        using var s = new ScriptedStream();
        var wire = new ScpiFraming(s, () => s.Available > 0, "192.168.1.20");

        var payload = new byte[] { 0x01, 0x0A, 0x23, 0xFF, 0x00, 0x0A, 0x7F, 0x10 };
        s.Deliver(Encoding.ASCII.GetBytes("#18"));   // one length digit, eight bytes
        s.Deliver(payload);
        s.Deliver("\n");

        Assert.Equal(payload, await wire.ReadBlockAsync(default));
    }

    [Fact]
    public async Task A_block_that_stops_part_way_through_is_a_failure_not_a_short_block()
    {
        using var s = new ScriptedStream();
        var wire = new ScpiFraming(s, () => s.Available > 0, "COM3");

        s.Deliver(Encoding.ASCII.GetBytes("#18"));
        s.Deliver(new byte[] { 1, 2, 3 });   // three of the eight promised
        s.EndOfStream();

        await Assert.ThrowsAsync<IOException>(() => wire.ReadBlockAsync(default));
    }

    /// <summary>#0 is the indefinite-length form: everything up to the newline.</summary>
    [Fact]
    public async Task An_indefinite_length_block_runs_to_the_newline()
    {
        using var s = new ScriptedStream();
        var wire = new ScpiFraming(s, () => s.Available > 0, "COM3");

        s.Deliver("#0abc\n");

        Assert.Equal("abc"u8.ToArray(), await wire.ReadBlockAsync(default));
    }

    /// <summary>Not every reply to a binary query is binary — an error is a line of text.</summary>
    [Fact]
    public async Task A_text_reply_where_a_block_was_expected_is_returned_as_text()
    {
        using var s = new ScriptedStream();
        var wire = new ScpiFraming(s, () => s.Available > 0, "COM3");

        s.Deliver("-113,\"Undefined header\"\n");

        Assert.Equal("-113,\"Undefined header\"",
                     Encoding.ASCII.GetString(await wire.ReadBlockAsync(default)));
    }

    [Fact]
    public async Task A_block_header_that_is_not_a_number_is_refused()
    {
        using var s = new ScriptedStream();
        var wire = new ScpiFraming(s, () => s.Available > 0, "COM3");

        s.Deliver("#X1234\n");

        await Assert.ThrowsAsync<IOException>(() => wire.ReadBlockAsync(default));
    }

    /// <summary>
    /// The one thing that genuinely differs between the two transports: a raw socket always
    /// ends a command with a line feed, while a good many RS-232 instruments want CRLF and
    /// treat a bare LF as no command at all.
    /// </summary>
    [Theory]
    [InlineData("\n", "*IDN?\n")]
    [InlineData("\r\n", "*IDN?\r\n")]
    [InlineData("\r", "*IDN?\r")]
    public async Task A_command_is_written_with_the_terminator_it_was_given(string terminator, string expected)
    {
        using var s = new ScriptedStream();
        var wire = new ScpiFraming(s, () => s.Available > 0, "COM3");

        await wire.WriteAsync("*IDN?", terminator, default);

        Assert.Equal(expected, Encoding.ASCII.GetString(s.Written));
    }

    /// <summary>A terminator the caller already typed is not doubled.</summary>
    [Fact]
    public async Task A_command_that_already_ends_in_a_newline_is_not_terminated_twice()
    {
        using var s = new ScriptedStream();
        var wire = new ScpiFraming(s, () => s.Available > 0, "COM3");

        await wire.WriteAsync("*RST\r\n", "\n", default);

        Assert.Equal("*RST\n", Encoding.ASCII.GetString(s.Written));
    }

    /// <summary>
    /// What is left over from a previous slow query is not the answer to the next one.
    /// </summary>
    [Fact]
    public async Task Draining_discards_what_was_already_waiting()
    {
        using var s = new ScriptedStream();
        var wire = new ScpiFraming(s, () => s.Available > 0, "COM3");

        s.Deliver("late answer\n");
        wire.Drain();
        s.Deliver("1.234\n");

        Assert.Equal("1.234", await wire.ReadLineAsync(1000, default));
    }

    // ------------------------------------------------------------------ the stand-in

    /// <summary>
    /// A stream that delivers exactly what a test says, when it says. Reads with nothing
    /// waiting park until something arrives or the token is cancelled — which is how both
    /// a socket and a serial port behave, and what makes the timeout cases real rather
    /// than a call that returns zero straight away.
    /// </summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly Queue<byte> _incoming = new();
        private readonly MemoryStream _outgoing = new();
        private readonly Lock _gate = new();
        private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _ended;

        /// <summary>Make the driver's own read timeout fire, as a serial port's does.</summary>
        public bool ThrowTimeoutOnRead { get; init; }

        public byte[] Written => _outgoing.ToArray();

        public int Available { get { lock (_gate) return _incoming.Count; } }

        public void Deliver(string text) => Deliver(Encoding.ASCII.GetBytes(text));

        public void Deliver(byte[] bytes)
        {
            lock (_gate)
            {
                foreach (byte b in bytes) _incoming.Enqueue(b);
                Wake();
            }
        }

        /// <summary>The far end hung up: reads return zero from here on.</summary>
        public void EndOfStream()
        {
            lock (_gate) { _ended = true; Wake(); }
        }

        private void Wake()
        {
            var waiting = _arrived;
            _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting.TrySetResult();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (ThrowTimeoutOnRead) throw new TimeoutException("The operation has timed out.");

            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (_incoming.Count > 0)
                    {
                        int n = Math.Min(buffer.Length, _incoming.Count);
                        for (int i = 0; i < n; i++) buffer.Span[i] = _incoming.Dequeue();
                        return n;
                    }
                    if (_ended) return 0;
                    wait = _arrived.Task;
                }

                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                int n = Math.Min(count, _incoming.Count);
                for (int i = 0; i < n; i++) buffer[offset + i] = _incoming.Dequeue();
                return n;
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => _outgoing.WriteAsync(buffer, ct);

        public override void Write(byte[] buffer, int offset, int count) => _outgoing.Write(buffer, offset, count);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _outgoing.Dispose();
            base.Dispose(disposing);
        }
    }
}
