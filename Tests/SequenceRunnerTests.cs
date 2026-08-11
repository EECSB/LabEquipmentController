using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The multi-instrument sequence language.
///
/// The thing worth testing hardest is not that a command arrives, but that it arrives at
/// the *right instrument* — a generator's command sent to a meter is the failure this
/// project exists to prevent, and a sequence is the first place the app can make it.
/// </summary>
public class SequenceRunnerTests
{
    private sealed class Bench
    {
        public readonly Dictionary<string, FakeInstrumentClient> Instruments =
            new(StringComparer.OrdinalIgnoreCase);

        public readonly List<string> Output = new();
        public readonly List<string> Errors = new();
        public readonly List<SequenceRow> Rows = new();

        public FakeInstrumentClient Add(string model)
            => Instruments[model] = new FakeInstrumentClient { Host = model };

        public Task RunAsync(string script, CancellationToken ct = default)
            => SequenceRunner.RunAsync(
                script,
                model => Instruments.TryGetValue(model, out var c) ? c : null,
                (text, kind) =>
                {
                    Output.Add(text);
                    if (kind == ScriptOutputKind.Error) Errors.Add(text);
                },
                Rows.Add,
                ct);

        public IReadOnlyList<string> Sent(string model) => Instruments[model].Log;
    }

    // ------------------------------------------------------------------- addressing

    [Fact]
    public async Task A_prefixed_line_goes_only_to_that_instrument()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");
        bench.Add("SDM3065X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            DEVICE dmm : SDM3065X
            gen: C1:OUTP ON
            dmm: MEASure:VOLTage:DC?
            """);

        Assert.Empty(bench.Errors);
        Assert.Equal(new[] { "SEND:C1:OUTP ON" }, bench.Sent("SDG2042X"));
        Assert.Equal(new[] { "QUERY:MEASure:VOLTage:DC?" }, bench.Sent("SDM3065X"));
    }

    /// <summary>
    /// The point of the whole design: with two instruments declared, a line that does not
    /// say which one it is for is refused rather than sent to whichever was declared first.
    /// </summary>
    [Fact]
    public async Task An_unaddressed_line_is_refused_when_several_instruments_are_declared()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");
        bench.Add("SDM3065X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            DEVICE dmm : SDM3065X
            *IDN?
            """);

