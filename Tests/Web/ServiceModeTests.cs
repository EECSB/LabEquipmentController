using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LabEquipmentController.Web;
using LabEquipmentController.Web.Bench;
using LabEquipmentController.Web.Client.Contracts;
using LabEquipmentController.Web.Hubs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabEquipmentController.Tests;

/// <summary>
/// The server started as a host's bench: a token on the API and the hub, and, independently, the
/// app below a path. Its data directory is its own, as <see cref="WebFactory"/>'s is.
/// </summary>
public sealed class ServiceFactory(string token, string? pathBase = null) : WebApplicationFactory<WebEntryPoint>
{
    private readonly string _data =
        Path.Combine(Path.GetTempPath(), "lec-service-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["LEC_DATA"] = _data,
                [ServiceMode.TokenVariable] = token,
                [ServiceMode.PathBaseVariable] = pathBase,
            }));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true); }
        catch { /* a temp folder that outlives the run is not worth failing a test over */ }
    }
}

/// <summary>
/// Service mode (<see cref="ServiceMode"/>): what a host that runs this server as its bench is
/// promised, and what the standalone app keeps doing exactly as before without the token.
/// </summary>
/// <remarks>
/// Treeality's Instruments plugin is the host this was built for; its requirements list the
/// changes as prerequisites 1 to 6 and 11, and each of them is a test here. No instrument is
/// involved: <c>FakeInstrumentClient</c> stands in, as it does for every other server test.
/// </remarks>
public class ServiceModeTests
{
    private const string Token = "bench-7f3a-token";
    private const string Meter = "Siglent Technologies,SDM3065X,SDM36HCD801207,3.02.01.13";
    private const string Generator = "Siglent Technologies,SDG2042X,SDG2XCAD1R0001,2.01.01.35R3B2";

    // ------------------------------------------------------------ the token

    [Fact]
    public async Task Without_a_token_configured_the_api_is_open_as_it_always_was()
    {
        using var factory = new WebFactory();
        var client = factory.CreateClient();

        var reply = await client.GetAsync("/api/catalogs");

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
    }

