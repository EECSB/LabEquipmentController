using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LabEquipmentController.Web.Client.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace LabEquipmentController.Web.Client.Services;

/// <summary>
/// The browser's whole view of the bench: one HTTP call per operation, plus a hub
/// connection for the things that arrive over time.
/// </summary>
/// <remarks>
/// Deliberately the only place in the client that knows a URL. A component asking for
/// instruments should not also be deciding what the route is called, and when the API moves
/// there is one file to change.
///
/// Two clients, one pipeline: <paramref name="ai"/> carries the two calls that wait on a model,
/// which can run past the 100 seconds <paramref name="http"/> gives everything else.
/// </remarks>
public sealed class BenchClient(HttpClient http, AiHttp ai) : IAsyncDisposable
{
    private HubConnection? _hub;

    public HttpClient Http => http;

    /// <summary>
    /// Raised when an instrument is connected or closed.
    ///
    /// The window chrome shows how many are open, and it used to re-read that whenever the
    /// user moved between pages — which worked only for as long as connecting to something
    /// meant going somewhere afterwards. Consoles are tabs on the bench page now, so a whole
    /// session can be spent without a single navigation, and a count that never updates is
    /// worse than no count. Nothing here polls: the one place that opens and closes sessions
    /// says so, and whoever is displaying the number listens.
    /// </summary>
    public event Action? SessionsChanged;

    // ------------------------------------------------------------------- discovery

    public async Task<IReadOnlyList<LocalInterfaceDto>> InterfacesAsync()
        => await http.GetFromJsonAsync<List<LocalInterfaceDto>>("api/interfaces") ?? [];

    /// <summary>
    /// The serial ports on the machine running the server, which is the only machine whose
    /// ports mean anything here — a browser cannot open one. Listed, never probed, so this
    /// is safe to call whenever the list is about to be shown.
    /// </summary>
    public async Task<IReadOnlyList<string>> PortsAsync()
        => await http.GetFromJsonAsync<List<string>>("api/ports") ?? [];

    /// <summary>
    /// The same ports as results rows, with the line settings Core would use and no identity
    /// — nothing has been asked. Rows rather than names so the "9600-8-N-1" on screen is the
    /// one <c>SerialSettings.Default</c> actually means.
    /// </summary>
    public async Task<IReadOnlyList<DeviceDto>> SerialPortRowsAsync()
        => await http.GetFromJsonAsync<List<DeviceDto>>("api/serial/list") ?? [];

    public async Task<ScanReport> ScanAsync(ScanRequest req, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/scan", req, ct);
        return await response.Content.ReadFromJsonAsync<ScanReport>(cancellationToken: ct)
               ?? new ScanReport([], 0, false, "The scan returned nothing at all.");
    }

    /// <summary>
    /// The same sweep, reported as it happens: how far along it is, and every instrument the
    /// moment it answers rather than all of them at the end.
    /// </summary>
    /// <remarks>
    /// Cancelling <paramref name="ct"/> stops the sweep on the server, not just the listening
    /// here — a stream that the browser stops reading cancels the token the server is running
    /// under. That is what the Stop button does, and it is also what closing the tab does.
    /// </remarks>
    public async IAsyncEnumerable<ScanEvent> ScanStreamAsync(
        ScanRequest req, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var hub = await HubAsync();
        await foreach (var e in hub.StreamAsync<ScanEvent>("Scan", req, ct))
            yield return e;
    }

    /// <summary>
    /// The same stream over the server's serial ports. Every port reports, answered or not —
    /// see <c>SerialScanner</c> for why a silent serial port is still a row.
    /// </summary>
    public async IAsyncEnumerable<ScanEvent> ScanSerialStreamAsync(
        SerialScanRequest req, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var hub = await HubAsync();
        await foreach (var e in hub.StreamAsync<ScanEvent>("ScanSerial", req, ct))
            yield return e;
    }

