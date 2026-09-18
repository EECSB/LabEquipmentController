using System.Net;
using System.Net.Http.Json;
using LabEquipmentController.Web.Bench;
using LabEquipmentController.Web.Client.Contracts;
using LabEquipmentController.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using LabEquipmentController.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace LabEquipmentController.Tests;

/// <summary>
/// The server under test, with its data directory pointed at a folder of its own.
/// </summary>
/// <remarks>
/// Without this the AI settings tests read whatever <c>ai.json</c> the machine running them
/// happens to have under AppData — so "the AI is off when no key is configured" would pass on
/// a build agent and fail on the developer's own bench PC, which has a key saved. A test that
/// depends on the tester is not a test.
///
/// One directory per factory, and xUnit builds one factory per test class, so a class that
/// writes settings cannot disturb a class that asserts the defaults.
/// </remarks>
public sealed class WebFactory : WebApplicationFactory<WebEntryPoint>
{
    private readonly string _data =
        Path.Combine(Path.GetTempPath(), "lec-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["LEC_DATA"] = _data }));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true); }
        catch { /* a temp folder that outlives the run is not worth failing a test over */ }
    }
}

/// <summary>
/// The web server, started in memory and asked real questions.
/// </summary>
/// <remarks>
/// No instrument is involved: everything here is either a catalog lookup, address parsing,
/// or an error path. That is deliberate — these are the parts that have to work before a
/// socket is worth opening, and they are the parts that break silently. The endpoints that
/// need hardware are exercised on the bench, not here.
/// </remarks>
public class WebApiTests : IClassFixture<WebFactory>
{
    private readonly WebFactory _factory;

    public WebApiTests(WebFactory factory) => _factory = factory;

    [Fact]
    public async Task The_catalog_list_carries_every_family_that_has_one()
    {
        var client = _factory.CreateClient();
        var catalogs = await client.GetFromJsonAsync<List<CatalogSummary>>("/api/catalogs");

        Assert.NotNull(catalogs);
        // The same 36 the desktop app and the CLI serve — the catalogs are embedded in Core,
        // so a web server that reports fewer has lost its resources somewhere in packaging.
        //
        // The totals are deliberate tripwires, not incidental facts. Nothing else notices a
        // catalog quietly losing entries, so these have to be edited by hand and the edit has
        // to be justified. 23,174 became 23,163 when eleven entries were removed from the R&S
        // power-supply catalog — parameter values a table-reading extractor had promoted to
        // commands. 35 catalogs became 36 and 23,163 entries became 24,292 when the FSIQ was
        // transcribed from an Operating Manual that had been thought lost. BenchVerified did
        // not move through any of it: the FSIQ is a guide, not an instrument on this bench.
        // 24,292 became 24,286 when RUN, STOP and SINGLE and their query forms came out of
        // that catalog — front-panel softkey labels, not commands — and 24,266 when twenty
        // more came out of the R&S scope for the same reason, and 24,265 when the DSA800
        // got back the parameter clause that had become an entry of its own.
        //
        // BenchVerified went 518 → 598 and back to 518 inside one afternoon, and the round
        // trip is worth keeping. The old sweep-generator.md listed 104 commands Answered
        // against 27 ticks, so the eighty were written back as ticks. They should not have
        // been: that report predates the Empty outcome, and its header has no "answered with
        // nothing" line because the sweep could not yet tell the two apart. Re-run against
        // the instrument, the same eighty came back Answered 24, Empty 80 — and an empty read
        // is not confirmation of anything, which is the whole reason Empty was split out.
        //
        // A report is evidence of what the sweep could distinguish when it ran. Reading one
        // without checking which outcomes it knew about is how eighty commands came to claim
        // a bench check none of them had.
        Assert.Equal(36, catalogs!.Count);
        Assert.Equal(23_978, catalogs.Sum(c => c.CommandCount));
        Assert.Equal(518, catalogs.Sum(c => c.BenchVerified));
    }

    [Fact]
    public async Task A_catalog_can_be_filtered_by_the_command_as_it_would_be_sent()
    {
        var client = _factory.CreateClient();
        // The guide prints "[SENSe:]VOLTage[:DC]:NPLC"; nobody types the brackets, and a
        // plain substring search would find nothing.
        var hits = await client.GetFromJsonAsync<List<CatalogCommandDto>>(
            "/api/catalogs/KeysightMultimeter?filter=VOLTage:DC:NPLC");

        Assert.NotNull(hits);
        Assert.NotEmpty(hits!);
    }

