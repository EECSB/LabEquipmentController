using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// Whole conversations, run against a stand-in provider with a bench under them.
///
/// The provider answers from a script in its own wire format, and where what reaches an instrument
/// is the point the tools are the real <see cref="BenchToolRunner"/> over a <see cref="FakeBench"/> —
/// so what is checked is what a conversation sent to the instrument, what went back to the model
/// after each call, and what a person sees on the way: the cards, the waits, a decline, a stop. GDS
/// Viewer's AiChatTests, with a bench where it had a layout.
/// </summary>
public class AiChatTests
{
    private const string Generator = "Siglent Technologies,SDG2042X,SDG2XCAD1R0001,2.01.01.35R3B2";
    private const string Meter = "Siglent Technologies,SDM3065X,SDM36HCD801207,3.02.01.13";

    private const string SendOn = """{"instrument":"gen","command":"C1:OUTP ON"}""";
    private const string ReadVolts = """{"instrument":"dmm","command":"MEAS:VOLT:DC?"}""";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    /// <summary>A generator and a meter, the meter reading 4.99876 V.</summary>
    private static (FakeBench Bench, FakeInstrument Gen, FakeInstrument Dmm) Bench()
    {
        var gen = new FakeInstrument(Generator, "192.168.1.20");
        var dmm = new FakeInstrument(Meter, "192.168.1.21") { Replies = { ["MEAS:VOLT:DC?"] = "4.99876E+00" } };
        return (new FakeBench(gen, dmm), gen, dmm);
    }

    /// <summary>A Claude connection left on its provider's default model, claude-sonnet-5.</summary>
    private static AiConnection Claude() => new() { Provider = AiProvider.Anthropic };

    private static AiConnection Gemini() => new() { Provider = AiProvider.Gemini, Model = "gemini-test" };

    private static AnthropicProvider Anthropic(AiStandIn standIn)
        => new(standIn.Client(), new DirectLlmEndpoint(Claude(), "test-key"), Claude());

    private static OpenAiProvider Chat(AiStandIn standIn)
        => new(standIn.Client(), new DirectLlmEndpoint(Gemini(), "test-key"), Gemini());

    private static string Args(params (string Name, string Value)[] pairs)
    {
        var o = new JsonObject();
        foreach (var (name, value) in pairs) o[name] = value;
        return o.ToJsonString();
    }

    /// <summary>A message's words, whether they went as a string or as blocks — the last one of a tool loop's request is a block, to carry Anthropic's cache mark.</summary>
    private static string? WordsOf(JsonNode? content) => content switch
    {
        JsonValue words => (string?)words,
        JsonArray blocks => string.Concat(blocks.Select(block => (string?)block?["text"] ?? "")),
        _ => null,
    };

    /// <summary>
    /// A run awaited for no longer than a stand-in ever takes, so a gate that never lets go fails its
    /// test rather than hanging the whole run — which two deliberate breaks of the approval switch did
    /// to GDS Viewer's suite.
    /// </summary>
    private static Task<T> Within<T>(Task<T> running) => running.WaitAsync(TimeSpan.FromSeconds(20));

    /// <summary>Waits until a card is waiting for an answer, which happens partway through a send — on a pool thread here.</summary>
    private static async Task<ChatTurn> Waiting(AiChat chat)
    {
        for (int i = 0; i < 2000; i++)
        {
            if (chat.Turns.LastOrDefault(turn => turn.Kind == ChatTurn.Tool && turn.Decision is null) is ChatTurn card) return card;
            await Task.Delay(10);
        }
        throw new TimeoutException("No card came up to be answered.");
    }

    // ------------------------------------------------------------------------------ the loop

    /// <summary>The whole round trip on the middle rung: a send waits, is approved, reaches the instrument, and its answer goes back under the call's id.</summary>
    [Fact]
    public async Task A_send_approved_reaches_the_instrument_and_is_answered()
    {
        var (bench, gen, _) = Bench();
        var standIn = new AiStandIn(AiStandIn.AnthropicCall("toolu_1", "send", SendOn, "Switching it on."), AiStandIn.AnthropicText("Channel 1 is on."));
        var chat = new AiChat();

        Task<ChatOutcome> sending = chat.SendAsync("Switch on channel 1", Anthropic(standIn), "system", new BenchToolRunner(bench));
        ChatTurn card = await Waiting(chat);

        Assert.Equal("send", card.ToolName);
        Assert.True(card.Changes);
        Assert.False(card.Auto);
        Assert.Empty(gen.Sent);

        chat.Decide(card, true);

        Assert.Equal(ChatOutcome.Answered, await Within(sending));
        Assert.Equal(new[] { "C1:OUTP ON" }, gen.Sent);
        Assert.Equal(new[] { ChatTurn.User, ChatTurn.Tool, ChatTurn.Assistant }, chat.Turns.Select(turn => turn.Kind));
        Assert.Equal("Channel 1 is on.", chat.Turns[2].Text);
        Assert.True(card.Decision);
        Assert.StartsWith("Sent C1:OUTP ON to gen.", card.Result);

        // The second request carries the model's own call back, and the answer to it under the call's id.
        JsonArray messages = standIn.Requests[1].Body["messages"]!.AsArray();
        JsonNode answer = messages[^1]!["content"]![0]!;
        Assert.Equal("assistant", (string?)messages[^2]!["role"]);
        Assert.Equal("tool_result", (string?)answer["type"]);
        Assert.Equal("toolu_1", (string?)answer["tool_use_id"]);
        Assert.Equal(card.Result, (string?)answer["content"]);
    }

    /// <summary>The same through the OpenAI-compatible adapter at Google's endpoint: a read runs at once on the middle rung, and the call goes back with its signature.</summary>
    [Fact]
    public async Task A_read_asked_of_gemini_runs_at_once_and_is_answered()
    {
        var (bench, _, dmm) = Bench();
        var standIn = new AiStandIn(AiStandIn.OpenAiCall("call_1", "query", ReadVolts), AiStandIn.OpenAiText("It reads 4.99876 V."));
        var chat = new AiChat();

        Assert.Equal(ChatOutcome.Answered, await Within(chat.SendAsync("What does the meter read?", Chat(standIn), "system", new BenchToolRunner(bench))));

        Assert.Equal(new[] { "MEAS:VOLT:DC?" }, dmm.Sent);
        Assert.True(chat.Turns[1].Auto);
        Assert.Contains("4.99876E+00", chat.Turns[1].Result);
        Assert.Equal("It reads 4.99876 V.", chat.Turns[^1].Text);

        JsonArray messages = standIn.Requests[1].Body["messages"]!.AsArray();
        Assert.Equal("c2lnbmVk", (string?)messages[^2]!["tool_calls"]![0]!["extra_content"]!["google"]!["thought_signature"]);
        Assert.Equal("call_1", (string?)messages[^1]!["tool_call_id"]);
    }

    /// <summary>A refusal goes back as one, nothing reaches the bench, and the model gets to try again.</summary>
    [Fact]
    public async Task A_refused_call_goes_back_marked_and_nothing_reaches_the_bench()
    {
        var (bench, _, _) = Bench();
        var standIn = new AiStandIn(AiStandIn.AnthropicCall("toolu_1", "query", """{"instrument":"psu","command":"VOLT?"}"""), AiStandIn.AnthropicText("No supply is open."));
        var chat = new AiChat();

        await Within(chat.SendAsync("What is the supply set to?", Anthropic(standIn), "system", new BenchToolRunner(bench)));

        Assert.Empty(bench.Written);
        Assert.True(chat.Turns[1].ResultIsError);
        Assert.Contains("No instrument called \"psu\" is open", chat.Turns[1].Result);
        Assert.True((bool?)standIn.Requests[1].Body["messages"]!.AsArray()[^1]!["content"]![0]!["is_error"]);
    }

