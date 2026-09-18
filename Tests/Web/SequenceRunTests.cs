using System.Collections.Concurrent;
using LabEquipmentController.Web.Bench;
using LabEquipmentController.Web.Client.Contracts;
using LabEquipmentController.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabEquipmentController.Tests;

/// <summary>
/// A multi-instrument script run by the web server, from the table that says what it will run
/// on to what each instrument heard — fake instruments, but the server's own session and run
/// code.
/// </summary>
/// <remarks>
/// The page binds every alias to an open session in the table over the editor, and those
/// bindings are what a run has to honour. They were collected and then looked up by the wrong
/// name: the runner asked for each DEVICE line's model, the server searched its aliases for
/// it, and <c>DEVICE gen : SDG2042X</c> with <c>gen</c> bound failed with "no connected
/// instrument matches" unless every alias happened to be spelled like its model. Nothing ran
/// a sequence through here before these; the Playwright specs stop at Run being enabled.
/// </remarks>
public class WebSequenceRunTests
{
    private const string Generator = "Siglent Technologies,SDG2042X,SDG2XCAD1R0001,2.01.01.35R3B2";
    private const string Meter = "Siglent Technologies,SDM3065X,SDM36HCD801207,3.02.01.13";
    private const string OtherMeter = "Siglent Technologies,SDM3065X,SDM36HCD801208,3.02.01.13";

    [Fact]
    public async Task Each_alias_runs_on_the_instrument_it_was_bound_to()
    {
        await using var server = new Server();
        var gen = Instrument(Generator);
        var dmm = Instrument(Meter);
        dmm.Responses["MEASure:VOLTage:AC?"] = "+7.071E-01";

        var bindings = new Dictionary<string, string>
        {
            ["gen"] = await server.ConnectAsync("192.168.1.5", gen),
            ["dmm"] = await server.ConnectAsync("192.168.1.7", dmm),
        };
        int genBefore = gen.Log.Count, dmmBefore = dmm.Log.Count;   // the *IDN? asked on connecting

        var (failed, error) = await server.RunAsync("""
            DEVICE gen : SDG2042X
            DEVICE dmm : SDM3065X
            COLUMNS Frequency, Vout
            FOR f = 100 TO 200 STEP 100
                gen: C1:BSWV FRQ,$f
                dmm: MEASure:VOLTage:AC? -> v
                RECORD $f, $v
            END
            """, bindings);

        Assert.Empty(server.Page.Errors);
        Assert.False(failed, error);
        Assert.Equal(["SEND:C1:BSWV FRQ,100", "SEND:C1:BSWV FRQ,200"], gen.Log.Skip(genBefore));
        Assert.Equal(["QUERY:MEASure:VOLTage:AC?", "QUERY:MEASure:VOLTage:AC?"], dmm.Log.Skip(dmmBefore));
        Assert.Equal(["100,+7.071E-01", "200,+7.071E-01"],
                     server.Page.Rows.Select(r => string.Join(",", r.Values)));
    }

    /// <summary>
    /// The case the picker in the binding table exists for. Two meters of one model are the
    /// same answer to "which SDM3065X?", so only the alias can say which one a line means — and
    /// a run that fell back on the model would read one meter twice and report it as two.
    /// </summary>
    [Fact]
    public async Task Two_meters_of_one_model_are_told_apart_by_their_aliases()
    {
        await using var server = new Server();
        var left = Instrument(Meter);
        var right = Instrument(OtherMeter);

        var bindings = new Dictionary<string, string>
        {
            ["left"] = await server.ConnectAsync("192.168.1.7", left),
            ["right"] = await server.ConnectAsync("192.168.1.8", right),
        };
        int leftBefore = left.Log.Count, rightBefore = right.Log.Count;

        var (failed, error) = await server.RunAsync("""
            DEVICE left  : SDM3065X
            DEVICE right : SDM3065X
            left:  MEASure:VOLTage:DC?
            right: MEASure:CURRent:DC?
            """, bindings);

        Assert.Empty(server.Page.Errors);
        Assert.False(failed, error);
        Assert.Equal(["QUERY:MEASure:VOLTage:DC?"], left.Log.Skip(leftBefore));
        Assert.Equal(["QUERY:MEASure:CURRent:DC?"], right.Log.Skip(rightBefore));
    }

    // ------------------------------------------------------------------- the table

    /// <summary>
    /// The table over the editor is filled on the server by the desktop's own rule, so two meters
    /// of one model are not handed out in the order they connected: both rows wait to be told.
    /// It used to fill both with the first meter, and Run then read that meter twice.
    /// </summary>
    [Fact]
    public async Task The_table_leaves_two_meters_of_one_model_for_you_to_pick()
    {
        await using var server = new Server();
        await server.ConnectAsync("192.168.1.7", Instrument(Meter));
        await server.ConnectAsync("192.168.1.8", Instrument(OtherMeter));

        var rows = server.Bind("DEVICE left : SDM3065X\nDEVICE right : SDM3065X");

        Assert.All(rows, r =>
        {
            Assert.Null(r.SessionId);
            Assert.Equal("2 connected: pick one", r.Note);
        });
    }

