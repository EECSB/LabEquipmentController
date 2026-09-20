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

    /// <summary>
    /// Wherever the DEVICE line stands. Found as the run reached it, this one stopped the run
    /// after the generator's output had been turned on, with a meter that was never going to
    /// read it.
    /// </summary>
    [Fact]
    public async Task A_missing_instrument_further_down_is_found_before_anything_is_sent()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            gen: C1:OUTP ON
            DEVICE dmm : SDM3065X
            dmm: MEASure:VOLTage:AC?
            """);

        string error = Assert.Single(bench.Errors);
        Assert.StartsWith("Line 3:", error);
        Assert.Contains("SDM3065X", error);
        Assert.Empty(bench.Sent("SDG2042X"));
    }

    /// <summary>A DEVICE line that cannot be read is found as early, and for the same reason.</summary>
    [Fact]
    public async Task A_device_line_that_cannot_be_read_is_found_before_anything_is_sent()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            gen: C1:OUTP ON
            DEVICE SDM3065X
            """);

        string error = Assert.Single(bench.Errors);
        Assert.StartsWith("Line 3: expected  DEVICE", error);
        Assert.Empty(bench.Sent("SDG2042X"));
    }

    /// <summary>
    /// Found once, however often the run passes it: a DEVICE line inside a loop is one part, not
    /// a question asked again on every pass.
    /// </summary>
    [Fact]
    public async Task A_device_line_inside_a_loop_is_found_once()
    {
        var bench = new Bench();
        bench.Add("SDM3065X");
        int asked = 0;

        await SequenceRunner.RunAsync("""
            REPEAT 3
                DEVICE dmm : SDM3065X
                dmm: MEASure:VOLTage:DC?
            END
            """,
            model => { asked++; return bench.Instruments[model]; },
            (_, _) => { }, _ => { }, CancellationToken.None);

        Assert.Equal(1, asked);
        Assert.Equal(3, bench.Sent("SDM3065X").Count);
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

    // ------------------------------------------------------- RECORD field boundaries

    [Fact]
    public async Task A_recorded_value_containing_commas_stays_one_column()
    {
        // The same defect the single-instrument runner had: substituting before splitting
        // let a reply's own commas become column boundaries. A script moved between the two
        // windows has to record the same thing, so the fix and its test live in both.
        var bench = new Bench();
        var dmm = bench.Add("SDM3065X");
        dmm.Responses["*IDN?"] = "Siglent Technologies,SDM3065X,SN1,3.02";
        dmm.Responses["READ?"] = "1.234";

        await bench.RunAsync("""
            DEVICE dmm : SDM3065X
            COLUMNS Volts, Identity
            dmm: *IDN? -> who
            dmm: READ? -> v
            RECORD $v, $who
            """);

        var row = Assert.Single(bench.Rows);
        Assert.Equal(2, row.Values.Count);
        Assert.Equal("1.234", row.Values[0]);
        Assert.Equal("Siglent Technologies,SDM3065X,SN1,3.02", row.Values[1]);
    }

    [Fact]
    public async Task A_reading_that_did_not_arrive_leaves_a_gap_rather_than_shifting_the_row()
    {
        var bench = new Bench();
        var dmm = bench.Add("SDM3065X");
        dmm.Responses["ONE?"] = "1";
        dmm.Responses["TWO?"] = "";
        dmm.Responses["THREE?"] = "3";

        await bench.RunAsync("""
            DEVICE dmm : SDM3065X
            COLUMNS A, B, C
            dmm: ONE? -> a
            dmm: TWO? -> b
            dmm: THREE? -> c
            RECORD $a, $b, $c
            """);

        var row = Assert.Single(bench.Rows);
        Assert.Equal(["1", "", "3"], row.Values);
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

    /// <summary>
    /// An exact tie is as ambiguous as a prefix one. It went to whichever connected first, so a
    /// script reading two meters read one of them twice and reported it as two.
    /// </summary>
    [Fact]
    public void Two_instruments_of_one_model_resolve_to_nothing()
    {
        SessionRegistry bench = Bench(
            ("192.168.1.7", "Siglent Technologies,SDM3065X,SDM000A,1.0"),
            ("192.168.1.8", "Siglent Technologies,SDM3065X,SDM000B,1.0"));

        Assert.Null(bench.FindForSequence("SDM3065X"));
        Assert.Null(bench.FindForSequence("SDM306"));
    }

    /// <summary>
    /// ...which is what the serial number is for. It is on the box and in *IDN?, and unlike the
    /// address it does not move when DHCP does.
    /// </summary>
    [Fact]
    public void A_serial_number_names_one_of_two_of_a_kind()
    {
        SessionRegistry bench = Bench(
            ("192.168.1.7", "Siglent Technologies,SDM3065X,SDM000A,1.0"),
            ("192.168.1.8", "Siglent Technologies,SDM3065X,SDM000B,1.0"));

        Assert.Equal("192.168.1.8", bench.FindForSequence("SDM000B")?.Host);
        Assert.Equal("192.168.1.7", bench.FindForSequence("sdm000a")?.Host);
    }

    /// <summary>
    /// The desktop's whole path, as SequenceForm runs it: the script bound against the session
    /// list once, then run by alias on exactly that. Two meters of one model, one named by
    /// serial number, and the other line finding the one that is left.
    /// </summary>
    [Fact]
    public async Task A_run_the_desktop_way_drives_what_its_strip_shows()
    {
        SessionRegistry bench = Bench(
            ("192.168.1.5", "Siglent Technologies,SDG2042X,SDG000,1.0"),
            ("192.168.1.7", "Siglent Technologies,SDM3065X,SDM000A,1.0"),
            ("192.168.1.8", "Siglent Technologies,SDM3065X,SDM000B,1.0"));
        var gen = (FakeInstrumentClient)bench.FindByHost("192.168.1.5")!.Client;
        var meterA = (FakeInstrumentClient)bench.FindByHost("192.168.1.7")!.Client;
        var meterB = (FakeInstrumentClient)bench.FindByHost("192.168.1.8")!.Client;
        var errors = new List<string>();

        const string script = """
            DEVICE gen   : SDG2042X
            DEVICE right : SDM3065X
            DEVICE left  : SDM000B
            gen:   C1:OUTP ON
            left:  MEASure:VOLTage:DC?
            right: MEASure:CURRent:DC?
            """;

        var bound = bench.BindSequence(SequenceRunner.Requirements(script));
        Assert.All(bound, b => Assert.Equal(DeviceBindingState.Bound, b.State));

        await SequenceRunner.RunAsync(
            script,
            (alias, model) => bound.FirstOrDefault(b =>
                    string.Equals(b.Alias, alias, StringComparison.OrdinalIgnoreCase)
                 && string.Equals(b.Model, model, StringComparison.OrdinalIgnoreCase))
                ?.Instrument?.Client,
            (text, kind) => { if (kind == ScriptOutputKind.Error) errors.Add(text); },
            _ => { },
            CancellationToken.None);

        Assert.Empty(errors);
        Assert.Equal(["SEND:C1:OUTP ON"], gen.Log);
        Assert.Equal(["QUERY:MEASure:VOLTage:DC?"], meterB.Log);
        Assert.Equal(["QUERY:MEASure:CURRent:DC?"], meterA.Log);
    }
}

/// <summary>
/// The rule a whole script is bound by, in both builds (SPEC §9a): a model binds only when
/// exactly one connected instrument answers to it that no other alias already has, and
/// anything that is not bound says why.
/// </summary>
public class SequenceBindingTests
{
    private sealed record Box(string Host, string Idn);

    private static readonly Box Gen = new("192.168.1.5", "Siglent Technologies,SDG2042X,SDG000,1.0");
    private static readonly Box MeterA = new("192.168.1.7", "Siglent Technologies,SDM3065X,SDM000A,1.0");
    private static readonly Box MeterB = new("192.168.1.8", "Siglent Technologies,SDM3065X,SDM000B,1.0");

    private static IReadOnlyList<DeviceBinding<Box>> Bind(
        string script, IReadOnlyList<Box> bench, Dictionary<string, Box>? picked = null)
        => SequenceBinding.Bind(SequenceRunner.Requirements(script), bench, b => b.Idn, b => b.Host, picked);

    [Fact]
    public void Each_model_binds_to_the_one_instrument_that_answers_to_it()
    {
        var bound = Bind("DEVICE gen : SDG2042X\nDEVICE dmm : SDM3065X", [Gen, MeterA]);

        Assert.Equal([Gen, MeterA], bound.Select(b => b.Instrument));
        Assert.All(bound, b => Assert.Null(b.Reason));
    }

    [Fact]
    public void Two_instruments_of_one_model_are_not_guessed_between()
    {
        var bound = Bind("DEVICE left : SDM3065X\nDEVICE right : SDM3065X", [MeterA, MeterB]);

        Assert.All(bound, b =>
        {
            Assert.Null(b.Instrument);
            Assert.Equal(DeviceBindingState.Ambiguous, b.State);
            Assert.Equal("2 connected", b.Reason);
        });
    }

    /// <summary>
    /// A script that needs one meter is no less ambiguous on a bench with two. It got the one
    /// that connected first, which is not a fact about which one is wired to the circuit.
    /// </summary>
    [Fact]
    public void One_line_is_as_ambiguous_as_two()
        => Assert.Equal(DeviceBindingState.Ambiguous,
                        Assert.Single(Bind("DEVICE dmm : SDM3065X", [MeterA, MeterB])).State);

    /// <summary>
    /// One meter, two parts asking for it. The first line gets it, and the second says who has
    /// it rather than sharing it: bound to both, the script reads one meter twice.
    /// </summary>
    [Fact]
    public void A_second_alias_is_not_given_the_instrument_the_first_has()
    {
        var bound = Bind("DEVICE dmm : SDM3065X\nDEVICE spare : SDM3065X", [MeterA]);

        Assert.Same(MeterA, bound[0].Instrument);
        Assert.Null(bound[1].Instrument);
        Assert.Equal(DeviceBindingState.Taken, bound[1].State);
        Assert.Equal("taken by dmm", bound[1].Reason);
    }

    /// <summary>
    /// Everything in the way is named, in the order the script declares it: "taken by left,
    /// right", not in whatever order the bench happens to list the meters.
    /// </summary>
    [Fact]
    public void A_line_with_nothing_left_names_every_alias_in_its_way()
    {
        var spare = Bind("DEVICE left : SDM000B\nDEVICE right : SDM3065X\nDEVICE spare : SDM3065X",
                         [MeterA, MeterB])[2];

        Assert.Equal(DeviceBindingState.Taken, spare.State);
        Assert.Equal("taken by left, right", spare.Reason);
    }

    /// <summary>The same alias declared twice is one part, not two competing for the meter.</summary>
    [Fact]
    public void An_alias_declared_twice_is_one_part()
        => Assert.All(Bind("DEVICE dmm : SDM3065X\nDEVICE dmm : SDM3065X", [MeterA]),
                      b => Assert.Same(MeterA, b.Instrument));

    /// <summary>
    /// Naming one of two identical meters is enough. A serial number is taken first, wherever
    /// its line sits in the script, and the model line after it finds the one that is left.
    /// </summary>
    [Theory]
    [InlineData("DEVICE left : SDM000B\nDEVICE right : SDM3065X")]
    [InlineData("DEVICE right : SDM3065X\nDEVICE left : SDM000B")]
    [InlineData("DEVICE right : SDM3065X\nDEVICE left : 192.168.1.8")]
    public void Naming_one_of_two_leaves_the_other_to_the_model(string script)
    {
        var bound = Bind(script, [MeterA, MeterB]).ToDictionary(b => b.Alias, b => b.Instrument);

        Assert.Same(MeterB, bound["left"]);
        Assert.Same(MeterA, bound["right"]);
    }

    /// <summary>
    /// The order they connected in is not an input. Every answer above is the same with the
    /// bench listed the other way round — which is what "not guessed" means.
    /// </summary>
    [Theory]
    [InlineData("DEVICE left : SDM3065X\nDEVICE right : SDM3065X")]
    [InlineData("DEVICE right : SDM3065X\nDEVICE left : SDM000B")]
    [InlineData("DEVICE dmm : SDM3065X")]
    public void Which_instrument_connected_first_makes_no_difference(string script)
        => Assert.Equal(
            Bind(script, [MeterA, MeterB]).Select(b => (b.Instrument, b.State)),
            Bind(script, [MeterB, MeterA]).Select(b => (b.Instrument, b.State)));

    /// <summary>
    /// A pick is taken at its word and the rest bind around it: say which meter is left, and
    /// right is the other one.
    /// </summary>
    [Fact]
    public void A_pick_is_taken_first_and_the_rest_bind_around_it()
    {
        var picked = new Dictionary<string, Box>(StringComparer.OrdinalIgnoreCase) { ["LEFT"] = MeterB };
        var bound = Bind("DEVICE left : SDM3065X\nDEVICE right : SDM3065X", [MeterA, MeterB], picked);

        Assert.Same(MeterB, bound[0].Instrument);
        Assert.Same(MeterA, bound[1].Instrument);
    }

    /// <summary>
    /// ...including one instrument for two parts. Chosen by hand that is a decision, where
    /// handed out by the rule it was a guess.
    /// </summary>
    [Fact]
    public void A_pick_may_give_one_instrument_two_parts()
    {
        var picked = new Dictionary<string, Box>(StringComparer.OrdinalIgnoreCase)
        {
            ["dmm"] = MeterA,
            ["spare"] = MeterA,
        };

        Assert.All(Bind("DEVICE dmm : SDM3065X\nDEVICE spare : SDM3065X", [MeterA], picked),
                   b => Assert.Same(MeterA, b.Instrument));
    }

    [Fact]
    public void A_prefix_two_models_share_is_ambiguous_and_an_exact_name_is_not()
    {
        Box sdm3055 = new("1.1.1.1", "Siglent Technologies,SDM3055,X1,1.0");
        Box sdm3055x = new("1.1.1.2", "Siglent Technologies,SDM3055X,X2,1.0");

        var bound = Bind("DEVICE a : SDM305\nDEVICE b : SDM3055", [sdm3055, sdm3055x]);

        Assert.Equal(DeviceBindingState.Ambiguous, bound[0].State);
        Assert.Same(sdm3055, bound[1].Instrument);
    }

    /// <summary>
    /// One address can front several instruments — a GPIB or serial gateway, told apart by port
    /// or device name. Written as a bare host it answers for all of them, and is refused too.
    /// </summary>
    [Fact]
    public void An_address_two_instruments_share_is_ambiguous_too()
    {
        Box first = new("192.168.1.20", "");
        Box second = new("192.168.1.20", "");

        var only = Assert.Single(Bind("DEVICE x : 192.168.1.20", [first, second]));
        Assert.Equal(DeviceBindingState.Ambiguous, only.State);
    }

    [Fact]
    public void A_line_nothing_answers_to_is_not_connected()
    {
        var only = Assert.Single(Bind("DEVICE scope : DS2202", [Gen, MeterA]));

        Assert.Equal(DeviceBindingState.NotConnected, only.State);
        Assert.Equal("not connected", only.Reason);
    }
}

/// <summary>
/// DEVICE lines resolved by alias: the overload for a front end that lets each alias be bound
/// to an instrument, which the web's binding table and <c>lec seq --device</c> both do.
/// </summary>
public class SequenceAliasBindingTests
{
    private sealed class Bench
    {
        public readonly Dictionary<string, FakeInstrumentClient> Bound =
            new(StringComparer.OrdinalIgnoreCase);

        public readonly List<(string Alias, string Model)> Asked = new();
        public readonly List<string> Errors = new();

        public FakeInstrumentClient Bind(string alias)
            => Bound[alias] = new FakeInstrumentClient { Host = alias };

        public Task RunAsync(string script)
            => SequenceRunner.RunAsync(
                script,
                (alias, model) =>
                {
                    Asked.Add((alias, model));
                    return Bound.TryGetValue(alias, out var c) ? c : null;
                },
                (text, kind) => { if (kind == ScriptOutputKind.Error) Errors.Add(text); },
                _ => { },
                CancellationToken.None);
    }

    [Fact]
    public async Task Each_line_goes_to_the_instrument_bound_to_its_alias()
    {
        var bench = new Bench();
        var gen = bench.Bind("gen");
        var dmm = bench.Bind("dmm");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            DEVICE dmm : SDM3065X
            gen: C1:OUTP ON
            dmm: MEASure:VOLTage:DC?
            """);

        Assert.Empty(bench.Errors);
        Assert.Equal(["SEND:C1:OUTP ON"], gen.Log);
        Assert.Equal(["QUERY:MEASure:VOLTage:DC?"], dmm.Log);
    }

    /// <summary>The model travels with the alias, for a caller that wants to check a binding against it.</summary>
    [Fact]
    public async Task The_resolver_is_asked_by_alias_and_told_the_model()
    {
        var bench = new Bench();
        bench.Bind("gen");
        bench.Bind("dmm");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            DEVICE dmm : SDM3065X
            """);

        Assert.Equal([("gen", "SDG2042X"), ("dmm", "SDM3065X")], bench.Asked);
    }

    /// <summary>
    /// What resolving by alias is for. Asked by model, both lines are the same question and
    /// get the same answer — one meter read twice and reported as two.
    /// </summary>
    [Fact]
    public async Task Two_instruments_of_one_model_stay_two_instruments()
    {
        var bench = new Bench();
        var left = bench.Bind("left");
        var right = bench.Bind("right");

        await bench.RunAsync("""
            DEVICE left  : SDM3065X
            DEVICE right : SDM3065X
            left:  MEASure:VOLTage:DC?
            right: MEASure:CURRent:DC?
            WITH right
                MEASure:RESistance?
            END
            """);

        Assert.Empty(bench.Errors);
        Assert.Equal(["QUERY:MEASure:VOLTage:DC?"], left.Log);
        Assert.Equal(["QUERY:MEASure:CURRent:DC?", "QUERY:MEASure:RESistance?"], right.Log);
    }

    /// <summary>
    /// A missing binding is reported as one: the alias with nothing behind it, and the model it
    /// was for. "No connected instrument matches SDM3065X" would send someone looking for a
    /// meter that is connected and answering, when what is missing is which one plays the part.
    /// </summary>
    [Fact]
    public async Task An_alias_bound_to_nothing_stops_the_run_and_is_named()
    {
        var bench = new Bench();
        var left = bench.Bind("left");

        await bench.RunAsync("""
            DEVICE left  : SDM3065X
            DEVICE right : SDM3065X
            left: MEASure:VOLTage:DC?
            """);

        string error = Assert.Single(bench.Errors);
        Assert.Contains("\"right\"", error);
        Assert.Contains("SDM3065X", error);
        Assert.DoesNotContain("no connected instrument matches", error);
        Assert.Empty(left.Log);   // nothing ran, not even on the instrument that was there
    }

    /// <summary>...and so is one declared after the first line has run. A program handing this
    /// its own bindings has nothing else to stop it.</summary>
    [Fact]
    public async Task An_alias_bound_to_nothing_further_down_is_found_before_anything_is_sent()
    {
        var bench = new Bench();
        var left = bench.Bind("left");

        await bench.RunAsync("""
            DEVICE left  : SDM3065X
            left: CONFigure:VOLTage:DC
            DEVICE right : SDM3065X
            right: MEASure:VOLTage:DC?
            """);

        string error = Assert.Single(bench.Errors);
        Assert.StartsWith("Line 3:", error);
        Assert.Contains("\"right\"", error);
        Assert.Empty(left.Log);
    }
}
