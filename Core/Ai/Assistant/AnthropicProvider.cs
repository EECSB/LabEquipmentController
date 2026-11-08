using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// Anthropic's Messages API, with its tool_use and tool_result loop.
///
/// Request building and reply reading are static and tested on their own; the sending is thin.
/// GDS Viewer's provider, with what a page calling Anthropic itself needs taken out: no page here
/// does, so there is no direct-browser header, and the key is the <see cref="ILlmEndpoint"/>'s
/// business rather than this class's. With it went GDS Viewer's "this model has no API key"
/// before sending — whether there is a key is known only to whoever made the endpoint, and a
/// request without one answers 401, which <see cref="LlmProviders.SendAsync"/> says as a key that
/// may be wrong.
/// </summary>
public sealed class AnthropicProvider : ILlmProvider
{
    /// <summary>
    /// The output cap every request is sent with, which Anthropic requires.
    ///
    /// Generous on purpose: a ceiling, not an allowance. What is billed is what the model writes,
    /// and a cap set too low cuts an answer off mid-sentence rather than costing less. A model that
    /// calls tools spends this on its calls too, and a long script proposed for the editor is a
    /// long call. The datasheet reader sends the same figure (<see cref="AiRequest"/>).
    /// </summary>
    public const int DefaultMaxTokens = 8192;

    /// <summary>The beta a request in fast mode is sent under, beside its speed — see <see cref="AiModels.SupportsFast"/>.</summary>
    public const string FastBeta = "fast-mode-2026-02-01";

    /// <summary>What the beta goes in: one of <see cref="LlmEndpoints.RelayedHeaders"/>, so the web page's relay passes it on.</summary>
    private static readonly IReadOnlyDictionary<string, string> FastHeaders =
        new Dictionary<string, string> { ["anthropic-beta"] = FastBeta };

    /// <summary>
    /// The provider's name in sentences: <see cref="AiProviderInfo.Label"/>, the name the AI
    /// Connection box gives it — fixed for every Anthropic connection, so known without one.
    /// </summary>
    private static string Label => AiProviderInfo.For(AiProvider.Anthropic).Label;

    private readonly HttpClient _http;
    private readonly ILlmEndpoint _endpoint;
    private readonly AiConnection _connection;
    private readonly string _effort;
    private readonly bool _fast;

    /// <summary>
    /// A provider for one connection, with the effort and fast mode chosen for this request —
    /// either dropped when the model does not take it. The connection is copied, so a setting
    /// changed while a run is going does not change the run.
    /// </summary>
    public AnthropicProvider(HttpClient http, ILlmEndpoint endpoint, AiConnection connection,
                             string effort = "", bool fast = false)
    {
        _http = http;
        _endpoint = endpoint;
        _connection = connection.Clone();
        Model = _connection.EffectiveModel;
        _effort = AiModels.ResolveEffort(_connection, effort);
        _fast = fast && AiModels.SupportsFast(_connection);
        Price = AiPricing.For(_connection, DateOnly.FromDateTime(DateTime.UtcNow), _fast);
    }

    public string Model { get; }

    public LlmUsage Spent { get; } = new();

    public LlmPrice? Price { get; }

    // ------------------------------------------------------------------------- the request