    [Fact]
    public async Task With_a_token_configured_the_api_takes_it_as_a_bearer_and_refuses_everything_else()
    {
        using var factory = new ServiceFactory(Token);
        var client = factory.CreateClient();

        var bare = await client.GetAsync("/api/catalogs");
        Assert.Equal(HttpStatusCode.Unauthorized, bare.StatusCode);
        Assert.Equal("Bearer", bare.Headers.WwwAuthenticate.ToString());
        Assert.Equal(ServiceMode.Challenge, await bare.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");
        var wrong = await client.GetAsync("/api/catalogs");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var right = await client.GetAsync("/api/catalogs");
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);

        // The hub takes the same token — as a header, or in the query the way SignalR's own client
        // carries one on a WebSocket, where there are no headers to set.
        client.DefaultRequestHeaders.Authorization = null;
        var hubBare = await client.PostAsync("/hub/bench/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, hubBare.StatusCode);

        var hubQuery = await client.PostAsync($"/hub/bench/negotiate?negotiateVersion=1&access_token={Token}", null);
        Assert.Equal(HttpStatusCode.OK, hubQuery.StatusCode);

        // The page itself stays open: it is the host's frame that loads it, and it can do nothing
        // without the API.
        var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<base href=\"/\" />", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public void A_token_is_matched_whole_and_in_either_place_it_may_travel()
    {
        var mode = new ServiceMode(" " + Token + " ");
        Assert.True(mode.Enabled);
        Assert.Equal(Token, mode.Token);

        // Nothing configured at all is the standalone app, whichever way nothing is spelled.
        Assert.False(new ServiceMode((string?)null).Enabled);
        Assert.False(new ServiceMode("   ").Enabled);
        Assert.Equal("", new ServiceMode((string?)null).PathBase);
    }

    // ------------------------------------------------------------ the path

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("bench", "/bench")]
    [InlineData("/bench", "/bench")]
    [InlineData("/bench/", "/bench")]
    [InlineData(" /instruments/bench/ ", "/instruments/bench")]
    public void A_path_base_is_normalized_however_it_was_written(string? raw, string expected)
        => Assert.Equal(expected, ServiceMode.NormalizePathBase(raw));

    [Fact]
    public async Task Below_a_path_the_page_says_where_it_lives_and_the_api_answers_there()
    {
        // The path alone: a host may proxy the standalone app at a path with no token at all.
        using var factory = new ServiceFactory("", "/instruments/bench");
        var client = factory.CreateClient();

        var page = await client.GetAsync("/instruments/bench/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        string html = await page.Content.ReadAsStringAsync();
        Assert.Contains("<base href=\"/instruments/bench/\" />", html);
        Assert.DoesNotContain("<base href=\"/\" />", html);

        var api = await client.GetAsync("/instruments/bench/api/catalogs");
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);

        // And it is the API that answered. The status code alone cannot tell: the page shell
        // answers 200 to anything routing did not match, so an API call that fell through to it
        // looks exactly like a success and arrives as HTML.
        Assert.Equal("application/json", api.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Below_a_path_an_authorized_api_call_reaches_the_api_rather_than_the_page()
    {
        // This is the one the release of 2026-09-20 shipped broken, and it is worth saying how it
        // hid. Without an explicit UseRouting, WebApplication inserts routing at the START of the
        // pipeline — before UsePathBase — so the endpoint is chosen from the path as it arrived,
        // /instruments/bench/api/… , which no API route matches and the fallback does. The token
        // middleware sits after the strip, so it kept working perfectly: 401 without a token, and
        // with one, the page's own HTML at 200. Treeality's Instruments plugin found it the next
        // day, the first time anything served this server below a path for real.
        using var factory = new ServiceFactory(Token, "/instruments/bench");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var api = await client.GetAsync("/instruments/bench/api/catalogs");

        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        Assert.Equal("application/json", api.Content.Headers.ContentType?.MediaType);

        string body = await api.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<!DOCTYPE html>", body);

        // The hub is below the path too, and refusing it for the right reason — a 401 rather than
        // a page — is the same question asked of the other half.
        var hub = await client.GetAsync("/instruments/bench/hub/bench");
        Assert.NotEqual("text/html", hub.Content.Headers.ContentType?.MediaType);
    }

    // ------------------------------------------------------------ the bench

    private static (BenchService Bench, RunService Runs) Server(ServiceMode? service)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        var hub = services.BuildServiceProvider().GetRequiredService<IHubContext<BenchHub>>();
        var bench = new BenchService(NullLogger<BenchService>.Instance, hub, service);
        var runs = new RunService(bench, hub, NullLogger<RunService>.Instance, service);
        return (bench, runs);
    }

    private static FakeInstrumentClient Instrument(string identity)
    {
        var fake = new FakeInstrumentClient();
        fake.Responses["*IDN?"] = identity;
        return fake;
    }

    private static async Task<string> ConnectAsync(BenchService bench, string address, FakeInstrumentClient instrument)
        => (await bench.OpenAsync(BenchService.ParseAddress(address), () => instrument, default)).Id;

    [Fact]
    public async Task Opening_the_page_leaves_an_instrument_a_run_holds_and_lets_the_idle_ones_go()
    {
        var (bench, _) = Server(new ServiceMode(Token));
        await using var _ = bench;
        string held = await ConnectAsync(bench, "192.168.1.7", Instrument(Meter));
        string idle = await ConnectAsync(bench, "192.168.1.5", Instrument(Generator));

        using (bench.Drive(held))
        {
            await bench.PageOpenedAsync(resumed: false);

            Assert.NotNull(bench.Raw(held));    // the measurement goes on
            Assert.Null(bench.Raw(idle));       // nobody was using this one
        }

        // Given back, it is an idle instrument like any other, and the next opening takes it.
        await bench.PageOpenedAsync(resumed: false);
        Assert.Null(bench.Raw(held));
    }

    [Fact]
    public async Task Without_the_token_opening_the_page_still_closes_the_whole_bench_as_the_desktop_does()
    {
        var (bench, _) = Server(null);
        await using var _ = bench;
        string held = await ConnectAsync(bench, "192.168.1.7", Instrument(Meter));

        using (bench.Drive(held))
            await bench.PageOpenedAsync(resumed: false);

        Assert.Null(bench.Raw(held));
    }

    [Fact]
    public async Task A_held_instrument_takes_nothing_else_in_service_mode_and_answers_again_when_let_go()
    {
        var (bench, _) = Server(new ServiceMode(Token));
        await using var _ = bench;
        var meter = Instrument(Meter);
        meter.Responses["MEASure:VOLTage:DC?"] = "+1.000E+00";
        string id = await ConnectAsync(bench, "192.168.1.7", meter);
        int before = meter.Log.Count;

        using (bench.Drive(id))
        {
            var reply = await bench.SendAsync(id, "*IDN?", default);
            Assert.Equal(ServiceMode.HeldMessage, reply.Error);
            Assert.True(reply.IsQuery);

            var readings = new List<ReadingDto>();
            await foreach (var r in bench.ReadoutAsync(id, "MEASure:VOLTage:DC?", 100, default)) readings.Add(r);
            Assert.Equal(ServiceMode.HeldMessage, Assert.Single(readings).Error);

            Assert.Equal(ServiceMode.HeldMessage, (await bench.WaveformAsync(id, [1], default)).Error);
            Assert.Equal(ServiceMode.HeldMessage, (await bench.ScreenshotAsync(id, default)).Error);
            Assert.False((await bench.DiscoverAsync(id, default)).Success);

            // Nothing reached the instrument.
            Assert.Equal(before, meter.Log.Count);
        }

        var answered = await bench.SendAsync(id, "*IDN?", default);
        Assert.Null(answered.Error);
        Assert.Equal(Meter, answered.Reply);
    }

    [Fact]
    public async Task Without_the_token_a_held_instrument_still_answers_the_console_which_locks_itself()
    {
        var (bench, _) = Server(null);
        await using var _ = bench;
        string id = await ConnectAsync(bench, "192.168.1.7", Instrument(Meter));

        using (bench.Drive(id))
        {
            var reply = await bench.SendAsync(id, "*IDN?", default);
            Assert.Null(reply.Error);
        }
    }

    [Fact]
    public async Task A_run_on_a_held_instrument_is_refused_in_service_mode()
    {
        var (bench, runs) = Server(new ServiceMode(Token));
        await using var _ = bench;
        string id = await ConnectAsync(bench, "192.168.1.7", Instrument(Meter));

        using (bench.Drive(id))
        {
            var script = runs.StartScript(new ScriptRunRequest(id, "*IDN?"));
            Assert.True(script.Failed);
            Assert.Equal(ServiceMode.HeldMessage, script.Error);

            var sequence = runs.StartSequence(new SequenceRunRequest(
                "DEVICE dmm : SDM3065X\ndmm: *IDN?", new Dictionary<string, string> { ["dmm"] = id }));
            Assert.True(sequence.Failed);
            Assert.Contains("'dmm' is bound to an instrument a run holds", sequence.Error);
        }
    }

    // ------------------------------------------------------------- the record

    [Fact]
    public async Task A_run_is_readable_after_it_ends_with_everything_it_said_and_recorded()
    {
        var (bench, runs) = Server(new ServiceMode(Token));
        await using var _ = bench;
        var meter = Instrument(Meter);
        meter.Responses["MEASure:VOLTage:DC?"] = "+1.000E+00";
        string id = await ConnectAsync(bench, "192.168.1.7", meter);

        var started = runs.StartScript(new ScriptRunRequest(id, "COLUMNS V\nMEASure:VOLTage:DC? -> v\nRECORD $v"));
        Assert.False(started.Failed, started.Error);

        // Running from the moment it is started, and readable as such.
        var running = runs.Record(started.RunId);
        Assert.NotNull(running);
        Assert.Equal("running", running.Status);
        Assert.Equal("script", running.Kind);
        Assert.Equal(["V"], running.Columns);

        runs.Listening(started.RunId);   // what the hub does once a page joins; without it the run waits two seconds

        RunRecordDto? ended = null;
        for (int i = 0; i < 200 && ended?.EndedAt is null; i++)
        {
            await Task.Delay(25);
            ended = runs.Record(started.RunId);
        }

        Assert.NotNull(ended);
        Assert.Equal("finished", ended.Status);
        Assert.NotNull(ended.EndedAt);
        Assert.Null(ended.Error);
        Assert.Equal(["+1.000E+00"], Assert.Single(ended.Rows).Values);
        Assert.NotEmpty(ended.Output);

        // Listed, newest first, and gone from the list of what is active.
        Assert.Contains(runs.Records(), r => r.RunId == started.RunId);
        Assert.DoesNotContain(started.RunId, runs.Active);
    }

    [Fact]
    public async Task A_run_nobody_started_is_not_there_to_read()
    {
        using var factory = new WebFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/runs/no-such-run")).StatusCode);

        var all = await client.GetFromJsonAsync<List<RunRecordDto>>("/api/runs");
        Assert.NotNull(all);
        Assert.Empty(all);
    }

    // -------------------------------------------------------------- the AI

    /// <summary>A provider that answers a script and keeps what it was given to answer with.</summary>
    private sealed class SpyProvider : IAiClient
    {
        public AiConnection? Connection { get; private set; }
        public string? ApiKey { get; private set; }

        public Task<string> CompleteAsync(AiConnection connection, string apiKey, string instruction,
                                          AiPayload payload, JsonNode schema, CancellationToken ct = default)
        {
            Connection = connection;
            ApiKey = apiKey;
            return Task.FromResult("""{"script": "*IDN?"}""");
        }
    }

    private static (AiService Ai, SpyProvider Provider, BenchService Bench) Ai(ServiceMode? service)
    {
        var (bench, _) = Server(service);
        var provider = new SpyProvider();
        string data = Path.Combine(Path.GetTempPath(), "lec-service-ai-" + Guid.NewGuid().ToString("N"));
        var settings = new AiSettingsStore(
            new AiOptions { ApiKey = "sk-the-servers-own" },
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["LEC_DATA"] = data })
                .Build(),
            NullLogger<AiSettingsStore>.Instance);
        var ai = new AiService(settings, bench, provider, NullLogger<AiService>.Instance, service);
        return (ai, provider, bench);
    }