    [Fact]
    public async Task The_about_box_counts_the_same_catalogs_the_catalog_list_serves()
    {
        var client = _factory.CreateClient();
        var about = await client.GetFromJsonAsync<AboutDto>("/api/about");
        var catalogs = await client.GetFromJsonAsync<List<CatalogSummary>>("/api/catalogs");

        Assert.NotNull(about);
        Assert.NotNull(catalogs);

        // Two endpoints counting the same embedded catalogs, and no number written down in
        // either place. Cross-checked rather than pinned: the totals move as catalogs are
        // corrected, and a test that had to be edited alongside them would only ever be
        // edited to agree. What must not happen is the About box quoting one figure while
        // the app serves another.
        Assert.Equal(catalogs!.Count, about!.Catalogs);
        Assert.Equal(catalogs.Sum(c => c.CommandCount), about.Commands);
        Assert.Equal(catalogs.Sum(c => c.BenchVerified), about.BenchVerified);

        // The version, runtime and OS are read off the assembly and the framework, so the
        // only way any of them is empty is if that reading has stopped working.
        Assert.False(string.IsNullOrWhiteSpace(about.Version));
        Assert.False(string.IsNullOrWhiteSpace(about.Framework));
        Assert.False(string.IsNullOrWhiteSpace(about.Os));
        // The SDK appends "+<commit sha>" to the informational version. Not for an About box.
        Assert.DoesNotContain('+', about.Version);
    }

    [Fact]
    public async Task An_unknown_family_is_a_404_rather_than_an_empty_list()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/catalogs/NoSuchInstrument");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Sessions_start_empty_and_an_unknown_one_cannot_be_closed()
    {
        var client = _factory.CreateClient();
        var sessions = await client.GetFromJsonAsync<List<SessionDto>>("/api/sessions");
        Assert.NotNull(sessions);

        var response = await client.DeleteAsync("/api/sessions/nothing-by-that-name");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Sending_to_a_session_that_is_not_open_is_reported_not_thrown()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/sessions/gone/command", new CommandRequest("*IDN?"));
        var reply = await response.Content.ReadFromJsonAsync<CommandReply>();

        // A closed session is an ordinary outcome — a browser left open overnight will hit
        // it — so it comes back as a reply carrying an error, not as a 500.
        Assert.NotNull(reply);
        Assert.NotNull(reply!.Error);
    }

    [Fact]
    public async Task A_sequence_reports_the_instruments_it_needs()
    {
        var client = _factory.CreateClient();
        const string script = """
            DEVICE gen : SDG2042X
            DEVICE scope : DS2202
            COLUMNS Frequency, Vout
            """;

        var response = await client.PostAsJsonAsync("/api/sequence/requirements",
            new SequenceRunRequest(script, new Dictionary<string, string>()));
        var required = await response.Content.ReadFromJsonAsync<List<SequenceRequirement>>();

        Assert.NotNull(required);
        Assert.Equal(2, required!.Count);
        Assert.Contains(required, r => r.Alias == "gen" && r.Model == "SDG2042X");

        // And what each is bound to, which on an empty bench is nothing, said as the table says it.
        Assert.All(required, r =>
        {
            Assert.Null(r.SessionId);
            Assert.Equal("not connected", r.Note);
        });
    }

