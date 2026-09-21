using LabEquipmentController.Cli;

namespace LabEquipmentController.Tests;

/// <summary>
/// <c>lec seq</c> end to end against fake instruments: a script file, its
/// <c>--device alias=address</c> bindings, what each instrument heard, and the table printed.
/// </summary>
/// <remarks>
/// The bindings name aliases, and a run has to honour them. It did not: the runner asked for
/// each DEVICE line's model, the verb searched its aliases for it, and the usage text's own
/// example — <c>lec seq filter.seq --device gen=… --device scope=…</c> — stopped on its first
/// line with "no connected instrument matches "SDG2042X"".
/// </remarks>
public class CliSequenceTests
{
    [Fact]
    public async Task Each_alias_runs_on_the_address_it_was_given()
    {
        using var bench = new Bench();
        var gen = bench.At("192.168.1.5");
        var dmm = bench.At("192.168.1.7");
        dmm.Responses["MEASure:VOLTage:AC?"] = "+7.071E-01";

        int code = await bench.RunAsync("""
            DEVICE gen : SDG2042X
            DEVICE dmm : SDM3065X
            COLUMNS Frequency, Vout
            FOR f = 100 TO 200 STEP 100
                gen: C1:BSWV FRQ,$f
                dmm: MEASure:VOLTage:AC? -> v
                RECORD $f, $v
            END
            """, "--device", "gen=192.168.1.5", "--device", "dmm=192.168.1.7", "--csv", "--quiet");

        Assert.True(code == Commands.Ok, bench.Stderr.ToString());
        Assert.Equal(["SEND:C1:BSWV FRQ,100", "SEND:C1:BSWV FRQ,200"], gen.Log);
        Assert.Equal(["QUERY:MEASure:VOLTage:AC?", "QUERY:MEASure:VOLTage:AC?"], dmm.Log);
        Assert.Equal("Frequency,Vout\r\n100,+7.071E-01\r\n200,+7.071E-01\r\n", bench.Stdout.ToString());
    }

    /// <summary>
    /// Two meters of one model are the same answer to "which SDM3065X?", so only the alias can
    /// say which one a line means. On a command line that is the only way to say it at all.
    /// </summary>
    [Fact]
    public async Task Two_meters_of_one_model_are_told_apart_by_their_aliases()
    {
        using var bench = new Bench();
        var left = bench.At("192.168.1.7");
        var right = bench.At("192.168.1.8");

        int code = await bench.RunAsync("""
            DEVICE left  : SDM3065X
            DEVICE right : SDM3065X
            left:  MEASure:VOLTage:DC?
            right: MEASure:CURRent:DC?
            """, "--device", "left=192.168.1.7", "--device", "right=192.168.1.8", "--quiet");

        Assert.True(code == Commands.Ok, bench.Stderr.ToString());
        Assert.Equal(["QUERY:MEASure:VOLTage:DC?"], left.Log);
        Assert.Equal(["QUERY:MEASure:CURRent:DC?"], right.Log);
    }

    /// <summary>
    /// A value given with <c>--input</c> reaches the command it stands in, and the flag may be
    /// repeated the way <c>--device</c> is.
    /// </summary>
    [Fact]
    public async Task Values_given_with_input_reach_the_instrument()
    {
        using var bench = new Bench();
        var psu = bench.At("1.1.1.1");

        int code = await bench.RunAsync("""
            INPUT vset : number V = 5 (0 TO 30)
            INPUT serial : text
            DEVICE psu : SPD3303X
            PRINT Testing $serial
            psu: CH1:VOLTage $vset
            """, "--device", "psu=1.1.1.1", "--input", "vset=12", "--input", "serial=A7");

        Assert.True(code == Commands.Ok, bench.Stderr.ToString());
        Assert.Equal(["SEND:CH1:VOLTage 12"], psu.Log);
        Assert.Contains("Testing A7", bench.Stderr.ToString());
    }

    /// <summary>
    /// And a value the script would refuse stops it before a socket is opened. The fake bench
    /// would answer, so the instrument having heard nothing is the whole assertion: on a real
    /// bench this is a connection not made and an instrument not touched.
    /// </summary>
    [Fact]
    public async Task A_value_outside_its_range_stops_the_run_before_anything_is_connected()
    {
        using var bench = new Bench();
        var psu = bench.At("1.1.1.1");

        int code = await bench.RunAsync("""
            INPUT vset : number V = 5 (0 TO 30)
            DEVICE psu : SPD3303X
            psu: CH1:VOLTage $vset
            """, "--device", "psu=1.1.1.1", "--input", "vset=99");

        Assert.Equal(Commands.Misused, code);
        Assert.Contains("is above 30", bench.Stderr.ToString());
        Assert.Empty(psu.Log);
    }

    [Fact]
    public async Task An_input_with_no_default_and_no_value_is_refused_by_name()
    {
        using var bench = new Bench();
        bench.At("1.1.1.1");

        int code = await bench.RunAsync("""
            INPUT serial : text
            DEVICE psu : SPD3303X
            """, "--device", "psu=1.1.1.1");

        Assert.Equal(Commands.Misused, code);
        Assert.Contains("No value for \"serial\"", bench.Stderr.ToString());
    }

    /// <summary>
    /// <c>lec seq</c> over a script file, with every <c>--device</c> address answered by the fake
    /// instrument registered at it rather than by a socket.
    /// </summary>
    private sealed class Bench : IDisposable
    {
        private readonly string _file =
            Path.Combine(Path.GetTempPath(), "lec-seq-" + Guid.NewGuid().ToString("N") + ".seq");
        private readonly Dictionary<string, FakeInstrumentClient> _at = new(StringComparer.OrdinalIgnoreCase);

        public StringWriter Stdout { get; } = new();
        public StringWriter Stderr { get; } = new();

        public FakeInstrumentClient At(string host) => _at[host] = new FakeInstrumentClient { Host = host };

        public Task<int> RunAsync(string script, params string[] options)
        {
            File.WriteAllText(_file, script);
            return Commands.Sequence(CommandLine.Parse(["seq", _file, .. options]), Stdout, Stderr,
                                     (ep, _) => _at[ep.Host], CancellationToken.None);
        }

        public void Dispose()
        {
            try { File.Delete(_file); } catch { /* a temp file that outlives the run is not worth failing a test over */ }
        }
    }
}
