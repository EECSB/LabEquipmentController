using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// Any OpenAI-compatible chat-completions endpoint: OpenAI itself, Google's Gemini through its
/// OpenAI-compatible endpoint, or a server of one's own — Ollama, LM Studio, vLLM, a company's own.
///
/// **One adapter for three kinds of server**, which is GraphDBViewer's choice, kept by GDS Viewer
/// and here: Google answers this shape at its own address, so a Gemini key works here without a
/// provider of its own. Where a request goes is the <see cref="ILlmEndpoint"/>'s to say
/// (<see cref="LlmEndpoints.ChatUrl"/>), as is the key, so nothing here holds either — and GDS
/// Viewer's refusals before sending, for a server with no address and a model with no key or no
/// id, went with them: a connection here always has an address and a model
/// (<see cref="AiConnection.EffectiveBaseUrl"/>, <see cref="AiConnection.EffectiveModel"/>), and
/// whether it has a key is the endpoint's to know.
///
/// **A picture goes back in a user turn.** Chat completions take an image only from the user, never
/// in a tool's result, so a tool that took one answers in words in its tool turn and the picture
/// follows in a user turn of its own, saying which call it came from.
/// </summary>
public sealed class OpenAiProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly ILlmEndpoint _endpoint;
    private readonly AiConnection _connection;
    private readonly string _label;
    private readonly string _effort;

    /// <summary>
    /// A provider for one connection, with the effort chosen for this request — dropped when the
    /// model does not take it. The connection is copied, so a setting changed while a run is going
    /// does not change the run.
    /// </summary>
    public OpenAiProvider(HttpClient http, ILlmEndpoint endpoint, AiConnection connection, string effort = "")
    {
        _http = http;
        _endpoint = endpoint;
        _connection = connection.Clone();
        _label = _connection.Info.Label;
        Model = _connection.EffectiveModel;
        _effort = AiModels.ResolveEffort(_connection, effort);
        Price = AiPricing.For(_connection, DateOnly.FromDateTime(DateTime.UtcNow));
    }

    public string Model { get; }

    public LlmUsage Spent { get; } = new();

    public LlmPrice? Price { get; }

    // ------------------------------------------------------------------------- the request

    /// <summary>
    /// The conversation in the chat-completions shape, led by the system turn when there is a
    /// prompt for it. A prompt with nothing in it, whitespace alone included, is no prompt, and no
    /// turn is sent for it rather than one whose content is null, which the API refuses.
    ///
    /// Empty turns are dropped: one is what a stopped or failed answer leaves behind, and some
    /// servers refuse an empty content outright.
    ///
    /// A question with files is a list of parts, the files ahead of its words, written for the
    /// connection it goes to — which decides whether a PDF goes as the file or as its words; see
    /// <see cref="AiAttachment.PdfGoesAsFile"/>.
    /// </summary>
    public static JsonArray ToWireMessages(string system, IReadOnlyList<LlmMessage> conversation, AiConnection connection)
    {
        var messages = new JsonArray();

        if (!string.IsNullOrWhiteSpace(system))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });

        foreach (LlmMessage turn in conversation)
        {
            if (turn.Attachments.Count > 0)
                messages.Add(new JsonObject { ["role"] = turn.Role, ["content"] = ContentWithFiles(turn, connection) });
            else if (!string.IsNullOrWhiteSpace(turn.Text))
                messages.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Text });
        }

        return messages;
    }

    /// <summary>A question's files as parts ahead of its words: a picture as an image_url, a PDF as a file or as its words, a text file as text between tags.</summary>
    public static JsonArray ContentWithFiles(LlmMessage turn, AiConnection connection)
    {
        var content = new JsonArray();

        foreach (AttachmentPart part in AiAttachment.Parts(turn.Attachments, connection))
            content.Add(Part(part));

        if (!string.IsNullOrWhiteSpace(turn.Text))
            content.Add(new JsonObject { ["type"] = "text", ["text"] = turn.Text });

        return content;
    }

    /// <summary>
    /// One part of a question's files as chat completions take it.
    ///
    /// A picture is a data URL in an image_url part. A PDF is a file part with its bytes as a data
    /// URL in file_data — the one kind of file OpenAI's chat completions take, and sent only where
    /// the connection says the endpoint reads one (<see cref="AiConnection.ChatTakesPdf"/>), which
    /// of this family is OpenAI's own address alone. OpenAI's reference marks filename optional and
    /// says only that file_data is base64, but requests with bare base64, or the name spelled
    /// file_name, are reported failing; GDS Viewer sends the data URL and the name, and so does this.
    /// </summary>
    public static JsonObject Part(AttachmentPart part) => part.Kind switch
    {
        AttachmentKind.Image => new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject { ["url"] = $"data:{part.MediaType};base64,{part.Base64}" },
        },
        AttachmentKind.Pdf => new JsonObject
        {
            ["type"] = "file",
            ["file"] = new JsonObject
            {
                ["filename"] = part.Name,
                ["file_data"] = $"data:application/pdf;base64,{part.Base64}",
            },
        },
        _ => new JsonObject { ["type"] = "text", ["text"] = part.Text },
    };

    /// <summary>The tools as the tools array: each a function with a name, a description and its parameters.</summary>
    public static JsonArray BuildToolsArray(IReadOnlyList<LlmTool> tools)
    {
        var array = new JsonArray();

        foreach (LlmTool tool in tools)
        {
            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.InputSchemaJson),
                },
            });
        }

        return array;
    }

    /// <summary>
    /// A request: the model, the conversation so far, the tools when there are any, and an effort
    /// when one was chosen.
    ///
    /// **The effort only where the model takes it.** A reasoning model reads it, and a chat model
    /// refuses the whole request over it — so it is resolved against the model's levels when the
    /// provider is made (<see cref="AiModels.ResolveEffort"/>), and an empty one sends no field at
    /// all. GDS Viewer sent whatever its window chose, trusting the window to offer only levels the
    /// model takes; here the provider drops a stale one too, as GDS Viewer's Anthropic provider does.
    /// </summary>
    public static string BuildRequestBody(string model, JsonArray messages, JsonArray? tools, string effort)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages.DeepClone(),
        };

        if (tools is not null && tools.Count > 0) body["tools"] = tools.DeepClone();
        if (!string.IsNullOrEmpty(effort)) body["reasoning_effort"] = effort;

        return body.ToJsonString();
    }

    /// <summary>
    /// The assistant's message as it goes back into the next request: its words and its calls.
    ///
    /// **The calls are copied as they came**, not rebuilt from what was read out of them. Gemini 3
    /// hands back a signature of its thinking inside each call — <c>extra_content</c> — and refuses
    /// the next request if it does not come back with it. Anything else a server adds rides along
    /// the same way.
    ///
    /// Only the role, the content and the calls, though, rather than the whole message: OpenAI's
    /// reply carries fields of its own — refusal, annotations — that its requests do not take.
    /// </summary>
    public static JsonObject EchoAssistantMessage(JsonElement message)
    {
        var echo = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = StringIn(message, "content"),
        };

        if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("tool_calls", out JsonElement calls))
            echo["tool_calls"] = JsonNode.Parse(calls.GetRawText());

        return echo;
    }

    /// <summary>A call's answer, as a tool turn. **A refusal says so in words**, there being no flag for it here.</summary>
    public static JsonObject ToolTurn(string id, LlmToolOutput output) => new()
    {
        ["role"] = "tool",
        ["tool_call_id"] = id,
        ["content"] = output.IsError ? "Refused: " + output.Text : output.Text,
    };

    /// <summary>The pictures a round of calls took, as one user turn after their tool turns, each named by its call.</summary>
    public static JsonObject PictureTurn(IReadOnlyList<(string Tool, string Png)> pictures)
    {
        var parts = new JsonArray();

        foreach (var (tool, png) in pictures)
        {
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = $"The picture {tool} took:" });
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + png },
            });
        }

        return new JsonObject { ["role"] = "user", ["content"] = parts };
    }

    // --------------------------------------------------------------------------- the reply

    /// <summary>The calls in an assistant message: empty when it answered in words.</summary>
    public static List<LlmToolCall> ParseToolCalls(JsonElement message)
    {
        var calls = new List<LlmToolCall>();

        if (message.ValueKind != JsonValueKind.Object
         || !message.TryGetProperty("tool_calls", out JsonElement asked)
         || asked.ValueKind != JsonValueKind.Array)
            return calls;

        foreach (JsonElement call in asked.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object
             || !call.TryGetProperty("function", out JsonElement function)
             || StringIn(function, "name") is not string called)
                continue;

            // A string of JSON, as OpenAI sends it, or the object itself, as some servers do.
            string arguments = "{}";
            if (function.TryGetProperty("arguments", out JsonElement said))
            {
                if (said.ValueKind == JsonValueKind.String) arguments = said.GetString() ?? "{}";
                else if (said.ValueKind == JsonValueKind.Object) arguments = said.GetRawText();
            }

            calls.Add(new LlmToolCall { Id = StringIn(call, "id") ?? "", Name = called, ArgumentsJson = arguments });
        }

        return calls;
    }

    /// <summary>A plain reply read: choices[0].message.content, or what the server said was wrong. <paramref name="label"/> names the server.</summary>
    public static LlmResult ParseResponse(string json, string label)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (LlmProviders.ErrorIn(json) is string said) return LlmResult.Failure(said);

            if (FirstChoice(document.RootElement) is not JsonElement choice
             || choice.ValueKind != JsonValueKind.Object
             || !choice.TryGetProperty("message", out JsonElement message))
                return LlmResult.Failure($"{label} answered in a shape this app does not know.");

            return Answered(ContentOf(message), FinishReason(choice));
        }
        catch (JsonException)
        {
            return LlmResult.Failure($"{label} answered with something that is not JSON.");
        }
    }

    // ------------------------------------------------------------------------------ sending

    public async Task<LlmResult> CompleteAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation, CancellationToken ct)
    {
        string body = BuildRequestBody(Model, ToWireMessages(systemPrompt, conversation, _connection), tools: null, _effort);

        var (json, problem) = await LlmProviders.SendAsync(_http, _endpoint.Post(body), _label, ct).ConfigureAwait(false);
        if (problem is not null || json is null) return LlmResult.Failure(problem ?? $"{_label} sent nothing back.");

        RecordUsage(json);
        return ParseResponse(json, _label);
    }

    public async Task<LlmResult> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation,
                                                        ILlmToolRunner tools, CancellationToken ct,
                                                        int maxRounds = LlmProviders.DefaultToolRounds)
    {
        JsonArray toolsArray = BuildToolsArray(tools.Tools);
        JsonArray messages = ToWireMessages(systemPrompt, conversation, _connection);
        var progress = new ToolProgress();

        for (int round = 0; round < maxRounds; round++)
        {
            string body = BuildRequestBody(Model, messages, toolsArray, _effort);

            var (json, problem) = await LlmProviders.SendAsync(_http, _endpoint.Post(body), _label, ct).ConfigureAwait(false);
            if (problem is not null || json is null) return LlmResult.Failure(problem ?? $"{_label} sent nothing back.");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return LlmResult.Failure($"{_label} answered with something that is not JSON.");
            }

            using (document)
            {
                // Before anything can end the run: a round is billed whatever comes of it.
                Spent.Add(LlmUsage.FromOpenAi(document.RootElement));

                if (LlmProviders.ErrorIn(json) is string said) return LlmResult.Failure(said);

                if (FirstChoice(document.RootElement) is not JsonElement choice
                 || choice.ValueKind != JsonValueKind.Object
                 || !choice.TryGetProperty("message", out JsonElement message))
                    return LlmResult.Failure($"{_label} answered in a shape this app does not know.");

                List<LlmToolCall> calls = ParseToolCalls(message);
                if (calls.Count == 0) return Answered(ContentOf(message), FinishReason(choice));

                // A call cut off at the limit has half its arguments, and running it would be
                // running a guess.
                if (FinishReason(choice) == "length")
                    return LlmResult.Failure("The model ran into its length limit partway through a call, so the call was not run.");

                messages.Add(EchoAssistantMessage(message));

                var pictures = new List<(string Tool, string Png)>();
                var ran = new List<(LlmToolCall, LlmToolOutput)>();

                foreach (LlmToolCall call in calls)
                {
                    LlmToolOutput output = await tools.RunToolAsync(call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
                    messages.Add(ToolTurn(call.Id, output));
                    ran.Add((call, output));

                    if (output.Png is string png) pictures.Add((call.Name, png));
                }

                // After every tool turn of the round, since the API wants each call answered
                // before anything else is said.
                if (pictures.Count > 0) messages.Add(PictureTurn(pictures));

                if (progress.IsStuckAfter(ran)) return LlmResult.Failure(LlmProviders.RoundsStuck);
            }
        }

        return LlmResult.Paused(LlmProviders.RoundsPaused(maxRounds));
    }

    // ---------------------------------------------------------------------------- helpers

    private static JsonElement? FirstChoice(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("choices", out JsonElement choices)
        && choices.ValueKind == JsonValueKind.Array
        && choices.GetArrayLength() > 0
            ? choices[0]
            : null;

    private static string ContentOf(JsonElement message) => (StringIn(message, "content") ?? "").Trim();

    private static string FinishReason(JsonElement choice) => StringIn(choice, "finish_reason") ?? "";

    /// <summary>A string property of an object, or null where there is no object, no property, or no string.</summary>
    private static string? StringIn(JsonElement owner, string name)
        => owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The text of a finished reply, said as cut off when the length limit is what finished it.</summary>
    private static LlmResult Answered(string text, string reason)
    {
        if (text.Length == 0)
            return LlmResult.Failure(reason == "length"
                ? "The model ran into its length limit before it wrote anything."
                : "The model sent back an empty answer.");

        return LlmResult.Success(reason == "length"
            ? text + Environment.NewLine + Environment.NewLine + "(Cut off at the length limit.)"
            : text);
    }

    /// <summary>Adds what an answer says it cost to <see cref="Spent"/>.</summary>
    private void RecordUsage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            Spent.Add(LlmUsage.FromOpenAi(document.RootElement));
        }
        catch (JsonException)
        {
            // Not JSON is ParseResponse's to say; here it is only nothing to count.
        }
    }
}