    [Fact]
    public async Task Running_a_sequence_with_an_unbound_alias_names_it_rather_than_starting()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/runs/sequence",
            new SequenceRunRequest("DEVICE gen : SDG2042X\nCOLUMNS A\n", new Dictionary<string, string>()));
        var summary = await response.Content.ReadFromJsonAsync<RunSummary>();

        Assert.NotNull(summary);
        Assert.True(summary!.Failed);
        Assert.Contains("gen", summary.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_script_guide_the_web_serves_is_the_one_the_desktop_window_reads(bool sequence)
    {
        var client = _factory.CreateClient();
        var served = await client.GetFromJsonAsync<ScriptGuideDto>($"/api/script-guide?sequence={sequence}");

        // The point of moving this text into Core was that there is one copy of it. A second
        // page of prose in the browser half would read the same on the day it was written and
        // then quietly stop, which is the failure this pins.
        Assert.NotNull(served);
        Assert.Equal(ScriptGuide.Title(sequence), served!.Title);
        Assert.Equal(ScriptGuide.Lead(sequence), served.Lead);

        var core = ScriptGuide.Sections(sequence);
        Assert.Equal(core.Count, served.Sections.Count);
        for (int i = 0; i < core.Count; i++)
        {
            Assert.Equal(core[i].Heading, served.Sections[i].Heading);
            Assert.Equal(core[i].Prose, served.Sections[i].Prose);
            Assert.Equal(core[i].Example, served.Sections[i].Example);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_word_the_language_has_is_explained_somewhere_in_the_guide(bool sequence)
    {
        // A guide that has fallen behind the language is worse than none: it is read as the
        // whole of it. Aliases are covered by their primary word's section saying so — WAIT is
        // named under DELAY, ECHO and LOG under PRINT — so the text is searched rather than
        // the headings.
        var language = ScriptLanguage.For(sequence);
        string text = string.Join("\n",
            ScriptGuide.Sections(sequence).SelectMany(s => new[] { s.Heading, s.Prose, s.Example }));

        var missing = language.Keywords.Concat(language.InnerKeywords)
            .Where(k => !text.Contains(k, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(missing.Count == 0,
            "The script guide never mentions: " + string.Join(", ", missing)
          + ". Add a section for it in Core/ScriptGuide.cs, or name it in an existing one.");
    }

    [Fact]
    public async Task The_ai_status_says_it_is_off_when_no_key_is_configured()
    {
        var client = _factory.CreateClient();
        var status = await client.GetFromJsonAsync<AiStatus>("/api/ai");

        // The test host has no key, and the UI has to say so plainly rather than failing
        // at the first request.
        Assert.NotNull(status);
        Assert.False(status!.Configured);
        Assert.NotNull(status.Reason);
    }

    [Fact]
    public async Task The_ai_status_names_the_connection_even_with_no_key()
    {
        var client = _factory.CreateClient();
        var status = await client.GetFromJsonAsync<AiStatus>("/api/ai");

        // The settings box behind the gear exists to answer "which service is this bench
        // pointed at, and where is that set" — a question asked precisely when nothing is
        // working yet. So the connection has to be stated whether or not a key is configured,
        // which it is here: the provider's defaults, until the environment overrides them.
        Assert.NotNull(status);
        Assert.False(status!.Configured);
        Assert.NotEmpty(status.Provider);
        Assert.NotEmpty(status.Model);
        Assert.StartsWith("http", status.Endpoint);
        Assert.True(status.TimeoutSeconds > 0, "a timeout of zero would mean every request fails at once.");

        // The box has a provider dropdown, and the browser half cannot reference Core, so the
        // presets have to arrive with the status or the dropdown has nothing to offer.
        Assert.NotEmpty(status.Providers);
        Assert.All(status.Providers, p =>
        {
            Assert.NotEmpty(p.Label);
            Assert.StartsWith("http", p.DefaultBaseUrl);
            Assert.NotEmpty(p.DefaultModel);
        });
    }
}

/// <summary>
/// The settings box writing back: the web build's answer to the desktop's AI Connection
/// dialog. Its own factory, so what these tests apply cannot reach the tests above.
/// </summary>
public class AiSettingsApiTests : IClassFixture<WebFactory>
{
    private readonly WebFactory _factory;

    public AiSettingsApiTests(WebFactory factory) => _factory = factory;

    /// <summary>
    /// The connection as one line. Compared this way rather than as whole records: AiStatus
    /// carries the provider presets, and two separately deserialised lists are never equal to
    /// a record's generated Equals, so comparing the records would only ever prove that.
    /// </summary>
    private static string Summary(AiStatus s) =>
        $"{s.Provider} | {s.Endpoint} | {s.Model} | {s.TimeoutSeconds}s | "
      + $"local={s.ExtractTextLocally?.ToString() ?? "provider"} | key={s.Configured}";

    [Fact]
    public async Task A_connection_applied_from_the_browser_is_the_one_the_server_then_reports()
    {
        var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/ai", new AiSettingsUpdate(
            "Anthropic", "https://proxy.bench.local", "claude-sonnet-5", true, 120, null));
        var reply = await response.Content.ReadFromJsonAsync<AiSettingsReply>();

        Assert.NotNull(reply);
        Assert.Null(reply!.Error);

        // The reply is the server's own state rather than an echo, and asking again has to
        // agree with it — otherwise the box shows one thing and the next request sends another.
        var after = await client.GetFromJsonAsync<AiStatus>("/api/ai");
        Assert.NotNull(after);
        Assert.Equal("Anthropic", after!.Provider);
        Assert.Equal("https://proxy.bench.local", after.Endpoint);
        Assert.Equal("claude-sonnet-5", after.Model);
        Assert.Equal(120, after.TimeoutSeconds);
        Assert.True(after.ExtractTextLocally);
        Assert.Equal(Summary(reply.Status!), Summary(after));
    }

    [Fact]
    public async Task An_endpoint_with_no_scheme_is_refused_and_changes_nothing()
    {
        var client = _factory.CreateClient();
        var before = await client.GetFromJsonAsync<AiStatus>("/api/ai");

        // What someone types when they mean the obvious thing. Everything downstream hands
        // this to an HttpClient, which cannot do anything with a bare host — so it is caught
        // here, where there is a box to say so in, rather than at the next request.
        var response = await client.PutAsJsonAsync("/api/ai", new AiSettingsUpdate(
            "Gemini", "api.openai.com", "", null, 300, null));
        var reply = await response.Content.ReadFromJsonAsync<AiSettingsReply>();

        Assert.NotNull(reply);
        Assert.Null(reply!.Status);
        Assert.Contains("http", reply.Error);

        var after = await client.GetFromJsonAsync<AiStatus>("/api/ai");
        Assert.Equal(Summary(before!), Summary(after!));
    }

    [Fact]
    public async Task A_key_goes_up_and_never_comes_back_down()
    {
        var client = _factory.CreateClient();
        const string key = "sk-a-key-that-must-not-be-echoed";

        await client.PutAsJsonAsync("/api/ai", new AiSettingsUpdate(
            "Gemini", "https://generativelanguage.googleapis.com", "", null, 300, key));

        // Read as text, not as a DTO: the point is that the key is nowhere in the response at
        // all, not merely that no property this build happens to have is carrying it.
        string body = await client.GetStringAsync("/api/ai");
        Assert.DoesNotContain(key, body);
        Assert.Contains("\"configured\":true", body);

        var status = await client.GetFromJsonAsync<AiStatus>("/api/ai");
        Assert.True(status!.Configured);
        Assert.False(status.KeyFromConfiguration, "it was set here, not in the environment.");
    }
}

/// <summary>The meter readout's polling loop, in the cases that need no instrument.</summary>
public class BenchReadoutTests
{
    /// <summary>
    /// A bench with a real hub context behind it. The service pushes the connection's command
    /// queue over SignalR as it changes, so it needs one — and a hub context built from a bare
    /// service collection is cheaper to keep honest than a hand-written stub of IHubContext,
    /// which is four interfaces deep before it does anything.
    /// </summary>
    private static BenchService Bench()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        var provider = services.BuildServiceProvider();

        return new BenchService(
            NullLogger<BenchService>.Instance,
            provider.GetRequiredService<IHubContext<BenchHub>>());
    }

    [Fact]
    public async Task Polling_a_session_that_is_not_there_says_so_once_and_stops()
    {
        var bench = Bench();

        var readings = new List<ReadingDto>();
        await foreach (var r in bench.ReadoutAsync("no-such-session", "MEASure:VOLTage:DC?", 1000, default))
            readings.Add(r);

        // One message and an end, rather than a stream that never starts and never finishes.
        // A pane waiting for a first reading that is not coming looks like a slow instrument,
        // and the session it wants was closed from another browser a minute ago.
        var only = Assert.Single(readings);
        Assert.NotNull(only.Error);
        Assert.Equal(0, only.Count);
    }

    [Fact]
    public async Task Polling_stops_when_whoever_asked_for_it_stops_listening()
    {
        var bench = Bench();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // An instrument left being polled by nobody is an instrument nobody else can use, so
        // the token the stream runs under is the whole of the stop mechanism — the Stop button
        // and the pane being closed both come down to cancelling it.
        var readings = new List<ReadingDto>();
        await foreach (var r in bench.ReadoutAsync("no-such-session", "MEASure:VOLTage:DC?", 1000, cancelled.Token))
            readings.Add(r);

        Assert.All(readings, r => Assert.NotNull(r.Error));
    }
}

/// <summary>The settings file itself: what is written, and what is read back.</summary>
public class AiSettingsStoreTests
{
    private static AiSettingsStore StoreIn(string directory) => new(
        new AiOptions(),
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["LEC_DATA"] = directory })
            .Build(),
        NullLogger<AiSettingsStore>.Instance);

    [Fact]
    public void An_applied_connection_is_still_there_after_a_restart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lec-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            string? error = StoreIn(dir).Apply(new AiSettingsUpdate(
                "OpenAiCompatible", "http://ollama.bench.local:11434", "qwen2.5", false, 45, "sk-secret"));
            Assert.Null(error);

            // A second store over the same folder is what the next start-up is. Settings that
            // only last until the container is replaced are not settings.
            var restarted = StoreIn(dir);
            Assert.Equal(AiProvider.OpenAiCompatible, restarted.Connection.Provider);
            Assert.Equal("http://ollama.bench.local:11434", restarted.Connection.EffectiveBaseUrl);
            Assert.Equal("qwen2.5", restarted.Connection.EffectiveModel);
            Assert.Equal(45, restarted.Connection.TimeoutSeconds);
            Assert.False(restarted.Connection.ExtractTextLocally);
            Assert.Equal("sk-secret", restarted.ApiKey);
            Assert.False(restarted.KeyFromConfiguration);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void A_key_from_the_environment_is_not_copied_into_the_settings_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lec-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configured = new AiOptions { ApiKey = "sk-from-the-environment" };
            var store = new AiSettingsStore(
                configured,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["LEC_DATA"] = dir })
                    .Build(),
                NullLogger<AiSettingsStore>.Instance);

            Assert.True(store.Configured);
            Assert.True(store.KeyFromConfiguration);

            // Changing the model must not write the operator's key into a second file. It
            // already lives somewhere they control; copying it there would quietly create
            // another place to change it, and another place to leak it from.
            Assert.Null(store.Apply(new AiSettingsUpdate(
                "Gemini", "https://generativelanguage.googleapis.com", "gemini-3.6-pro", null, 300, null)));

            string written = File.ReadAllText(Path.Combine(dir, "ai.json"));
            Assert.DoesNotContain("sk-from-the-environment", written);
            Assert.Contains("gemini-3.6-pro", written);

            // At the next start-up the two layers come back together: the model off the file,
            // the key still off the environment.
            var restarted = new AiSettingsStore(
                configured,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["LEC_DATA"] = dir })
                    .Build(),
                NullLogger<AiSettingsStore>.Instance);

            Assert.Equal("gemini-3.6-pro", restarted.Connection.EffectiveModel);
            Assert.Equal("sk-from-the-environment", restarted.ApiKey);
            Assert.True(restarted.KeyFromConfiguration);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}

