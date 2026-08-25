using System;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;
using Xunit.Abstractions;

namespace LabEquipmentController.Tests.Bench;

/// <summary>
/// The serial transport against a real instrument on a real port.
///
/// Everything above the wire is proved elsewhere without hardware — the message framing in
/// <c>ScpiFramingTests</c> against a stream, the address spellings in
/// <c>SerialAddressTests</c> — and this is the part no stand-in can prove: that a port opens
/// at the settings it was given, that an instrument on the other end of it answers, and that
/// the answer is the same shape as one that came over Ethernet.
///
/// Skipped unless <c>LEC_BENCH=1</c> and <c>LEC_SERIAL</c> names the port, since there is no
/// serial instrument on this bench and no way to sweep for one:
///
///     set LEC_BENCH=1
///     set LEC_SERIAL=serial://COM3?baud=115200
///     dotnet test --filter "FullyQualifiedName~Bench"
///
/// Read-only, like the rest of the bench suite. Nothing here changes a setting, arms
/// anything or turns an output on.
/// </summary>
[Collection(BenchCollection.Name)]
public class SerialBenchTests
{
    private readonly ITestOutputHelper _out;
    public SerialBenchTests(ITestOutputHelper output) => _out = output;

    private static IInstrumentClient Open(int timeoutMs = 5000)
    {
        Assert.True(InstrumentAddress.TryParse(Bench.SerialAddress, out var address, out string error), error);
        Assert.Equal(InstrumentTransport.Serial, address.Transport);
        return address.CreateClient(timeoutMs);
    }

    /// <summary>
    /// The whole thing in one line: the port opens at the settings given, the instrument
    /// hears a command terminated the way it expects, and a reply comes back whole.
    ///
    /// If this fails on a port that is definitely right, the settings are the place to look
    /// before the code is — a wrong baud rate answers with rubbish rather than silence, and
    /// a wrong terminator looks exactly like a dead port.
    /// </summary>
    [SerialBenchFact]
    public async Task An_instrument_on_a_serial_port_identifies_itself()
    {
        using IInstrumentClient client = Open();
        await client.ConnectAsync();

        Assert.True(client.IsConnected);

        string idn = await client.QueryAsync("*IDN?");
        _out.WriteLine($"{Bench.SerialAddress}: {idn.Trim()}");
        _out.WriteLine($"transport: {client.Description}");

        Assert.NotEmpty(idn.Trim());
        Assert.Contains(",", idn);   // manufacturer,model,serial,firmware

        await Bench.ReleaseAsync(client);
    }

    /// <summary>
    /// The same identity, over a transport the layers above cannot tell apart. This is the
    /// claim the whole design rests on — that a session, a script runner or a capture does
    /// not know which wire it is on — and the cheapest way to catch it becoming untrue.
    /// </summary>
    [SerialBenchFact]
    public async Task A_serial_instrument_classifies_the_same_way_a_LAN_one_does()
    {
        using IInstrumentClient client = Open();
        await client.ConnectAsync();

        string idn = await client.QueryAsync("*IDN?");
        var family = InstrumentProfile.FamilyForIdentity(idn);
        var profile = InstrumentProfile.ForIdentity(idn);

        _out.WriteLine($"{idn.Trim()} → {family} ({profile.Name})");
        _out.WriteLine($"catalog: {CommandReference.ForFamily(family).Commands.Count} commands");

        // Not asserting which family — that depends on whose instrument is plugged in. What
        // is being pinned is that an identity arrived intact enough to classify at all;
        // Generic is what a reply mangled by wrong line settings would land on.
        Assert.NotEqual(InstrumentFamily.Generic, family);

        await Bench.ReleaseAsync(client);
    }

    /// <summary>
    /// Ask twice and get two answers, in order. Serial has no framing of its own — no
    /// packets, no connection — so a reply that overran its terminator would be read as the
    /// front of the next one, and the second query would return the tail of the first.
    /// </summary>
    [SerialBenchFact]
    public async Task Two_queries_in_a_row_stay_aligned()
    {
        using IInstrumentClient client = Open();
        await client.ConnectAsync();

        string first = await client.QueryAsync("*IDN?");
        string second = await client.QueryAsync("*IDN?");

        Assert.Equal(first.Trim(), second.Trim());

        await Bench.ReleaseAsync(client);
    }

    /// <summary>
    /// A query nothing will answer has to fail, and say which port went quiet. Returning
    /// what arrived instead — nothing, or half a number — is the failure this transport
    /// shares with every other one here, and the one worth proving on real hardware.
    /// </summary>
    [SerialBenchFact]
    public async Task A_query_that_is_never_answered_times_out_rather_than_returning_nothing()
    {
        using IInstrumentClient client = Open(timeoutMs: 1500);
        await client.ConnectAsync();

        // Not a command any instrument implements, so nothing replies. (It goes in the
        // error queue, which is where an unrecognised command is supposed to go.)
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.QueryAsync(":LEC:NOSUCHTHING?"));
        _out.WriteLine($"{ex.GetType().Name}: {ex.Message}");
        Assert.True(ex is TimeoutException or System.IO.IOException);

        await Bench.ReleaseAsync(client);
    }
}
