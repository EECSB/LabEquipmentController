using System.Net;
using System.Net.Http.Json;
using LabEquipmentController.Web.Client.Contracts;
using LabEquipmentController.Web.Client.Services;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// Write a Script with AI's conversation, with a fake server where the bench's would be: the same
/// conversation the assistant has, asked of the server's writer. What goes back with each request
/// is the point, because every one of them is a bill.
/// </summary>
public class ScriptWriterModelTests
{
    private static readonly Uri Base = new("http://bench.test/");
    private static readonly AiConnection Connection = new() { Provider = AiProvider.OpenAiCompatible, Model = "stub" };
    private static readonly ScriptWriterAsk Ask = new(["s1"], false, "*RST", "ran fine", null, null);

    private static string Written(string script, string notes = "") => new WrittenScript(script, notes, []).ToText();

    // ------------------------------------------------------------- what goes with a request

    [Fact]
    public void The_newest_question_is_the_request_and_the_answered_ones_are_the_history()
    {
        var (asked, history, _) = ScriptWriterModel.Read(
        [
            LlmMessage.FromUser("first"),
            LlmMessage.FromAssistant(new WrittenScript("*IDN?", "said hello", ["C1:NOPE"]).ToText()),
            LlmMessage.FromUser("second"),
        ]);

        Assert.Equal("second", asked);
        AiTurn turn = Assert.Single(history);
        Assert.Equal("first", turn.Request);
        Assert.Equal("*IDN?", turn.Script);
        Assert.Equal("said hello", turn.Notes);
        Assert.Equal(["C1:NOPE"], turn.Undocumented);
    }

    /// <summary>It failed or was stopped: shown to a model, it reads as something still to do.</summary>
    [Fact]
    public void A_question_with_no_answer_after_it_is_left_out()
    {
        var (asked, history, _) = ScriptWriterModel.Read([LlmMessage.FromUser("this one failed"), LlmMessage.FromUser("asked again")]);

        Assert.Equal("asked again", asked);
        Assert.Empty(history);
    }

    /// <summary>It goes as a question and has no answer, and it is the only memory of what it stands in for.</summary>
    [Fact]
    public void A_compaction_briefing_stays_though_nothing_answers_it()
    {
        string briefing = AiChat.SummaryPreface + "\n\nThe sweep was 100 Hz to 100 kHz.";

        var (_, history, _) = ScriptWriterModel.Read([LlmMessage.FromUser(briefing), LlmMessage.FromUser("now at 5 V")]);

        Assert.Equal(briefing, Assert.Single(history).Request);
    }

    [Fact]
    public void Every_file_still_ticked_goes_and_an_unticked_one_does_not()
    {
        var kept = new AiAttachment("kept.txt", "text/plain", "one"u8.ToArray());
        var dropped = new AiAttachment("dropped.txt", "text/plain", "two"u8.ToArray()) { Excluded = true };
        var fresh = new AiAttachment("fresh.txt", "text/plain", "three"u8.ToArray());

        var (_, _, files) = ScriptWriterModel.Read(
        [
            LlmMessage.FromUser("first", [kept, dropped]),
            LlmMessage.FromAssistant(Written("*IDN?")),
            LlmMessage.FromUser("second", [fresh]),
        ]);

        Assert.Equal(["kept.txt", "fresh.txt"], files.Select(f => f.Name));
        Assert.Equal(Convert.ToBase64String("three"u8.ToArray()), files[1].Base64);
    }

    // ------------------------------------------------------------------- the whole conversation

    [Fact]
    public async Task An_answer_lands_as_a_script_and_the_next_request_carries_it()
    {
        var sent = new List<AiScriptRequest>();
        var chat = new AiChat();
        ScriptWriterModel model = Model((request, _) =>
        {
            sent.Add(request);
            return Task.FromResult(new AiScriptReply($"# turn {sent.Count}", [], null, $"note {sent.Count}"));
        });

        await chat.SendAsync("set it up", model, "", null);
        await chat.SendAsync("now at 5 V", model, "", null);

        WrittenScript last = WrittenScript.Read(chat.Turns[^1].Text)!;
        Assert.Equal("# turn 2", last.Script);
        Assert.Equal("note 2", last.Notes);

        Assert.Empty(sent[0].History!);
        AiTurn before = Assert.Single(sent[1].History!);
        Assert.Equal("set it up", before.Request);
        Assert.Equal("# turn 1", before.Script);

        // What the editor and the switches said went with it.
        Assert.Equal("*RST", sent[1].CurrentScript);
        Assert.Equal("ran fine", sent[1].RecentOutput);
        Assert.Equal(["s1"], sent[1].SessionIds);
    }

    [Fact]
    public async Task What_the_server_says_was_billed_is_put_on_the_answer_and_added_up()
    {
        var chat = new AiChat();
        ScriptWriterModel model = Model((_, _) => Task.FromResult(new AiScriptReply(
            "*IDN?", [], null, "", new LlmUsage { InputTokens = 900, OutputTokens = 40, Requests = 1 })));

        await chat.SendAsync("read it", model, "", null);

        Assert.Equal(900, chat.Turns[^1].Usage!.InputTokens);
        Assert.Equal("stub", chat.Turns[^1].Model);
        Assert.Equal(900, chat.Used.InputTokens);
    }

    [Fact]
    public async Task A_refusal_is_a_failure_in_the_servers_words_and_the_files_go_back_in_the_box()
    {
        var chat = new AiChat();
        Assert.Null(chat.Attach(new AiAttachment("wiring.txt", "text/plain", "CH1"u8.ToArray())));

        ChatOutcome outcome = await chat.SendAsync("read it", Model((_, _) => Task.FromResult(new AiScriptReply("", [], "No key."))), "", null);

        Assert.Equal(ChatOutcome.Failed, outcome);
        Assert.Equal("No key.", chat.Turns[^1].Text);
        Assert.Equal("wiring.txt", Assert.Single(chat.Pending).Name);
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_said_as_that()
    {
        var chat = new AiChat();

        await chat.SendAsync("read it", Model((_, _) => throw new HttpRequestException("Failed to fetch")), "", null);

        Assert.Equal(ChatTurn.Error, chat.Turns[^1].Kind);
        Assert.Equal("Could not reach the server (Failed to fetch).", chat.Turns[^1].Text);
    }

    [Fact]
    public async Task Stop_stops_it()
    {
        var chat = new AiChat();
        var waiting = new TaskCompletionSource();
        ScriptWriterModel model = Model(async (_, ct) =>
        {
            waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new AiScriptReply("", [], null);
        });

        Task<ChatOutcome> sending = chat.SendAsync("read it", model, "", null);
        await waiting.Task;
        chat.Cancel();

        Assert.Equal(ChatOutcome.Stopped, await sending);
    }

    private static ScriptWriterModel Model(Func<AiScriptRequest, CancellationToken, Task<AiScriptReply>> answer)
    {
        var handler = new Handler(async (request, ct) =>
        {
            AiScriptRequest asked = (await request.Content!.ReadFromJsonAsync<AiScriptRequest>(ct))!;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(await answer(asked, ct)) };
        });

        var bench = new BenchClient(new HttpClient(handler) { BaseAddress = Base }, new AiHttp(handler, Base));
        return new ScriptWriterModel(bench, Connection, "", Ask);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request, ct);
    }
}
