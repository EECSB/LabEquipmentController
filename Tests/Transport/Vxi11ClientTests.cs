using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The VXI-11 client against a loopback instrument. Everything here is about what happens
/// after a query the instrument is slow to answer — the case a bench found and no offline
/// test covered.
/// </summary>
public class Vxi11ClientTests
{
    // Connect generously, then tighten. The budget a test passes is for the query it is about to
    // make too slow, and setting it before the link is opened spends it on the VXI-11 handshake as
    // well: a loaded machine then fails at setup, for a reason the test is not about. The macOS
    // runner did exactly that on 2026-09-21 — "127.0.0.1 did not answer create_link within 150 ms"
    // — and passed on a re-run with nothing changed, which is the shape of a test that measures
    // the machine it runs on rather than the thing it names.
    private static async Task<Vxi11Client> ConnectedTo(FakeVxi11Instrument inst, int timeoutMs = 3000)
    {
        var client = new Vxi11Client("127.0.0.1") { TimeoutMs = 3000 };
        await client.OpenCoreAsync(inst.Port, CancellationToken.None);
        client.TimeoutMs = timeoutMs;
        return client;
    }

    [Fact]
    public async Task Links_and_reads_identity()
    {
        using var inst = new FakeVxi11Instrument();
        using Vxi11Client client = await ConnectedTo(inst);

        Assert.True(client.IsConnected);
        Assert.Equal(FakeVxi11Instrument.Identity, await client.QueryAsync("*IDN?"));
    }

    [Fact]
    public async Task A_query_answered_too_late_times_out()
    {
        using var inst = new FakeVxi11Instrument { SlowCommand = "MEASure:RESistance?", SlowMs = 700 };
        using Vxi11Client client = await ConnectedTo(inst, timeoutMs: 200);

        await Assert.ThrowsAsync<TimeoutException>(() => client.QueryAsync("MEASure:RESistance?"));
    }

    /// <summary>
    /// The bug this suite was written for. A Siglent SDM3065X asked for resistance straight
    /// after a current reading takes longer than the five seconds the app allows, and its
    /// answer turns up anyway a moment later. Read blindly, that answer became the reply to
    /// the *next* call: one timeout reported "device_write failed (I/O timeout)" for a write
    /// the instrument had not answered yet, and then every query after it — *IDN? included —
    /// came back as "Index was outside the bounds of the array".
    /// </summary>
    [Fact]
    public async Task A_late_reply_is_not_read_as_the_answer_to_the_next_query()
    {
        using var inst = new FakeVxi11Instrument { SlowCommand = "MEASure:RESistance?", SlowMs = 600 };
        using Vxi11Client client = await ConnectedTo(inst, timeoutMs: 150);

        await Assert.ThrowsAsync<TimeoutException>(() => client.QueryAsync("MEASure:RESistance?"));

        // Long enough for the abandoned answer to arrive and be waiting on the connection.
        await Task.Delay(700);

        client.TimeoutMs = 3000;
        Assert.Equal(FakeVxi11Instrument.Identity, await client.QueryAsync("*IDN?"));
        Assert.Equal("1.234", await client.QueryAsync("MEASure:VOLTage:DC?"));
    }

    /// <summary>The same, without the wait: the late answer lands mid-call instead.</summary>
    [Fact]
    public async Task The_link_recovers_even_if_the_late_reply_arrives_mid_call()
    {
        using var inst = new FakeVxi11Instrument { SlowCommand = ":SLOW?", SlowMs = 400 };
        using Vxi11Client client = await ConnectedTo(inst, timeoutMs: 150);

        await Assert.ThrowsAsync<TimeoutException>(() => client.QueryAsync(":SLOW?"));

        client.TimeoutMs = 3000;
        Assert.Equal(FakeVxi11Instrument.Identity, await client.QueryAsync("*IDN?"));
    }

    /// <summary>
    /// The one thing an id cannot rescue: a reply cut off part way through leaves bytes that
    /// belong to no message, and the next record marker read would be the middle of this one.
    /// The link says so instead of decoding rubbish.
    /// </summary>
    [Fact]
    public async Task A_reply_cut_off_part_way_through_ends_the_link_rather_than_guessing()
    {
        using var inst = new FakeVxi11Instrument { TruncateCommand = ":HALF?" };
        using Vxi11Client client = await ConnectedTo(inst, timeoutMs: 200);

        await Assert.ThrowsAsync<TimeoutException>(() => client.QueryAsync(":HALF?"));

        client.TimeoutMs = 3000;
        var ex = await Assert.ThrowsAsync<IOException>(() => client.QueryAsync("*IDN?"));
        Assert.Contains("out of step", ex.Message);
        Assert.Contains("Reconnect", ex.Message);
    }

    /// <summary>Reconnecting is what clears it, and it has to actually clear it.</summary>
    [Fact]
    public async Task Connecting_again_puts_an_out_of_step_link_back_in_service()
    {
        using var inst = new FakeVxi11Instrument { TruncateCommand = ":HALF?" };
        using Vxi11Client client = await ConnectedTo(inst, timeoutMs: 200);
        await Assert.ThrowsAsync<TimeoutException>(() => client.QueryAsync(":HALF?"));

        using var again = new FakeVxi11Instrument();
        client.Close();
        client.TimeoutMs = 3000;
        await client.OpenCoreAsync(again.Port, CancellationToken.None);

        Assert.Equal(FakeVxi11Instrument.Identity, await client.QueryAsync("*IDN?"));
    }
}