    /// <summary>A decline is an answer to the call: the instrument is never sent it — not even its error queue is read — and the model is told.</summary>
    [Fact]
    public async Task A_declined_send_never_reaches_the_instrument_and_the_model_is_told()
    {
        var (bench, gen, _) = Bench();
        var standIn = new AiStandIn(AiStandIn.AnthropicCall("toolu_1", "send", SendOn), AiStandIn.AnthropicText("All right, I left it off."));
        var chat = new AiChat();

        Task<ChatOutcome> sending = chat.SendAsync("Switch on channel 1", Anthropic(standIn), "system", new BenchToolRunner(bench));
        chat.Decide(await Waiting(chat), false);

        Assert.Equal(ChatOutcome.Answered, await Within(sending));
        Assert.Empty(gen.Sent);
        Assert.Empty(bench.Written);
        Assert.False(chat.Turns[1].Decision);

        JsonNode answer = standIn.Requests[1].Body["messages"]!.AsArray()[^1]!["content"]![0]!;
        Assert.Equal(ApprovingToolRunner.DeclinedMessage, (string?)answer["content"]);
    }

    /// <summary>A stop while a card waits lets it go as a no: nothing is sent, no request follows, and the window is free.</summary>
    [Fact]
    public async Task A_stop_while_a_card_waits_ends_the_answer_and_sends_nothing()
    {
        var (bench, gen, _) = Bench();
        var standIn = new AiStandIn(AiStandIn.AnthropicCall("toolu_1", "send", SendOn), AiStandIn.AnthropicText("never"));
        var chat = new AiChat();

        Task<ChatOutcome> sending = chat.SendAsync("Switch on channel 1", Anthropic(standIn), "system", new BenchToolRunner(bench));
        await Waiting(chat);

        chat.Cancel();

        Assert.Equal(ChatOutcome.Stopped, await Within(sending));
        Assert.Empty(gen.Sent);
        Assert.Single(standIn.Requests);
        Assert.Equal("Stopped.", chat.Turns[^1].Text);
        Assert.False(chat.Generating);
    }

    /// <summary>A stop or a failure after something was sent says it stays sent: nothing on a bench is taken back.</summary>
    [Fact]
    public async Task A_failure_after_a_send_says_what_was_sent_stays_sent()
    {
        var (bench, gen, _) = Bench();
        var standIn = new AiStandIn((index, _) => index == 0
            ? AiStandIn.Json(AiStandIn.AnthropicCall("toolu_1", "send", SendOn))
            : AiStandIn.Json("""{"error":{"message":"Overloaded."}}""", HttpStatusCode.ServiceUnavailable));
        var chat = new AiChat { Mode = ToolApprovalMode.AutoReadWrite };

        Assert.Equal(ChatOutcome.Failed, await Within(chat.SendAsync("Switch on channel 1", Anthropic(standIn), "system", new BenchToolRunner(bench))));

        Assert.Equal(new[] { "C1:OUTP ON" }, gen.Sent);
        Assert.Equal("Anthropic Claude answered 503 Service Unavailable: Overloaded. Anything it sent to an instrument before that stays sent — there is no undo on hardware.",
                     chat.Turns[^1].Text);
    }

    /// <summary>
    /// A model that keeps calling, every call working, is paused at the cap rather than failed — and
    /// Continue carries it on, the calls before the pause going with it in words, the pause itself not.
    /// </summary>
    [Fact]
    public async Task A_model_that_keeps_working_is_paused_at_the_cap_and_continue_carries_it_on()
    {
        var standIn = new AiStandIn((index, _) => AiStandIn.Json(index < 3
            ? AiStandIn.AnthropicCall("toolu_" + index, "read_meter", $$"""{"sample":{{index}}}""")
            : AiStandIn.AnthropicText("All three read 4.998 V.")));
        var chat = new AiChat { MaxToolRounds = 3 };
        AnthropicProvider provider = Anthropic(standIn);
        var tools = new ScriptedTools();

        Assert.Equal(ChatOutcome.Paused, await Within(chat.SendAsync("Read it three times", provider, "system", tools)));

        ChatTurn paused = chat.Turns[^1];
        Assert.Equal(ChatTurn.Paused, paused.Kind);
        Assert.Equal(LlmProviders.RoundsPaused(3), paused.Text);
        Assert.Equal(3, standIn.Requests.Count);
        Assert.False(chat.Generating);

        Assert.Equal(ChatOutcome.Answered, await Within(chat.SendAsync("Continue", provider, "system", tools)));

        List<string> carried = [.. standIn.Requests[3].Body["messages"]!.AsArray().Select(message => WordsOf(message!["content"]) ?? "")];
        Assert.Contains(carried, words => words.Contains("- You called read_meter {\"sample\":2}, and it answered: 4.998 V"));
        Assert.Equal("Continue", carried[^1]);
        Assert.DoesNotContain(carried, words => words.Contains("is paused"));
        Assert.Equal("All three read 4.998 V.", chat.Turns[^1].Text);
    }

    /// <summary>A model whose every call is refused is stopped as stuck after a few rounds, not left to run to the cap.</summary>
    [Fact]
    public async Task A_model_whose_calls_keep_failing_is_stopped_as_stuck()
    {
        var (bench, _, _) = Bench();
        int calls = 0;
        var standIn = new AiStandIn((_, _) => AiStandIn.Json(AiStandIn.AnthropicCall("toolu_" + calls, "query", $$"""{"instrument":"psu{{calls++}}","command":"VOLT?"}""")));
        var chat = new AiChat();

        Assert.Equal(ChatOutcome.Failed, await Within(chat.SendAsync("Read the supply", Anthropic(standIn), "system", new BenchToolRunner(bench))));

        Assert.Equal(LlmProviders.MaxStuckRounds, standIn.Requests.Count);
        Assert.Equal(ChatTurn.Error, chat.Turns[^1].Kind);
        Assert.StartsWith(LlmProviders.RoundsStuck, chat.Turns[^1].Text);
    }

    [Fact]
    public async Task A_failure_is_a_turn_that_says_what_went_wrong()
    {
        var standIn = new AiStandIn((_, _) => AiStandIn.Json("""{"error":{"message":"model not found"}}""", HttpStatusCode.NotFound));
        var chat = new AiChat();

        await Within(chat.SendAsync("Hi", Chat(standIn), "system", new ScriptedTools()));

        Assert.Equal(ChatTurn.Error, chat.Turns[^1].Kind);
        Assert.Equal("Google Gemini answered 404 Not Found: model not found", chat.Turns[^1].Text);
    }

    // -------------------------------------------------------------------------------- the rungs