/// <summary>Address parsing, which decides which transport an instrument gets.</summary>
public class WebAddressTests
{
    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20", InstrumentTransport.RawSocket, 5025)]
    [InlineData("192.168.1.20:5555", "192.168.1.20", InstrumentTransport.RawSocket, 5555)]
    [InlineData("vxi://192.168.1.20", "192.168.1.20", InstrumentTransport.Vxi11, 111)]
    [InlineData("TCPIP0::192.168.1.20::inst0::INSTR", "192.168.1.20", InstrumentTransport.Vxi11, 111)]
    public void Every_spelling_of_an_address_resolves_the_way_the_other_front_ends_resolve_it(
        string text, string host, InstrumentTransport transport, int port)
    {
        var (h, t, p, _) = BenchService.ParseAddress(text);
        Assert.Equal(host, h);
        Assert.Equal(transport, t);
        Assert.Equal(port, p);
    }

    [Fact]
    public void Port_111_means_VXI_11_however_it_was_written()
    {
        // The portmapper answers on 111 but never speaks SCPI. Read as a raw socket the
        // connection succeeds and then waits forever for a reply.
        var (_, transport, _, _) = BenchService.ParseAddress("192.168.1.20:111");
        Assert.Equal(InstrumentTransport.Vxi11, transport);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[fe80::1]:99999")]
    public void An_unusable_address_is_refused(string text)
        => Assert.Throws<ArgumentException>(() => BenchService.ParseAddress(text));

    /// <summary>
    /// A gateway is one host with several instruments behind it, and what tells them apart is
    /// the port or the VXI-11 device name. Sessions are matched on this string, so two
    /// instruments that produce the same one are two instruments the server cannot tell apart:
    /// connecting to the second used to hand back the first one's session, leaving you driving
    /// the wrong instrument with the right name in the tab.
    /// </summary>
    [Theory]
    [InlineData("192.168.1.20:5025", "192.168.1.20:5026")]                 // a two-port serial gateway
    [InlineData("vxi://192.168.1.20/gpib0,9", "vxi://192.168.1.20/gpib0,22")]  // two GPIB addresses
    [InlineData("192.168.1.20:5025", "192.168.1.21:5025")]                 // plainly different boxes
    [InlineData("192.168.1.20:5025", "vxi://192.168.1.20")]                // same box, two ways in
    [InlineData("serial://COM3", "serial://COM4")]                         // two ports, two instruments
    [InlineData("serial://COM3", "192.168.1.20:5025")]                     // a port is not a host
    public void Two_instruments_behind_one_address_are_told_apart(string first, string second)
        => Assert.NotEqual(EndpointOf(first), EndpointOf(second), StringComparer.OrdinalIgnoreCase);

    [Theory]
    // The same instrument, spelled differently. These have to collapse, or reconnecting puts
    // a second socket on something that answers one conversation at a time.
    [InlineData("192.168.1.20", "192.168.1.20:5025")]
    [InlineData("vxi://192.168.1.20", "TCPIP0::192.168.1.20::inst0::INSTR")]
    [InlineData("vxi://192.168.1.20", "192.168.1.20:111")]
    [InlineData("tcp://192.168.1.20:5555", "192.168.1.20:5555")]
    // The same port at the same speed, however it was spelled. A serial port answers one
    // conversation at a time even more strictly than a socket does — it cannot be opened
    // twice at all — so these have to collapse onto one session.
    [InlineData("serial://COM3", "ASRL3::INSTR")]
    [InlineData("serial://COM3", "serial://COM3?baud=9600")]
    public void The_same_instrument_written_two_ways_is_one_session(string first, string second)
        => Assert.Equal(EndpointOf(first), EndpointOf(second), StringComparer.OrdinalIgnoreCase);

    [Theory]
    // What it writes back out: something you could type into the Address box to get here again.
    [InlineData("192.168.1.20", "192.168.1.20:5025")]
    [InlineData("vxi://192.168.1.20", "vxi://192.168.1.20")]
    [InlineData("TCPIP0::192.168.1.20::gpib0,9::INSTR", "vxi://192.168.1.20/gpib0,9")]
    public void An_address_is_written_back_the_way_it_could_be_typed_in(string text, string expected)
        => Assert.Equal(expected, EndpointOf(text));

    private static string EndpointOf(string text)
        => BenchService.Endpoint(BenchService.ParseAddress(text));

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png")]
    [InlineData(new byte[] { 0x42, 0x4D, 0x36, 0x00 }, "image/bmp")]
    [InlineData(new byte[] { 0x00, 0x01, 0x02, 0x03 }, "application/octet-stream")]
    public void A_screenshot_is_typed_from_its_bytes_not_from_a_guess(byte[] data, string expected)
    {
        // The instrument chooses the format — a Rigol sends BMP whatever was asked for —
        // and a browser handed the wrong MIME type shows a broken image and says nothing.
        Assert.Equal(expected, BenchService.ImageType(data));
    }
}

/// <summary>
/// Export Results, and the four column headings that go with it.
///
/// The headings are not decoration: the desktop's list, the web's table and the CSV all name
/// the same four things, and they were three different sets of words. The web's table said
/// Address / Port / Transport / Identity while the file said IP Address / Port / Protocol /
/// Identity — the same columns under different names, which is how a column called Protocol
/// in a spreadsheet stops matching the one on screen.
/// </summary>
public class ScanExportTests : IClassFixture<WebFactory>
{
    private readonly WebFactory _factory;

