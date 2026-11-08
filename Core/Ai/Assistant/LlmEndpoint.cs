using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;

namespace LabEquipmentController;

/// <summary>
/// Where the assistant's requests go, and how they are signed.
///
/// Its own thing rather than part of a provider because the two builds hold the key in
/// different places. The desktop has it, under DPAPI, and signs the request itself
/// (<see cref="DirectLlmEndpoint"/>). The web page never has it: its requests go to the bench's
/// own server, which looks the connection up, signs the request with the key in
/// <c>ai.json</c> and forwards it — the "server-proxied" column of the AI assistant guide, and
/// the shape the page's script writer has always had. The providers build the same body
/// either way and cannot tell which they were given.
/// </summary>
public interface ILlmEndpoint
{
    /// <summary>
    /// A request carrying <paramref name="body"/> to the connection's chat endpoint, signed for
    /// it. <paramref name="headers"/> are the provider's own additions — Anthropic's beta flag
    /// for fast mode — and are the only headers a relay passes on.
    /// </summary>
    HttpRequestMessage Post(string body, IReadOnlyDictionary<string, string>? headers = null);
}

/// <summary>
/// The chat endpoint each provider family is reached at, and how a request to it is signed —
/// in one place, so the desktop's direct requests and the web server's relayed ones cannot
/// disagree about either.
/// </summary>
public static class LlmEndpoints
{
    /// <summary>The headers a relay may pass on from the page: the provider's own, never auth.</summary>
    public static readonly IReadOnlyList<string> RelayedHeaders = ["anthropic-beta"];

    /// <summary>
    /// The address a connection's chat requests go to.
    ///
    /// Gemini is reached through Google's OpenAI-compatible endpoint rather than the
    /// Interactions API the datasheet reader uses: the assistant's tool loop is written for two
    /// families, Anthropic's and the OpenAI-compatible one, and a Google AI Studio key works at
    /// that endpoint as it is. What it costs is PDFs — the compatible endpoint takes pictures
    /// and not files — so a PDF goes to Gemini as its text (<see cref="AiConnection.ChatPdfAsText"/>).
    /// </summary>
    public static string ChatUrl(AiConnection connection) => connection.Provider switch
    {
        AiProvider.Anthropic => connection.EffectiveBaseUrl + "/v1/messages",
        AiProvider.Gemini => connection.EffectiveBaseUrl + "/v1beta/openai/chat/completions",
        _ => connection.EffectiveBaseUrl + "/v1/chat/completions",
    };

    /// <summary>
    /// Puts the key where the connection's provider reads it. A blank key is sent as nothing at
    /// all rather than as an empty header, since a server of one's own may need none.
    /// </summary>
    public static void Sign(HttpRequestMessage request, AiConnection connection, string? apiKey)
    {
        if (connection.Provider == AiProvider.Anthropic)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            return;
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
    }

    /// <summary>A JSON POST of <paramref name="body"/> to <paramref name="url"/> with the provider's own headers on it.</summary>
    public static HttpRequestMessage Json(string url, string body, IReadOnlyDictionary<string, string>? headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);
        }
        return request;
    }
}

/// <summary>
/// Straight to the provider, with the key in hand: the desktop's way, and the web server's when
/// it forwards a relayed request.
/// </summary>
public sealed class DirectLlmEndpoint(AiConnection connection, string? apiKey) : ILlmEndpoint
{
    public HttpRequestMessage Post(string body, IReadOnlyDictionary<string, string>? headers = null)
    {
        var request = LlmEndpoints.Json(LlmEndpoints.ChatUrl(connection), body, headers);
        LlmEndpoints.Sign(request, connection, apiKey);
        return request;
    }
}
