using System;
using System.Linq;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;
using Xunit.Abstractions;

namespace LabEquipmentController.Tests.Bench;

/// <summary>
/// The things a catalog sweep cannot reach: that an instrument is recognised as what it is,
/// that a capture comes back decodable, and that the readout and scripting paths work
/// against real replies rather than a fake.
///
/// Read-only throughout. Nothing here changes an instrument's settings, arms anything, or
/// turns an output on — the generator tests read its configuration back without touching it.
/// </summary>
[Collection(BenchCollection.Name)]
public class FeatureBenchTests
{
    private readonly ITestOutputHelper _out;
    public FeatureBenchTests(ITestOutputHelper output) => _out = output;

    // ------------------------------------------------------------------------- identity

    public static TheoryData<string, InstrumentFamily> Expected() => new()
    {
        { "scope", InstrumentFamily.Oscilloscope },
        { "generator", InstrumentFamily.SiglentGenerator },
        { "multimeter", InstrumentFamily.Multimeter },
    };

    /// <summary>
    /// The whole app hangs off this: an *IDN? that classifies wrongly gives every button,
    /// every readout and every catalog lookup the wrong answer at once.
    /// </summary>
    [BenchTheory]
    [MemberData(nameof(Expected))]
    public async Task An_instrument_is_recognised_as_what_it_is(string which, InstrumentFamily family)
    {
        string host = Host(which);
        using IInstrumentClient client = await Bench.ConnectAsync(host);

        string idn = await client.QueryAsync("*IDN?");
        _out.WriteLine($"{which} at {host}: {idn.Trim()}");

        Assert.Equal(family, InstrumentProfile.FamilyForIdentity(idn));

        CommandReference? reference = CommandReference.ForIdentity(idn);
        Assert.NotNull(reference);
        _out.WriteLine($"  -> {reference!.Instrument}, {reference.Commands.Count} commands");

        await Bench.ReleaseAsync(client);
    }

    /// <summary>
    /// Every quick-command button that only reads. The buttons that change something are
    /// deliberately not pressed — this suite must leave the bench as it found it — so what
    /// is proven is that the query half of each profile is accepted.
    /// </summary>
    [BenchTheory]
    [MemberData(nameof(Expected))]
    public async Task The_read_only_quick_commands_all_answer(string which, InstrumentFamily family)
    {
        using IInstrumentClient client = await Bench.ConnectAsync(Host(which));
        InstrumentProfile profile = InstrumentProfile.ForIdentity(await client.QueryAsync("*IDN?"));

        var queries = profile.Commands
            .Where(c => c.Command.TrimEnd().EndsWith("?"))
            .ToList();
        Assert.NotEmpty(queries);

        var failed = new System.Collections.Generic.List<string>();
        foreach (QuickCommand q in queries)
        {
            try
            {
                string reply = await client.QueryAsync(q.Command);
                _out.WriteLine($"  {q.Label,-12} {q.Command,-34} -> {reply.Trim()}");
                if (string.IsNullOrWhiteSpace(reply)) failed.Add($"{q.Command} (empty)");
            }
            catch (Exception ex) { failed.Add($"{q.Command} ({ex.Message})"); }
        }

        await Bench.ReleaseAsync(client);
        Assert.True(failed.Count == 0, $"{family}: {string.Join("; ", failed)}");
    }

    // -------------------------------------------------------------------------- capture

    /// <summary>
    /// The Rigol path is the only waveform dialect that has ever met hardware, and this
    /// change moved it out of the console into <see cref="WaveformReader"/>. What matters is
    /// not that it returns points but that they decode to sane numbers: a wrong formula
    /// still returns the right *count*.
    /// </summary>
    [BenchFact]
    public async Task The_scope_returns_a_decodable_waveform()
    {
        using IInstrumentClient client = await Bench.ConnectAsync(Bench.Scope, timeoutMs: 15000);

        WaveformCapture wave = await WaveformReader.ReadAsync(client, WaveformDialect.Rigol);
        _out.WriteLine($"{wave.Samples.Count} points, {wave.XIncrement:g4} s apart");

        Assert.NotEmpty(wave.Samples);
        Assert.True(wave.XIncrement > 0, "sample spacing must be positive");

        // A DS2202 is a ±100 V instrument on its widest range; anything outside that is a
        // decode fault, not a signal.
        Assert.All(wave.Samples, s =>
        {
            Assert.True(Math.Abs(s.Voltage) < 500, $"{s.Voltage} V is not a real reading");
            Assert.False(double.IsNaN(s.Voltage) || double.IsInfinity(s.Voltage));
        });

        // Time must advance monotonically, which catches a preamble read in the wrong order.
        for (int i = 1; i < Math.Min(wave.Samples.Count, 200); i++)
            Assert.True(wave.Samples[i].Time > wave.Samples[i - 1].Time,
                $"time went backwards at sample {i}");

        _out.WriteLine($"first {wave.Samples[0].Time:g4} s @ {wave.Samples[0].Voltage:g4} V, " +
                       $"last {wave.Samples[^1].Time:g4} s @ {wave.Samples[^1].Voltage:g4} V");
        Assert.False(string.IsNullOrEmpty(wave.ToCsv()));

        await Bench.ReleaseAsync(client);
    }

