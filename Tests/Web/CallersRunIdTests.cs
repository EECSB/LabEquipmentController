using System.Net;
using System.Net.Http.Json;
using LabEquipmentController.Web.Bench;
using LabEquipmentController.Web.Client.Contracts;
using LabEquipmentController.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabEquipmentController.Tests;

/// <summary>
/// A run started under an id its caller chose, <c>PUT /api/runs/{id}</c>: what lets a host whose
/// start went unanswered still read the run and stop it.
/// </summary>
/// <remarks>
/// Built on 2026-09-29 for Treeality's Instruments plugin. Until then a run's id was made here and
/// sent back in the start's answer and nowhere else, so a host that never heard that answer could
/// neither follow the run nor stop it, while the run held every instrument it binds against any
/// other run until its deadline. <c>FakeInstrumentClient</c> stands in, as everywhere else.
/// </remarks>
public class CallersRunIdTests
{
    private const string Meter = "Siglent Technologies,SDM3065X,SDM36HCD801207,3.02.01.13";

    private const string Script = """
        DEVICE dmm : SDM3065X
        COLUMNS Vout
        dmm: MEASure:VOLTage:DC? -> v
        RECORD $v
        """;

    /// <summary>A run that needs no instrument, for the tests that go through the API.</summary>
    private static readonly SequenceRunRequest Literal =
        new("COLUMNS V\nRECORD 1", new Dictionary<string, string>());

