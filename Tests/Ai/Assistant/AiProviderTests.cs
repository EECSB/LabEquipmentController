using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The two providers: what they send, what they read back, how their loop of tool calls ends, and
/// what they say when something goes wrong.
///
/// Each request is checked as the JSON a provider would receive, and each reply is written in that
/// provider's own shape — see <see cref="AiStandIn"/> — so nothing here needs a network or a key.
/// The requests go through the same <see cref="DirectLlmEndpoint"/> the desktop uses, so the
/// headers checked are the ones a real request carries.
/// </summary>
public class AiProviderTests
{
    private static AiConnection Claude(string model = "") => new() { Provider = AiProvider.Anthropic, Model = model };

    private static AiConnection Gemini(string model = "gemini-test") => new() { Provider = AiProvider.Gemini, Model = model };

    /// <summary>OpenAI itself: the OpenAI-compatible provider at its default address.</summary>
    private static AiConnection OpenAi(string model = "gpt-5-mini") => new() { Provider = AiProvider.OpenAiCompatible, Model = model };

    private static AiConnection OwnServer(string model = "llama3.3")
        => new() { Provider = AiProvider.OpenAiCompatible, BaseUrl = "http://localhost:11434", Model = model };

    private static AnthropicProvider Anthropic(AiStandIn standIn, AiConnection? connection = null, string effort = "", bool fast = false)
    {
        connection ??= Claude();
        return new AnthropicProvider(standIn.Client(), new DirectLlmEndpoint(connection, "test-key"), connection, effort, fast);
    }

    private static OpenAiProvider Chat(AiStandIn standIn, AiConnection connection, string effort = "")
        => new(standIn.Client(), new DirectLlmEndpoint(connection, "test-key"), connection, effort);

    private static IReadOnlyList<LlmMessage> Asked(string text) => [LlmMessage.FromUser(text)];

    /// <summary>The web page's way: to the bench's own server, signed by nobody here, with the provider's own headers passed on.</summary>
    private sealed class RelayEndpoint : ILlmEndpoint
    {
        public HttpRequestMessage Post(string body, IReadOnlyDictionary<string, string>? headers = null)
            => LlmEndpoints.Json("http://bench.test/api/ai/chat", body, headers);
    }

    private static int CacheMarks(AiStandIn.Sent sent) => sent.Body.ToJsonString().Split("cache_control").Length - 1;

    // --------------------------------------------------------------------------- Anthropic

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_anthropic_model_runs_the_providers_default(string model)
        => Assert.Equal(AiProviderInfo.For(AiProvider.Anthropic).DefaultModel, Anthropic(new AiStandIn(), Claude(model)).Model);

    [Fact]
    public async Task Anthropic_is_sent_the_model_the_cap_the_system_prompt_and_the_conversation()
    {
        var standIn = new AiStandIn(AiStandIn.AnthropicText("Hello."));

        LlmResult result = await Anthropic(standIn, Claude("claude-haiku-5-5")).CompleteAsync("Be brief.", Asked("Hi"), CancellationToken.None);

        Assert.False(result.IsError, result.Error);
        Assert.Equal("Hello.", result.Text);

        AiStandIn.Sent sent = Assert.Single(standIn.Requests);
        Assert.Equal("https://api.anthropic.com/v1/messages", sent.Uri);
        Assert.Equal("claude-haiku-5-5", (string?)sent.Body["model"]);
        Assert.Equal(AnthropicProvider.DefaultMaxTokens, (int?)sent.Body["max_tokens"]);

        // A connection's own cap goes in its place.
        var capped = new AiStandIn(AiStandIn.AnthropicText("ok"));
        var connection = new AiConnection { Provider = AiProvider.Anthropic, Model = "claude-sonnet-5", MaxTokens = 16000 };
        await new AnthropicProvider(capped.Client(), new DirectLlmEndpoint(connection, "test-key"), connection).CompleteAsync("", [LlmMessage.FromUser("hi")], default);
        Assert.Equal(16000, (int?)capped.Requests[0].Body["max_tokens"]);
        Assert.Equal("Be brief.", (string?)sent.Body["system"]);
        Assert.Equal("Hi", (string?)sent.Body["messages"]![0]!["content"]);
        Assert.Null(sent.Body["tools"]);
    }

    /// <summary>
    /// Signed by the endpoint, and nothing a page calling Anthropic itself would need: no page here
    /// does, so the direct-browser header GDS Viewer sends is not sent.
    /// </summary>
    [Fact]
    public async Task Anthropic_is_signed_by_the_endpoint_and_sent_no_browser_header()
    {
        var standIn = new AiStandIn(AiStandIn.AnthropicText("Hello."));

        await Anthropic(standIn).CompleteAsync("", Asked("Hi"), CancellationToken.None);

        Dictionary<string, string> headers = standIn.Requests[0].Headers;
        Assert.Equal("test-key", headers["x-api-key"]);
        Assert.Equal("2023-06-01", headers["anthropic-version"]);
        Assert.False(headers.ContainsKey("anthropic-dangerous-direct-browser-access"));
        Assert.False(headers.ContainsKey("anthropic-beta"));
    }