    [Fact]
    public async Task Picking_one_of_two_meters_gives_the_other_line_the_other()
    {
        await using var server = new Server();
        string a = await server.ConnectAsync("192.168.1.7", Instrument(Meter));
        string b = await server.ConnectAsync("192.168.1.8", Instrument(OtherMeter));

        var rows = server.Bind("DEVICE left : SDM3065X\nDEVICE right : SDM3065X", new() { ["left"] = b });

        Assert.Equal([b, a], rows.Select(r => r.SessionId));
        Assert.All(rows, r => Assert.Null(r.Note));
    }

    /// <summary>
    /// One meter and two parts asking for it: the first line has it, and the second says who,
    /// rather than both being filled with it. Picking it for the second on purpose is still
    /// allowed — that is a decision, where filling it in was a guess.
    /// </summary>
    [Fact]
    public async Task One_meter_goes_to_the_first_line_that_asks_for_it()
    {
        await using var server = new Server();
        string meter = await server.ConnectAsync("192.168.1.7", Instrument(Meter));
        const string script = "DEVICE dmm : SDM3065X\nDEVICE spare : SDM3065X";

        var rows = server.Bind(script);
        Assert.Equal(meter, rows[0].SessionId);
        Assert.Null(rows[1].SessionId);
        Assert.Equal("taken by dmm", rows[1].Note);

        var both = server.Bind(script, new() { ["dmm"] = meter, ["spare"] = meter });
        Assert.All(both, r => Assert.Equal(meter, r.SessionId));
    }

    [Fact]
    public async Task A_pick_of_a_session_that_has_closed_is_not_a_pick()
    {
        await using var server = new Server();
        string meter = await server.ConnectAsync("192.168.1.7", Instrument(Meter));

        var row = Assert.Single(server.Bind("DEVICE dmm : SDM3065X", new() { ["dmm"] = "closed-an-hour-ago" }));

        Assert.Equal(meter, row.SessionId);   // back to the rule, which has an answer
    }

    private static FakeInstrumentClient Instrument(string identity)
    {
        var fake = new FakeInstrumentClient();
        fake.Responses["*IDN?"] = identity;
        return fake;
    }

    /// <summary>
    /// The server's bench and run services, over a hub that keeps what it would have told the
    /// page watching the run.
    /// </summary>
    private sealed class Server : IAsyncDisposable
    {
        public readonly Watcher Page = new();
        private readonly BenchService _bench;
        private readonly RunService _runs;

        public Server()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSignalR();
            services.AddSingleton<HubLifetimeManager<BenchHub>>(Page);
            var hub = services.BuildServiceProvider().GetRequiredService<IHubContext<BenchHub>>();

            _bench = new BenchService(NullLogger<BenchService>.Instance, hub);
            _runs = new RunService(_bench, hub, NullLogger<RunService>.Instance);
        }

        /// <summary>Open a session on a fake instrument, as Connect would on a real one.</summary>
        public async Task<string> ConnectAsync(string address, FakeInstrumentClient instrument)
            => (await _bench.OpenAsync(BenchService.ParseAddress(address), () => instrument, default)).Id;

        /// <summary>The table over the editor, for a script and what has been picked in it.</summary>
        public IReadOnlyList<SequenceRequirement> Bind(string script, Dictionary<string, string>? picks = null)
            => _bench.BindSequence(script, picks ?? new());

        /// <summary>Start a sequence, watch it the way the page does, and wait for it to end.</summary>
        public async Task<(bool Failed, string? Error)> RunAsync(string script, Dictionary<string, string> bindings)
        {
            var summary = _runs.StartSequence(new SequenceRunRequest(script, bindings));
            Assert.False(summary.Failed, summary.Error);

            // What BenchHub.Watch does once the page has joined the run's group. Without it the
            // run waits two seconds for a watcher before it starts.
            _runs.Listening(summary.RunId);
            return await Page.Finished.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public ValueTask DisposeAsync() => _bench.DisposeAsync();
    }

    /// <summary>
    /// SignalR's own in-memory hub with its group sends kept: what a page watching the run would
    /// have been told, in the order it would have been told it.
    /// </summary>
    /// <remarks>
    /// The real plumbing with one method tapped, rather than a hand-written IHubContext — which,
    /// as BenchReadoutTests says, is four interfaces deep before it does anything.
    /// </remarks>
    private sealed class Watcher()
        : DefaultHubLifetimeManager<BenchHub>(NullLogger<DefaultHubLifetimeManager<BenchHub>>.Instance)
    {
        private readonly TaskCompletionSource<(bool Failed, string? Error)> _finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<ScriptOutputLine> Output { get; } = new();
        public ConcurrentQueue<RecordedRow> Rows { get; } = new();
        public Task<(bool Failed, string? Error)> Finished => _finished.Task;

        public IReadOnlyList<string> Errors =>
            Output.Where(l => l.Kind == nameof(ScriptOutputKind.Error)).Select(l => l.Text).ToList();

        public override Task SendGroupAsync(string groupName, string methodName, object?[] args,
                                            CancellationToken cancellationToken = default)
        {
            switch (methodName)
            {
                case "RunOutput": Output.Enqueue((ScriptOutputLine)args[1]!); break;
                case "RunRow": Rows.Enqueue((RecordedRow)args[1]!); break;
                case "RunFinished": _finished.TrySetResult(((bool)args[1]!, (string?)args[2])); break;
            }
            return base.SendGroupAsync(groupName, methodName, args, cancellationToken);
        }
    }
}