    /// <summary>
    /// The screen dump comes back as an IEEE 488.2 block that has to be a real image — the
    /// 64 KB truncation this transport once had produced a block that was the right shape
    /// and half the picture.
    /// </summary>
    [BenchFact]
    public async Task The_scope_returns_a_decodable_screenshot()
    {
        using IInstrumentClient client = await Bench.ConnectAsync(Bench.Scope, timeoutMs: 20000);

        InstrumentProfile profile = InstrumentProfile.ForIdentity(await client.QueryAsync("*IDN?"));
        Assert.NotNull(profile.ScreenCaptureCommand);

        foreach (string setup in profile.ScreenCaptureSetup) await client.SendAsync(setup);
        byte[] data = await client.QueryBinaryAsync(profile.ScreenCaptureCommand!);

        _out.WriteLine($"{data.Length:N0} bytes from {profile.ScreenCaptureCommand}");
        Assert.True(data.Length > 100_000, $"only {data.Length} bytes — a truncated transfer?");

        using var ms = new System.IO.MemoryStream(data);
        using var bmp = System.Drawing.Image.FromStream(ms);
        _out.WriteLine($"decoded {bmp.Width}x{bmp.Height}");
        Assert.True(bmp.Width >= 320 && bmp.Height >= 200);

        await Bench.ReleaseAsync(client);
    }

    // -------------------------------------------------------------------------- readout

    /// <summary>
    /// The live readout polls one query repeatedly and parses each reply invariantly. A
    /// meter is the instrument this exists for, so it is the one worth proving it against.
    /// </summary>
    [BenchFact]
    public async Task The_multimeter_readout_queries_all_parse_as_numbers()
    {
        using IInstrumentClient client = await Bench.ConnectAsync(Bench.Multimeter, timeoutMs: 10000);
        InstrumentProfile profile = InstrumentProfile.ForIdentity(await client.QueryAsync("*IDN?"));

        Assert.True(profile.SupportsLiveReadout);

        foreach (ReadoutFunction f in profile.ReadoutFunctions)
        {
            string reply = await client.QueryAsync(f.Query);
            bool ok = double.TryParse(reply.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v);

            _out.WriteLine($"  {f.Label,-12} {f.Query,-28} -> {reply.Trim()}  ({(ok ? $"{v:g6} {f.Unit}" : "UNPARSED")})");
            Assert.True(ok, $"{f.Label}: '{reply.Trim()}' did not parse as a number");
        }

        await Bench.ReleaseAsync(client);
    }

    // ------------------------------------------------------------------------ transport

    /// <summary>
    /// One console per address is a rule the app enforces because the DS2202's firmware
    /// wedges under rapid reconnection. Worth proving the instrument survives a connect,
    /// release and reconnect cycle, since that is what a test run does to it repeatedly.
    /// </summary>
    [BenchTheory]
    [MemberData(nameof(Expected))]
    public async Task An_instrument_survives_reconnection(string which, InstrumentFamily _)
    {
        string host = Host(which);
        for (int i = 1; i <= 3; i++)
        {
            using IInstrumentClient client = await Bench.ConnectAsync(host);
            string idn = await client.QueryAsync("*IDN?");
            Assert.False(string.IsNullOrWhiteSpace(idn));
            await Bench.ReleaseAsync(client);
            _out.WriteLine($"  cycle {i}: {idn.Trim()}");
        }
    }

    private static string Host(string which) => which switch
    {
        "scope" => Bench.Scope,
        "generator" => Bench.Generator,
        _ => Bench.Multimeter,
    };
}