        Assert.Single(bench.Errors);
        Assert.Contains("which instrument", bench.Errors[0], StringComparison.OrdinalIgnoreCase);
        Assert.Empty(bench.Sent("SDG2042X"));
        Assert.Empty(bench.Sent("SDM3065X"));
    }

    /// <summary>...but one instrument is unambiguous, so a sequence of one needs no prefixes.</summary>
    [Fact]
    public async Task One_declared_instrument_needs_no_prefix()
    {
        var bench = new Bench();
        bench.Add("SDM3065X");

        await bench.RunAsync("""
            DEVICE dmm : SDM3065X
            *IDN?
            """);

        Assert.Empty(bench.Errors);
        Assert.Equal(new[] { "QUERY:*IDN?" }, bench.Sent("SDM3065X"));
    }

    [Fact]
    public async Task With_sets_the_target_for_a_block_and_restores_it_after()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");
        bench.Add("SDM3065X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            DEVICE dmm : SDM3065X
            WITH gen
                C1:OUTP ON
                C1:BSWV WVTP,SINE
            END
            dmm: MEASure:VOLTage:AC?
            """);

        Assert.Empty(bench.Errors);
        Assert.Equal(new[] { "SEND:C1:OUTP ON", "SEND:C1:BSWV WVTP,SINE" }, bench.Sent("SDG2042X"));
        Assert.Equal(new[] { "QUERY:MEASure:VOLTage:AC?" }, bench.Sent("SDM3065X"));
    }

    [Fact]
    public async Task A_missing_instrument_stops_the_run_before_anything_is_sent()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            DEVICE dmm : SDM3065X
            gen: C1:OUTP ON
            """);

        Assert.Single(bench.Errors);
        Assert.Contains("SDM3065X", bench.Errors[0]);
        Assert.Empty(bench.Sent("SDG2042X"));   // nothing ran, not even the line before it
    }

    // ------------------------------------------------------------------------ sweeps

    [Fact]
    public async Task A_linear_sweep_substitutes_each_value()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            FOR f = 100 TO 300 STEP 100
                gen: C1:BSWV FRQ,$f
            END
            """);

        Assert.Empty(bench.Errors);
        Assert.Equal(new[]
        {
            "SEND:C1:BSWV FRQ,100",
            "SEND:C1:BSWV FRQ,200",
            "SEND:C1:BSWV FRQ,300",
        }, bench.Sent("SDG2042X"));
    }

    /// <summary>
    /// A filter response is read per decade. A linear sweep from 100 Hz to 100 kHz puts 99%
    /// of its points above 1 kHz, which is where a low-pass response has already stopped
    /// being interesting.
    /// </summary>
    [Fact]
    public async Task A_log_sweep_spaces_its_points_per_decade()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            FOR f = 100 TO 100000 POINTS 4 LOG
                gen: C1:BSWV FRQ,$f
            END
            """);

        Assert.Empty(bench.Errors);
        Assert.Equal(new[]
        {
            "SEND:C1:BSWV FRQ,100",
            "SEND:C1:BSWV FRQ,1000",
            "SEND:C1:BSWV FRQ,10000",
            "SEND:C1:BSWV FRQ,100000",
        }, bench.Sent("SDG2042X"));
    }

    [Fact]
    public async Task A_sweep_accepts_engineering_suffixes()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            FOR f = 1k TO 3k STEP 1k
                gen: C1:BSWV FRQ,$f
            END
            """);

        Assert.Empty(bench.Errors);
        Assert.Equal(3, bench.Sent("SDG2042X").Count);
        Assert.Contains("SEND:C1:BSWV FRQ,2000", bench.Sent("SDG2042X"));
    }

    [Fact]
    public async Task A_sweep_that_would_never_finish_is_refused()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            FOR f = 1 TO 10 STEP 0
                gen: C1:BSWV FRQ,$f
            END
            """);

        Assert.Single(bench.Errors);
        Assert.Empty(bench.Sent("SDG2042X"));
    }

    // ------------------------------------------------------- capture and recording

    [Fact]
    public async Task A_query_reply_can_be_captured_and_recorded()
    {
        var bench = new Bench();
        var dmm = bench.Add("SDM3065X");
        dmm.Responses["MEASure:VOLTage:AC?"] = "+1.234E-01";
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            DEVICE dmm : SDM3065X
            FOR f = 100 TO 200 STEP 100
                gen: C1:BSWV FRQ,$f
                dmm: MEASure:VOLTage:AC? -> vout
                RECORD $f, $vout
            END
            """);

        Assert.Empty(bench.Errors);
        Assert.Equal(2, bench.Rows.Count);
        Assert.Equal(new[] { "100", "+1.234E-01" }, bench.Rows[0].Values);
        Assert.Equal(new[] { "200", "+1.234E-01" }, bench.Rows[1].Values);
    }

    [Fact]
    public async Task Capturing_from_a_command_that_is_not_a_query_is_an_error()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            gen: C1:OUTP ON -> state
            """);

        Assert.Single(bench.Errors);
        Assert.Contains("no '?'", bench.Errors[0]);
    }

    /// <summary>
    /// An unknown $name is left as written. Blanking it would turn "C1:BSWV FRQ,$typo"
    /// into "C1:BSWV FRQ," — a command the instrument might well accept, with a value
    /// nobody chose.
    /// </summary>
    [Fact]
    public async Task An_unknown_variable_is_left_alone_rather_than_blanked()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            gen: C1:BSWV FRQ,$nosuch
            """);

        Assert.Equal(new[] { "SEND:C1:BSWV FRQ,$nosuch" }, bench.Sent("SDG2042X"));
    }

    // ------------------------------------------------------------------ housekeeping

    [Fact]
    public void Requirements_lists_what_a_sequence_needs_before_it_runs()
    {
        var needs = SequenceRunner.Requirements("""
            # a filter sweep
            DEVICE gen : SDG2042X
            DEVICE dmm : SDM3065X
            gen: C1:OUTP ON
            """);

        Assert.Equal(2, needs.Count);
        Assert.Equal(("gen", "SDG2042X"), needs[0]);
        Assert.Equal(("dmm", "SDM3065X"), needs[1]);
    }

    [Fact]
    public void Columns_are_read_from_the_script()
    {
        Assert.Equal(new[] { "Frequency (Hz)", "Vout (V)" },
            SequenceRunner.Columns("COLUMNS Frequency (Hz), Vout (V)"));
    }

    [Fact]
    public async Task An_error_from_an_instrument_names_which_one_it_was()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");
        bench.Instruments["SDM3065X"] = new FakeInstrumentClient { ThrowOn = "MEASure:VOLTage:AC?" };

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            DEVICE dmm : SDM3065X
            dmm: MEASure:VOLTage:AC?
            """);

        Assert.Single(bench.Errors);
        Assert.Contains("(dmm)", bench.Errors[0]);
    }

    [Fact]
    public async Task Stop_cancels_between_lines()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bench.RunAsync("""
            DEVICE gen : SDG2042X
            gen: C1:OUTP ON
            """, cts.Token));

        Assert.Empty(bench.Sent("SDG2042X"));
    }

    [Fact]
    public async Task Nested_loops_inside_a_with_block_keep_their_target()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");
        bench.Add("SDM3065X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            DEVICE dmm : SDM3065X
            WITH gen
                REPEAT 2
                    C1:OUTP ON
                END
            END
            dmm: *IDN?
            """);

        Assert.Empty(bench.Errors);
        Assert.Equal(2, bench.Sent("SDG2042X").Count);
        Assert.Equal(new[] { "QUERY:*IDN?" }, bench.Sent("SDM3065X"));
    }
}

/// <summary>
/// Turning "DEVICE gen : SDG2042X" into a connection. Separate from the language tests
/// because this is where a sequence meets the actual bench, and the bench moves: these
/// instruments are on DHCP, so a script that named addresses would break every lease.
/// </summary>
public class SequenceDeviceResolutionTests
{
    private static InstrumentSession Session(string host, string idn)
        => new(new FakeInstrumentClient { Host = host }, idn,
               InstrumentProfile.ForIdentity(idn), 3000);

    private static SessionRegistry Bench(params (string Host, string Idn)[] instruments)
    {
        var r = new SessionRegistry();
        foreach (var (host, idn) in instruments) r.Add(Session(host, idn));
        return r;
    }

    [Fact]
    public void A_model_resolves_to_its_session()
    {
        SessionRegistry bench = Bench(
            ("192.168.1.5", "Siglent Technologies,SDG2042X,SDG000,1.0"),
            ("192.168.1.7", "Siglent Technologies,SDM3065X,SDM000,1.0"));

        Assert.Equal("192.168.1.5", bench.FindForSequence("SDG2042X")?.Host);
        Assert.Equal("192.168.1.7", bench.FindForSequence("SDM3065X")?.Host);
    }

    [Fact]
    public void The_model_match_ignores_case()
        => Assert.NotNull(Bench(("1.1.1.1", "Siglent Technologies,SDG2042X,X,1.0"))
                          .FindForSequence("sdg2042x"));

    /// <summary>An SDS2354X answers *IDN? as "SDS2354X Plus"; the short name should find it.</summary>
    [Fact]
    public void A_short_model_name_finds_the_qualified_one()
        => Assert.NotNull(Bench(("1.1.1.1", "Siglent Technologies,SDS2354X Plus,X,1.0"))
                          .FindForSequence("SDS2354X"));

    /// <summary>
    /// Two instruments whose models share a prefix cannot be told apart by that prefix, so
    /// nothing is returned. Picking one would send a sequence's commands to whichever
    /// happened to be connected first.
    /// </summary>
    [Fact]
    public void An_ambiguous_prefix_resolves_to_nothing()
    {
        SessionRegistry bench = Bench(
            ("1.1.1.1", "Siglent Technologies,SDM3055,X,1.0"),
            ("1.1.1.2", "Siglent Technologies,SDM3055X,X,1.0"));

        Assert.Null(bench.FindForSequence("SDM305"));
        // ...but the exact name still works, since an exact match is preferred.
        Assert.Equal("1.1.1.1", bench.FindForSequence("SDM3055")?.Host);
    }

    [Fact]
    public void An_address_still_works_for_an_instrument_that_will_not_identify()
    {
        SessionRegistry bench = Bench(("192.168.1.9", ""));
        Assert.Equal("192.168.1.9", bench.FindForSequence("192.168.1.9")?.Host);
    }

    [Fact]
    public void An_unknown_model_resolves_to_nothing()
        => Assert.Null(Bench(("1.1.1.1", "Siglent Technologies,SDG2042X,X,1.0"))
                       .FindForSequence("DS2202"));
}
