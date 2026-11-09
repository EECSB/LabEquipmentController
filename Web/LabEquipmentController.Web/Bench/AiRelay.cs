using System.Text.Json;
using System.Text.Json.Nodes;
using LabEquipmentController.Web.Client.Contracts;

namespace LabEquipmentController.Web.Bench;

/// <summary>
/// The page's assistant, reaching its provider through this server — which holds the key.
///
/// The page builds each request whole, in the provider's own shape, exactly as the desktop
/// does (<see cref="AnthropicProvider"/>, <see cref="OpenAiProvider"/>), and posts it here
/// under the id of a saved connection. This looks the connection up, signs the request with
/// its key from <c>ai.json</c> or configuration, and forwards it to the address that
/// connection's provider is reached at; the provider's answer goes back as it came, status
/// and all, so the page reads its own errors. It is the AI guide's server-proxied shape, kept
/// stateless: the page sends the conversation every time, and a hundred conversations are a
/// hundred calls to one endpoint.
///
/// What it does not take from the page is anything that decides where the key goes or what
/// it pays for. The address is the connection's, never the request's, so a request cannot
/// send the key elsewhere; and the model in the body is replaced with the connection's, so
/// a page cannot spend the key on a model nobody chose. Only the provider's own headers are
/// passed on (<see cref="LlmEndpoints.RelayedHeaders"/>) — never anything that authorises.
/// Anyone who can open the page can already spend the key through the script writer; this is
/// the same door, with the same lock.
/// </summary>
public sealed class AiRelay
{
    // No clock of its own, as LlmProviders says of every client that talks to a model: the
    // page's request carries five minutes, and a page that gives up cancels this through the
    // request's own token.
    private static readonly HttpClient Shared = LlmProviders.NewHttpClient();

    private readonly AiSettingsStore settings;
    private readonly ILogger<AiRelay> log;
    private readonly HttpClient Http;

    public AiRelay(AiSettingsStore settings, ILogger<AiRelay> log) : this(settings, log, Shared) { }

    /// <summary>With the client the provider is reached through — a stand-in, in the tests.</summary>
    internal AiRelay(AiSettingsStore settings, ILogger<AiRelay> log, HttpClient http)
    {
        this.settings = settings;
        this.log = log;
        Http = http;
    }

    /// <summary>A little over the page's own clock, so the page is the one that says how long it waited.</summary>
    public static readonly TimeSpan MostWait = LlmProviders.RequestTimeout + TimeSpan.FromSeconds(30);

    public async Task<IResult> ForwardAsync(string id, HttpRequest request, CancellationToken ct)
    {
        AiConnection? connection = settings.Book.Find(id);
        if (connection is null)
            return Said(StatusCodes.Status404NotFound, "That AI connection is not on this server any more - it may have been deleted in AI settings. Pick another.");

        string key = settings.KeyFor(id);
        if (key.Length == 0 && connection.Provider != AiProvider.OpenAiCompatible)
            return Said(StatusCodes.Status400BadRequest, $"No API key is set for {connection.EffectiveName}. Add one in the AI Connection settings.");

        JsonObject body;
        try
        {
            body = (await JsonNode.ParseAsync(request.Body, cancellationToken: ct)) as JsonObject
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return Said(StatusCodes.Status400BadRequest, "The page sent something that is not a request.");
        }
        body["model"] = connection.EffectiveModel;

        var headers = new Dictionary<string, string>();
        foreach (string name in LlmEndpoints.RelayedHeaders)
        {
            if (request.Headers.TryGetValue(name, out var value) && value.Count > 0)
                headers[name] = value.ToString();
        }

        using var forward = new DirectLlmEndpoint(connection, key).Post(body.ToJsonString(), headers);
        using var clock = CancellationTokenSource.CreateLinkedTokenSource(ct);
        clock.CancelAfter(MostWait);

        try
        {
            using HttpResponseMessage answer = await Http.SendAsync(forward, clock.Token);
            string text = await answer.Content.ReadAsStringAsync(clock.Token);
            return Results.Content(text, answer.Content.Headers.ContentType?.ToString() ?? "application/json", null, (int)answer.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Said(StatusCodes.Status504GatewayTimeout, $"{connection.Info.Label} did not answer this server within {(int)MostWait.TotalSeconds} s.");
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning("AI relay could not reach {Url}: {Message}", LlmEndpoints.ChatUrl(connection), ex.Message);
            return Said(StatusCodes.Status502BadGateway, $"This server could not reach {LlmEndpoints.ChatUrl(connection)}: {ex.Message}. Check the connection's address, and that the server can reach the internet.");
        }
    }

    /// <summary>
    /// A recording from the page's microphone, written out by the connection's provider under the
    /// key this server holds. The page says which connection, as it does for a question; the
    /// address is the provider's own and is not the page's to choose.
    /// </summary>
    public async Task<AiTranscribeReply> TranscribeAsync(string id, AiTranscribeRequest asked, CancellationToken ct)
    {
        AiConnection? connection = settings.Book.Find(id);
        if (connection is null)
            return new AiTranscribeReply(null, "That AI connection is not on this server any more. Pick another under AI settings.");

        byte[] wav;
        try
        {
            wav = Convert.FromBase64String(asked.Wav);
        }
        catch (FormatException)
        {
            return new AiTranscribeReply(null, "The page sent a recording that could not be read.");
        }

        try
        {
            var (text, problem) = await SpeechTranscriber.TranscribeAsync(Http, connection, settings.KeyFor(id), wav, asked.Vocabulary, ct);
            return new AiTranscribeReply(text, problem);
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning("AI transcription could not reach {Provider}: {Message}", connection.Info.Label, ex.Message);
            return new AiTranscribeReply(null, $"This server could not reach {connection.Info.Label}: {ex.Message}");
        }
    }

    // In the shape every provider writes its own errors in, so the page's provider reads this
    // server's refusals the same way it reads Anthropic's.
    private static IResult Said(int status, string message)
        => Results.Json(new JsonObject { ["error"] = new JsonObject { ["message"] = message } }, statusCode: status);
}