    public ScanExportTests(WebFactory factory) => _factory = factory;

    /// <summary>The desktop's four ColumnHeader texts, quoted from MainForm.Designer.cs.</summary>
    [Fact]
    public void The_columns_are_the_ones_the_desktop_list_is_drawn_with()
        => Assert.Equal(
            new[] { "IP Address", "Port", "Protocol", "Identity" },
            ScanResultExport.Columns);

    [Fact]
    public void Both_ways_of_asking_write_the_same_document()
    {
        var devices = new[]
        {
            new ScpiDevice { Address = IPAddress.Parse("192.168.1.7"), Port = 111,
                             Transport = InstrumentTransport.Vxi11,
                             Identity = "Siglent Technologies,SDM3065X,SDM36HCD801207,3.02.01.13" },
            new ScpiDevice { Address = IPAddress.Parse("192.168.1.50"), Port = 5555,
                             Identity = "RIGOL TECHNOLOGIES,DS2202,DS2A152001051,00.01.00" },
        };

        string fromDevices = ScanResultExport.ToCsv(devices);
        string fromRows = ScanResultExport.ToCsv(devices.Select(
            d => (d.Address.ToString(), d.Port, d.TransportName, d.Identity)));

        Assert.Equal(fromDevices, fromRows);
    }

    [Fact]
    public void An_identity_full_of_commas_is_quoted_rather_than_spread_across_columns()
    {
        string csv = ScanResultExport.ToCsv(
            [("192.168.1.7", 111, "VXI-11", "Siglent Technologies,SDM3065X,SDM36HCD801207,3.02.01.13")]);

        Assert.Equal("IP Address,Port,Protocol,Identity\r\n"
            + "192.168.1.7,111,VXI-11,\"Siglent Technologies,SDM3065X,SDM36HCD801207,3.02.01.13\"\r\n",
            csv);
    }

