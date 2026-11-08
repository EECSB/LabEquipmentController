using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController.Tests;

/// <summary>
/// A model provider that is not there: an <see cref="HttpMessageHandler"/> that answers each
/// request from a script, and keeps what it was sent.
///
/// GDS Viewer's, and what lets a whole run of tool calls go without a network or a key. The
/// request bodies are read back as JSON and the replies are written in each provider's own shape,
/// so the code under test is the code that talks to the real thing — a stand-in, not a mock.
/// </summary>
internal sealed class AiStandIn : HttpMessageHandler
{
    private readonly Func<int, JsonObject, HttpResponseMessage> _reply;

    public AiStandIn(Func<int, JsonObject, HttpResponseMessage> reply) => _reply = reply;

    /// <summary>Replies with these, one per request, repeating the last when they run out.</summary>
    public AiStandIn(params string[] replies) : this((index, _) => Json(replies[Math.Min(index, replies.Length - 1)]))
    {
    }

    public sealed record Sent(string Uri, Dictionary<string, string> Headers, JsonObject Body);

    public List<Sent> Requests { get; } = [];

    /// <summary>A client over this, with no clock of its own — as <see cref="LlmProviders.NewHttpClient"/> makes one.</summary>
    public HttpClient Client() => new(this) { Timeout = Timeout.InfiniteTimeSpan };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers) headers[header.Key] = string.Join(",", header.Value);

        var body = (JsonObject)JsonNode.Parse(text)!;

        lock (Requests) Requests.Add(new Sent(request.RequestUri!.ToString(), headers, body));

        return _reply(Requests.Count - 1, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // ------------------------------------------------------------------- Anthropic's shape

    public static string AnthropicText(string text, string stopReason = "end_turn") => new JsonObject
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
        ["stop_reason"] = stopReason,
    }.ToJsonString();

    public static string AnthropicCall(string id, string name, string inputJson, string said = "", string stopReason = "tool_use")
    {
        var content = new JsonArray();
        if (said.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = said });

        content.Add(new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = id,
            ["name"] = name,
            ["input"] = JsonNode.Parse(inputJson),
        });

        return new JsonObject { ["content"] = content, ["stop_reason"] = stopReason }.ToJsonString();
    }

    /// <summary>A reply that says what it cost, the way Anthropic does: input after the cache, output, and what was read from the cache and written into it.</summary>
    public static string WithAnthropicUsage(string reply, long input, long output, long read = 0, long written = 0)
    {
        var body = (JsonObject)JsonNode.Parse(reply)!;
        body["usage"] = new JsonObject
        {
            ["input_tokens"] = input,
            ["output_tokens"] = output,
            ["cache_read_input_tokens"] = read,
            ["cache_creation_input_tokens"] = written,
        };
        return body.ToJsonString();
    }

    // -------------------------------------------------------------- the OpenAI-compatible shape

    public static string OpenAiText(string text, string finish = "stop") => new JsonObject
    {
        ["choices"] = new JsonArray
        {
            new JsonObject
            {
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text },
                ["finish_reason"] = finish,
            },
        },
    }.ToJsonString();

    /// <summary>
    /// A call, with the thought signature Gemini 3 puts in each one — which has to come back as it
    /// went — and OpenAI's own refusal field, which must not.
    /// </summary>
    public static string OpenAiCall(string id, string name, string argumentsJson, string finish = "tool_calls") => new JsonObject
    {
        ["choices"] = new JsonArray
        {
            new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = null,
                    ["refusal"] = null,
                    ["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = id,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = argumentsJson },
                            ["extra_content"] = new JsonObject
                            {
                                ["google"] = new JsonObject { ["thought_signature"] = "c2lnbmVk" },
                            },
                        },
                    },
                },
                ["finish_reason"] = finish,
            },
        },
    }.ToJsonString();

    /// <summary>A reply that says what it cost, the chat-completions way — with a total past the other two where a test wants Gemini's unsaid thinking.</summary>
    public static string WithOpenAiUsage(string reply, long prompt, long completion, long cached = 0, long reasoning = 0, long? total = null)
    {
        var body = (JsonObject)JsonNode.Parse(reply)!;
        body["usage"] = new JsonObject
        {
            ["prompt_tokens"] = prompt,
            ["completion_tokens"] = completion,
            ["total_tokens"] = total ?? prompt + completion,
            ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cached },
            ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = reasoning },
        };
        return body.ToJsonString();
    }
}

/// <summary>
/// Runs whatever it is told to, and keeps what it was asked — the tools a provider's loop calls,
/// with nothing of a bench under them. Every call answers <see cref="Answer"/> unless a test says
/// otherwise; the first tool reads and the second changes, so a gate has one of each to decide on.
/// </summary>
internal sealed class ScriptedTools : ILlmToolRunner
{
    private const string NoArguments = """{"type":"object","properties":{}}""";

    public IReadOnlyList<LlmTool> Tools { get; init; } =
    [
        new LlmTool { Name = "read_meter", Description = "Reads the meter.", InputSchemaJson = NoArguments },
        new LlmTool { Name = "set_output", Description = "Switches the supply's output.", InputSchemaJson = NoArguments, Changes = true },
    ];

    /// <summary>What each call answers, by its name, its arguments and how many calls came before it.</summary>
    public Func<string, string, int, LlmToolOutput> Answer { get; init; } = (_, _, _) => new LlmToolOutput { Text = "4.998 V" };

    public List<(string Name, string ArgumentsJson)> Calls { get; } = [];

    public Task<LlmToolOutput> RunToolAsync(string name, string argumentsJson, CancellationToken ct)
    {
        int before = Calls.Count;
        Calls.Add((name, argumentsJson));
        return Task.FromResult(Answer(name, argumentsJson, before));
    }
}