    [Fact]
    public async Task In_service_mode_the_ai_spends_the_connection_the_host_sent_and_none_of_its_own()
    {
        var (ai, provider, bench) = Ai(new ServiceMode(Token));
        await using var _ = bench;
        string id = await ConnectAsync(bench, "192.168.1.7", Instrument(Meter));

        // The server has a key of its own configured, and says nothing of it: connections come
        // from the host.
        var status = ai.Status();
        Assert.True(status.HostConnections);
        Assert.False(status.Configured);
        Assert.Equal(ServiceMode.HostConnectionsMessage, status.Reason);
        Assert.Empty(status.Connections);

        var host = new HostAiConnection("Anthropic", "https://api.anthropic.com", "claude-sonnet-5", "sk-the-persons", 120, null, "High");
        var reply = await ai.WriteScriptAsync(new AiScriptRequest("read the meter", [id], false, null, null, Host: host), default);

        Assert.Null(reply.Error);
        Assert.Equal("*IDN?", reply.Script);
        Assert.Equal("sk-the-persons", provider.ApiKey);
        Assert.Equal(AiProvider.Anthropic, provider.Connection!.Provider);
        Assert.Equal("https://api.anthropic.com", provider.Connection.BaseUrl);
        Assert.Equal("claude-sonnet-5", provider.Connection.Model);
        Assert.Equal(120, provider.Connection.TimeoutSeconds);
        Assert.Equal(AiEffort.High, provider.Connection.Effort);

        // Nothing of it stayed: the settings box has nothing to set, and refuses to.
        Assert.Equal(ServiceMode.HostConnectionsMessage,
            ai.Apply(new AiSettingsUpdate("Anthropic", "https://api.anthropic.com", "m", null, 300, "sk-x")).Error);
        Assert.Equal(ServiceMode.HostConnectionsMessage, ai.AddConnection("Gemini").Error);
        Assert.False(ai.Status().Configured);
    }

