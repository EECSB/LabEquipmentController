using System.Net.Http.Json;
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
/// </remarks>
public sealed class BenchClient(HttpClient http) : IAsyncDisposable
{
    private HubConnection? _hub;

    public HttpClient Http => http;

    // ------------------------------------------------------------------- discovery

    public async Task<IReadOnlyList<LocalInterfaceDto>> InterfacesAsync()
        => await http.GetFromJsonAsync<List<LocalInterfaceDto>>("api/interfaces") ?? [];

    public async Task<ScanReport> ScanAsync(ScanRequest req, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/scan", req, ct);
        return await response.Content.ReadFromJsonAsync<ScanReport>(cancellationToken: ct)
               ?? new ScanReport([], 0, false, "The scan returned nothing at all.");
    }

    // -------------------------------------------------------------------- sessions

    public async Task<IReadOnlyList<SessionDto>> SessionsAsync()
        => await http.GetFromJsonAsync<List<SessionDto>>("api/sessions") ?? [];

    public async Task<(SessionDto? Session, string? Error)> ConnectAsync(string address, int timeoutMs = 5000)
    {
        var response = await http.PostAsJsonAsync("api/sessions", new ConnectRequest(address, timeoutMs));
        if (response.IsSuccessStatusCode)
            return (await response.Content.ReadFromJsonAsync<SessionDto>(), null);

        // The server hands back its own message — "connection refused", "did not answer
        // within 5000 ms" — and that is far more use than "502".
        string body = await response.Content.ReadAsStringAsync();
        return (null, Readable(body, response.StatusCode.ToString()));
    }

    public async Task DisconnectAsync(string id) => await http.DeleteAsync($"api/sessions/{id}");

    public async Task<CommandReply> SendAsync(string id, string text)
    {
        var response = await http.PostAsJsonAsync($"api/sessions/{id}/command", new CommandRequest(text));
        return await response.Content.ReadFromJsonAsync<CommandReply>()
               ?? new CommandReply(text, null, false, 0, "No reply from the server.");
    }

    public async Task<WaveformDto> WaveformAsync(string id, int channel)
    {
        var response = await http.PostAsync($"api/sessions/{id}/waveform?channel={channel}", null);
        return await response.Content.ReadFromJsonAsync<WaveformDto>()
               ?? new WaveformDto([], [], 0, "No reply from the server.");
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

    // --------------------------------------------------------------------- scripts

    public async Task<IReadOnlyList<ExampleDto>> ScriptExamplesAsync(string family)
        => await http.GetFromJsonAsync<List<ExampleDto>>($"api/examples/script/{family}") ?? [];

    public async Task<IReadOnlyList<ExampleDto>> SequenceExamplesAsync()
        => await http.GetFromJsonAsync<List<ExampleDto>>("api/examples/sequence") ?? [];

    public async Task<IReadOnlyList<SequenceRequirement>> RequirementsAsync(string script)
    {
        var response = await http.PostAsJsonAsync("api/sequence/requirements",
            new SequenceRunRequest(script, new Dictionary<string, string>()));
        return await response.Content.ReadFromJsonAsync<List<SequenceRequirement>>() ?? [];
    }

    public async Task<RunSummary> RunScriptAsync(string sessionId, string script)
    {
        var response = await http.PostAsJsonAsync("api/runs/script", new ScriptRunRequest(sessionId, script));
        return await response.Content.ReadFromJsonAsync<RunSummary>()
               ?? new RunSummary("", [], true, "No reply from the server.");
    }

    public async Task<RunSummary> RunSequenceAsync(string script, IReadOnlyDictionary<string, string> bindings)
    {
        var response = await http.PostAsJsonAsync("api/runs/sequence", new SequenceRunRequest(script, bindings));
        return await response.Content.ReadFromJsonAsync<RunSummary>()
               ?? new RunSummary("", [], true, "No reply from the server.");
    }

    public async Task StopAsync(string runId) => await http.PostAsync($"api/runs/{runId}/stop", null);

    // -------------------------------------------------------------------------- AI

    public async Task<AiStatus> AiStatusAsync()
        => await http.GetFromJsonAsync<AiStatus>("api/ai") ?? new AiStatus(false, "", "", "The server did not answer.");

    public async Task<AiScriptReply> AiScriptAsync(AiScriptRequest req)
    {
        var response = await http.PostAsJsonAsync("api/ai/script", req);
        return await response.Content.ReadFromJsonAsync<AiScriptReply>()
               ?? new AiScriptReply("", [], "No reply from the server.");
    }

    public async Task<AiExtractReply> AiExtractAsync(AiExtractRequest req)
    {
        var response = await http.PostAsJsonAsync("api/ai/extract", req);
        return await response.Content.ReadFromJsonAsync<AiExtractReply>()
               ?? new AiExtractReply(0, 0, [], null, "No reply from the server.");
    }

    // ------------------------------------------------------------------------- hub

    /// <summary>Connect once, lazily; every page that watches a run shares the connection.</summary>
    public async Task<HubConnection> HubAsync()
    {
        if (_hub is { State: HubConnectionState.Connected }) return _hub;
        _hub ??= new HubConnectionBuilder()
            .WithUrl(new Uri(http.BaseAddress!, "hub/bench"))
            .WithAutomaticReconnect()
            .Build();
        if (_hub.State == HubConnectionState.Disconnected) await _hub.StartAsync();
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
