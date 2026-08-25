using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

public class ScpiClientTests
{
    [Fact]
    public async Task Connects_and_reads_identity()
    {
        using var inst = new FakeRawInstrument();
        using var c = new ScpiClient("127.0.0.1", inst.Port) { TimeoutMs = 3000 };

        await c.ConnectAsync();

        Assert.True(c.IsConnected);
        Assert.Equal("FAKE INSTRUMENTS,MODEL-1,SN123,1.0", await c.QueryAsync("*IDN?"));
    }

    [Fact]
    public async Task A_query_that_is_never_answered_throws_instead_of_returning_nothing()
    {
        using var inst = new FakeRawInstrument();
        using var c = new ScpiClient("127.0.0.1", inst.Port) { TimeoutMs = 5000 };
        await c.ConnectAsync();
        c.TimeoutMs = 300;

        // It used to come back as "", which the console prints as "(no response)" — no
        // exception, so nothing marks the connection, and the late reply is read as the
        // answer to whatever is asked next.
        await Assert.ThrowsAsync<TimeoutException>(() => c.QueryAsync(":SILent?"));
    }

    [Fact]
    public async Task A_reply_cut_off_mid_number_throws_rather_than_returning_the_start_of_it()
    {
        using var inst = new FakeRawInstrument();
        using var c = new ScpiClient("127.0.0.1", inst.Port) { TimeoutMs = 5000 };
        await c.ConnectAsync();
        c.TimeoutMs = 300;

        // The dangerous case: "+8.39" is a plausible voltage. Returned, it would be recorded
        // and plotted as a reading, with the rest of the line left to corrupt the next read.
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => c.QueryAsync(":HALF?"));
        Assert.DoesNotContain("+8.39", ex.Message.Replace("5 character(s)", ""));
    }

    [Fact]
    public async Task Query_after_a_fire_and_forget_send_stays_aligned()
    {
        using var inst = new FakeRawInstrument();
        using var c = new ScpiClient("127.0.0.1", inst.Port) { TimeoutMs = 3000 };
        await c.ConnectAsync();

        await c.SendAsync(":RUN");                        // produces no reply
        Assert.Equal("1.234", await c.QueryAsync(":VOLTage?"));
        Assert.StartsWith("FAKE", await c.QueryAsync("*IDN?"));
    }

    [Fact]
    public async Task QueryBinary_reads_an_exact_length_block_over_raw_socket()
    {
        using var inst = new FakeRawInstrument();
        using var c = new ScpiClient("127.0.0.1", inst.Port) { TimeoutMs = 3000 };
        await c.ConnectAsync();

        byte[] data = await c.QueryBinaryAsync(":WAVeform:DATA?");
        Assert.Equal(new byte[] { 0x01, 0x0A, 0x00, 0xFF, 0x7E }, data);

        // A normal text query still works right after the binary read (stayed aligned).
        Assert.StartsWith("FAKE", await c.QueryAsync("*IDN?"));
    }

    [Fact]
    public async Task A_binary_read_that_is_never_answered_reports_a_timeout()
    {
        using var inst = new FakeRawInstrument();
        // Connect on a generous clock and only then tighten it. The subject here is the
        // read that never gets an answer, not how fast a loopback socket connects — and a
        // 200 ms budget spanning the connect made this fail on a busy CI runner, where
        // opening the socket alone took longer than that. The test was reporting the
        // runner's load, not the client's behaviour.
        using var c = new ScpiClient("127.0.0.1", inst.Port) { TimeoutMs = 5000 };
        await c.ConnectAsync();
        c.TimeoutMs = 200;

        // ":SILENT" draws no reply at all, so the read runs out the clock.
        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(
            async () => await c.QueryBinaryAsync(":SILENT"));

        // The message has to be enough to act on: which instrument, and which command.
        Assert.Contains($"127.0.0.1:{inst.Port}", ex.Message);
        Assert.Contains(":SILENT", ex.Message);
        Assert.Contains("200 ms", ex.Message);
    }

    [Fact]
    public async Task A_binary_read_stopped_by_the_user_is_still_a_cancel()
    {
        using var inst = new FakeRawInstrument();
        using var c = new ScpiClient("127.0.0.1", inst.Port) { TimeoutMs = 30_000 };
        await c.ConnectAsync();

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        OperationCanceledException ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await c.QueryBinaryAsync(":SILENT", cts.Token));

        Assert.IsNotType<TimeoutException>(ex);
    }

    [Fact]
    public async Task A_block_that_stops_part_way_is_a_timeout_not_a_short_block()
    {
        using var inst = new FakeRawInstrument();
        using var c = new ScpiClient("127.0.0.1", inst.Port) { TimeoutMs = 5000 };
        await c.ConnectAsync();
        c.TimeoutMs = 200;

        // Two bytes of a promised five. Returning them would hand back a waveform or a
        // screenshot that is quietly missing its tail — worse than saying nothing came.
        await Assert.ThrowsAsync<TimeoutException>(
            async () => await c.QueryBinaryAsync(":TRUNCated:DATA?"));
    }

    [Fact]
    public void IsQuery_keys_off_the_question_mark()
    {
        Assert.True(ScpiClient.IsQuery("*IDN?"));
        Assert.True(ScpiClient.IsQuery(":MEASure:VPP? CHANnel1"));
        Assert.False(ScpiClient.IsQuery(":RUN"));
    }

    [Fact]
    public void Description_names_the_transport_and_port()
    {
        using var c = new ScpiClient("127.0.0.1", 5555);
        Assert.Contains("5555", c.Description);
        Assert.Contains("raw socket", c.Description);
    }

    [Fact]
    public async Task Querying_when_not_connected_throws()
    {
        using var c = new ScpiClient("127.0.0.1", 5555);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await c.QueryAsync("*IDN?"));
    }

    /// <summary>Minimal loopback SCPI instrument: newline-terminated, replies to queries only.</summary>
    private sealed class FakeRawInstrument : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public int Port { get; }

        public FakeRawInstrument()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(ct);
                NetworkStream stream = client.GetStream();
                var sb = new StringBuilder();
                var buf = new byte[1];
                while (!ct.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buf, ct);
                    if (n == 0) break;
                    char ch = (char)buf[0];
                    if (ch == '\n')
                    {
                        byte[]? reply = Respond(sb.ToString().TrimEnd('\r'));
                        sb.Clear();
                        if (reply != null) await stream.WriteAsync(reply, ct);
                    }
                    else sb.Append(ch);
                }
            }
            catch { /* listener stopped */ }
        }

        private static byte[]? Respond(string cmd)
        {
            if (cmd == "*IDN?") return Encoding.ASCII.GetBytes("FAKE INSTRUMENTS,MODEL-1,SN123,1.0\n");
            if (cmd == ":WAVeform:DATA?")
            {
                // Definite-length block whose 5-byte payload contains a newline and a null
                // byte, plus a trailing '\n' — to prove exact-length reading.
                byte[] payload = { 0x01, 0x0A, 0x00, 0xFF, 0x7E };
                byte[] header = Encoding.ASCII.GetBytes("#15");
                var block = new byte[header.Length + payload.Length + 1];
                header.CopyTo(block, 0);
                payload.CopyTo(block, header.Length);
                block[^1] = (byte)'\n';
                return block;
            }
            if (cmd == ":TRUNCated:DATA?")
            {
                // Announces five bytes and sends two, then goes quiet — a transfer that
                // died mid-flight, which must not come back as a short but plausible block.
                var block = new byte[] { (byte)'#', (byte)'1', (byte)'5', 0x01, 0x0A };
                return block;
            }
            // A query the instrument simply never answers — a mistyped mnemonic, or one it
            // does not implement. The read has nothing to go on and must not pass for a reply.
            if (cmd == ":SILent?") return null;

            // A reading that starts and stops: the digits arrive, the terminator never does.
            // "+8.39" is a plausible voltage, which is exactly what makes it dangerous.
            if (cmd == ":HALF?") return Encoding.ASCII.GetBytes("+8.39");

            if (cmd.Contains('?')) return Encoding.ASCII.GetBytes("1.234\n");
            return null;   // set commands produce no output
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }
}
