using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabEquipmentController;

/// <summary>What is being sent: either text, or a document to be uploaded whole.</summary>
public sealed class AiPayload
{
    private AiPayload() { }

    public string? Text { get; private init; }
    public byte[]? Bytes { get; private init; }
    public string? MimeType { get; private init; }

    public bool IsDocument => Bytes != null;

    public static AiPayload FromText(string text) => new() { Text = text };

    public static AiPayload FromDocument(byte[] bytes, string mimeType)
        => new() { Bytes = bytes, MimeType = mimeType };
}

/// <summary>
/// Builds the request body for each provider, and finds the model's text in the reply.
///
/// Split out from the HTTP so the three shapes can be asserted in tests without a network:
/// the bodies are the part most likely to be silently wrong, and the part that changes when
/// a provider revises its API.
/// </summary>
public static class AiRequest
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>Path appended to the base URL, per provider.</summary>
    public static string PathFor(AiProvider provider) => provider switch
    {
        AiProvider.Gemini => "/v1beta/interactions",
        AiProvider.Anthropic => "/v1/messages",
        AiProvider.OpenAiCompatible => "/v1/chat/completions",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    /// <summary>
    /// The request body. <paramref name="schema"/> is a JSON Schema the reply must satisfy;
    /// each provider asks for that a different way, and the OpenAI-compatible one can only
    /// be asked for "some JSON object", so the schema also goes in the prompt there.
    /// </summary>
    public static string Body(AiConnection cn, string instruction, AiPayload payload, JsonNode schema)
    {
        return cn.Provider switch
        {
            AiProvider.Gemini => Gemini(cn, instruction, payload, schema),
            AiProvider.Anthropic => Anthropic(cn, instruction, payload, schema),
            AiProvider.OpenAiCompatible => OpenAi(cn, instruction, payload, schema),
            _ => throw new ArgumentOutOfRangeException(nameof(cn)),
        };
    }

    // Gemini, Interactions API: a flat input[] of typed parts, and response_format carrying
    // the schema. Documents go inline as base64 under "data".
    private static string Gemini(AiConnection cn, string instruction, AiPayload payload, JsonNode schema)
    {
        var input = new JsonArray();
        if (payload.IsDocument)
        {
            input.Add(new JsonObject
            {
                ["type"] = "document",
                ["mime_type"] = payload.MimeType,
                ["data"] = Convert.ToBase64String(payload.Bytes!),
            });
        }
        input.Add(new JsonObject { ["type"] = "text", ["text"] = Prompt(instruction, payload) });

        var body = new JsonObject
        {
            ["model"] = cn.EffectiveModel,
            ["input"] = input,
            ["response_format"] = new JsonObject
            {
                ["type"] = "text",
                ["mime_type"] = "application/json",
                ["schema"] = schema.DeepClone(),
            },
        };

        // How hard to think, where Gemini keeps it. Verified against the live API: it
        // rejects unknown parameters, at the top level and inside generation_config alike,
        // so a request that goes through is a field that exists. It bites, too — the same
        // arithmetic question came back after 519 thought tokens at "low" and 3,030 at
        // "high". The four names below are the four the API listed when asked for one it
        // does not take.
        if (Level(cn.Effort) is string level)
            body["generation_config"] = new JsonObject { ["thinking_level"] = level };

        return body.ToJsonString(Compact);
    }

    // Anthropic Messages API: content blocks inside one user message. A PDF is a "document"
    // block with a base64 source. max_tokens is required, not optional.
    private static string Anthropic(AiConnection cn, string instruction, AiPayload payload, JsonNode schema)
    {
        var content = new JsonArray();
        if (payload.IsDocument)
        {
            content.Add(new JsonObject
            {
                ["type"] = "document",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = payload.MimeType,
                    ["data"] = Convert.ToBase64String(payload.Bytes!),
                },
            });
        }
        content.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = Prompt(instruction, payload) + "\n\nReply with JSON matching this schema "
                     + "and nothing else:\n" + schema.ToJsonString(Compact),
        });

        var body = new JsonObject
        {
            ["model"] = cn.EffectiveModel,
            ["max_tokens"] = 8192,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = content },
            },
        };

        // Anthropic buys thinking by the token rather than by the word, so the scale is
        // spent rather than named. Minimal is thinking off, which is what the Messages API
        // does when asked for nothing — so it sends nothing, and differs from Default only
        // in that the user chose it.
        //
        // max_tokens has to leave room for the answer as well as the thinking: the API
        // requires it to exceed the budget, and a ceiling only a little over the budget is a
        // model that thinks and then has nowhere to write. The eight thousand the reply was
        // already given is kept on top of whatever is spent thinking.
        if (Budget(cn.Effort) is int budget)
        {
            body["thinking"] = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = budget,
            };
            body["max_tokens"] = 8192 + budget;
        }

        return body.ToJsonString(Compact);
    }

    // OpenAI-compatible: plain chat completions. json_object mode is as much as the common
    // denominator supports, so the schema is stated in the prompt rather than enforced.
    private static string OpenAi(AiConnection cn, string instruction, AiPayload payload, JsonNode schema)
    {
        if (payload.IsDocument)
            throw new NotSupportedException(
                "This endpoint cannot take an uploaded document; extract the text first.");

        var body = new JsonObject
        {
            ["model"] = cn.EffectiveModel,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = Prompt(instruction, payload)
                                + "\n\nReply with JSON matching this schema and nothing else:\n"
                                + schema.ToJsonString(Compact),
                },
            },
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
        };

        // The one field of the three that is sent only when asked for. This shape covers
        // endpoints that have never heard of reasoning_effort — Ollama, LM Studio, an older
        // model behind OpenAI's own URL — and some of them refuse a request carrying a field
        // they do not know rather than ignoring it. Default sends nothing, which is why it
        // is the default.
        if (Level(cn.Effort) is string effort)
            body["reasoning_effort"] = effort;

        return body.ToJsonString(Compact);
    }

    /// <summary>
    /// The effort as a word, for the two providers that take one — Gemini's thinking_level
    /// and an OpenAI-compatible endpoint's reasoning_effort, which happen to spell the same
    /// four the same way. Null for <see cref="AiEffort.Default"/>, and null means the field
    /// is left out rather than sent empty.
    /// </summary>
    private static string? Level(AiEffort effort) => effort switch
    {
        AiEffort.Minimal => "minimal",
        AiEffort.Low => "low",
        AiEffort.Medium => "medium",
        AiEffort.High => "high",
        _ => null,
    };

    /// <summary>
    /// The effort as a token budget, for the provider that takes one.
    ///
    /// Null where no thinking block should be sent at all, which is both Default and Minimal:
    /// the Messages API has no way to ask for a little thinking — the floor is 1,024 tokens —
    /// so the honest reading of "minimal" there is none.
    /// </summary>
    private static int? Budget(AiEffort effort) => effort switch
    {
        AiEffort.Low => 4096,
        AiEffort.Medium => 12288,
        AiEffort.High => 24576,
        _ => null,
    };

    private static string Prompt(string instruction, AiPayload payload)
        => payload.IsDocument
            ? instruction
            : instruction + "\n\n--- BEGIN DOCUMENT ---\n" + payload.Text + "\n--- END DOCUMENT ---";

    // -------------------------------------------------------------------------- responses

    /// <summary>
    /// Pull the model's text out of a reply. Each provider puts it somewhere different; if
    /// none of the known paths match, the whole body is returned so the caller's error names
    /// something the user can act on rather than "parse failed".
    /// </summary>
    public static string TextFrom(AiProvider provider, string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return json; }
        if (root == null) return json;

        string? text = provider switch
        {
            AiProvider.Gemini => GeminiText(root),
            AiProvider.Anthropic => FirstTextIn(root["content"]),
            AiProvider.OpenAiCompatible =>
                root["choices"]?[0]?["message"]?["content"]?.GetValue<string>(),
            _ => null,
        };

        return string.IsNullOrWhiteSpace(text) ? json : text!;
    }

    /// <summary>
    /// The answer out of an Interactions reply.
    ///
    /// The response is a list of execution steps, and the model's text is in the content of
    /// the last one that has any. Reasoning models put a <c>"type":"thought"</c> step in
    /// front carrying only an opaque signature, so the steps are walked from the back and
    /// thoughts are skipped — taking the first step would return the reasoning, or nothing.
    ///
    /// Verified against a live gemini-3.6-flash reply. An earlier version read
    /// <c>output_text</c>, which the documentation summary suggested and the API does not
    /// send; every extraction came back empty because the whole envelope was being handed to
    /// the JSON parser as if it were the answer.
    /// </summary>
    private static string? GeminiText(JsonNode root)
    {
        if (root["output_text"] is JsonNode direct && direct.GetValueKind() == JsonValueKind.String)
            return direct.GetValue<string>();

        if (root["steps"] is JsonArray steps)
        {
            for (int i = steps.Count - 1; i >= 0; i--)
            {
                JsonNode? step = steps[i];
                if (step?["type"]?.GetValueKind() == JsonValueKind.String
                    && step["type"]!.GetValue<string>() == "thought") continue;

                if (FirstTextIn(step?["content"]) is string s && s.Length > 0) return s;
            }
        }

        return FirstTextIn(root["model_output"]);
    }

    /// <summary>First {"type":"text","text":...} block in an array of content blocks.</summary>
    private static string? FirstTextIn(JsonNode? node)
    {
        if (node is not JsonArray arr) return null;
        foreach (JsonNode? item in arr)
        {
            if (item?["text"] is JsonNode t && t.GetValueKind() == JsonValueKind.String)
                return t.GetValue<string>();
        }
        return null;
    }

    /// <summary>
    /// A provider's error message, dug out of whatever error envelope it uses. Falls back to
    /// the raw body, truncated — an unreadable wall of JSON in a message box helps nobody.
    /// </summary>
    public static string ErrorFrom(string json)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(json);
            string? message = root?["error"]?["message"]?.GetValue<string>()
                           ?? root?["error"]?.GetValue<string>()
                           ?? root?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message)) return message!;
        }
        catch { /* fall through to the raw body */ }

        json = json.Trim();
        return json.Length <= 400 ? json : json[..400] + "…";
    }
}