    /// <summary>
    /// The browser's Export Results is the desktop's file. The list is posted back up because a
    /// scan streams its results out as it goes and the server keeps no copy of the last one.
    /// </summary>
    [Fact]
    public async Task The_browser_is_handed_the_same_file_the_desktop_writes()
    {
        var client = _factory.CreateClient();
        var devices = new[]
        {
            new DeviceDto("192.168.1.7", 111, "VXI-11", "Siglent Technologies,SDM3065X,SDM36HCD801207,3.02.01.13"),
            new DeviceDto("192.168.1.50", 5555, "Raw socket", "RIGOL TECHNOLOGIES,DS2202,DS2A152001051,00.01.00"),
        };

        var response = await client.PostAsJsonAsync("/api/scan/export", devices);
        response.EnsureSuccessStatusCode();
        string csv = await response.Content.ReadAsStringAsync();

        Assert.Equal(ScanResultExport.ToCsv(devices.Select(d => (d.Address, d.Port, d.Transport, d.Identity))), csv);
        Assert.StartsWith("IP Address,Port,Protocol,Identity\r\n", csv, StringComparison.Ordinal);
    }

    /// <summary>
    /// The instrument timeout starts where the desktop's spinner starts. It is the timeout for
    /// the connection and for every query on the session afterwards, so a default that differs
    /// between the two builds means the same instrument answers in one and not the other.
    /// </summary>
    [Fact]
    public void An_unasked_connection_waits_as_long_as_the_desktop_waits()
        => Assert.Equal(3000, new ConnectRequest("192.168.1.7").TimeoutMs);
}

/// <summary>
/// The plot the browser draws. The arithmetic is Core's — these check the translation of it:
/// what the page is told to draw, and what it is told it may not.
/// </summary>
public class PlotServiceTests
{
    private static readonly string[] ConsoleColumns = ["Time", "Command", "Value"];