    /// <summary>
    /// The discovered list as a CSV document. Written on the server by Core's ScanResultExport,
    /// the same writer the desktop's Export Results uses, so the two builds cannot hand out
    /// different files for the same bench.
    /// </summary>
    public async Task<string> ExportDevicesAsync(IReadOnlyList<DeviceDto> devices)
    {
        var response = await http.PostAsJsonAsync("api/scan/export", devices);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// The curve for a table of recorded rows: series, axes and ticks, all as fractions the
    /// page can draw straight onto an SVG. Worked out on the server by Core's ResultPlot, the
    /// same arithmetic the desktop's plot uses.
    /// </summary>
    public async Task<PlotReply?> PlotAsync(PlotRequest request)
    {
        var response = await http.PostAsJsonAsync("api/plot", request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<PlotReply>()
            : null;
    }

    // -------------------------------------------------------------------- sessions

    public async Task<IReadOnlyList<SessionDto>> SessionsAsync()
        => await http.GetFromJsonAsync<List<SessionDto>>("api/sessions") ?? [];

    /// <param name="ct">
    /// Cancelled by Connect doubling as Cancel. It reaches the connection attempt itself: a
    /// minimal API's CancellationToken is the request's, and BenchService drops the socket on
    /// the way out, so nothing is left half-open.
    /// </param>
    public async Task<(SessionDto? Session, string? Error)> ConnectAsync(
        string address, int timeoutMs = 3000, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/sessions", new ConnectRequest(address, timeoutMs), ct);
        if (response.IsSuccessStatusCode)
        {
            var session = await response.Content.ReadFromJsonAsync<SessionDto>();
            SessionsChanged?.Invoke();
            return (session, null);
        }

        // The server hands back its own message — "connection refused", "did not answer
        // within 5000 ms" — and that is far more use than "502".
        string body = await response.Content.ReadAsStringAsync();
        return (null, Readable(body, response.StatusCode.ToString()));
    }

    public async Task DisconnectAsync(string id)
    {
        await http.DeleteAsync($"api/sessions/{id}");
        SessionsChanged?.Invoke();
    }

    public async Task<CommandReply> SendAsync(string id, string text)
    {
        var response = await http.PostAsJsonAsync($"api/sessions/{id}/command", new CommandRequest(text));
        return await response.Content.ReadFromJsonAsync<CommandReply>()
               ?? new CommandReply(text, null, false, 0, "No reply from the server.");
    }

    /// <summary>
    /// Ask the instrument to list its own commands, which is what the desktop's Discover Commands
    /// does before it falls back to the catalog.
    /// </summary>
    public async Task<DiscoveryReply> DiscoverAsync(string id)
    {
        var response = await http.PostAsync($"api/sessions/{id}/discover", null);
        return await response.Content.ReadFromJsonAsync<DiscoveryReply>()
               ?? new DiscoveryReply("SYSTem:HELP:HEADers?", false, 0, "");
    }

    /// <summary>Capture these channels, against one time axis.</summary>
    public async Task<WaveformSetDto> WaveformAsync(string id, IEnumerable<int> channels)
    {
        var response = await http.PostAsync(
            $"api/sessions/{id}/waveform?channels={string.Join(',', channels)}", null);
        return await response.Content.ReadFromJsonAsync<WaveformSetDto>()
               ?? new WaveformSetDto([], [], 0, "No reply from the server.");
    }

    public async Task<ScreenshotDto> ScreenshotAsync(string id)
    {
        var response = await http.PostAsync($"api/sessions/{id}/screenshot", null);
        return await response.Content.ReadFromJsonAsync<ScreenshotDto>()
               ?? new ScreenshotDto("", "", 0, "", "No reply from the server.");
    }

    // -------------------------------------------------------------------- catalogs

    public async Task<IReadOnlyList<CatalogSummary>> CatalogsAsync()
        => await http.GetFromJsonAsync<List<CatalogSummary>>("api/catalogs") ?? [];

    public async Task<IReadOnlyList<CatalogCommandDto>> CatalogAsync(string family, string? filter, int limit = 500)
        => await http.GetFromJsonAsync<List<CatalogCommandDto>>(
               $"api/catalogs/{family}?filter={Uri.EscapeDataString(filter ?? "")}&limit={limit}") ?? [];

    // ----------------------------------------------------------------------- about

    /// <summary>The About box's facts, or null if the server did not answer — a dialog that
    /// states nothing is better than one stating something invented.</summary>
    public async Task<AboutDto?> AboutAsync()
    {
        try { return await http.GetFromJsonAsync<AboutDto>("api/about"); }
        catch { return null; }
    }

    // --------------------------------------------------------------------- scripts

    public async Task<IReadOnlyList<ExampleDto>> ScriptExamplesAsync(string family)
        => await http.GetFromJsonAsync<List<ExampleDto>>($"api/examples/script/{family}") ?? [];

    public async Task<IReadOnlyList<ExampleDto>> SequenceExamplesAsync()
        => await http.GetFromJsonAsync<List<ExampleDto>>("api/examples/sequence") ?? [];

    /// <summary>The worked example the Script Editor opens on, as the desktop's ScriptForm does.</summary>
    public async Task<string> ScriptStartAsync()
        => (await http.GetFromJsonAsync<ExampleDto>("api/examples/script-start"))?.Script ?? "";

    /// <summary>
    /// A script's DEVICE lines and what each is bound to on the bench right now, given what has
    /// been picked by hand. The server works it out, by the rule the desktop's strip uses.
    /// </summary>
    public async Task<IReadOnlyList<SequenceRequirement>> RequirementsAsync(
        string script, IReadOnlyDictionary<string, string> picks)
    {
        var response = await http.PostAsJsonAsync("api/sequence/requirements",
            new SequenceRunRequest(script, picks));
        return await response.Content.ReadFromJsonAsync<List<SequenceRequirement>>() ?? [];
    }

    public async Task<RunSummary> RunScriptAsync(string sessionId, string script)
    {
        var response = await http.PostAsJsonAsync("api/runs/script", new ScriptRunRequest(sessionId, script));
        return await response.Content.ReadFromJsonAsync<RunSummary>()
               ?? new RunSummary("", [], true, "No reply from the server.");
    }

    /// <param name="inputs">A value for each INPUT line the script declares, or null for none.</param>
    public async Task<RunSummary> RunSequenceAsync(
        string script,
        IReadOnlyDictionary<string, string> bindings,
        IReadOnlyDictionary<string, string>? inputs = null)
    {
        var response = await http.PostAsJsonAsync("api/runs/sequence",
            new SequenceRunRequest(script, bindings, inputs));
        return await response.Content.ReadFromJsonAsync<RunSummary>()
               ?? new RunSummary("", [], true, "No reply from the server.");
    }

    public async Task StopAsync(string runId) => await http.PostAsync($"api/runs/{runId}/stop", null);

    /// <summary>
    /// Poll one measurement and read back what it says. Cancelling the enumeration stops the
    /// polling on the server — the instrument is not left being asked by nobody.
    /// </summary>
    public async IAsyncEnumerable<ReadingDto> ReadoutStreamAsync(
        string sessionId, string query, int intervalMs,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var hub = await HubAsync();
        await foreach (var r in hub.StreamAsync<ReadingDto>("Readout", sessionId, query, intervalMs, ct))
            yield return r;
    }

    // ------------------------------------------------------------- script language

    /// <summary>The script-language guide, for the multi-instrument language or the other one.</summary>
    public async Task<ScriptGuideDto?> ScriptGuideAsync(bool sequence)
        => await http.GetFromJsonAsync<ScriptGuideDto>(
            "api/script-guide?sequence=" + (sequence ? "true" : "false"));

    /// <summary>Every word the language has, for the Snippets menu.</summary>
    public async Task<IReadOnlyList<SnippetDto>> SnippetsAsync(bool sequence)
        => await http.GetFromJsonAsync<List<SnippetDto>>(
            "api/snippets?sequence=" + (sequence ? "true" : "false")) ?? [];

    // ------------------------------------------------------------------ datasheets

    /// <summary>The guide the server holds for this catalog, or null if it holds none.</summary>
    public async Task<DatasheetDto?> DatasheetForAsync(string family)
    {
        var response = await http.GetAsync($"api/datasheets/for/{Uri.EscapeDataString(family)}");
        return response.StatusCode == System.Net.HttpStatusCode.NoContent
            ? null
            : await response.Content.ReadFromJsonAsync<DatasheetDto>();
    }

    /// <summary>Put a guide into the server's collection, under its manufacturer.</summary>
    public async Task<DatasheetDto?> DatasheetUploadAsync(DatasheetUpload upload)
    {
        var response = await http.PostAsJsonAsync("api/datasheets", upload);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<DatasheetDto>()
            : null;
    }

    // -------------------------------------------------------------------------- AI

    public async Task<AiStatus> AiStatusAsync()
        => await http.GetFromJsonAsync<AiStatus>("api/ai")
           ?? new AiStatus(false, "", "", "", 0, null, false, "The server did not answer.", [],
                           "Default", [], [], "");

    /// <summary>Send an edited connection back. The reply is the connection as it now is.</summary>
    public async Task<AiSettingsReply> AiApplyAsync(AiSettingsUpdate update)
    {
        var response = await http.PutAsJsonAsync("api/ai", update);
        return await response.Content.ReadFromJsonAsync<AiSettingsReply>()
               ?? new AiSettingsReply(null, "No reply from the server.");
    }

    /// <summary>Add a connection on a provider's defaults. The server selects it.</summary>
    public async Task<AiSettingsReply> AiAddAsync(string provider)
        => await Sent(await http.PostAsync($"api/ai/connections?provider={Uri.EscapeDataString(provider)}", null));

    /// <summary>A copy of a connection, key and all, right after it and in use.</summary>
    public async Task<AiSettingsReply> AiDuplicateAsync(string id)
        => await Sent(await http.PostAsync($"api/ai/connections/{Uri.EscapeDataString(id)}/duplicate", null));

    /// <summary>Forget a connection, and its key with it.</summary>
    public async Task<AiSettingsReply> AiRemoveAsync(string id)
        => await Sent(await http.DeleteAsync($"api/ai/connections/{Uri.EscapeDataString(id)}"));

    /// <summary>Use this one, everywhere. One selection for the server, not one per window.</summary>
    public async Task<AiSettingsReply> AiSelectAsync(string id)
        => await Sent(await http.PostAsync($"api/ai/connections/{Uri.EscapeDataString(id)}/select", null));

    private static async Task<AiSettingsReply> Sent(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<AiSettingsReply>()
           ?? new AiSettingsReply(null, "No reply from the server.");

    /// <summary>
    /// How long the page waits for a script before it stops waiting: the connection's own limit,
    /// which the server enforces and reports in its own words, and half a minute more for that
    /// report to arrive. The server floors the limit at ten seconds, and so does this. Without a
    /// connection to read it from, the most the settings allow.
    /// </summary>
    public static TimeSpan ScriptWait(int? connectionTimeoutSeconds)
        => TimeSpan.FromSeconds(Math.Max(10, connectionTimeoutSeconds ?? 900) + 30);

    /// <summary>
    /// Ask for a script, and wait no longer than <paramref name="wait"/> for it.
    ///
    /// The writer is one model call, and the server already puts the connection's limit on it,
    /// so this clock only ever fires when the server has stopped answering — and the writer has
    /// no Stop button to get out of that with. When it fires the answer is a reply saying so,
    /// like any other failure the server reports, rather than an exception for the window to
    /// catch. See <see cref="ScriptWait"/>.
    /// </summary>
    /// <param name="ct">A real cancellation, which is let through rather than said as a failure.</param>
    public async Task<AiScriptReply> AiScriptAsync(AiScriptRequest req, TimeSpan wait, CancellationToken ct = default)
    {
        using var clock = CancellationTokenSource.CreateLinkedTokenSource(ct);
        clock.CancelAfter(wait);
        try
        {
            using var response = await ai.Http.PostAsJsonAsync("api/ai/script", req, clock.Token);
            return await ReadAiAsync(response, error => new AiScriptReply("", [], error), clock.Token)
                   ?? new AiScriptReply("", [], "No reply from the server.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AiScriptReply("", [],
                $"The server did not reply within {wait.TotalSeconds:0} s — longer than the AI connection "
              + "is allowed — so this page stopped waiting. The server may have stopped answering.");
        }
    }

    /// <summary>How long a request to the writer is before the conversation, the files and the box. 0 when it cannot be said: this only feeds an estimate.</summary>
    public async Task<int> AiScriptSizeAsync(AiScriptRequest req)
    {
        try
        {
            using var response = await ai.Http.PostAsJsonAsync("api/ai/script/size", req);
            if (!response.IsSuccessStatusCode) return 0;
            return (await response.Content.ReadFromJsonAsync<AiScriptSize>())?.Characters ?? 0;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Read a datasheet. No clock here, on purpose: a guide read locally is one model call per
    /// part, each with the connection's full limit, so nothing short of the whole document's
    /// length bounds it. Stop is what ends it early.
    /// </summary>
    /// <param name="ct">
    /// Cancelled by the Stop button. It reaches the extraction itself: a minimal API's
    /// CancellationToken is the request's, so aborting here aborts the model call rather than
    /// merely looking away from it.
    /// </param>
    public async Task<AiExtractReply> AiExtractAsync(AiExtractRequest req, CancellationToken ct = default)
    {
        using var response = await ai.Http.PostAsJsonAsync("api/ai/extract", req, ct);
        return await ReadAiAsync(response, error => new AiExtractReply(0, 0, [], null, error), ct)
               ?? new AiExtractReply(0, 0, [], null, "No reply from the server.");
    }

    /// <summary>
    /// Read an AI reply, having looked at the status first.
    ///
    /// The server answers every AI request with a reply, failures included, so anything else
    /// came from something in between: a proxy's 502 page while the container restarts, a 413
    /// for a guide over the upload limit. Parsed as a reply, those failed as "'&lt;' is an
    /// invalid start of a value", which says nothing about what happened. The status does.
    /// </summary>
    private static async Task<T?> ReadAiAsync<T>(
        HttpResponseMessage response, Func<string, T> failed, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            // A fetch over HTTP/2 carries no reason phrase, so the code's own name stands in.
            string reason = string.IsNullOrWhiteSpace(response.ReasonPhrase)
                ? response.StatusCode.ToString()
                : response.ReasonPhrase;
            return failed($"The server answered {(int)response.StatusCode} {reason} instead of a reply.");
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return failed("The server answered, but not with a reply this page can read.");
        }
    }

    /// <summary>Write out the ticked commands from an extraction.</summary>
    public async Task<AiSaveExtractReply> AiSaveExtractedAsync(AiSaveExtractRequest req)
    {
        var response = await http.PostAsJsonAsync("api/ai/extract/save", req);
        return await response.Content.ReadFromJsonAsync<AiSaveExtractReply>()
               ?? new AiSaveExtractReply(0, null, "No reply from the server.");
    }

    // ------------------------------------------------------------------------- hub

    /// <summary>Connect once, lazily; every page that watches a run shares the connection.</summary>
    public async Task<HubConnection> HubAsync()
    {
        if (_hub is { State: HubConnectionState.Connected }) return _hub;
        if (_hub is null)
        {
            _hub = new HubConnectionBuilder()
                .WithUrl(new Uri(http.BaseAddress!, "hub/bench"))
                .WithAutomaticReconnect()
                .Build();

            // The bench changed somewhere else — another window connected something, or the app
            // was opened afresh and let the last one's instruments go. Raised through the same
            // event as a change made here, so that everything showing the list or counting it
            // hears about both the same way: the tab strip updating live while the count in the
            // status bar stayed at what it was when this page loaded is a window disagreeing
            // with itself.
            _hub.On("Bench", () => SessionsChanged?.Invoke());

            // Whatever happened while this connection was down was not heard, and nothing
            // will say it again. Re-read once it is back, for the same reason as below.
            _hub.Reconnected += _ =>
            {
                SessionsChanged?.Invoke();
                return Task.CompletedTask;
            };
        }
        if (_hub.State == HubConnectionState.Disconnected)
        {
            await _hub.StartAsync();

            // And once it is up for the first time. A page opened a moment before someone
            // else connected an instrument is listening too late to be told about it: the
            // push has already gone out, nothing repeats it, and the tab strip stays empty
            // until the page is reloaded. Asking once here costs one request and closes the
            // window between reading the bench and being able to hear about it.
            SessionsChanged?.Invoke();
        }
        return _hub;
    }

    private static string Readable(string body, string fallback)
    {
        if (string.IsNullOrWhiteSpace(body)) return fallback;
        // ASP.NET wraps Results.Problem in JSON; show the detail, not the envelope.
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var detail))
                return detail.GetString() ?? fallback;
        }
        catch { /* not JSON — fall through and show it raw */ }
        return body.Length > 400 ? body[..400] : body;
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
    }
}
