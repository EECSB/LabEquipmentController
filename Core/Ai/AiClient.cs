using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>Raised when a provider refuses a request, carrying its own message.</summary>
public sealed class AiException : Exception
{
    public AiException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Sends one prompt to a configured provider and returns the model's text.</summary>
public interface IAiClient
{
    Task<string> CompleteAsync(AiConnection connection, string apiKey, string instruction,
                               AiPayload payload, JsonNode schema, CancellationToken ct = default);
}

/// <summary>
/// The real client. One <see cref="HttpClient"/> for the process, per the usual guidance —
/// a new one per request exhausts sockets.
///
/// Kept deliberately thin: the request bodies and reply parsing live in
/// <see cref="AiRequest"/> where they can be tested without a network, and this class is
/// only the transport, the auth headers and the error surface.
/// </summary>
public sealed class AiClient : IAiClient
{
    /// <summary>
    /// Shared client with **no timeout of its own**, so the one the user configured is the one
    /// that applies.
    ///
    /// HttpClient defaults to 100 seconds. Left at that it fired first on every request longer
    /// than 100s, and the handler below — which cannot tell one cancellation from another —
    /// reported the configured figure regardless. Setting 600 produced "did not answer within
    /// 600s" after a hundred. The dialog offers up to 900 and nothing above 100 had ever
    /// worked, which is precisely the case this exists for: a whole programming guide goes to
    /// a provider that takes PDFs as a single request.
    ///
    /// The linked CancellationTokenSource below is now the only clock, which is the intended
    /// way to bound a request per-call anyway.
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<string> CompleteAsync(AiConnection connection, string apiKey,
                                            string instruction, AiPayload payload,
                                            JsonNode schema, CancellationToken ct = default)
    {
        if (connection == null) throw new ArgumentNullException(nameof(connection));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new AiException("No API key is set. Add one under Help ▸ AI Connection.");

        if (payload.IsDocument && !connection.Info.SupportsPdfUpload)
            throw new AiException(
                $"{connection.Info.Label} cannot accept an uploaded document. "
              + "Tick “Extract text locally before sending”, or choose a provider that can.");

        string url = connection.EffectiveBaseUrl + AiRequest.PathFor(connection.Provider);
        string body = AiRequest.Body(connection, instruction, payload, schema);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        AddAuth(request, connection.Provider, apiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, connection.TimeoutSeconds)));

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiException(
                $"{connection.Info.Label} did not answer within {connection.TimeoutSeconds}s. "
              + "A whole programming guide can take longer — raise the timeout, or send it in "
              + "smaller pieces.");
        }
        catch (HttpRequestException ex)
        {
            throw new AiException($"Could not reach {url}: {ex.Message}", ex);
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new AiException(
                    $"{connection.Info.Label} returned {(int)response.StatusCode} "
                  + $"{response.ReasonPhrase}: {AiRequest.ErrorFrom(text)}");
            }

            return AiRequest.TextFrom(connection.Provider, text);
        }
    }

    /// <summary>Each provider names its key header differently; none of them agree.</summary>
    private static void AddAuth(HttpRequestMessage request, AiProvider provider, string key)
    {
        switch (provider)
        {
            case AiProvider.Gemini:
                request.Headers.TryAddWithoutValidation("x-goog-api-key", key);
                break;

            case AiProvider.Anthropic:
                request.Headers.TryAddWithoutValidation("x-api-key", key);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                break;

            case AiProvider.OpenAiCompatible:
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                break;
        }
    }
}