    /// <summary>
    /// Through the bench's own server, as the web page sends: the same body, no key, and fast mode's
    /// beta passed on as the one header of the provider's own — so the provider cannot tell, and
    /// the relay can forward it.
    /// </summary>
    [Fact]
    public async Task A_request_through_the_benchs_own_server_is_the_same_request_with_no_key()
    {
        var direct = new AiStandIn(AiStandIn.AnthropicText("Hello."));
        var relayed = new AiStandIn(AiStandIn.AnthropicText("Hello."));
        AiConnection opus = Claude("claude-opus-5-5");

        await Anthropic(direct, opus, "high", fast: true).CompleteAsync("Be brief.", Asked("Hi"), CancellationToken.None);
        await new AnthropicProvider(relayed.Client(), new RelayEndpoint(), opus, "high", fast: true)
            .CompleteAsync("Be brief.", Asked("Hi"), CancellationToken.None);

        AiStandIn.Sent sent = relayed.Requests[0];
        Assert.Equal("http://bench.test/api/ai/chat", sent.Uri);
        Assert.True(JsonNode.DeepEquals(direct.Requests[0].Body, sent.Body));
        Assert.False(sent.Headers.ContainsKey("x-api-key"));
        Assert.False(sent.Headers.ContainsKey("Authorization"));
        Assert.Equal(AnthropicProvider.FastBeta, sent.Headers["anthropic-beta"]);
    }

    /// <summary>An effort and fast mode go only when chosen: the effort in output_config, fast mode as the speed with its beta beside it, at twice the price.</summary>
    [Fact]
    public async Task Anthropic_is_sent_an_effort_and_fast_mode_when_they_were_chosen()
    {
        var standIn = new AiStandIn(AiStandIn.AnthropicText("Hello."));
        AnthropicProvider provider = Anthropic(standIn, Claude("claude-opus-5-5"), "xhigh", fast: true);

        await provider.CompleteAsync("", Asked("Hi"), CancellationToken.None);

        AiStandIn.Sent sent = standIn.Requests[0];
        Assert.Equal("xhigh", (string?)sent.Body["output_config"]!["effort"]);
        Assert.Equal("fast", (string?)sent.Body["speed"]);
        Assert.Equal(AnthropicProvider.FastBeta, sent.Headers["anthropic-beta"]);
        Assert.Equal(2 * 4.00m, provider.Price!.Input);
    }

    /// <summary>
    /// What a model does not take is not sent, since Anthropic refuses it rather than ignoring it:
    /// fast mode on Opus 4.6, or a level from another model's list.
    /// </summary>
    [Fact]
    public async Task Anthropic_is_not_sent_what_the_model_does_not_take()
    {
        var standIn = new AiStandIn(AiStandIn.AnthropicText("Hello."));
        AnthropicProvider provider = Anthropic(standIn, Claude("claude-opus-4-6"), "xhigh", fast: true);

        await provider.CompleteAsync("", Asked("Hi"), CancellationToken.None);

        AiStandIn.Sent sent = standIn.Requests[0];
        Assert.Null(sent.Body["output_config"]);
        Assert.Null(sent.Body["speed"]);
        Assert.False(sent.Headers.ContainsKey("anthropic-beta"));
        Assert.Equal(5.00m, provider.Price!.Input);
    }

    [Fact]
    public void Empty_turns_are_not_sent()
    {
        IReadOnlyList<LlmMessage> conversation =
        [
            LlmMessage.FromUser("one"),
            LlmMessage.FromAssistant(""),
            LlmMessage.FromUser("  "),
            LlmMessage.FromUser("two"),
        ];

        Assert.Equal(2, AnthropicProvider.ToWireMessages(conversation, Claude()).Count);
        Assert.Equal(3, OpenAiProvider.ToWireMessages("system", conversation, Gemini()).Count);
    }