    [Fact]
    public async Task In_service_mode_a_request_without_the_hosts_connection_runs_on_nothing()
    {
        var (ai, provider, bench) = Ai(new ServiceMode(Token));
        await using var _ = bench;
        string id = await ConnectAsync(bench, "192.168.1.7", Instrument(Meter));

        var reply = await ai.WriteScriptAsync(new AiScriptRequest("read the meter", [id], false, null, null), default);

        Assert.Equal(ServiceMode.NoHostConnectionMessage, reply.Error);
        Assert.Null(provider.ApiKey);   // the server's own key was not spent instead

        var unknown = new HostAiConnection("Copilot", "", "", "sk-x");
        var refused = await ai.WriteScriptAsync(new AiScriptRequest("read the meter", [id], false, null, null, Host: unknown), default);
        Assert.Contains("is not a provider this bench knows", refused.Error);

        var keyless = new HostAiConnection("Gemini", "", "", "");
        var noKey = await ai.WriteScriptAsync(new AiScriptRequest("read the meter", [id], false, null, null, Host: keyless), default);
        Assert.Equal("The host sent an AI connection with no key.", noKey.Error);
    }

    [Fact]
    public async Task Without_the_token_a_connection_sent_with_a_request_is_refused_and_the_servers_own_is_used()
    {
        var (ai, provider, bench) = Ai(null);
        await using var _ = bench;
        string id = await ConnectAsync(bench, "192.168.1.7", Instrument(Meter));

        var smuggled = new HostAiConnection("Gemini", "", "", "sk-somebody-elses");
        var refused = await ai.WriteScriptAsync(new AiScriptRequest("read the meter", [id], false, null, null, Host: smuggled), default);
        Assert.Equal(ServiceMode.NotAServiceMessage, refused.Error);
        Assert.Null(provider.ApiKey);

        var own = await ai.WriteScriptAsync(new AiScriptRequest("read the meter", [id], false, null, null), default);
        Assert.Null(own.Error);
        Assert.Equal("sk-the-servers-own", provider.ApiKey);
        Assert.False(ai.Status().HostConnections);
    }
}