    private static PlotRequest Console(params (string At, string Command, string Value)[] rows)
        => new(ConsoleColumns,
               rows.Select(r => new RecordedRow([r.At, r.Command, r.Value])).ToList(),
               XColumn: 0, YColumns: null);

    [Fact]
    public void A_column_of_commands_is_not_offered_as_a_curve()
    {
        // The console's Command column holds the same string on every row. Ticked by default
        // it added a legend entry and a colour to a series with no points in it.
        var reply = PlotService.Build(Console(
            ("20:14:03", "MEAS:VOLT:DC?", "1.201"),
            ("20:14:04", "MEAS:VOLT:DC?", "1.207")));

        Assert.Equal([0, 2], reply.Plottable);
        Assert.Equal([2], reply.ChosenY);
        Assert.Equal("Value", Assert.Single(reply.Series).Name);
    }

    [Fact]
    public void The_unit_is_read_off_the_command_that_took_the_reading()
    {
        // An instrument asked for volts can be wired across a shunt, so this is a guess and
        // the box is typeable — but it is the guess that makes the ticks read 8 mV.
        var reply = PlotService.Build(Console(("20:14:03", "MEAS:VOLT:DC?", "1.201")));
        Assert.Equal("V", reply.GuessedUnit);
    }

    [Fact]
    public void A_column_of_timestamps_is_labelled_as_a_clock()
    {
        var reply = PlotService.Build(Console(
            ("20:14:03", "READ?", "1.0"),
            ("20:14:04", "READ?", "2.0"),
            ("20:14:05", "READ?", "3.0")));

        Assert.All(reply.X.Ticks, t => Assert.Contains(':', t.Label));
        Assert.DoesNotContain(reply.Y.Ticks, t => t.Label.Contains(':'));
    }