    /// <summary>The middle rung, where the window starts: a read runs at once, a send waits for a person.</summary>
    [Fact]
    public async Task On_the_middle_rung_a_read_runs_and_a_send_waits()
    {
        var (bench, gen, dmm) = Bench();
        var standIn = new AiStandIn(
            AiStandIn.AnthropicCall("toolu_1", "query", ReadVolts),
            AiStandIn.AnthropicCall("toolu_2", "send", SendOn),
            AiStandIn.AnthropicText("Read it, then switched it on."));
        var chat = new AiChat();

        Assert.Equal(ToolApprovalMode.AutoRead, chat.Mode);

        Task<ChatOutcome> sending = chat.SendAsync("Read, then switch on", Anthropic(standIn), "system", new BenchToolRunner(bench));
        ChatTurn card = await Waiting(chat);

        // The read went through on its own and is on screen as having run; the send waits, and nothing is on.
        ChatTurn read = chat.Turns.Single(turn => turn.ToolName == "query");
        Assert.True(read.Auto);
        Assert.NotNull(read.Result);
        Assert.Equal(new[] { "MEAS:VOLT:DC?" }, dmm.Sent);
        Assert.Equal("send", card.ToolName);
        Assert.Empty(gen.Sent);

        chat.Decide(card, true);
        await Within(sending);

        Assert.Equal(new[] { "C1:OUTP ON" }, gen.Sent);
        Assert.Equal("Read it, then switched it on.", chat.Turns[^1].Text);
    }

    [Fact]
    public async Task On_the_bottom_rung_even_a_read_waits()
    {
        var (bench, _, dmm) = Bench();
        var standIn = new AiStandIn(AiStandIn.AnthropicCall("toolu_1", "query", ReadVolts), AiStandIn.AnthropicText("4.99876 V."));
        var chat = new AiChat { Mode = ToolApprovalMode.Ask };

        Task<ChatOutcome> sending = chat.SendAsync("What does it read?", Anthropic(standIn), "system", new BenchToolRunner(bench));
        ChatTurn card = await Waiting(chat);

        Assert.Equal("query", card.ToolName);
        Assert.Empty(dmm.Sent);

        chat.Decide(card, true);
        await Within(sending);

        Assert.Equal(new[] { "MEAS:VOLT:DC?" }, dmm.Sent);
    }

    /// <summary>
    /// **A rung chosen in the middle of an answer is in force at once**: a send waiting on the middle
    /// rung is let go by a switch to the top one, marked as run without asking, and the send after it
    /// in the same answer runs unasked.
    /// </summary>
    [Fact]
    public async Task A_switch_to_the_top_rung_lets_a_waiting_send_go_and_the_next_runs_unasked()
    {
        var (bench, gen, _) = Bench();
        var standIn = new AiStandIn(
            AiStandIn.AnthropicCall("toolu_1", "send", SendOn),
            AiStandIn.AnthropicCall("toolu_2", "send", """{"instrument":"gen","command":"C2:OUTP ON"}"""),
            AiStandIn.AnthropicText("Both channels are on."));
        var chat = new AiChat();

        Task<ChatOutcome> sending = chat.SendAsync("Switch both on", Anthropic(standIn), "system", new BenchToolRunner(bench));
        ChatTurn card = await Waiting(chat);

        Assert.Empty(gen.Sent);

        chat.Mode = ToolApprovalMode.AutoReadWrite;
        await Within(sending);

        Assert.Equal(new[] { "C1:OUTP ON", "C2:OUTP ON" }, gen.Sent);

        List<ChatTurn> cards = [.. chat.Turns.Where(turn => turn.Kind == ChatTurn.Tool)];
        Assert.Equal(2, cards.Count);
        Assert.Same(card, cards[0]);
        Assert.All(cards, ran => Assert.True(ran.Auto));
        Assert.All(cards, ran => Assert.True(ran.Decision));
    }

    /// <summary>A switch to a rung that still asks for sends leaves a waiting send waiting, for a person to answer.</summary>
    [Fact]
    public async Task A_switch_that_still_asks_leaves_a_waiting_send_waiting()
    {
        var (bench, gen, _) = Bench();
        var standIn = new AiStandIn(AiStandIn.AnthropicCall("toolu_1", "send", SendOn), AiStandIn.AnthropicText("On."));
        var chat = new AiChat { Mode = ToolApprovalMode.Ask };

        Task<ChatOutcome> sending = chat.SendAsync("Switch on channel 1", Anthropic(standIn), "system", new BenchToolRunner(bench));
        ChatTurn card = await Waiting(chat);

        chat.Mode = ToolApprovalMode.AutoRead;

        Assert.False(card.Gate!.Task.IsCompleted);
        Assert.False(card.Auto);
        Assert.Empty(gen.Sent);

        chat.Decide(card, true);
        await Within(sending);

        Assert.Equal(new[] { "C1:OUTP ON" }, gen.Sent);
        Assert.False(card.Auto);
    }

    /// <summary>A read waiting on the bottom rung goes when the middle one is chosen, since that rung reads unasked.</summary>
    [Fact]
    public async Task A_switch_to_the_middle_rung_lets_a_waiting_read_go()
    {
        var (bench, _, dmm) = Bench();
        var standIn = new AiStandIn(AiStandIn.AnthropicCall("toolu_1", "query", ReadVolts), AiStandIn.AnthropicText("4.99876 V."));
        var chat = new AiChat { Mode = ToolApprovalMode.Ask };

        Task<ChatOutcome> sending = chat.SendAsync("What does it read?", Anthropic(standIn), "system", new BenchToolRunner(bench));
        ChatTurn card = await Waiting(chat);

        chat.Mode = ToolApprovalMode.AutoRead;
        await Within(sending);

        Assert.True(card.Auto);
        Assert.True(card.Decision);
        Assert.Equal(new[] { "MEAS:VOLT:DC?" }, dmm.Sent);
    }

    /// <summary>The other way: a switch to a stricter rung partway through an answer stops the calls after it, which wait for a person.</summary>
    [Fact]
    public async Task A_switch_to_a_stricter_rung_stops_the_calls_after_it()
    {
        var (bench, _, dmm) = Bench();
        var chat = new AiChat { Mode = ToolApprovalMode.AutoReadWrite };
        var standIn = new AiStandIn((index, _) =>
        {
            // Asked for between the first call and the second, as a person reaching for the rung would.
            if (index == 1) chat.Mode = ToolApprovalMode.Ask;

            return AiStandIn.Json(index switch
            {
                0 => AiStandIn.AnthropicCall("toolu_1", "query", ReadVolts),
                1 => AiStandIn.AnthropicCall("toolu_2", "query", """{"instrument":"dmm","command":"MEAS:CURR:DC?"}"""),
                _ => AiStandIn.AnthropicText("Read both."),
            });
        });

        Task<ChatOutcome> sending = chat.SendAsync("Read volts and amps", Anthropic(standIn), "system", new BenchToolRunner(bench));
        ChatTurn card = await Waiting(chat);

        Assert.Equal("query", card.ToolName);
        Assert.Contains("MEAS:CURR:DC?", card.ArgumentsJson);
        Assert.Equal(new[] { "MEAS:VOLT:DC?" }, dmm.Sent);

        chat.Decide(card, true);
        await Within(sending);

        Assert.Equal(new[] { "MEAS:VOLT:DC?", "MEAS:CURR:DC?" }, dmm.Sent);
        Assert.False(card.Auto);
    }

    // ---------------------------------------------------------------------------- proposals

