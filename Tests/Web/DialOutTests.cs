using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabEquipmentController.Web;
using LabEquipmentController.Web.Bench;
using LabEquipmentController.Web.Client.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabEquipmentController.Tests;

/// <summary>
/// The server started as a bench that dials out: it opens the connection to its host rather than
/// waiting to be called. Its data directory is its own, as <see cref="WebFactory"/>'s is.
/// </summary>
/// <param name="mostBytes">
/// What one list of one run may keep on disk, when a test means to reach it; the server's own GiB
/// otherwise, which no test does.
/// </param>
public sealed class DialFactory(string url, string token, string? serviceToken = null, long? mostBytes = null)
    : WebApplicationFactory<WebEntryPoint>
{
    private readonly string _data =
        Path.Combine(Path.GetTempPath(), "lec-dial-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["LEC_DATA"] = _data,
                [DialOutMode.UrlVariable] = url,
                [DialOutMode.TokenVariable] = token,
                [ServiceMode.TokenVariable] = serviceToken,
            }));

        if (mostBytes is { } most)
            builder.ConfigureTestServices(services => services.AddSingleton(new RunFiles(Path.Combine(_data, "runs"), most)));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true); }
        catch { /* a temp folder that outlives the run is not worth failing a test over */ }
    }
}

/// <summary>
/// The host's half, stood up for real on a real port: a hub the bench dials, which records what
/// dialed in and can put a question down the connection.
/// </summary>
/// <remarks>
/// A real socket, because this is the one thing that cannot be faked usefully. Treeality's own hub
/// is the production version of this; what is here is the smallest thing that is the same shape, so
/// the bench under test is doing exactly what it will do on a line.
/// </remarks>
public sealed class FakeHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeHost(WebApplication app, string address) => (_app, Address) = (app, address);

    /// <summary>Where the bench dials: this host's hub, absolute.</summary>
    public string Address { get; }

    public Dialed Dialed => _app.Services.GetRequiredService<Dialed>();

    public static async Task<FakeHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        // The size of message Treeality's hub takes. SignalR's own default is 32 KB, and an answer in
        // parts sends parts larger than that — which is the host's to allow, and Treeality's does.
        builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 8 * 1024 * 1024);
        builder.Services.AddSingleton<Dialed>();

        var app = builder.Build();
        app.MapHub<HostHub>("/dial");
        await app.StartAsync();

        string root = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();

        return new FakeHost(app, root.TrimEnd('/') + "/dial");
    }

    /// <summary>Puts one request down the connection and waits for the bench's answer.</summary>
    public async Task<BenchSaid> AskAsync(string method, string path, object? body = null)
    {
        Dialed dialed = Dialed;
        string id = Guid.NewGuid().ToString("N");
        var waiting = new TaskCompletionSource<BenchSaid>(TaskCreationOptions.RunContinuationsAsynchronously);

        dialed.Pending[id] = waiting;

        var hub = _app.Services.GetRequiredService<IHubContext<HostHub>>();
        string? json = body is null ? null : JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        await hub.Clients.Client(dialed.ConnectionId!).SendAsync(DialOutService.AskMethod, new BenchAsk(id, method, path, json));

        return await waiting.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>What has dialed in, and what is waiting on an answer.</summary>
public sealed class Dialed
{
    public TaskCompletionSource<string> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string? ConnectionId { get; set; }

    /// <summary>The bearer the bench presented, which is how the host knows which bench this is.</summary>
    public string? Token { get; set; }

    public ConcurrentDictionary<string, TaskCompletionSource<BenchSaid>> Pending { get; } = new();

    /// <summary>How many messages each answer arrived in, by its id.</summary>
    public ConcurrentDictionary<string, int> Messages { get; } = new();

    /// <summary>The parts of an answer still arriving, put back together as they come.</summary>
    public ConcurrentDictionary<string, System.Text.StringBuilder> Arriving { get; } = new();
}

public sealed class HostHub(Dialed dialed) : Hub
{
    public override Task OnConnectedAsync()
    {
        string header = Context.GetHttpContext()?.Request.Headers.Authorization.ToString() ?? "";

        dialed.ConnectionId = Context.ConnectionId;
        dialed.Token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : "";
        dialed.Connected.TrySetResult(Context.ConnectionId);

        return base.OnConnectedAsync();
    }

    public Task Said(BenchSaid said)
    {
        dialed.Messages.AddOrUpdate(said.Id, 1, (_, seen) => seen + 1);

        // Put back together as the host puts it: an answer in parts is whole at its last part.
        if (said.Parts > 1)
        {
            var body = dialed.Arriving.GetOrAdd(said.Id, _ => new System.Text.StringBuilder());
            body.Append(said.Body);

            if (said.Part < said.Parts - 1) return Task.CompletedTask;

            dialed.Arriving.TryRemove(said.Id, out _);
            said = said with { Body = body.ToString(), Part = 0, Parts = 1 };
        }

        if (dialed.Pending.TryRemove(said.Id, out var waiting)) waiting.TrySetResult(said);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Dial-out mode (<see cref="DialOutMode"/>): a bench on a line its host cannot reach, which opens
/// the connection itself and answers down it.
/// </summary>
/// <remarks>
/// Treeality's Instruments plugin is the host this was built for; its requirements list it as
/// prerequisite 12, and the phase that needs it is its Instruments phase 4. No instrument is
/// involved here: what is being proved is the connection, the named list of what may be asked over
/// it, and that the answers are the ones the API routes give.
/// </remarks>
public class DialOutTests
{
    private const string Token = "trl_a-host-issued-this-one-for-one-bench";

    /// <summary>What the API routes are serialized with, so the test reads what the host would.</summary>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string Meter = "Siglent Technologies,SDM3065X,SDM36HCD801207,3.02.01.13";

    private const string Script = """
        DEVICE dmm : SDM3065X
        INPUT vset : number V = 5 (0 TO 30)
        COLUMNS Frequency, Vout
        FOR f = 100 TO 200 STEP 100
            dmm: MEASure:VOLTage:AC? -> v
            RECORD $f, $v
        END
        """;

    // ------------------------------------------------------------ the switch

    [Fact]
    public void Neither_variable_set_is_the_app_and_service_mode_exactly_as_they_were()
    {
        var off = new DialOutMode(null, null);

        Assert.False(off.Enabled);
        Assert.Null(off.Refusal);
    }

    [Fact]
    public void One_variable_without_the_other_is_said_rather_than_failing_quietly()
    {
        Assert.Contains("nowhere to dial", new DialOutMode(null, Token).Refusal);
        Assert.Contains("nothing to dial with", new DialOutMode("https://treeality.example/dial", null).Refusal);
        Assert.Contains("absolute", new DialOutMode("treeality.example/dial", Token).Refusal);
        Assert.Contains("http or https", new DialOutMode("ftp://treeality.example/dial", Token).Refusal);

        Assert.Null(new DialOutMode("https://treeality.example/dial", Token).Refusal);
    }

    [Fact]
    public void A_bench_that_dials_out_is_driven_by_a_host_without_taking_a_bearer()
    {
        var dialing = new ServiceMode(token: null, pathBase: null, dialUrl: "https://treeality.example/dial");

        // Nothing is guarded: the page is the console for whoever is standing at this bench, and the
        // host is at the other end of a connection rather than on the API.
        Assert.False(dialing.Enabled);

        // But a host drives it, so a run it started holds its instruments and opening the page lets
        // go of the idle ones rather than all of them.
        Assert.True(dialing.Driven);

        var standalone = new ServiceMode(token: null);
        Assert.False(standalone.Driven);
    }

    // ------------------------------------------------- what may be asked for

    [Fact]
    public async Task The_named_list_is_answered_and_everything_else_is_refused()
    {
        await using var host = await FakeHost.StartAsync();
        using var bench = new DialFactory(host.Address, Token);

        await Connected(host, bench);

        // What running a released procedure takes.
        Assert.Equal(200, (await host.AskAsync("GET", "/api/sessions")).Status);
        Assert.Equal(200, (await host.AskAsync("POST", "/api/sequence/declares", new SequenceRunRequest(Script, new Dictionary<string, string>()))).Status);
        Assert.Equal(200, (await host.AskAsync("POST", "/api/sequence/requirements", new SequenceRunRequest(Script, new Dictionary<string, string>()))).Status);

        // And nothing else. Connecting to an address is the one that matters most: a host at the far
        // end of this connection has no whitelist of its own between it and this factory's network.
        Assert.Equal(403, (await host.AskAsync("POST", "/api/sessions", new { })).Status);
        Assert.Equal(403, (await host.AskAsync("POST", "/api/scan", new { })).Status);
        Assert.Equal(403, (await host.AskAsync("POST", "/api/serial/scan", new { })).Status);
        Assert.Equal(403, (await host.AskAsync("PUT", "/api/ai", new { })).Status);
        Assert.Equal(403, (await host.AskAsync("GET", "/api/catalogs")).Status);
        Assert.Equal(403, (await host.AskAsync("GET", "/api/datasheets")).Status);

        // The right verb on a listed path, and not another.
        Assert.Equal(403, (await host.AskAsync("DELETE", "/api/sessions")).Status);

        var refused = JsonNode.Parse((await host.AskAsync("GET", "/api/catalogs")).Body!);
        Assert.Equal(DialOutCalls.RefusedMessage, refused!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task What_it_answers_over_the_connection_is_what_it_answers_over_its_api()
    {
        await using var host = await FakeHost.StartAsync();
        using var bench = new DialFactory(host.Address, Token);

        var client = await Connected(host, bench);
        var request = new SequenceRunRequest(Script, new Dictionary<string, string>());

        // The one that matters: a host reads a procedure over the API today and will read it over
        // the connection tomorrow, and the columns it stores must not depend on which.
        var overHttp = await (await client.PostAsJsonAsync("/api/sequence/declares", request)).Content.ReadAsStringAsync();
        var overDial = await host.AskAsync("POST", "/api/sequence/declares", request);

        Assert.Equal(200, overDial.Status);
        Assert.Equal(JsonNode.Parse(overHttp)!.ToJsonString(), JsonNode.Parse(overDial.Body!)!.ToJsonString());

        var declared = JsonSerializer.Deserialize<SequenceDeclarationDto>(overDial.Body!, Web)!;
        Assert.Equal(["SDM3065X"], declared.Models);
        Assert.Equal(["Frequency", "Vout"], declared.Columns);
        Assert.Equal("vset", Assert.Single(declared.Inputs).Name);

        var sessionsOverHttp = await client.GetStringAsync("/api/sessions");
        var sessionsOverDial = await host.AskAsync("GET", "/api/sessions");

        Assert.Equal(JsonNode.Parse(sessionsOverHttp)!.ToJsonString(), JsonNode.Parse(sessionsOverDial.Body!)!.ToJsonString());
    }

    [Fact]
    public async Task A_run_the_bench_never_heard_of_is_no_run_rather_than_an_error()
    {
        await using var host = await FakeHost.StartAsync();
        using var bench = new DialFactory(host.Address, Token);

        await Connected(host, bench);

        Assert.Equal(404, (await host.AskAsync("GET", "/api/runs/never-started")).Status);
        Assert.Equal(404, (await host.AskAsync("POST", "/api/runs/never-started/stop")).Status);
    }

    [Fact]
    public async Task A_measurement_is_bound_started_and_read_back_over_the_connection()
    {
        await using var host = await FakeHost.StartAsync();
        using var bench = new DialFactory(host.Address, Token);

        await Connected(host, bench);

        // A fake instrument opened on this bench's own service, as every other server test opens
        // one: no socket, and everything after this line is the real session and run code.
        var meter = new FakeInstrumentClient();
        meter.Responses["*IDN?"] = Meter;
        meter.Responses["MEASure:VOLTage:AC?"] = "+7.071E-01";

        var service = bench.Services.GetRequiredService<BenchService>();
        var session = await service.OpenAsync(BenchService.ParseAddress("192.168.1.7"), () => meter, default);

        // Exactly the four things a host does to run a released procedure, in order, and every one
        // of them down the connection the bench opened.
        var requirements = await host.AskAsync("POST", "/api/sequence/requirements", new SequenceRunRequest(Script, new Dictionary<string, string>()));
        Assert.Equal(200, requirements.Status);

        var bound = JsonSerializer.Deserialize<List<SequenceRequirement>>(requirements.Body!, Web)!;
        Assert.Equal(session.Id, Assert.Single(bound).SessionId);

        var started = await host.AskAsync("POST", "/api/runs/sequence",
            new SequenceRunRequest(Script, new Dictionary<string, string> { ["dmm"] = session.Id }, new Dictionary<string, string> { ["vset"] = "5" }));
        Assert.Equal(200, started.Status);

        var summary = JsonSerializer.Deserialize<RunSummary>(started.Body!, Web)!;
        Assert.False(summary.Failed, summary.Error);
        Assert.NotEqual("", summary.RunId);

        // And then the host follows it by asking again, which is what it does through a restart and
        // through a connection that dropped. The bench waits two seconds for a watcher before it
        // starts, because nothing here is a page that joined its group.
        RunRecordDto record = await FinishedAsync(host, summary.RunId);

        Assert.Equal("finished", record.Status);
        Assert.Equal(["Frequency", "Vout"], record.Columns);
        Assert.Equal([["100", "+7.071E-01"], ["200", "+7.071E-01"]], record.Rows.Select(r => r.Values));
    }

    /// <summary>
    /// A start under the id the host chose goes down the connection as it goes to the API, and so
    /// does the stop that closes a start the host gave up on. This connection is where a start's
    /// answer is likeliest to be lost — a line that goes with the answer in flight — so it is where
    /// the host most needs to know the run's id without it (since 2026-09-29).
    /// </summary>
    [Fact]
    public async Task A_start_under_the_hosts_id_and_a_stop_that_came_first_go_down_the_connection()
    {
        await using var host = await FakeHost.StartAsync();
        using var bench = new DialFactory(host.Address, Token);

        await Connected(host, bench);

        var request = new SequenceRunRequest(Sweep(3, "0.5"), new Dictionary<string, string>());
        string id = Guid.NewGuid().ToString("N");

        var started = await host.AskAsync("PUT", "/api/runs/" + id, request);
        Assert.Equal(200, started.Status);

        var summary = JsonSerializer.Deserialize<RunSummary>(started.Body!, Web)!;
        Assert.False(summary.Failed, summary.Error);
        Assert.Equal(id, summary.RunId);

        Assert.Equal("finished", (await FinishedAsync(host, id)).Status);

        // Stopped before it began, and refused when it comes.
        string stopped = Guid.NewGuid().ToString("N");
        Assert.Equal(404, (await host.AskAsync("POST", "/api/runs/" + stopped + "/stop")).Status);

        var late = JsonSerializer.Deserialize<RunSummary>((await host.AskAsync("PUT", "/api/runs/" + stopped, request)).Body!, Web)!;
        Assert.True(late.Failed);
        Assert.Equal(RunService.StoppedBeforeMessage, late.Error);
        Assert.Equal(404, (await host.AskAsync("GET", "/api/runs/" + stopped)).Status);
    }

    // ------------------------------------------------------- a record of a real size

    /// <summary>A sweep of literals, which needs no instrument and records as much as it is asked to.</summary>
    private static string Sweep(int rows, string reading)
        => $"COLUMNS Index, Reading\nFOR i = 1 TO {rows} STEP 1\n    RECORD $i, {reading}\nEND";

    /// <summary>Starts a run down the connection and hands back its id.</summary>
    private static async Task<string> StartedAsync(FakeHost host, string script)
    {
        var started = await host.AskAsync("POST", "/api/runs/sequence", new SequenceRunRequest(script, new Dictionary<string, string>()));
        Assert.Equal(200, started.Status);

        var summary = JsonSerializer.Deserialize<RunSummary>(started.Body!, Web)!;
        Assert.False(summary.Failed, summary.Error);

        return summary.RunId;
    }

    [Fact]
    public async Task A_record_too_long_for_one_message_crosses_in_parts()
    {
        await using var host = await FakeHost.StartAsync();
        using var bench = new DialFactory(host.Address, Token);

        await Connected(host, bench);

        // Four thousand rows of a hundred-character reading, and the line each RECORD prints beside
        // it: a million characters as a record, which is four parts.
        string reading = new('7', 100);
        string runId = await StartedAsync(host, Sweep(4000, reading));

        RunRecordDto record = await FinishedAsync(host, runId);

        Assert.Equal(4000, record.Rows.Count);
        Assert.Equal(["4000", reading], record.Rows[^1].Values);
        Assert.Equal(0, record.RowsDropped);

        // It came in several messages and was put back together whole — the read that found the run
        // finished among them.
        Assert.True(host.Dialed.Messages.Values.Max() >= 4);
    }

    [Fact]
    public async Task A_run_is_read_from_where_the_reader_left_off_the_same_over_the_connection_and_the_api()
    {
        await using var host = await FakeHost.StartAsync();
        using var bench = new DialFactory(host.Address, Token);

        var client = await Connected(host, bench);

        string runId = await StartedAsync(host, Sweep(10, "0.5"));
        RunRecordDto whole = await FinishedAsync(host, runId);

        Assert.Equal(0, whole.RowsFrom);
        Assert.Equal(10, whole.Rows.Count);

        // What a host following the run asks for: what it has not read yet, and nothing it has.
        var overDial = await host.AskAsync("GET", $"/api/runs/{runId}?outputFrom=4&rowsFrom=7");
        var tail = JsonSerializer.Deserialize<RunRecordDto>(overDial.Body!, Web)!;

        Assert.Equal(7, tail.RowsFrom);
        Assert.Equal(whole.Rows.Skip(7).Select(r => r.Values), tail.Rows.Select(r => r.Values));
        Assert.Equal(4, tail.OutputFrom);
        Assert.Equal(whole.Output.Skip(4).Select(l => l.Text), tail.Output.Select(l => l.Text));

        // The route answers the same question the same way.
        var overHttp = await client.GetStringAsync($"/api/runs/{runId}?outputFrom=4&rowsFrom=7");
        Assert.Equal(JsonNode.Parse(overHttp)!.ToJsonString(), JsonNode.Parse(overDial.Body!)!.ToJsonString());

        // Past the end is the end, said as where the nothing starts.
        var beyond = JsonSerializer.Deserialize<RunRecordDto>((await host.AskAsync("GET", $"/api/runs/{runId}?rowsFrom=99")).Body!, Web)!;
        Assert.Equal(10, beyond.RowsFrom);
        Assert.Empty(beyond.Rows);

        // And a question that is not a number is refused as the route refuses it.
        Assert.Equal(400, (await host.AskAsync("GET", $"/api/runs/{runId}?rowsFrom=many")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/runs/{runId}?rowsFrom=many")).StatusCode);
    }

    [Fact]
    public async Task A_run_that_records_more_than_its_record_keeps_says_how_many_it_did_not_keep()
    {
        await using var host = await FakeHost.StartAsync();

        // Four KiB of rows a run, where the server's own is a GiB: the same bound, reached in a test's time.
        using var bench = new DialFactory(host.Address, Token, mostBytes: 4096);

        var client = await Connected(host, bench);

        string runId = await StartedAsync(host, Sweep(500, "0.5"));
        RunRecordDto record = await FinishedAsync(host, runId);

        // The first rows kept, in order, and every one after them said rather than lost.
        Assert.InRange(record.RowsKept, 1, 499);
        Assert.Equal(500, record.RowsKept + record.RowsDropped);
        Assert.Equal(Enumerable.Range(1, record.RowsKept).Select(i => i.ToString()), record.Rows.Select(r => r.Values[0]));
        Assert.True(record.OutputDropped > 0);

        // Said over the API as well, to anything that reads it there.
        var overHttp = (await client.GetFromJsonAsync<RunRecordDto>($"/api/runs/{runId}?rowsFrom={record.RowsKept}", Web))!;
        Assert.Empty(overHttp.Rows);
        Assert.Equal(record.RowsDropped, overHttp.RowsDropped);
    }

    [Fact]
    public async Task A_record_longer_than_one_read_is_read_a_page_at_a_time_to_the_end()
    {
        await using var host = await FakeHost.StartAsync();
        using var bench = new DialFactory(host.Address, Token);

        await Connected(host, bench);

        // More rows than one read answers: twice as many as a whole record held while it was in memory.
        int rows = RunService.MostPerRead * 2 + 345;
        string runId = await StartedAsync(host, Sweep(rows, "0.5"));
        RunRecordDto first = await FinishedAsync(host, runId);

        // One read is a page, and says how much there is.
        Assert.Equal(RunService.MostPerRead, first.Rows.Count);
        Assert.Equal(rows, first.RowsKept);
        Assert.Equal(0, first.RowsDropped);
        Assert.True(first.OutputKept > first.Output.Count);

        // Read on from where each page ended, as a host does, until both lists reach what the record holds.
        var read = new List<RecordedRow>(first.Rows);
        int lines = first.Output.Count;

        for (int pages = 1; read.Count < first.RowsKept || lines < first.OutputKept; pages++)
        {
            Assert.True(pages < 10, "The reads never reached the end.");

            var next = JsonSerializer.Deserialize<RunRecordDto>((await host.AskAsync("GET", $"/api/runs/{runId}?outputFrom={lines}&rowsFrom={read.Count}")).Body!, Web)!;

            Assert.Equal(read.Count, next.RowsFrom);
            Assert.Equal(lines, next.OutputFrom);

            read.AddRange(next.Rows);
            lines += next.Output.Count;
        }

        Assert.Equal(Enumerable.Range(1, rows).Select(i => i.ToString()), read.Select(r => r.Values[0]));
        Assert.Equal(first.OutputKept, lines);
    }

    [Fact]
    public void An_answer_too_long_for_one_message_is_cut_into_numbered_parts_that_rejoin_whole()
    {
        // One that fits is itself, and one with no body at all is too.
        var one = Assert.Single(DialOutService.Parts(new BenchSaid("a1", 200, "short"), length: 8));
        Assert.Equal((0, 1, "short"), (one.Part, one.Parts, one.Body));
        Assert.Null(Assert.Single(DialOutService.Parts(new BenchSaid("a2", 404, null), length: 8)).Body);

        // Twenty characters in eights: eight, eight and four, numbered, each with the id and status.
        string body = "abcdefghijklmnopqrst";
        var parts = DialOutService.Parts(new BenchSaid("a3", 200, body), length: 8).ToList();

        Assert.Equal([0, 1, 2], parts.Select(p => p.Part));
        Assert.All(parts, p => Assert.Equal(("a3", 200, 3), (p.Id, p.Status, p.Parts)));
        Assert.Equal(body, string.Concat(parts.Select(p => p.Body)));

        // A character outside the basic plane is two chars, and a cut between them would send two
        // halves that are each not a character: the cut moves back one instead.
        string paired = "abcdefg" + char.ConvertFromUtf32(0x1F50C) + "hij";
        var around = DialOutService.Parts(new BenchSaid("a4", 200, paired), length: 8).ToList();

        Assert.Equal(paired, string.Concat(around.Select(p => p.Body)));
        Assert.All(around, p => Assert.False(char.IsHighSurrogate(p.Body![^1])));
    }

    /// <summary>Asks for the run until it has ended, the way a host following one does.</summary>
    private static async Task<RunRecordDto> FinishedAsync(FakeHost host, string runId)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < until)
        {
            var said = await host.AskAsync("GET", "/api/runs/" + runId);
            Assert.Equal(200, said.Status);

            var record = JsonSerializer.Deserialize<RunRecordDto>(said.Body!, Web)!;
            if (record.Status != "running") return record;

            await Task.Delay(200);
        }

        throw new TimeoutException("The run never ended.");
    }

    [Fact]
    public async Task A_body_the_bench_cannot_read_is_a_bad_request_and_not_a_dropped_connection()
    {
        await using var host = await FakeHost.StartAsync();
        using var bench = new DialFactory(host.Address, Token);

        await Connected(host, bench);

        Assert.Equal(400, (await host.AskAsync("POST", "/api/sequence/declares", "not a request at all")).Status);

        // And the connection is still good afterwards, which is the half worth checking.
        Assert.Equal(200, (await host.AskAsync("GET", "/api/sessions")).Status);
    }

    [Fact]
    public void The_envelope_spells_its_wire_names_out()
    {
        // The other half of this contract is in another repository and cannot be referenced from
        // here, so nothing but a walk would catch the two parting. These are the names the host
        // writes with JsonPropertyName; if this test is changed, that side has to change with it in
        // the same breath, and a bench already in a building has to be updated before its host is.
        string ask = JsonSerializer.Serialize(new BenchAsk("a1", "GET", "/api/sessions", null));
        string said = JsonSerializer.Serialize(new BenchSaid("a1", 200, "{}"));

        foreach (string name in new[] { "\"id\"", "\"method\"", "\"path\"", "\"body\"" })
            Assert.Contains(name, ask);

        foreach (string name in new[] { "\"id\"", "\"status\"", "\"body\"", "\"part\"", "\"parts\"" })
            Assert.Contains(name, said);

        // And nothing capitalized, which is what a default naming policy would hand over instead.
        Assert.DoesNotContain("\"Id\"", ask);
        Assert.DoesNotContain("\"Status\"", said);
    }

    [Fact]
    public async Task A_call_that_fails_inside_the_bench_is_a_500_and_not_a_silence()
    {
        await using var host = await FakeHost.StartAsync();
        using var bench = new DialFactory(host.Address, Token);

        await Connected(host, bench);

        // Readable JSON of the shape the path takes, with nothing where the script goes. The body
        // parses, so this is past the 400 above and what fails is the call itself. It has to come
        // back as an answer: an exception left to escape is sent nowhere, and the host would wait
        // out its own timeout and report a bench that did not answer — a network story for a body
        // this bench refused. Over HTTP the framework writes this 500; over a connection nothing
        // does unless this server does.
        Assert.Equal(500, (await host.AskAsync("POST", "/api/sequence/declares", new { script = (string?)null })).Status);
        Assert.Equal(500, (await host.AskAsync("POST", "/api/sequence/requirements", new { script = (string?)null })).Status);

        // And the connection is still good afterwards, as it is after a 400.
        Assert.Equal(200, (await host.AskAsync("GET", "/api/sessions")).Status);
    }

    [Fact]
    public async Task The_bench_presents_the_token_it_was_given()
    {
        await using var host = await FakeHost.StartAsync();
        using var bench = new DialFactory(host.Address, Token);

        await Connected(host, bench);

        Assert.Equal(Token, host.Dialed.Token);
    }

    [Fact]
    public async Task Dialing_out_does_not_put_a_bearer_on_this_servers_own_api()
    {
        await using var host = await FakeHost.StartAsync();
        using var bench = new DialFactory(host.Address, Token);

        var client = await Connected(host, bench);

        // The page is the console for whoever is standing at this bench, and criterion 34 of the
        // host's phase says it will not be framed — so guarding it here would leave nobody able to
        // connect an instrument at all. LEC_SERVICE_TOKEN still guards it when it is set.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/catalogs")).StatusCode);
    }

    /// <summary>Starts the bench, waits for it to dial in, and hands back a client on its own API.</summary>
    private static async Task<HttpClient> Connected(FakeHost host, DialFactory bench)
    {
        var client = bench.CreateClient();

        await host.Dialed.Connected.Task.WaitAsync(TimeSpan.FromSeconds(30));

        return client;
    }
}