    /// <summary>
    /// The conversation in the Messages API's own shape.
    ///
    /// Empty turns are dropped rather than sent: the API refuses an empty content string, and an
    /// empty turn is what a stopped or failed answer leaves behind, so a conversation carrying one
    /// would fail every request after it.
    ///
    /// A question with files is a list of blocks, the files ahead of its words — see
    /// <see cref="ContentWithFiles"/>; anything else stays the string it always was, so nothing
    /// about a conversation without files changes.
    /// </summary>
    public static JsonArray ToWireMessages(IReadOnlyList<LlmMessage> conversation, AiConnection connection)
    {
        var messages = new JsonArray();

        foreach (LlmMessage turn in conversation)
        {
            if (turn.Attachments.Count > 0)
                messages.Add(new JsonObject { ["role"] = turn.Role, ["content"] = ContentWithFiles(turn, connection) });
            else if (!string.IsNullOrWhiteSpace(turn.Text))
                messages.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Text });
        }

        return messages;
    }

    /// <summary>
    /// A question's files as blocks ahead of its words: a picture as an image block, a PDF as a
    /// document or as its words by what the connection takes (<see cref="AiAttachment.PdfGoesAsFile"/>),
    /// and a text file as text between tags.
    /// </summary>
    public static JsonArray ContentWithFiles(LlmMessage turn, AiConnection connection)
    {
        var content = new JsonArray();

        foreach (AttachmentPart part in AiAttachment.Parts(turn.Attachments, connection))
            content.Add(Block(part));

        if (!string.IsNullOrWhiteSpace(turn.Text))
            content.Add(new JsonObject { ["type"] = "text", ["text"] = turn.Text });

        return content;
    }

    /// <summary>One part of a question's files as the Messages API takes it.</summary>
    public static JsonObject Block(AttachmentPart part)
        => part.Kind is AttachmentKind.Image or AttachmentKind.Pdf
            ? new JsonObject
            {
                ["type"] = part.Kind == AttachmentKind.Pdf ? "document" : "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = part.MediaType,
                    ["data"] = part.Base64,
                },
            }
            : new JsonObject { ["type"] = "text", ["text"] = part.Text };

    /// <summary>The tools as Anthropic's tools array: a name, a description and an input_schema each.</summary>
    public static JsonArray BuildToolsArray(IReadOnlyList<LlmTool> tools)
    {
        var array = new JsonArray();

        foreach (LlmTool tool in tools)
        {
            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = JsonNode.Parse(tool.InputSchemaJson),
            });
        }

        return array;
    }

    /// <summary>
    /// A request: the model, the cap, the system prompt when there is one, the conversation so far
    /// and, when there are any, the tools — marked where Anthropic should cache when asked. An
    /// effort goes in output_config and fast mode as the speed, both only when chosen, so a request
    /// that chose neither is the one it always was.
    ///
    /// **A prompt with nothing in it is no prompt.** The field is left out rather than sent empty,
    /// since the API refuses <c>"system": null</c> and <c>""</c> alike. Every caller of the app's
    /// own passes words, so this is the edge a host's own call reaches first.
    ///
    /// The conversation and the tools are copied into the body rather than placed in it, so marks
    /// for the cache land on the copy: see <see cref="MarkForCache"/>.
    /// </summary>
    public static string BuildRequestBody(string model, int maxTokens, string system, JsonArray messages,
                                          JsonArray? tools, bool cache = false, string effort = "", bool fast = false)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["messages"] = messages.DeepClone(),
        };

        if (!string.IsNullOrWhiteSpace(system)) body["system"] = system;
        if (tools is not null && tools.Count > 0) body["tools"] = tools.DeepClone();
        if (!string.IsNullOrEmpty(effort)) body["output_config"] = new JsonObject { ["effort"] = effort };
        if (fast) body["speed"] = "fast";
        if (cache) MarkForCache(body);

        return body.ToJsonString();
    }

    /// <summary>
    /// Tells Anthropic what to cache: everything up to the last tool, and everything up to the end
    /// of the request.
    ///
    /// **Anthropic caches only what it is told to**, where OpenAI and Google cache a repeated
    /// opening by themselves — and a request reads back what an earlier one cached up to one of its
    /// own marks. The order it caches in is the tools, then the system prompt, then the messages.
    /// So two marks:
    ///
    /// - **After the tools**, which are the same in every request of every conversation and most of
    ///   what one sends. Not after the system prompt, which a conversation writes again for every
    ///   question with the bench as it stands in it, and which a mark would buy nothing on.
    /// - **After the last block of the conversation**, so the next round of a run of tool calls —
    ///   which sends all of this again with one more call and its answer — reads it back at a tenth
    ///   of the rate, or a twentieth on Opus and Sonnet. A string is made a block to carry it.
    ///
    /// **On the copy each request is built from**, never on the conversation the loop keeps adding
    /// to: marks left on every round's last block would pass Anthropic's limit of four in three
    /// rounds, and the request would be refused. And **only in the tool loop**: a write costs a
    /// quarter over the rate and lasts five minutes, which a run of calls uses at once, while a
    /// request answered in one round pays it for an entry nothing reads.
    /// </summary>
    public static void MarkForCache(JsonObject body)
    {
        if (body["tools"] is JsonArray tools && tools.Count > 0 && tools[tools.Count - 1] is JsonObject lastTool)
            lastTool["cache_control"] = Ephemeral();

        if (body["messages"] is not JsonArray messages || messages.Count == 0
         || messages[messages.Count - 1] is not JsonObject last)
            return;

        if (last["content"] is JsonValue words)
            last["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = words.GetValue<string>() } };

        if (last["content"] is JsonArray blocks && blocks.Count > 0 && blocks[blocks.Count - 1] is JsonObject block)
            block["cache_control"] = Ephemeral();
    }

    private static JsonObject Ephemeral() => new() { ["type"] = "ephemeral" };

    /// <summary>
    /// What one call answered, as a tool_result block.
    ///
    /// A picture goes in beside the words, which the Messages API takes inside a tool's result — so
    /// the model sees it as the answer to the call that asked for it, rather than as something the
    /// person said. **A refusal is marked as one**, with is_error: GraphDBViewer never set it, and a
    /// model told a call failed tries something else rather than building on it.
    /// </summary>
    public static JsonObject ToolResult(string id, LlmToolOutput output)
    {
        var result = new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = id,
            ["content"] = output.Png is null
                ? output.Text
                : new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = output.Text },
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = "image/png",
                            ["data"] = output.Png,
                        },
                    },
                },
        };

        if (output.IsError) result["is_error"] = true;

        return result;
    }

    // --------------------------------------------------------------------------- the reply

    /// <summary>The tool_use blocks in a reply's content: empty when the model answered in words.</summary>
    public static List<LlmToolCall> ParseToolUses(JsonElement content)
    {
        var calls = new List<LlmToolCall>();
        if (content.ValueKind != JsonValueKind.Array) return calls;

        foreach (JsonElement block in content.EnumerateArray())
        {
            if (StringIn(block, "type") != "tool_use" || StringIn(block, "name") is not string called) continue;

            string arguments = block.ValueKind == JsonValueKind.Object && block.TryGetProperty("input", out JsonElement input)
                ? input.GetRawText()
                : "{}";

            calls.Add(new LlmToolCall { Id = StringIn(block, "id") ?? "", Name = called, ArgumentsJson = arguments });
        }

        return calls;
    }

    /// <summary>The text blocks of a reply's content, joined.</summary>
    public static string TextOf(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array) return "";

        var builder = new StringBuilder();
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (StringIn(block, "type") == "text") builder.Append(StringIn(block, "text"));
        }

        return builder.ToString().Trim();
    }

    /// <summary>A plain reply read: its text, or what the API said was wrong, or that the model declined.</summary>
    public static LlmResult ParseResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (LlmProviders.ErrorIn(json) is string said) return LlmResult.Failure(said);
            if (StopReason(root) == "refusal") return LlmResult.Failure("The model declined to answer.");
            if (ContentOf(root) is not JsonElement content) return LlmResult.Failure(UnknownShape);

            return Answered(TextOf(content), StopReason(root));
        }
        catch (JsonException)
        {
            return LlmResult.Failure(NotJson);
        }
    }

    // ------------------------------------------------------------------------------ sending

    public async Task<LlmResult> CompleteAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation, CancellationToken ct)
    {
        string body = BuildRequestBody(Model, DefaultMaxTokens, systemPrompt, ToWireMessages(conversation, _connection),
                                       tools: null, cache: false, _effort, _fast);

        var (json, problem) = await LlmProviders.SendAsync(_http, Request(body), Label, ct).ConfigureAwait(false);
        if (problem is not null || json is null) return LlmResult.Failure(problem ?? $"{Label} sent nothing back.");

        RecordUsage(json);
        return ParseResponse(json);
    }

    public async Task<LlmResult> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation,
                                                        ILlmToolRunner tools, CancellationToken ct,
                                                        int maxRounds = LlmProviders.DefaultToolRounds)
    {
        JsonArray toolsArray = BuildToolsArray(tools.Tools);
        JsonArray messages = ToWireMessages(conversation, _connection);
        var progress = new ToolProgress();

        for (int round = 0; round < maxRounds; round++)
        {
            string body = BuildRequestBody(Model, DefaultMaxTokens, systemPrompt, messages, toolsArray,
                                           cache: true, _effort, _fast);

            var (json, problem) = await LlmProviders.SendAsync(_http, Request(body), Label, ct).ConfigureAwait(false);
            if (problem is not null || json is null) return LlmResult.Failure(problem ?? $"{Label} sent nothing back.");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return LlmResult.Failure(NotJson);
            }

            using (document)
            {
                JsonElement root = document.RootElement;

                // Before anything can end the run: a round is billed whatever comes of it.
                Spent.Add(LlmUsage.FromAnthropic(root));

                if (LlmProviders.ErrorIn(json) is string said) return LlmResult.Failure(said);
                if (StopReason(root) == "refusal") return LlmResult.Failure("The model declined to answer.");
                if (ContentOf(root) is not JsonElement content) return LlmResult.Failure(UnknownShape);

                List<LlmToolCall> calls = ParseToolUses(content);
                if (calls.Count == 0) return Answered(TextOf(content), StopReason(root));

                // A call cut off at the cap has half its arguments, and running it would be
                // running a guess — on a bench, a guess at a voltage.
                if (StopReason(root) == "max_tokens")
                    return LlmResult.Failure(
                        $"The model ran into its limit of {DefaultMaxTokens.ToString("N0", CultureInfo.InvariantCulture)} "
                      + "tokens partway through a call, so the call was not run. Ask for less in one go.");

                // Its reply goes back as it came, tool_use blocks and all, followed by an answer
                // to each call: a reply rebuilt from what was read out of it loses whatever it
                // carried that this class does not read.
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = JsonNode.Parse(content.GetRawText()) });

                var results = new JsonArray();
                var ran = new List<(LlmToolCall, LlmToolOutput)>();

                foreach (LlmToolCall call in calls)
                {
                    LlmToolOutput output = await tools.RunToolAsync(call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
                    results.Add(ToolResult(call.Id, output));
                    ran.Add((call, output));
                }

                messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });

                if (progress.IsStuckAfter(ran)) return LlmResult.Failure(LlmProviders.RoundsStuck);
            }
        }

        return LlmResult.Paused(LlmProviders.RoundsPaused(maxRounds));
    }

    // ---------------------------------------------------------------------------- helpers

    private static string UnknownShape => $"{Label} answered in a shape this app does not know.";

    private static string NotJson => $"{Label} answered with something that is not JSON.";

    private static string StopReason(JsonElement root) => StringIn(root, "stop_reason") ?? "";

    private static JsonElement? ContentOf(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("content", out JsonElement content)
            ? content
            : null;

    /// <summary>A string property of an object, or null where there is no object, no property, or no string.</summary>
    private static string? StringIn(JsonElement owner, string name)
        => owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The text of a finished reply, said as cut off when the cap is what finished it.</summary>
    private static LlmResult Answered(string text, string reason)
    {
        if (text.Length == 0)
            return LlmResult.Failure(reason == "max_tokens"
                ? "The model ran into its token limit before it wrote anything."
                : "The model sent back an empty answer.");

        return LlmResult.Success(reason == "max_tokens"
            ? text + Environment.NewLine + Environment.NewLine + "(Cut off at the token limit.)"
            : text);
    }

    /// <summary>Adds what an answer says it cost to <see cref="Spent"/>.</summary>
    private void RecordUsage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            Spent.Add(LlmUsage.FromAnthropic(document.RootElement));
        }
        catch (JsonException)
        {
            // Not JSON is ParseResponse's to say; here it is only nothing to count.
        }
    }

    /// <summary>
    /// The request for a body, from the endpoint: addressed and signed there, with fast mode's beta
    /// as the one header of the provider's own.
    /// </summary>
    private HttpRequestMessage Request(string body) => _endpoint.Post(body, _fast ? FastHeaders : null);
}