    [Fact]
    public void The_tools_go_to_anthropic_with_their_own_schemas()
    {
        var tools = new ScriptedTools();
        JsonArray array = AnthropicProvider.BuildToolsArray(tools.Tools);

        Assert.Equal(tools.Tools.Count, array.Count);
        foreach (LlmTool tool in tools.Tools)
        {
            JsonNode sent = array.Single(each => (string?)each!["name"] == tool.Name)!;
            Assert.Equal(tool.Description, (string?)sent["description"]);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(tool.InputSchemaJson), sent["input_schema"]));
        }
    }

    [Fact]
    public void A_tool_that_answered_in_words_goes_back_as_text()
    {
        JsonObject result = AnthropicProvider.ToolResult("toolu_1", new LlmToolOutput { Text = "4.998 V" });

        Assert.Equal("tool_result", (string?)result["type"]);
        Assert.Equal("toolu_1", (string?)result["tool_use_id"]);
        Assert.Equal("4.998 V", (string?)result["content"]);
        Assert.Null(result["is_error"]);
    }

    /// <summary>A picture goes back inside the result of the call that took it, as an image block beside the words.</summary>
    [Fact]
    public void A_picture_goes_back_to_anthropic_inside_the_tools_result()
    {
        JsonObject result = AnthropicProvider.ToolResult("toolu_2", new LlmToolOutput { Text = "The scope's screen.", Png = "iVBORw0KGgo=" });
        JsonArray content = result["content"]!.AsArray();

        Assert.Equal("text", (string?)content[0]!["type"]);
        Assert.Equal("The scope's screen.", (string?)content[0]!["text"]);
        Assert.Equal("image", (string?)content[1]!["type"]);
        Assert.Equal("base64", (string?)content[1]!["source"]!["type"]);
        Assert.Equal("image/png", (string?)content[1]!["source"]!["media_type"]);
        Assert.Equal("iVBORw0KGgo=", (string?)content[1]!["source"]!["data"]);
    }

    /// <summary>GraphDBViewer never set is_error; a model told a call was refused tries something else rather than building on it.</summary>
    [Fact]
    public void A_refused_call_is_marked_as_one()
    {
        JsonObject result = AnthropicProvider.ToolResult("toolu_3", LlmToolOutput.Refused("There is no channel 5 on this supply."));

        Assert.True((bool?)result["is_error"]);
    }

    [Fact]
    public void The_calls_in_a_reply_are_read_with_their_ids_and_arguments()
    {
        using var reply = JsonDocument.Parse(AiStandIn.AnthropicCall("toolu_9", "set_output", """{"channel":1,"on":true}""", "Switching it on."));

        LlmToolCall call = Assert.Single(AnthropicProvider.ParseToolUses(reply.RootElement.GetProperty("content")));
        Assert.Equal("toolu_9", call.Id);
        Assert.Equal("set_output", call.Name);
        Assert.Equal(1, JsonNode.Parse(call.ArgumentsJson)!["channel"]!.GetValue<int>());
    }

    [Fact]
    public void A_reply_in_words_has_no_calls()
    {
        using var reply = JsonDocument.Parse(AiStandIn.AnthropicText("Done."));

        Assert.Empty(AnthropicProvider.ParseToolUses(reply.RootElement.GetProperty("content")));
    }

    [Fact]
    public void Anthropics_error_is_what_is_said()
    {
        LlmResult result = AnthropicProvider.ParseResponse("""{"type":"error","error":{"type":"invalid_request_error","message":"max_tokens: too large"}}""");

        Assert.True(result.IsError);
        Assert.Equal("max_tokens: too large", result.Error);
    }

    [Fact]
    public void A_refusal_is_an_error()
    {
        LlmResult result = AnthropicProvider.ParseResponse("""{"content":[],"stop_reason":"refusal"}""");

        Assert.True(result.IsError);
        Assert.Contains("declined", result.Error);
    }

    [Fact]
    public void An_answer_cut_off_at_the_cap_says_so()
    {
        LlmResult result = AnthropicProvider.ParseResponse(AiStandIn.AnthropicText("The first half", "max_tokens"));

        Assert.False(result.IsError);
        Assert.StartsWith("The first half", result.Text);
        Assert.Contains("Cut off at the token limit", result.Text);
    }

    [Fact]
    public void An_empty_answer_is_an_error_rather_than_nothing()
        => Assert.True(AnthropicProvider.ParseResponse(AiStandIn.AnthropicText("")).IsError);

    /// <summary>A body that is JSON but not a reply — an array, a content that is not a list — is said as one rather than thrown out of the provider.</summary>
    [Theory]
    [InlineData("[1,2]")]
    [InlineData("""{"stop_reason":"end_turn"}""")]
    [InlineData("""{"content":[1,"two",{"type":5}]}""")]
    public void A_reply_in_a_shape_nobody_expected_is_said_rather_than_thrown(string json)
        => Assert.True(AnthropicProvider.ParseResponse(json).IsError);

    // ------------------------------------------------------------------- Anthropic's loop

    /// <summary>
    /// The whole round trip: a call, run, and its answer sent back under the call's id — after the
    /// model's own reply, exactly as it came, words and tool_use block together.
    /// </summary>
    [Fact]
    public async Task A_call_is_run_and_answered_under_its_id_after_the_reply_as_it_came()
    {
        string call = AiStandIn.AnthropicCall("toolu_1", "read_meter", """{"range":"auto"}""", "Reading it.");
        var standIn = new AiStandIn(call, AiStandIn.AnthropicText("It reads 4.998 V."));
        var tools = new ScriptedTools();

        LlmResult result = await Anthropic(standIn).CompleteWithToolsAsync("system", Asked("What does the meter read?"), tools, CancellationToken.None);

        Assert.Equal("It reads 4.998 V.", result.Text);
        Assert.Equal(("read_meter", """{"range":"auto"}"""), Assert.Single(tools.Calls));

        JsonArray messages = standIn.Requests[1].Body["messages"]!.AsArray();
        Assert.Equal("assistant", (string?)messages[^2]!["role"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(call)!["content"], messages[^2]!["content"]));

        JsonNode answer = messages[^1]!["content"]![0]!;
        Assert.Equal("user", (string?)messages[^1]!["role"]);
        Assert.Equal("tool_result", (string?)answer["type"]);
        Assert.Equal("toolu_1", (string?)answer["tool_use_id"]);
        Assert.Equal("4.998 V", (string?)answer["content"]);
    }

    [Fact]
    public async Task A_refused_call_goes_back_marked_and_the_model_gets_to_try_again()
    {
        var standIn = new AiStandIn(AiStandIn.AnthropicCall("toolu_1", "set_output", """{"channel":5}"""), AiStandIn.AnthropicText("There is no channel 5."));
        var tools = new ScriptedTools { Answer = (_, _, _) => LlmToolOutput.Refused("There is no channel 5 on this supply.") };

        LlmResult result = await Anthropic(standIn).CompleteWithToolsAsync("system", Asked("Switch on channel 5"), tools, CancellationToken.None);

        Assert.False(result.IsError, result.Error);
        JsonNode answer = standIn.Requests[1].Body["messages"]!.AsArray()[^1]!["content"]![0]!;
        Assert.True((bool?)answer["is_error"]);
    }

    /// <summary>A call cut off at the cap has half its arguments: it is not run, and the round it cost is still counted.</summary>
    [Fact]
    public async Task A_call_cut_off_at_the_cap_is_not_run_and_its_round_is_still_counted()
    {
        var standIn = new AiStandIn(AiStandIn.WithAnthropicUsage(
            AiStandIn.AnthropicCall("toolu_1", "set_output", """{"channel":1}""", stopReason: "max_tokens"), 900, 8192));
        var tools = new ScriptedTools();
        AnthropicProvider provider = Anthropic(standIn);

        LlmResult result = await provider.CompleteWithToolsAsync("system", Asked("Set 5 V"), tools, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("8,192 tokens partway through a call", result.Error);
        Assert.Empty(tools.Calls);
        Assert.Equal(1, provider.Spent.Requests);
        Assert.Equal(8192, provider.Spent.OutputTokens);
    }

    [Fact]
    public async Task A_refusal_partway_through_a_run_ends_it_as_one_and_every_round_is_counted()
    {
        var standIn = new AiStandIn(
            AiStandIn.WithAnthropicUsage(AiStandIn.AnthropicCall("toolu_1", "read_meter", "{}"), 1000, 20),
            AiStandIn.WithAnthropicUsage("""{"content":[],"stop_reason":"refusal"}""", 1100, 5));
        AnthropicProvider provider = Anthropic(standIn);

        LlmResult result = await provider.CompleteWithToolsAsync("system", Asked("Read it"), new ScriptedTools(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("declined", result.Error);
        Assert.Equal(2, provider.Spent.Requests);
        Assert.Equal(2100, provider.Spent.InputTokens);
    }

    /// <summary>
    /// **Two marks for Anthropic's cache in every round, never more**: after the tools, and on the
    /// last block of the conversation — made a block where it was a string. Put on the copy each
    /// request is built from, so they do not pile up on the conversation the loop keeps adding to:
    /// three rounds would pass Anthropic's limit of four, and the request would be refused.
    /// </summary>
    [Fact]
    public async Task Each_round_of_tool_calls_marks_the_tools_and_its_last_block_for_the_cache()
    {
        var standIn = new AiStandIn(
            AiStandIn.AnthropicCall("toolu_1", "read_meter", "{}"),
            AiStandIn.AnthropicCall("toolu_2", "set_output", "{}"),
            AiStandIn.AnthropicText("Done."));

        LlmResult result = await Anthropic(standIn).CompleteWithToolsAsync("system", Asked("Look"), new ScriptedTools(), CancellationToken.None);

        Assert.False(result.IsError, result.Error);
        Assert.Equal(3, standIn.Requests.Count);

        foreach (AiStandIn.Sent sent in standIn.Requests)
        {
            Assert.Equal(2, CacheMarks(sent));
            Assert.NotNull(sent.Body["tools"]!.AsArray()[^1]!["cache_control"]);
            Assert.NotNull(sent.Body["messages"]!.AsArray()[^1]!["content"]!.AsArray()[^1]!["cache_control"]);
        }

        // The first round's question, a string until then, went as a block to carry the mark.
        Assert.Equal("Look", (string?)standIn.Requests[0].Body["messages"]![0]!["content"]![0]!["text"]);

        // And the system prompt stays a string: written again for every question, a mark on it would buy nothing.
        Assert.Equal("system", (string?)standIn.Requests[0].Body["system"]);
    }

    /// <summary>A request answered in one round would only pay the write premium, so a plain completion is not marked.</summary>
    [Fact]
    public async Task A_plain_completion_is_not_marked_for_the_cache()
    {
        var standIn = new AiStandIn(AiStandIn.AnthropicText("Hello."));

        await Anthropic(standIn).CompleteAsync("system", Asked("Hi"), CancellationToken.None);

        Assert.Equal(0, CacheMarks(standIn.Requests[0]));
    }

    /// <summary>What every round was billed for is added up as it arrives — which is what lets a stopped run still be counted.</summary>
    [Fact]
    public async Task What_each_round_cost_is_added_to_the_providers_spend()
    {
        var standIn = new AiStandIn(
            AiStandIn.WithAnthropicUsage(AiStandIn.AnthropicCall("toolu_1", "read_meter", "{}"), 1200, 40, 0, 9000),
            AiStandIn.WithAnthropicUsage(AiStandIn.AnthropicText("Done."), 300, 60, 9000, 1100));
        AnthropicProvider provider = Anthropic(standIn, Claude("claude-sonnet-5-5"));

        await provider.CompleteWithToolsAsync("system", Asked("Look"), new ScriptedTools(), CancellationToken.None);

        Assert.Equal("claude-sonnet-5-5", provider.Model);
        Assert.Equal(2, provider.Spent.Requests);
        Assert.Equal(10_200 + 10_400, provider.Spent.InputTokens);
        Assert.Equal(9000, provider.Spent.CachedInputTokens);
        Assert.Equal(10_100, provider.Spent.CacheWriteTokens);
        Assert.Equal(100, provider.Spent.OutputTokens);
        Assert.Equal(2.00m, provider.Price!.Input);
    }

    /// <summary>A model that keeps calling, every call working and new, is paused at the cap rather than failed.</summary>
    [Fact]
    public async Task A_model_that_keeps_working_is_paused_at_the_cap_not_failed()
    {
        int calls = 0;
        var standIn = new AiStandIn((_, _) => AiStandIn.Json(AiStandIn.AnthropicCall("toolu_" + calls, "read_meter", $$"""{"sample":{{calls++}}}""")));

        LlmResult result = await Anthropic(standIn).CompleteWithToolsAsync("system", Asked("Log forever"), new ScriptedTools(), CancellationToken.None, maxRounds: 4);

        Assert.False(result.IsError, result.Error);
        Assert.True(result.IsPaused);
        Assert.Equal(LlmProviders.RoundsPaused(4), result.Text);
        Assert.Contains("another 4 rounds?", result.Text);
        Assert.Equal(4, standIn.Requests.Count);
    }

    [Fact]
    public async Task The_cap_on_rounds_is_thirty_unless_the_caller_says()
    {
        int calls = 0;
        var standIn = new AiStandIn((_, _) => AiStandIn.Json(AiStandIn.AnthropicCall("toolu_" + calls, "read_meter", $$"""{"sample":{{calls++}}}""")));

        LlmResult result = await Anthropic(standIn).CompleteWithToolsAsync("system", Asked("Log forever"), new ScriptedTools(), CancellationToken.None);

        Assert.True(result.IsPaused);
        Assert.Equal(LlmProviders.DefaultToolRounds, standIn.Requests.Count);
    }

    /// <summary>A model whose every call is refused is stopped as stuck after a few rounds, not left to run to the cap.</summary>
    [Fact]
    public async Task A_model_whose_calls_keep_failing_is_stopped_as_stuck()
    {
        int calls = 0;
        var standIn = new AiStandIn((_, _) => AiStandIn.Json(AiStandIn.AnthropicCall("toolu_" + calls, "set_output", $$"""{"channel":{{5 + calls++}}}""")));
        var tools = new ScriptedTools { Answer = (_, _, _) => LlmToolOutput.Refused("There is no such channel.") };

        LlmResult result = await Anthropic(standIn).CompleteWithToolsAsync("system", Asked("Switch it on"), tools, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(LlmProviders.RoundsStuck, result.Error);
        Assert.Equal(LlmProviders.MaxStuckRounds, standIn.Requests.Count);
    }

    /// <summary>A model asking the same thing over and over, and told the same each time, is stopped as stuck — the first ask was progress.</summary>
    [Fact]
    public async Task A_model_repeating_the_same_read_is_stopped_as_stuck()
    {
        int calls = 0;
        var standIn = new AiStandIn((_, _) => AiStandIn.Json(AiStandIn.AnthropicCall("toolu_" + calls++, "read_meter", "{}")));

        LlmResult result = await Anthropic(standIn).CompleteWithToolsAsync("system", Asked("Read it"), new ScriptedTools(), CancellationToken.None);

        Assert.Equal(LlmProviders.RoundsStuck, result.Error);
        Assert.Equal(1 + LlmProviders.MaxStuckRounds, standIn.Requests.Count);
    }

    /// <summary>
    /// A run whose every call the person declines is going nowhere too. A declined call is answered
    /// as a result rather than an error, on purpose — so counted as progress, a model asking for one
    /// change after another ran to the cap with every card answered no.
    /// </summary>
    [Fact]
    public async Task A_run_whose_every_call_is_declined_is_stopped_as_stuck()
    {
        int calls = 0;
        var standIn = new AiStandIn((_, _) => AiStandIn.Json(AiStandIn.AnthropicCall("toolu_" + calls, "set_output", $$"""{"volts":{{calls++}}}""")));
        var bench = new ScriptedTools();
        var declining = new ApprovingToolRunner(bench, (_, _) => Task.FromResult(false), null, ToolApprovalMode.Ask);

        LlmResult result = await Anthropic(standIn).CompleteWithToolsAsync("system", Asked("Ramp it"), declining, CancellationToken.None);

        Assert.Equal(LlmProviders.RoundsStuck, result.Error);
        Assert.Equal(LlmProviders.MaxStuckRounds, standIn.Requests.Count);
        Assert.Empty(bench.Calls);

        JsonNode answer = standIn.Requests[1].Body["messages"]!.AsArray()[^1]!["content"]![0]!;
        Assert.Equal(ApprovingToolRunner.DeclinedMessage, (string?)answer["content"]);
        Assert.Null(answer["is_error"]);
    }

    // ------------------------------------------------------------------ OpenAI-compatible

    /// <summary>The Gemini key goes to Google's OpenAI-compatible endpoint, as a bearer token, with the model asked for.</summary>
    [Fact]
    public async Task Gemini_goes_to_googles_openai_endpoint_with_the_key_as_a_bearer()
    {
        var standIn = new AiStandIn(AiStandIn.OpenAiText("Hello."));

        LlmResult result = await Chat(standIn, Gemini()).CompleteAsync("Be brief.", Asked("Hi"), CancellationToken.None);

        Assert.Equal("Hello.", result.Text);

        AiStandIn.Sent sent = Assert.Single(standIn.Requests);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", sent.Uri);
        Assert.Equal("Bearer test-key", sent.Headers["Authorization"]);
        Assert.Equal("gemini-test", (string?)sent.Body["model"]);
        Assert.Equal("system", (string?)sent.Body["messages"]![0]!["role"]);
        Assert.Equal("Be brief.", (string?)sent.Body["messages"]![0]!["content"]);
        Assert.Null(sent.Body["reasoning_effort"]);
    }

    [Fact]
    public async Task A_server_of_ones_own_is_sent_to_its_own_address()
    {
        var standIn = new AiStandIn(AiStandIn.OpenAiText("Hello."));

        await Chat(standIn, OwnServer()).CompleteAsync("", Asked("Hi"), CancellationToken.None);

        Assert.Equal("http://localhost:11434/v1/chat/completions", standIn.Requests[0].Uri);
    }

    /// <summary>
    /// reasoning_effort only where the model takes it — a chat model refuses the whole request over
    /// it, and a server of one's own may be anything, so it is sent nothing it might refuse.
    /// </summary>
    [Theory]
    [InlineData("gemini", "gemini-3.6-flash", "high", "high")]
    [InlineData("gemini", "gemini-2.0-flash", "high", null)]
    [InlineData("openai", "gpt-5-mini", "low", "low")]
    [InlineData("openai", "gpt-4o-mini", "high", null)]
    [InlineData("openai", "gpt-5-mini", "xhigh", null)]
    [InlineData("own", "o3", "high", null)]
    public async Task An_effort_is_sent_only_where_the_model_takes_it(string where, string model, string effort, string? sent)
    {
        AiConnection connection = where switch
        {
            "gemini" => Gemini(model),
            "openai" => OpenAi(model),
            _ => OwnServer(model),
        };
        var standIn = new AiStandIn(AiStandIn.OpenAiText("Hello."));

        await Chat(standIn, connection, effort).CompleteAsync("", Asked("Hi"), CancellationToken.None);

        Assert.Equal(sent, (string?)standIn.Requests[0].Body["reasoning_effort"]);
    }

    [Fact]
    public void The_tools_go_as_functions()
    {
        var tools = new ScriptedTools();
        JsonNode output = OpenAiProvider.BuildToolsArray(tools.Tools).Single(each => (string?)each!["function"]!["name"] == "set_output")!;

        Assert.Equal("function", (string?)output["type"]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(tools.Tools[1].InputSchemaJson), output["function"]!["parameters"]));
    }

    [Theory]
    [InlineData("\"{\\\"channel\\\":2}\"")]
    [InlineData("{\"channel\":2}")]
    public void A_calls_arguments_are_read_as_a_string_of_json_or_as_json(string arguments)
    {
        string json = $$$"""{"tool_calls":[{"id":"call_1","type":"function","function":{"name":"set_output","arguments":{{{arguments}}}}}]}""";

        using var message = JsonDocument.Parse(json);
        LlmToolCall call = Assert.Single(OpenAiProvider.ParseToolCalls(message.RootElement));

        Assert.Equal("call_1", call.Id);
        Assert.Equal(2, JsonNode.Parse(call.ArgumentsJson)!["channel"]!.GetValue<int>());
    }

    /// <summary>Gemini 3 refuses the next request if a call's thought signature does not come back with it, so the calls go back as they came.</summary>
    [Fact]
    public void A_call_goes_back_with_whatever_the_server_put_in_it()
    {
        using var reply = JsonDocument.Parse(AiStandIn.OpenAiCall("call_7", "read_meter", "{}"));

        JsonElement message = reply.RootElement.GetProperty("choices")[0].GetProperty("message");
        JsonObject echo = OpenAiProvider.EchoAssistantMessage(message);

        Assert.Equal("assistant", (string?)echo["role"]);
        Assert.Equal("c2lnbmVk", (string?)echo["tool_calls"]![0]!["extra_content"]!["google"]!["thought_signature"]);

        // And nothing of the reply's own that a request does not take.
        Assert.False(echo.ContainsKey("refusal"));
    }

    [Fact]
    public void A_refused_call_says_so_in_its_tool_turn()
    {
        JsonObject turn = OpenAiProvider.ToolTurn("call_1", LlmToolOutput.Refused("There is no channel 5 on this supply."));

        Assert.Equal("tool", (string?)turn["role"]);
        Assert.Equal("call_1", (string?)turn["tool_call_id"]);
        Assert.Equal("Refused: There is no channel 5 on this supply.", (string?)turn["content"]);
    }

    /// <summary>Chat completions take an image only from the user, so a picture follows the tool turns in a user turn of its own.</summary>
    [Fact]
    public void A_picture_goes_back_in_a_user_turn_after_the_calls()
    {
        JsonObject turn = OpenAiProvider.PictureTurn([("capture_screen", "iVBORw0KGgo=")]);
        JsonArray parts = turn["content"]!.AsArray();

        Assert.Equal("user", (string?)turn["role"]);
        Assert.Contains("capture_screen", (string?)parts[0]!["text"]);
        Assert.Equal("image_url", (string?)parts[1]!["type"]);
        Assert.Equal("data:image/png;base64,iVBORw0KGgo=", (string?)parts[1]!["image_url"]!["url"]);
    }

    /// <summary>
    /// A round as the next request carries it: the model's message with its calls echoed whole, an
    /// answer to each call under its id, and the picture after them in a user turn.
    /// </summary>
    [Fact]
    public async Task A_round_goes_back_as_the_echoed_calls_their_answers_and_then_the_picture()
    {
        var standIn = new AiStandIn(AiStandIn.OpenAiCall("call_1", "capture_screen", "{}"), AiStandIn.OpenAiText("A 1 kHz sine."));
        var tools = new ScriptedTools
        {
            Tools = [new LlmTool { Name = "capture_screen", Description = "The scope's screen.", InputSchemaJson = """{"type":"object","properties":{}}""" }],
            Answer = (_, _, _) => new LlmToolOutput { Text = "The scope's screen.", Png = "iVBORw0KGgo=" },
        };

        LlmResult result = await Chat(standIn, Gemini()).CompleteWithToolsAsync("system", Asked("What is on the scope?"), tools, CancellationToken.None);

        Assert.Equal("A 1 kHz sine.", result.Text);

        JsonArray messages = standIn.Requests[1].Body["messages"]!.AsArray();
        Assert.Equal(new[] { "system", "user", "assistant", "tool", "user" }, messages.Select(m => (string?)m!["role"]));
        Assert.Equal("c2lnbmVk", (string?)messages[2]!["tool_calls"]![0]!["extra_content"]!["google"]!["thought_signature"]);
        Assert.Equal("call_1", (string?)messages[3]!["tool_call_id"]);
        Assert.Equal("image_url", (string?)messages[4]!["content"]![1]!["type"]);
    }

    [Fact]
    public async Task A_call_cut_off_at_the_length_limit_is_not_run()
    {
        var standIn = new AiStandIn(AiStandIn.OpenAiCall("call_1", "set_output", """{"volts":""", finish: "length"));
        var tools = new ScriptedTools();

        LlmResult result = await Chat(standIn, OpenAi()).CompleteWithToolsAsync("system", Asked("Set it"), tools, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("length limit partway through a call", result.Error);
        Assert.Empty(tools.Calls);
    }

    [Fact]
    public void An_error_google_wraps_in_an_array_is_read_through()
    {
        LlmResult result = OpenAiProvider.ParseResponse("""[{"error":{"code":400,"message":"API key not valid.","status":"INVALID_ARGUMENT"}}]""", "Google Gemini");

        Assert.True(result.IsError);
        Assert.Equal("API key not valid.", result.Error);
    }

    [Fact]
    public void The_factory_picks_the_adapter_by_provider()
    {
        using HttpClient http = LlmProviders.NewHttpClient();
        var relay = new RelayEndpoint();

        Assert.IsType<AnthropicProvider>(LlmProviders.Create(http, relay, Claude()));
        Assert.IsType<OpenAiProvider>(LlmProviders.Create(http, relay, Gemini()));
        Assert.IsType<OpenAiProvider>(LlmProviders.Create(http, relay, OwnServer(), fast: true));
    }

    // ------------------------------------------------------------- files, and what it costs

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private static AiAttachment Guide() => new("guide.pdf", "application/pdf", DocumentTextTests.MinimalPdf("MEASure:VOLTage:DC?"));

    /// <summary>
    /// A system prompt with nothing in it is no prompt: Anthropic's body carries no system field and
    /// the chat-completions array leads with no system turn, rather than a null or an empty one the
    /// APIs refuse.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_system_prompt_with_nothing_in_it_is_left_out(string? system)
    {
        JsonObject body = JsonNode.Parse(AnthropicProvider.BuildRequestBody("claude-haiku-4-5", 512, system!,
            AnthropicProvider.ToWireMessages(Asked("Hi"), Claude()), null))!.AsObject();

        Assert.False(body.ContainsKey("system"));
        Assert.Equal("user", (string?)Assert.Single(OpenAiProvider.ToWireMessages(system!, Asked("Hi"), Gemini()))!["role"]);

        JsonObject withPrompt = JsonNode.Parse(AnthropicProvider.BuildRequestBody("claude-haiku-4-5", 512, "Be brief.",
            AnthropicProvider.ToWireMessages(Asked("Hi"), Claude()), null))!.AsObject();

        Assert.Equal("Be brief.", (string?)withPrompt["system"]);
        Assert.Equal(2, OpenAiProvider.ToWireMessages("Be brief.", Asked("Hi"), Gemini()).Count);
    }

    /// <summary>A question with files is blocks, the files ahead of its words; one without stays the string it always was.</summary>
    [Fact]
    public void Anthropic_is_sent_a_questions_files_ahead_of_its_words()
    {
        IReadOnlyList<LlmMessage> conversation =
        [
            LlmMessage.FromUser("What is this?", [new AiAttachment("screen.png", "image/png", Png), Guide(), new AiAttachment("sweep.scpi", "", "VOLT 5"u8.ToArray())]),
            LlmMessage.FromAssistant("A supply's manual."),
        ];

        JsonArray messages = AnthropicProvider.ToWireMessages(conversation, Claude());
        JsonArray blocks = messages[0]!["content"]!.AsArray();

        Assert.Equal(new[] { "text", "image", "text", "document", "text", "text" }, blocks.Select(block => (string?)block!["type"]));
        Assert.Equal("Attached file: screen.png", (string?)blocks[0]!["text"]);
        Assert.Equal("image/png", (string?)blocks[1]!["source"]!["media_type"]);
        Assert.Equal(Convert.ToBase64String(Png), (string?)blocks[1]!["source"]!["data"]);
        Assert.Equal("application/pdf", (string?)blocks[3]!["source"]!["media_type"]);
        Assert.StartsWith("<attachment name=\"sweep.scpi\">", (string?)blocks[4]!["text"]);
        Assert.Equal("What is this?", (string?)blocks[5]!["text"]);

        Assert.Equal("A supply's manual.", (string?)messages[1]!["content"]);
    }

    /// <summary>"Extract text locally", ticked on an Anthropic connection, sends a PDF as its words here too — the same box, meaning the same thing.</summary>
    [Fact]
    public void A_pdf_goes_to_anthropic_as_its_words_when_the_connection_reads_it_locally()
    {
        AiConnection local = Claude();
        local.ExtractTextLocally = true;

        JsonArray blocks = AnthropicProvider.ContentWithFiles(LlmMessage.FromUser("Which command?", [Guide()]), local);

        Assert.Equal(new[] { "text", "text" }, blocks.Select(block => (string?)block!["type"]));
        Assert.StartsWith("<attachment name=\"guide.pdf\">", (string?)blocks[0]!["text"]);
        Assert.Contains("MEASure:VOLTage:DC?", (string?)blocks[0]!["text"]);
    }

    /// <summary>A picture as a data URL and a PDF as a file part to OpenAI; to Gemini and a server of one's own, the PDF goes as its words.</summary>
    [Fact]
    public void Chat_completions_are_sent_files_as_parts_the_endpoint_reads()
    {
        LlmMessage question = LlmMessage.FromUser("What is this?", [new AiAttachment("screen.png", "image/png", Png), Guide()]);

        JsonArray toOpenAi = OpenAiProvider.ToWireMessages("system", [question], OpenAi())[1]!["content"]!.AsArray();

        Assert.Equal(new[] { "text", "image_url", "text", "file", "text" }, toOpenAi.Select(part => (string?)part!["type"]));
        Assert.StartsWith("data:image/png;base64,", (string?)toOpenAi[1]!["image_url"]!["url"]);
        Assert.Equal("guide.pdf", (string?)toOpenAi[3]!["file"]!["filename"]);
        Assert.StartsWith("data:application/pdf;base64,", (string?)toOpenAi[3]!["file"]!["file_data"]);

        foreach (AiConnection reads in new[] { Gemini(), OwnServer() })
        {
            JsonArray parts = OpenAiProvider.ToWireMessages("system", [question], reads)[1]!["content"]!.AsArray();

            Assert.Equal(new[] { "text", "image_url", "text", "text" }, parts.Select(part => (string?)part!["type"]));
            Assert.Contains("MEASure:VOLTage:DC?", (string?)parts[2]!["text"]);
        }
    }

    [Fact]
    public async Task A_chat_completion_says_what_it_cost_too()
    {
        var standIn = new AiStandIn(AiStandIn.WithOpenAiUsage(AiStandIn.OpenAiText("Hello."), 5000, 100, cached: 4096, total: 5900));
        OpenAiProvider provider = Chat(standIn, Gemini());

        await provider.CompleteAsync("system", Asked("Hi"), CancellationToken.None);

        Assert.Equal(5000, provider.Spent.InputTokens);
        Assert.Equal(4096, provider.Spent.CachedInputTokens);
        Assert.Equal(900, provider.Spent.OutputTokens);

        // gemini-test is no model anybody priced.
        Assert.Null(provider.Price);
    }

    // ------------------------------------------------------------------ when it goes wrong

    /// <summary>The status is read as well as the body: a refusal says which, and why, in the provider's own words.</summary>
    [Fact]
    public async Task A_refused_key_is_said_with_its_status_and_the_providers_words()
    {
        var standIn = new AiStandIn((_, _) => AiStandIn.Json("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", HttpStatusCode.Unauthorized));

        LlmResult result = await Anthropic(standIn).CompleteAsync("", Asked("Hi"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Anthropic Claude answered 401 Unauthorized: invalid x-api-key", result.Error);
    }

    [Fact]
    public async Task A_refused_key_with_nothing_readable_says_what_to_check()
    {
        var standIn = new AiStandIn((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("") });

        LlmResult result = await Chat(standIn, OpenAi()).CompleteAsync("", Asked("Hi"), CancellationToken.None);

        Assert.Equal("OpenAI-compatible endpoint answered 401 Unauthorized. The key may be wrong, or not allowed to use this model.", result.Error);
    }

    /// <summary>GraphDBViewer said "Could not parse model response" for a gateway's error page, which names the wrong problem.</summary>
    [Fact]
    public async Task A_failure_with_no_readable_body_is_said_by_its_status()
    {
        var standIn = new AiStandIn((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>Bad gateway</html>") });

        LlmResult result = await Chat(standIn, Gemini()).CompleteAsync("", Asked("Hi"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Google Gemini answered 502 Bad Gateway.", result.Error);
    }

    /// <summary>No page here calls a provider itself, so there is no CORS to blame — only an address to check and a server to start.</summary>
    [Fact]
    public async Task A_server_that_cannot_be_reached_is_named_with_what_to_check()
    {
        var standIn = new AiStandIn((_, _) => throw new HttpRequestException("No connection could be made because the target machine actively refused it."));

        LlmResult result = await Chat(standIn, OwnServer()).CompleteAsync("", Asked("Hi"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.StartsWith("Could not reach http://localhost:11434: No connection", result.Error);
        Assert.EndsWith("actively refused it. Check the address, and that the server is running.", result.Error);
        Assert.DoesNotContain("CORS", result.Error);
    }

    /// <summary>A server that never answers is given up on when the request's own clock runs out, and said as a wait — not thrown, and not said as a Stop.</summary>
    [Fact]
    public async Task A_server_that_never_answers_is_given_up_on_and_said_as_a_wait()
    {
        using var http = new HttpClient(new Silent()) { Timeout = Timeout.InfiniteTimeSpan };
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");

        var (body, problem) = await LlmProviders.SendAsync(http, request, "Anthropic Claude", CancellationToken.None, TimeSpan.FromMilliseconds(100));

        Assert.Null(body);
        Assert.Equal("Anthropic Claude did not answer within 1s.", problem);
    }

    /// <summary>
    /// **A Stop is let through as it is**, not said as a timeout. A person's Stop and the request's
    /// clock are the same exception; the filter on the caller's token is what tells them apart, and
    /// without it the window would report "did not answer" for a run somebody stopped.
    /// </summary>
    [Fact]
    public async Task A_stop_while_waiting_is_let_through_rather_than_said_as_a_timeout()
    {
        using var http = new HttpClient(new Silent()) { Timeout = Timeout.InfiniteTimeSpan };
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => LlmProviders.SendAsync(http, request, "Anthropic Claude", stop.Token, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task A_stop_before_anything_is_sent_is_let_through()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Anthropic(new AiStandIn(AiStandIn.AnthropicText("never"))).CompleteAsync("", Asked("Hi"), stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Chat(new AiStandIn(AiStandIn.OpenAiText("never")), Gemini()).CompleteWithToolsAsync("", Asked("Hi"), new ScriptedTools(), stop.Token));
    }

    [Fact]
    public void The_providers_client_has_no_timeout_of_its_own()
    {
        using HttpClient http = LlmProviders.NewHttpClient();

        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }

    /// <summary>A server that takes the request and never answers it, until it is told to stop waiting.</summary>
    private sealed class Silent : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }
}