    private static (BenchService Bench, RunService Runs) Server()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        var hub = services.BuildServiceProvider().GetRequiredService<IHubContext<BenchHub>>();
        var service = new ServiceMode("bench-7f3a-token");
        var bench = new BenchService(NullLogger<BenchService>.Instance, hub, service);
        var runs = new RunService(bench, hub, NullLogger<RunService>.Instance, service);
        return (bench, runs);
    }

    private static FakeInstrumentClient Instrument()
    {
        var fake = new FakeInstrumentClient();
        fake.Responses["*IDN?"] = Meter;
        fake.Responses["MEASure:VOLTage:DC?"] = "+1.000E+00";
        return fake;
    }

    private static async Task<string> ConnectAsync(BenchService bench, FakeInstrumentClient instrument)
        => (await bench.OpenAsync(BenchService.ParseAddress("192.168.1.7"), () => instrument, default)).Id;

    private static SequenceRunRequest Request(string session)
        => new(Script, new Dictionary<string, string> { ["dmm"] = session });

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Reads the run until it has ended, as a host following it does.</summary>
    private static async Task<RunRecordDto> EndedAsync(RunService runs, string runId)
    {
        RunRecordDto? record = null;
        for (int i = 0; i < 400 && record?.EndedAt is null; i++)
        {
            await Task.Delay(25);
            record = runs.Record(runId);
        }

        Assert.NotNull(record);
        Assert.NotNull(record.EndedAt);
        return record;
    }

    [Fact]
    public async Task A_run_started_under_the_callers_id_is_read_and_stopped_by_it()
    {
        var (bench, runs) = Server();
        await using var _ = bench;
        string session = await ConnectAsync(bench, Instrument());
        string id = NewId();

        var started = runs.StartSequence(Request(session), id);

        Assert.False(started.Failed, started.Error);
        Assert.Equal(id, started.RunId);
        Assert.Equal(["Vout"], started.Columns);
        Assert.Equal("running", runs.Record(id)!.Status);

        // Stopped by the id the caller already had, which is the point: nothing had to come back
        // from the start for the caller to be able to do this.
        Assert.True(runs.Stop(id));
        Assert.Equal("stopped", (await EndedAsync(runs, id)).Status);
    }

    [Fact]
    public async Task The_same_start_sent_again_begins_nothing_and_answers_the_run_it_began()
    {
        var (bench, runs) = Server();
        await using var _ = bench;
        var meter = Instrument();
        string session = await ConnectAsync(bench, meter);
        string id = NewId();

        var first = runs.StartSequence(Request(session), id);
        var again = runs.StartSequence(Request(session), id);

        // Not refused as a second run on the instrument the first one holds: it is the first one.
        Assert.False(again.Failed, again.Error);
        Assert.Equal(id, again.RunId);
        Assert.Equal(first.Columns, again.Columns);
        Assert.Single(runs.Records(), r => r.RunId == id);
        Assert.Single(runs.Active);

        runs.Listening(id);   // what the hub does once a page joins; without it the run waits two seconds
        var ended = await EndedAsync(runs, id);

        Assert.Equal("finished", ended.Status);
        Assert.Single(ended.Rows);
        Assert.Single(meter.Log, line => line == "QUERY:MEASure:VOLTage:DC?");

        // Sent once more after the run has ended, and stopped first: still the run it began, still
        // nothing begun. A stop that finds the run kept marks nothing.
        Assert.False(runs.Stop(id));

        var late = runs.StartSequence(Request(session), id);

        Assert.False(late.Failed, late.Error);
        Assert.Equal(id, late.RunId);
        Assert.Empty(runs.Active);
        Assert.Single(meter.Log, line => line == "QUERY:MEASure:VOLTage:DC?");
    }

    [Fact]
    public async Task A_stop_that_arrives_before_its_start_refuses_the_start()
    {
        var (bench, runs) = Server();
        await using var _ = bench;
        var meter = Instrument();
        string session = await ConnectAsync(bench, meter);
        int before = meter.Log.Count;
        string id = NewId();

        // Nothing has the id, and the caller is told so exactly as before.
        Assert.False(runs.Stop(id));

        // Then the start arrives: late, behind a slow answer on a connection or a request this
        // server had not got round to, after its host had stopped waiting for it.
        var late = runs.StartSequence(Request(session), id);

        Assert.True(late.Failed);
        Assert.Equal(RunService.StoppedBeforeMessage, late.Error);
        Assert.Null(runs.Record(id));
        Assert.Empty(runs.Active);
        Assert.Equal(before, meter.Log.Count);

        // And the instrument was never held: the next run takes it.
        var next = runs.StartSequence(Request(session), NewId());
        Assert.False(next.Failed, next.Error);
        Assert.True(runs.Stop(next.RunId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("run-1")]
    [InlineData("../../runs/0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("{0123456789abcdef0123456789abcdef}")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("00000000000000000000000000000000")]
    public async Task An_id_a_caller_could_not_have_chosen_is_refused_before_anything_is_touched(string id)
    {
        var (bench, runs) = Server();
        await using var _ = bench;
        var meter = Instrument();
        string session = await ConnectAsync(bench, meter);
        int before = meter.Log.Count;

        // It names the run's files on this server's disk and its group on the hub, so only the one
        // shape this server writes its own ids in is taken.
        var refused = runs.StartSequence(Request(session), id);

        Assert.True(refused.Failed);
        Assert.Equal(RunService.RunIdMessage, refused.Error);
        Assert.Empty(runs.Records());
        Assert.Empty(runs.Active);
        Assert.Equal(before, meter.Log.Count);
        Assert.False(RunService.IsCallersId(id));
    }

    [Fact]
    public async Task Over_the_api_a_start_under_the_callers_id_is_a_put_on_the_run()
    {
        using var factory = new WebFactory();
        var client = factory.CreateClient();
        string id = NewId();

        var put = await client.PutAsJsonAsync("/api/runs/" + id, Literal);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var summary = (await put.Content.ReadFromJsonAsync<RunSummary>())!;
        Assert.False(summary.Failed, summary.Error);
        Assert.Equal(id, summary.RunId);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/runs/" + id)).StatusCode);

        // A stop first, over the API, refuses a start under the id that arrives after it.
        string stopped = NewId();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/runs/" + stopped + "/stop", null)).StatusCode);

        var refused = (await (await client.PutAsJsonAsync("/api/runs/" + stopped, Literal)).Content.ReadFromJsonAsync<RunSummary>())!;
        Assert.True(refused.Failed);
        Assert.Equal(RunService.StoppedBeforeMessage, refused.Error);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/runs/" + stopped)).StatusCode);
    }

    /// <summary>
    /// Until 2026-09-29 the page shell answered every path no route did, the API's included, at 200.
    /// A server older than the PUT above answers it with the page, which a host has to tell from a
    /// start; this one answers a route it does not have, or a method a route does not take, with a
    /// 404, so the next route added is refused by this one plainly.
    /// </summary>
    [Fact]
    public async Task An_api_call_no_route_answers_is_a_404_and_not_the_page()
    {
        using var factory = new WebFactory();
        var client = factory.CreateClient();
        string id = NewId();

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/api/no-such-route"),
            (HttpMethod.Delete, "/api/runs/" + id),
            (HttpMethod.Post, "/api/runs/" + id),
            (HttpMethod.Put, "/api/sessions"),
            (HttpMethod.Get, "/hub/no-such-hub"),
        })
        {
            var answer = await client.SendAsync(new HttpRequestMessage(method, path));

            Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
            Assert.NotEqual("text/html", answer.Content.Headers.ContentType?.MediaType);
        }

        // Anywhere else is still the page, which routes itself in the browser.
        var page = await client.GetAsync("/sequence");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
    }
}