    [Fact]
    public void A_log_axis_is_refused_where_it_would_have_no_meaning()
    {
        // Left enabled, a single zero reading flattens the whole curve onto one edge.
        var reply = PlotService.Build(Console(
            ("20:14:03", "READ?", "0"),
            ("20:14:04", "READ?", "2.0")));

        Assert.False(reply.CanLogY);

        // And asking anyway is not an error: it comes back linear rather than empty.
        var asked = PlotService.Build(new PlotRequest(
            ConsoleColumns,
            [new RecordedRow(["20:14:03", "READ?", "0"]), new RecordedRow(["20:14:04", "READ?", "2.0"])],
            XColumn: 0, YColumns: [2], LogX: false, LogY: true));

        Assert.False(asked.Y.Logarithmic);
        Assert.NotEmpty(asked.Series);
    }

    [Fact]
    public void Every_point_lands_inside_the_box_it_is_drawn_in()
    {
        var reply = PlotService.Build(Console(
            ("20:14:03", "READ?", "1.0"),
            ("20:14:04", "READ?", "5.0"),
            ("20:14:05", "READ?", "3.0")));

        var points = Assert.Single(reply.Series).Points;
        Assert.All(points, p => Assert.InRange(p.X, 0, 1));
        Assert.All(points, p => Assert.InRange(p.Y, 0, 1));

        // The axis is padded, so the extremes sit inside the frame rather than on it — which
        // is what stops a maximum reading being drawn as a line along the top edge.
        Assert.All(points, p => Assert.InRange(p.Y, 0.01, 0.99));
    }

    [Fact]
    public void Two_readings_recorded_together_are_two_curves_in_different_colours()
    {
        var reply = PlotService.Build(new PlotRequest(
            ["Frequency", "Vpp", "Phase"],
            [
                new RecordedRow(["1000", "1.0", "10"]),
                new RecordedRow(["2000", "0.9", "20"]),
                new RecordedRow(["4000", "0.5", "35"]),
            ],
            XColumn: 0, YColumns: null));

        Assert.Equal(["Vpp", "Phase"], reply.Series.Select(s => s.Name));
        Assert.Equal(2, reply.Series.Select(s => s.Colour).Distinct().Count());
        Assert.Equal("Frequency", reply.XName);
    }

    [Fact]
    public void Choosing_the_columns_yourself_is_honoured()
    {
        var reply = PlotService.Build(new PlotRequest(
            ["Frequency", "Vpp", "Phase"],
            [new RecordedRow(["1000", "1.0", "10"]), new RecordedRow(["2000", "0.9", "20"])],
            XColumn: 0, YColumns: [2]));

        Assert.Equal("Phase", Assert.Single(reply.Series).Name);
        Assert.Equal([2], reply.ChosenY);
    }
}