    /// <summary>
    /// A proposal never waits — on the bottom rung either — changes nothing, and its card carries the
    /// script for Use This Script: the button is the person's say.
    /// </summary>
    [Fact]
    public async Task A_proposal_never_waits_and_its_card_carries_the_script()
    {
        var (bench, _, _) = Bench();
        string script = "C1:OUTP ON\nDELAY 500\nC1:FREQ 1000";
        var standIn = new AiStandIn(
            AiStandIn.AnthropicCall("toolu_1", "propose_script", Args(("kind", "instrument"), ("instrument", "gen"), ("script", script))),
            AiStandIn.AnthropicText("Here is a script; use it if it looks right."));
        var chat = new AiChat { Mode = ToolApprovalMode.Ask };

        Assert.Equal(ChatOutcome.Answered, await Within(chat.SendAsync("Write me a script", Anthropic(standIn), "system", new BenchToolRunner(bench))));

        ChatTurn card = chat.Turns[1];
        Assert.True(card.Proposes);
        Assert.False(card.Changes);
        Assert.True(card.Auto);
        Assert.True(card.Decision);
        Assert.Equal(script, card.Proposal);
        Assert.Contains("3 lines", card.Result);
        Assert.Empty(bench.Written);
    }

    /// <summary>A proposal is not replayed whole to the model that wrote it: the next question says how many lines it was, and for whom.</summary>
    [Fact]
    public async Task A_proposal_is_replayed_as_its_lines_and_for_whom_never_whole()
    {
        var (bench, _, _) = Bench();
        var standIn = new AiStandIn(
            AiStandIn.AnthropicCall("toolu_1", "propose_script", Args(("kind", "instrument"), ("instrument", "gen"), ("script", "C1:OUTP ON\nDELAY 500\nC1:FREQ 1000"))),
            AiStandIn.AnthropicText("Here it is."),
            AiStandIn.AnthropicText("Change the last line to C1:FREQ 2000."));
        var chat = new AiChat();
        AnthropicProvider provider = Anthropic(standIn);
        var tools = new BenchToolRunner(bench);

        await Within(chat.SendAsync("Write me a script", provider, "system", tools));
        await Within(chat.SendAsync("Make it 2 kHz", provider, "system", tools));

        string replayed = WordsOf(standIn.Requests[2].Body["messages"]![1]!["content"]) ?? "";

        Assert.Contains("- You proposed a script of 3 lines for gen, and it answered: A draft for gen's script", replayed);
        Assert.DoesNotContain("DELAY 500", replayed);
        Assert.DoesNotContain("propose_script", replayed);
    }

    // ------------------------------------------------------------------------ the conversation

    /// <summary>What was run is said in words in the next message, since a tool exchange cannot cross from one request to the next — readings exactly as the instrument wrote them.</summary>
    [Fact]
    public async Task What_was_run_is_replayed_in_words()
    {
        var (bench, _, _) = Bench();
        var standIn = new AiStandIn(AiStandIn.AnthropicCall("toolu_1", "query", ReadVolts), AiStandIn.AnthropicText("It reads 4.99876 V."), AiStandIn.AnthropicText("About 5 V."));
        var chat = new AiChat();
        AnthropicProvider provider = Anthropic(standIn);
        var tools = new BenchToolRunner(bench);

        await Within(chat.SendAsync("What does the meter read?", provider, "system", tools));
        await Within(chat.SendAsync("Roughly?", provider, "system", tools));

        List<(string? Role, string? Words)> conversation =
            [.. standIn.Requests[2].Body["messages"]!.AsArray().Select(each => ((string?)each!["role"], WordsOf(each["content"])))];

        Assert.Equal(("user", "What does the meter read?"), conversation[0]);
        Assert.Equal("user", conversation[1].Role);
        Assert.Contains("You called query {\"instrument\":\"dmm\",\"command\":\"MEAS:VOLT:DC?\"}, and it answered: dmm answered MEAS:VOLT:DC? with: 4.99876E+00", conversation[1].Words);
        Assert.Equal(("assistant", "It reads 4.99876 V."), conversation[2]);
        Assert.Equal(("user", "Roughly?"), conversation[3]);
    }

    /// <summary>On a bench what matters about a call is whether it reached the instrument, so the replay says a declined send never did.</summary>
    [Fact]
    public async Task A_declined_send_is_replayed_as_never_having_reached_the_instrument()
    {
        var (bench, _, _) = Bench();
        var standIn = new AiStandIn(AiStandIn.AnthropicCall("toolu_1", "send", SendOn), AiStandIn.AnthropicText("Left it off."), AiStandIn.AnthropicText("Fine."));
        var chat = new AiChat();
        AnthropicProvider provider = Anthropic(standIn);
        var tools = new BenchToolRunner(bench);

        Task<ChatOutcome> sending = chat.SendAsync("Switch on channel 1", provider, "system", tools);
        chat.Decide(await Waiting(chat), false);
        await Within(sending);
        await Within(chat.SendAsync("Leave it", provider, "system", tools));

        string replayed = WordsOf(standIn.Requests[2].Body["messages"]![1]!["content"]) ?? "";
        Assert.Contains("- You asked to call send", replayed);
        Assert.Contains("and the person declined, so it never reached the instrument.", replayed);
    }

    /// <summary>The bench changed: what came before stays on screen and is not sent.</summary>
    [Fact]
    public async Task After_starting_afresh_the_model_is_sent_only_what_came_after()
    {
        var standIn = new AiStandIn(AiStandIn.AnthropicText("First."), AiStandIn.AnthropicText("Second."));
        var chat = new AiChat();
        AnthropicProvider provider = Anthropic(standIn);

        await Within(chat.SendAsync("About the old bench", provider, "system", null));
        chat.StartAfresh("The instruments open have changed.");
        await Within(chat.SendAsync("About this one", provider, "system", null));

        Assert.Contains(chat.Turns, turn => turn.Text == "About the old bench");
        Assert.Contains(chat.Turns, turn => turn.Kind == ChatTurn.Note);

        JsonNode sent = Assert.Single(standIn.Requests[1].Body["messages"]!.AsArray())!;
        Assert.Equal("About this one", (string?)sent["content"]);
    }

    /// <summary>Clear during an answer: the answer's ending is not written into the conversation that replaced it, and nothing reaches the bench.</summary>
    [Fact]
    public async Task Clear_during_an_answer_leaves_an_empty_conversation()
    {
        var (bench, gen, _) = Bench();
        var standIn = new AiStandIn(AiStandIn.AnthropicCall("toolu_1", "send", SendOn), AiStandIn.AnthropicText("never"));
        var chat = new AiChat();

        Task<ChatOutcome> sending = chat.SendAsync("Switch on channel 1", Anthropic(standIn), "system", new BenchToolRunner(bench));
        await Waiting(chat);

        chat.Clear();

        Assert.Equal(ChatOutcome.Stopped, await Within(sending));
        Assert.Empty(chat.Turns);
        Assert.False(chat.Generating);
        Assert.Empty(gen.Sent);
    }

    [Fact]
    public async Task Nothing_is_sent_for_an_empty_message_or_during_an_answer()
    {
        var (bench, _, _) = Bench();
        var standIn = new AiStandIn(AiStandIn.AnthropicCall("toolu_1", "send", SendOn), AiStandIn.AnthropicText("Done."));
        var chat = new AiChat();
        AnthropicProvider provider = Anthropic(standIn);

        Assert.Equal(ChatOutcome.NotSent, await chat.SendAsync("   ", provider, "system", null));
        Assert.Empty(chat.Turns);

        Task<ChatOutcome> sending = chat.SendAsync("Switch on channel 1", provider, "system", new BenchToolRunner(bench));
        ChatTurn card = await Waiting(chat);

        Assert.Equal(ChatOutcome.NotSent, await chat.SendAsync("And another", provider, "system", null));
        Assert.DoesNotContain(chat.Turns, turn => turn.Text == "And another");

        chat.Decide(card, true);
        await Within(sending);
    }

