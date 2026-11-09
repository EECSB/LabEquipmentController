using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>How the AI assistant's microphone turns speech into words.</summary>
public enum SpeechMode
{
    /// <summary>The browser's own recognition, live as the words are spoken. Chrome and Edge, through Google's and Microsoft's servers.</summary>
    Browser,

    /// <summary>A recording sent to the AI provider, OpenAI's transcription or Gemini, when the microphone is pressed again.</summary>
    Provider,
}

/// <summary>
/// A recording written out by the AI provider, from GDS Viewer: the microphone's WAV sent to
/// OpenAI's transcription endpoint, or to Gemini, which takes audio in an ordinary request.
/// Anthropic's API takes no audio, so a Claude model picked in the chat leaves it to the first
/// saved OpenAI or Gemini model (<see cref="Pick"/>).
///
/// **Told the words a bench conversation is full of**, the instruments open and the words of
/// SCPI, which is most of what the provider is better at than the browser: "star I D N" written
/// as *IDN?, and a kilohertz as kHz.
/// </summary>
public static class SpeechTranscriber
{
    /// <summary>OpenAI's transcription model: more accurate than whisper-1 on technical words, at about the same price.</summary>
    public const string OpenAiModel = "gpt-4o-transcribe";

    public const string OpenAiUrl = "https://api.openai.com/v1/audio/transcriptions";

    /// <summary>Gemini's own API, which takes a WAV inline: its OpenAI-compatible endpoint has no transcription.</summary>
    public const string GeminiApi = "https://generativelanguage.googleapis.com/v1beta/models/";

    /// <summary>The words a bench conversation is full of, said to the provider so it writes them as the bench does.</summary>
    public const string BenchWords = "SCPI, *IDN?, *RST, oscilloscope, multimeter, power supply, signal generator, waveform, trigger, "
                                   + "channel, sweep, duty cycle, Vpp, Vrms, mV, kHz, MHz, GHz, mA, µA, ms, µs, ns, dBm, ohm";

    /// <summary>
    /// Whether a saved connection's provider can write out a recording: OpenAI's own, and Gemini's
    /// own. The provider's own address only, blank or written out in full: a server in between
    /// that speaks chat completions may have no transcription, and the key saved for it is that
    /// server's, not the provider's to be sent.
    /// </summary>
    public static bool CanTranscribe(AiConnection connection)
    {
        if (connection.Provider == AiProvider.Anthropic) return false;
        return string.Equals(connection.EffectiveBaseUrl.TrimEnd('/'), connection.Info.DefaultBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The connection a recording goes to: the one picked in the chat when it can, or else the first saved one that can, or null.</summary>
    public static AiConnection? Pick(AiConnection? picked, IEnumerable<AiConnection> saved)
    {
        if (picked is not null && CanTranscribe(picked)) return picked;
        return saved.FirstOrDefault(CanTranscribe);
    }

    /// <summary>The words to tell the provider: the bench's own and the names given, once each.</summary>
    public static string Vocabulary(IEnumerable<string> names)
    {
        var words = new List<string>(BenchWords.Split(", "));
        foreach (string name in names)
        {
            string trimmed = name.Trim();
            if (trimmed.Length > 0 && !words.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) words.Add(trimmed);
        }
        return string.Join(", ", words.Take(80));
    }

    public static HttpRequestMessage OpenAiRequest(string apiKey, byte[] wav, string vocabulary)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        form.Add(file, "file", "speech.wav");
        form.Add(new StringContent(OpenAiModel), "model");
        form.Add(new StringContent($"A question to a lab bench assistant about test instruments. Words that may come up: {vocabulary}."), "prompt");
        form.Add(new StringContent("json"), "response_format");

        var request = new HttpRequestMessage(HttpMethod.Post, OpenAiUrl) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    public static HttpRequestMessage GeminiRequest(AiConnection connection, string apiKey, byte[] wav, string vocabulary)
    {
        var body = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["parts"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["text"] = "Write out exactly what is said in this recording, a question to a lab bench assistant about test instruments. "
                                     + "Reply with the words alone: no quotes, no notes, nothing about the recording. Write numbers as digits and units "
                                     + $"as an engineer writes them (mV, kHz, µs). Words that may come up: {vocabulary}.",
                        },
                        new JsonObject
                        {
                            ["inline_data"] = new JsonObject { ["mime_type"] = "audio/wav", ["data"] = Convert.ToBase64String(wav) },
                        },
                    },
                },
            },
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{GeminiApi}{Uri.EscapeDataString(connection.EffectiveModel)}:generateContent")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-goog-api-key", apiKey);
        return request;
    }

    /// <summary>The words in a provider's answer, or null when it holds none.</summary>
    public static string? TextIn(AiProvider provider, string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (provider == AiProvider.Gemini) return GeminiText(root);

            return root.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()?.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GeminiText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out JsonElement candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
            return null;
        if (!candidates[0].TryGetProperty("content", out JsonElement content) || !content.TryGetProperty("parts", out JsonElement parts) || parts.ValueKind != JsonValueKind.Array)
            return null;

        var said = new StringBuilder();
        foreach (JsonElement part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String) said.Append(text.GetString());
        }
        return said.ToString().Trim();
    }

    /// <summary>Sends a recording to the connection's provider, and answers the words it heard or why there are none.</summary>
    public static async Task<(string? Text, string? Problem)> TranscribeAsync(HttpClient http, AiConnection connection, string apiKey,
                                                                             byte[] wav, string vocabulary, CancellationToken ct)
    {
        if (!CanTranscribe(connection))
            return (null, $"{connection.EffectiveName} cannot write out speech: that takes an OpenAI or Gemini model, at the provider's own address.");
        if (apiKey.Length == 0)
            return (null, $"No API key is set on {connection.EffectiveName}. Add one under AI settings.");

        using HttpRequestMessage request = connection.Provider == AiProvider.Gemini
            ? GeminiRequest(connection, apiKey, wav, vocabulary)
            : OpenAiRequest(apiKey, wav, vocabulary);

        string label = connection.Info.Label;
        (string? json, string? problem) = await LlmProviders.SendAsync(http, request, label, ct).ConfigureAwait(false);
        if (problem is not null) return (null, problem);

        return json is not null && TextIn(connection.Provider, json) is string text
            ? (text, null)
            : (null, $"{label} answered, with no words in it.");
    }
}