    /// <summary>
    /// **A window on a thread of its own**, as the desktop's is: every change is marshalled to it and
    /// waited on, and it reads the conversation there, while a reader that is told nothing reads it
    /// from a third — through twenty rounds of calls landing on pool threads. Raised with the
    /// conversation locked, the first change would deadlock against the window; handed the live lists,
    /// a reader would meet one changing under it.
    /// </summary>
    [Fact]
    public async Task A_window_on_a_thread_of_its_own_reads_the_conversation_while_a_run_changes_it()
    {
        var standIn = new AiStandIn((index, _) => AiStandIn.Json(index < 20
            ? AiStandIn.AnthropicCall("toolu_" + index, "read_meter", $$"""{"sample":{{index}}}""")
            : AiStandIn.AnthropicText("Done.")));
        var chat = new AiChat { Mode = ToolApprovalMode.AutoReadWrite };
        using var window = new WindowThread();
        int drawn = 0;

        chat.Changed += () =>
        {
            window.Invoke(() =>
            {
                foreach (ChatTurn turn in chat.Turns)
                {
                    _ = turn.Result?.Length;
                    _ = turn.Attachments.Count;
                }
                _ = chat.Spend.Count + chat.Pending.Count + chat.SentFiles().Count;
                drawn++;
            });
            return Task.CompletedTask;
        };

        using var stop = new CancellationTokenSource();
        Task reading = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (ChatTurn turn in chat.Turns) _ = turn.Kind;
                _ = chat.Used.InputTokens + chat.BuildConversation().Count;
            }
        });

        ChatOutcome outcome = await Within(chat.SendAsync("Log it", Anthropic(standIn), "system", new ScriptedTools()));
        stop.Cancel();
        await reading;

        Assert.Equal(ChatOutcome.Answered, outcome);
        Assert.Equal(20, chat.Turns.Count(turn => turn.Kind == ChatTurn.Tool));
        Assert.True(drawn > 20);
    }

    /// <summary>A window's thread: everything handed to it runs there, one at a time, and Invoke waits for it — as WinForms' Control.Invoke does.</summary>
    private sealed class WindowThread : IDisposable
    {
        private readonly BlockingCollection<Action> _work = [];

        public WindowThread()
        {
            var thread = new Thread(() =>
            {
                foreach (Action action in _work.GetConsumingEnumerable()) action();
            }) { IsBackground = true };
            thread.Start();
        }

        public void Invoke(Action action)
        {
            using var done = new ManualResetEventSlim();
            Exception? failed = null;
            _work.Add(() =>
            {
                try { action(); }
                catch (Exception ex) { failed = ex; }
                finally { done.Set(); }
            });
            done.Wait();
            if (failed is not null) throw failed;
        }

        public void Dispose() => _work.CompleteAdding();
    }

    // --------------------------------------------------------------------- files, and the bill

    /// <summary>A PDF of nine megabytes — two of them are past what one conversation sends.</summary>
    private static byte[] BigPdf()
    {
        var data = new byte[9 * 1024 * 1024];
        "%PDF-"u8.CopyTo(data);
        return data;
    }

    /// <summary>The files in the box go with the question and leave the box; and every later question sends them again, the whole conversation going each time.</summary>
    [Fact]
    public async Task Files_go_with_their_question_and_again_with_the_next()
    {
        var standIn = new AiStandIn(AiStandIn.AnthropicText("A sine, 1 kHz."), AiStandIn.AnthropicText("Its amplitude is 2 V."));
        var chat = new AiChat();
        AnthropicProvider provider = Anthropic(standIn);
        var screen = new AiAttachment("screen.png", "image/png", Png);

        Assert.Null(chat.Attach(screen));
        Assert.Equal(ChatOutcome.Answered, await Within(chat.SendAsync("What is on the scope?", provider, "system", null)));

        Assert.Empty(chat.Pending);
        Assert.Same(screen, Assert.Single(chat.Turns[0].Attachments));
        Assert.Equal("image", (string?)standIn.Requests[0].Body["messages"]![0]!["content"]![1]!["type"]);

        await Within(chat.SendAsync("And its amplitude?", provider, "system", null));

        Assert.Equal("image", (string?)standIn.Requests[1].Body["messages"]![0]!["content"]![1]!["type"]);
        Assert.Equal(Png.Length, chat.SentBytes);
        Assert.Single(chat.SentFiles());
    }

    /// <summary>Unticked in the files panel: on screen still, not sent, and said in a line where it was.</summary>
    [Fact]
    public async Task An_excluded_file_is_said_and_not_sent()
    {
        var standIn = new AiStandIn(AiStandIn.AnthropicText("A sine."), AiStandIn.AnthropicText("Fine."));
        var chat = new AiChat();
        AnthropicProvider provider = Anthropic(standIn);
        var screen = new AiAttachment("screen.png", "image/png", Png);

        chat.Attach(screen);
        await Within(chat.SendAsync("What is this?", provider, "system", null));

        Assert.Null(chat.Include(screen, false));
        Assert.Equal(0, chat.SentBytes);

        await Within(chat.SendAsync("Without the picture now", provider, "system", null));

        string sent = standIn.Requests[1].Body.ToJsonString();
        Assert.DoesNotContain(Convert.ToBase64String(Png), sent);
        Assert.Contains("has since left it out of the conversation", sent);
        Assert.Single(chat.SentFiles());
    }

    /// <summary>A refused file would be refused with every later question, so a failed question gives its files back to the box.</summary>
    [Fact]
    public async Task A_failed_question_gives_its_files_back()
    {
        var standIn = new AiStandIn((_, _) => AiStandIn.Json("""{"error":{"message":"The PDF could not be read."}}""", HttpStatusCode.BadRequest));
        var chat = new AiChat();
        var guide = new AiAttachment("guide.pdf", "application/pdf", DocumentTextTests.MinimalPdf("OUTPut ON"));

        Assert.Null(chat.Attach(guide));

        Assert.Equal(ChatOutcome.Failed, await Within(chat.SendAsync("Summarise it", Anthropic(standIn), "system", null)));
        Assert.True(chat.GaveFilesBack);
        Assert.Same(guide, Assert.Single(chat.Pending));
        Assert.Empty(chat.Turns[0].Attachments);
        Assert.Equal(ChatTurn.Error, chat.Turns[^1].Kind);
    }

    /// <summary>
    /// **A stop keeps its files where they were**, being no sign that one was refused — and what the run
    /// was billed for before it stopped is counted on the stop: each request was billed as it was answered.
    /// </summary>
    [Fact]
    public async Task A_stopped_question_keeps_its_files_and_its_spend_is_counted()
    {
        var (bench, _, _) = Bench();
        var standIn = new AiStandIn(AiStandIn.WithAnthropicUsage(AiStandIn.AnthropicCall("toolu_1", "send", SendOn), 2000, 50));
        var chat = new AiChat();

        chat.Attach(new AiAttachment("screen.png", "image/png", Png));

        Task<ChatOutcome> sending = chat.SendAsync("Switch on channel 1", Anthropic(standIn), "system", new BenchToolRunner(bench));
        await Waiting(chat);
        chat.Cancel();

        Assert.Equal(ChatOutcome.Stopped, await Within(sending));
        Assert.False(chat.GaveFilesBack);
        Assert.Single(chat.Turns[0].Attachments);
        Assert.Empty(chat.Pending);

        ChatTurn stopped = chat.Turns[^1];
        Assert.StartsWith("Stopped.", stopped.Text);
        Assert.Equal(2000, stopped.Usage!.InputTokens);
        Assert.Equal("claude-sonnet-5", stopped.Model);

        // 2,000 in at $2 and 50 out at $10 a million.
        Assert.Equal(0.0045m, stopped.Cost);
        Assert.Equal(0.0045m, chat.Cost);
    }

    /// <summary>Spend is kept by model, each at its own rates — none for a model nobody priced — and Clear does not take it back.</summary>
    [Fact]
    public async Task Spend_is_kept_by_model_and_outlives_clear()
    {
        var claude = new AiStandIn(AiStandIn.WithAnthropicUsage(AiStandIn.AnthropicText("Hi."), 1000, 100));
        var gemini = new AiStandIn(AiStandIn.WithOpenAiUsage(AiStandIn.OpenAiText("Hello."), 3000, 200));
        var chat = new AiChat();

        await Within(chat.SendAsync("Hi", Anthropic(claude), "system", null));
        await Within(chat.SendAsync("Hi again", Chat(gemini), "system", null));

        Assert.Equal(new[] { "claude-sonnet-5", "gemini-test" }, chat.Spend.Keys.OrderBy(model => model, StringComparer.Ordinal));
        Assert.Equal(0.003m, chat.Spend["claude-sonnet-5"].Cost);
        Assert.Null(chat.Spend["gemini-test"].Cost);
        Assert.Equal(4000, chat.Used.InputTokens);

        ChatTurn answer = chat.Turns[1];
        Assert.Equal("claude-sonnet-5", answer.Model);
        Assert.Equal(1, answer.Usage!.Requests);
        Assert.Null(chat.Turns[3].Cost);

        chat.Clear();

        Assert.Equal(0.003m, chat.Cost);
    }

    [Fact]
    public void A_question_carries_ten_files_and_a_conversation_twelve_megabytes()
    {
        var chat = new AiChat();
        for (int i = 0; i < AiAttachment.MostPerQuestion; i++)
            Assert.Null(chat.Attach(new AiAttachment($"note{i}.txt", "", "a"u8.ToArray())));

        Assert.Contains("10 files at most", chat.Attach(new AiAttachment("one more.txt", "", "a"u8.ToArray())));

        var heavy = new AiChat();
        Assert.Null(heavy.Attach(new AiAttachment("a.pdf", "", BigPdf())));
        Assert.Contains("past the 12 MB one conversation can send", heavy.Attach(new AiAttachment("b.pdf", "", BigPdf())));
    }

    /// <summary>A scanned PDF is attached, since a connection that reads the file takes it whole; whether it can go as its words is asked as Send waits.</summary>
    [Fact]
    public void A_scanned_pdf_is_attached_and_its_words_asked_for_only_by_a_connection_that_wants_them()
    {
        var chat = new AiChat();
        var scan = new AiAttachment("scan.pdf", "application/pdf", DocumentTextTests.MinimalPdf(""));

        Assert.Null(chat.Attach(scan));
        Assert.False(scan.PdfRead);
        Assert.Null(AiAttachment.NotSentTo(Claude(), scan));
        Assert.Contains("A scanned PDF has pictures of its pages", AiAttachment.NotSentTo(Gemini(), scan));
    }

    /// <summary>Unticking is the way out of a conversation that can take no more; ticking back in is held to the same budget.</summary>
    [Fact]
    public async Task A_file_ticked_back_in_is_held_to_the_budget()
    {
        var standIn = new AiStandIn(AiStandIn.AnthropicText("Read it."));
        var chat = new AiChat();
        var first = new AiAttachment("a.pdf", "", BigPdf());

        chat.Attach(first);
        await Within(chat.SendAsync("Read this", Anthropic(standIn), "system", null));

        Assert.Null(chat.Include(first, false));
        Assert.Null(chat.Attach(new AiAttachment("b.pdf", "", BigPdf())));
        Assert.Contains("Untick another first", chat.Include(first, true));
        Assert.True(first.Excluded);
    }

    /// <summary>The conversation started afresh: the files sent before it are not sent any more, and do not count against the budget.</summary>
    [Fact]
    public async Task Files_from_before_starting_afresh_are_no_longer_sent()
    {
        var standIn = new AiStandIn(AiStandIn.AnthropicText("A sine."));
        var chat = new AiChat();

        chat.Attach(new AiAttachment("screen.png", "image/png", Png));
        await Within(chat.SendAsync("What is this?", Anthropic(standIn), "system", null));

        chat.StartAfresh("The instruments open have changed.");

        Assert.Empty(chat.SentFiles());
        Assert.Equal(0, chat.SentBytes);
    }

    // ---------------------------------------------------------------------------- the estimate

    /// <summary>
    /// The estimate is what the next request would carry: the prompt, the tools' definitions, the
    /// conversation as it would be replayed with its ticked files, and the box — its words and its
    /// files — each file as the connection is sent it.
    /// </summary>
    [Fact]
    public async Task The_estimate_is_the_prompt_the_tools_the_conversation_and_the_box()
    {
        var standIn = new AiStandIn(AiStandIn.AnthropicText("A sine, 1 kHz."));
        var chat = new AiChat();
        AiConnection claude = Claude();
        IReadOnlyList<LlmTool> tools = BenchToolRunner.All;
        const string Prompt = "You are the assistant.";
        var screen = new AiAttachment("screen.png", "image/png", Png);

        chat.Attach(screen);
        await Within(chat.SendAsync("What is on the scope?", Anthropic(standIn), "system", null));

        long now = chat.EstimateTokens(Prompt, tools, claude, "");

        Assert.Equal(AiEstimate.Request(Prompt, AiEstimate.ToolTokens(tools), [.. chat.BuildConversation(), LlmMessage.FromUser("")], claude), now);

        // The words being typed, and a picture waiting in the box.
        Assert.Equal(now + AiEstimate.TokensIn(35), chat.EstimateTokens(Prompt, tools, claude, new string('a', 35)));
        Assert.Null(chat.Attach(new AiAttachment("again.png", "image/png", Png)));
        Assert.Equal(now + AiAttachment.ImageTokens + 15, chat.EstimateTokens(Prompt, tools, claude, ""));

        // And a sent picture left out costs its line, not itself.
        chat.Include(screen, false);
        Assert.Equal(now + AiAttachment.ImageTokens + 15 - (AiAttachment.ImageTokens + 15) + 25, chat.EstimateTokens(Prompt, tools, claude, ""));
    }

    [Fact]
    public void Tools_are_weighed_by_their_names_descriptions_and_schemas()
    {
        LlmTool tool = BenchToolRunner.All[0];
        long characters = tool.Name.Length + tool.Description.Length + tool.InputSchemaJson.Length + 40;

        Assert.Equal((long)Math.Ceiling(characters / AiEstimate.JsonCharactersPerToken), AiEstimate.ToolTokens([tool]));
        Assert.Equal(0, AiEstimate.ToolTokens([]));
    }

    // --------------------------------------------------------------- compacting, and the tools off

    /// <summary>A provider that never answers, for a stop to land on.</summary>
    private sealed class Silent : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }

    /// <summary>
    /// **Compacting puts a briefing in place of the conversation**: one request under the compaction's
    /// own instructions and no tools, carrying the transcript — each call with what came of it — and
    /// the files still ticked; and the next question goes with the briefing alone, the picture kept on
    /// it unticked.
    /// </summary>
    [Fact]
    public async Task Compacting_puts_a_briefing_in_place_of_the_conversation()
    {
        var (bench, _, _) = Bench();
        var standIn = new AiStandIn(
            AiStandIn.AnthropicCall("toolu_1", "send", SendOn),
            AiStandIn.AnthropicText("Channel 1 is on."),
            AiStandIn.WithAnthropicUsage(AiStandIn.AnthropicText("Working on: gen (SDG2042X at 192.168.1.20); C1:OUTP ON sent, queue empty."), 3000, 400),
            AiStandIn.AnthropicText("It is on."));
        var chat = new AiChat { Mode = ToolApprovalMode.AutoReadWrite };
        AnthropicProvider provider = Anthropic(standIn);
        var screen = new AiAttachment("screen.png", "image/png", Png);

        chat.Attach(screen);
        await Within(chat.SendAsync("Switch on channel 1", provider, "system", new BenchToolRunner(bench)));

        Assert.Equal(ChatOutcome.Answered, await Within(chat.CompactAsync(provider)));

        // The compaction's own request: its instructions, no tools, and one message holding the picture and the transcript.
        JsonObject compaction = standIn.Requests[2].Body;
        JsonNode message = Assert.Single(compaction["messages"]!.AsArray())!;
        string transcript = WordsOf(message["content"]) ?? "";

        Assert.Equal(AiChat.CompactSystem, (string?)compaction["system"]);
        Assert.Null(compaction["tools"]);
        Assert.Contains(message["content"]!.AsArray(), block => (string?)block!["type"] == "image");
        Assert.Contains("PERSON:", transcript);
        Assert.Contains("Switch on channel 1", transcript);
        Assert.Contains("- called send {", transcript);
        Assert.Contains("and it answered: Sent C1:OUTP ON to gen.", transcript);
        Assert.Contains("Channel 1 is on.", transcript);

        // In the conversation: the briefing after what it stands in for, with another of the picture on it, unticked.
        ChatTurn briefing = chat.Turns[^1];
        AiAttachment kept = Assert.Single(briefing.Attachments);
        Assert.Equal(ChatTurn.Summary, briefing.Kind);
        Assert.Equal(3, chat.FoldedBefore);
        Assert.NotSame(screen, kept);
        Assert.Same(screen.Data, kept.Data);
        Assert.True(kept.Excluded);
        Assert.False(screen.Excluded);
        Assert.Equal(0, chat.SentBytes);
        Assert.Equal(400, briefing.Usage!.OutputTokens);

        // And the next question goes with the briefing alone, introduced as what it is.
        await Within(chat.SendAsync("Is it still on?", provider, "system", null));

        string sent = standIn.Requests[3].Body.ToJsonString();
        Assert.Contains(AiChat.SummaryPreface, sent);
        Assert.Contains("C1:OUTP ON sent", sent);
        Assert.Contains("Is it still on?", sent);
        Assert.DoesNotContain("Switch on channel 1", sent);
        Assert.DoesNotContain(Convert.ToBase64String(Png), sent);
    }

    /// <summary>The briefing is asked to keep what a bench conversation turns on exactly, in the words the person asked for.</summary>
    [Fact]
    public void The_compaction_asks_for_the_bench_kept_exactly()
    {
        Assert.Contains("by the names the tools gave them - dmm, psu, scope - with their models and addresses", AiChat.CompactSystem);
        Assert.Contains("every command sent and every value read, exactly as it was written and never rounded or paraphrased", AiChat.CompactSystem);
        Assert.Contains("what the person declined and what a tool refused, and why", AiChat.CompactSystem);
        Assert.Contains("errors the instruments reported", AiChat.CompactSystem);
        Assert.Contains("the scripts proposed", AiChat.CompactSystem);
        Assert.Contains("what is still open", AiChat.CompactSystem);
    }

    /// <summary>A compaction refused leaves the conversation exactly as it was, with a line saying so.</summary>
    [Fact]
    public async Task A_compaction_that_fails_leaves_the_conversation_as_it_was()
    {
        var standIn = new AiStandIn((index, _) => index == 0
            ? AiStandIn.Json(AiStandIn.AnthropicText("A sine."))
            : AiStandIn.Json("""{"error":{"message":"The request was too large."}}""", HttpStatusCode.BadRequest));
        var chat = new AiChat();
        AnthropicProvider provider = Anthropic(standIn);

        await Within(chat.SendAsync("What is this?", provider, "system", null));
        List<string> before = [.. chat.BuildConversation().Select(message => message.Text)];

        Assert.Equal(ChatOutcome.Failed, await Within(chat.CompactAsync(provider)));

        Assert.DoesNotContain(chat.Turns, turn => turn.Kind == ChatTurn.Summary);
        Assert.StartsWith("The conversation could not be compacted, and is as it was.", chat.Turns[^1].Text);
        Assert.Contains("The request was too large.", chat.Turns[^1].Text);
        Assert.Equal(before, chat.BuildConversation().Select(message => message.Text));
        Assert.Equal(0, chat.FoldedBefore);
        Assert.False(chat.Generating);
    }

    [Fact]
    public async Task A_compaction_stopped_leaves_the_conversation_as_it_was()
    {
        var standIn = new AiStandIn(AiStandIn.AnthropicText("A sine."));
        var chat = new AiChat();

        await Within(chat.SendAsync("What is this?", Anthropic(standIn), "system", null));

        Task<ChatOutcome> compacting = chat.CompactAsync(new AnthropicProvider(new HttpClient(new Silent()), new DirectLlmEndpoint(Claude(), "test-key"), Claude()));

        Assert.True(chat.Compacting);

        chat.Cancel();

        Assert.Equal(ChatOutcome.Stopped, await Within(compacting));
        Assert.Equal("Compacting was stopped. The conversation is as it was.", chat.Turns[^1].Text);
        Assert.DoesNotContain(chat.Turns, turn => turn.Kind == ChatTurn.Summary);
        Assert.False(chat.Compacting);
        Assert.False(chat.Generating);
    }

    /// <summary>
    /// **Suggested once there is a lot to stop sending and a conversation likely to go on**: three
    /// questions since it began or was last compacted, and 20,000 tokens of history — two answers of
    /// 40,000 characters are past that alone. Put away until the next compaction, after which there is
    /// nothing new to compact.
    /// </summary>
    [Fact]
    public async Task Compacting_is_suggested_after_three_questions_and_twenty_thousand_tokens()
    {
        var standIn = new AiStandIn(AiStandIn.AnthropicText(new string('a', 40_000)));
        var chat = new AiChat();
        AnthropicProvider provider = Anthropic(standIn);

        Assert.Equal("There is nothing to compact yet.", chat.WhyNotCompact());

        await Within(chat.SendAsync("One", provider, "system", null));
        await Within(chat.SendAsync("Two", provider, "system", null));

        Assert.True(chat.HistoryTokens(Claude()) >= AiChat.CompactAfterTokens);
        Assert.False(chat.WorthCompacting(Claude()));

        await Within(chat.SendAsync("Three", provider, "system", null));
        Assert.True(chat.WorthCompacting(Claude()));

        chat.CompactSuggestionDismissed = true;
        Assert.False(chat.WorthCompacting(Claude()));

        await Within(chat.CompactAsync(provider));

        Assert.False(chat.CompactSuggestionDismissed);
        Assert.Equal(0, chat.QuestionsSinceAfresh);
        Assert.False(chat.WorthCompacting(Claude()));
        Assert.Equal("Nothing has been asked since it was last compacted.", chat.WhyNotCompact());
    }

    /// <summary>A second compaction reads the first one's briefing in place of what it stood for, and carries its files on, unticked again.</summary>
    [Fact]
    public async Task A_second_compaction_reads_the_first_briefing_and_keeps_its_files()
    {
        var standIn = new AiStandIn(AiStandIn.AnthropicText("Fine."), AiStandIn.AnthropicText("First briefing."), AiStandIn.AnthropicText("Sure."), AiStandIn.AnthropicText("Second briefing."));
        var chat = new AiChat();
        AnthropicProvider provider = Anthropic(standIn);

        chat.Attach(new AiAttachment("screen.png", "image/png", Png));

        await Within(chat.SendAsync("Look at this", provider, "system", null));
        await Within(chat.CompactAsync(provider));
        await Within(chat.SendAsync("And now this", provider, "system", null));
        await Within(chat.CompactAsync(provider));

        string second = standIn.Requests[3].Body.ToJsonString();
        Assert.Contains("SUMMARY LEFT BY AN EARLIER COMPACTION:", second);
        Assert.Contains("First briefing.", second);
        Assert.Contains("And now this", second);
        Assert.DoesNotContain("Look at this", second);

        Assert.Equal(5, chat.FoldedBefore);
        Assert.Equal("Second briefing.", chat.Turns[5].Text);

        var (onBriefing, file) = Assert.Single(chat.SentFiles());
        Assert.Same(chat.Turns[5], onBriefing);
        Assert.True(file.Excluded);
    }

    [Fact]
    public void A_compaction_is_priced_as_the_history_read_once_and_a_briefing_written()
    {
        var price = new LlmPrice { Input = 4, Cached = 0.2m, CacheWrite = 5, Output = 20 };

        // 20,000 tokens of history and the instructions read at $4 a million, and 2,000 written at $20.
        decimal expected = ((20_000 + AiEstimate.TokensIn(AiChat.CompactSystem.Length)) * 4m + 2_000 * 20m) / 1_000_000m;

        Assert.Equal(expected, AiChat.CompactCost(price, 20_000));
        Assert.Null(AiChat.CompactCost(null, 20_000));
    }

    /// <summary>A tool switched off in the picker is not offered, and a model that calls it anyway is told why rather than obeyed — nothing reaches the instrument.</summary>
    [Fact]
    public async Task A_tool_switched_off_is_not_offered_and_is_refused_if_called()
    {
        var (bench, gen, _) = Bench();
        var standIn = new AiStandIn(AiStandIn.AnthropicCall("toolu_1", "send", SendOn), AiStandIn.AnthropicText("send is switched off."));
        var chat = new AiChat { Mode = ToolApprovalMode.AutoReadWrite };

        chat.ToolsOff.Add(BenchToolRunner.Send);
        await Within(chat.SendAsync("Switch on channel 1", Anthropic(standIn), "system", new BenchToolRunner(bench) { Off = chat.ToolsOff }));

        List<string?> offered = [.. standIn.Requests[0].Body["tools"]!.AsArray().Select(tool => (string?)tool!["name"])];
        Assert.DoesNotContain("send", offered);
        Assert.Contains("query", offered);
        Assert.Empty(gen.Sent);
        Assert.True(chat.Turns[1].ResultIsError);
        Assert.Contains("switched off in this conversation", chat.Turns[1].Result);
    }

    [Fact]
    public void Tools_switched_off_stay_off_through_clear_and_starting_afresh()
    {
        var chat = new AiChat();

        chat.ToolsOff.Add(BenchToolRunner.LookAtScreen);
        chat.Clear();
        chat.StartAfresh("The instruments open have changed.");

        Assert.Contains(BenchToolRunner.LookAtScreen, chat.ToolsOff);
    }

    /// <summary>The export: every turn, a compaction's folded part included, each call and what it answered in full, a proposal's whole script, what was billed, and the files by name alone.</summary>
    [Fact]
    public async Task The_transcript_has_every_turn_the_calls_in_full_and_the_files_by_name()
    {
        var (bench, _, _) = Bench();
        var standIn = new AiStandIn(
            AiStandIn.WithAnthropicUsage(AiStandIn.AnthropicCall("toolu_1", "send", SendOn), 1000, 50),
            AiStandIn.WithAnthropicUsage(AiStandIn.AnthropicCall("toolu_2", "propose_script", Args(("kind", "instrument"), ("instrument", "gen"), ("script", "C1:OUTP ON\nDELAY 500"))), 1100, 60),
            AiStandIn.WithAnthropicUsage(AiStandIn.AnthropicText("On, and a script to do it again."), 1200, 20),
            AiStandIn.AnthropicText("The briefing."));
        var chat = new AiChat { Mode = ToolApprovalMode.AutoReadWrite };
        AnthropicProvider provider = Anthropic(standIn);

        chat.Attach(new AiAttachment("screen.png", "image/png", Png));
        await Within(chat.SendAsync("Switch on channel 1", provider, "system", new BenchToolRunner(bench)));
        await Within(chat.CompactAsync(provider));

        string text = chat.Transcript("gen, dmm", new DateTime(2026, 11, 7, 14, 5, 0));

        Assert.StartsWith("AI assistant conversation — gen, dmm", text);
        Assert.Contains("Exported 2026-11-07 14:05", text);
        Assert.Contains("Switch on channel 1", text);
        Assert.Contains("  Attached: screen.png (11 bytes)", text);
        Assert.Contains("  Called send {\"instrument\":\"gen\",\"command\":\"C1:OUTP ON\"} — it answered:", text);
        Assert.Contains("    Sent C1:OUTP ON to gen.", text);
        Assert.Contains("  Proposed a script of 2 lines for gen — it answered:", text);
        Assert.Contains("    The script:", text);
        Assert.Contains("      DELAY 500", text);
        Assert.Contains("On, and a script to do it again.", text);
        Assert.Contains("  Billed: 3 requests · 3,300 tokens in · 130 out · $0.008, on claude-sonnet-5", text);
        Assert.Contains("SUMMARY OF THE ABOVE (compacted)", text);
        Assert.Contains("  Kept from before it: screen.png (11 bytes, not sent)", text);
        Assert.DoesNotContain(Convert.ToBase64String(Png), text);
    }

    [Fact]
    public void A_runs_bill_reads_as_one_line()
    {
        var turn = new ChatTurn
        {
            Kind = ChatTurn.Assistant,
            Usage = new LlmUsage { InputTokens = 12_345, CachedInputTokens = 9_000, OutputTokens = 210, Requests = 2 },
            Cost = 0.0123m,
        };

        Assert.Equal("2 requests · 12,345 tokens in (9,000 cached) · 210 out · $0.012", AiChat.UsageLine(turn));
        Assert.Equal("", AiChat.UsageLine(new ChatTurn { Kind = ChatTurn.Assistant }));
    }
}
