using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LabEquipmentController.Tests;

/// <summary>
/// The assistant's system prompt and the lines both windows build from the bench: what the model is
/// told at every question, and what a kept chat records and says when it is opened again.
/// </summary>
public sealed class AssistantPromptTests
{
    private const string Generator = "Siglent Technologies,SDG2042X,SDG2XCAD1R0001,2.01.01.35R3B2";
    private const string Meter = "Siglent Technologies,SDM3065X,SDM36HCD801207,3.02.01.13";

    private static readonly DateTime Today = new(2026, 11, 7, 17, 56, 0);

    private static BenchInstrument Open(string identity, string host, bool busy = false)
        => new(host, identity, host, "VXI-11", busy);

    private static string Prompt(IReadOnlyList<BenchInstrument> bench, ToolApprovalMode mode, params string[] off)
        => AssistantPrompt.Build(bench, mode, new HashSet<string>(off), "the desktop app", Today);

    [Fact]
    public void The_prompt_names_where_it_is_the_date_and_the_conventions()
    {
        string prompt = Prompt([], ToolApprovalMode.AutoRead);

        Assert.Contains("working with the person at the desktop app", prompt);
        Assert.Contains("Today is 7 November 2026", prompt);
        Assert.Contains(BenchToolRunner.Conventions, prompt);
    }

    [Fact]
    public void Each_rung_is_said_as_what_it_lets_through()
    {
        Assert.Contains("Every tool call, reads included, waits", Prompt([], ToolApprovalMode.Ask));
        Assert.Contains("Reads run at once. Each send waits", Prompt([], ToolApprovalMode.AutoRead));
        Assert.Contains("never say that you cannot change an instrument", Prompt([], ToolApprovalMode.AutoRead));
        Assert.Contains("Every call runs at once, sends included", Prompt([], ToolApprovalMode.AutoReadWrite));
    }

    [Fact]
    public void Tools_switched_off_are_named_and_send_off_makes_the_conversation_read_only()
    {
        string some = Prompt([], ToolApprovalMode.AutoRead, BenchToolRunner.LookAtScreen);
        Assert.Contains($"Switched off in this conversation by the person, in the window's tool picker: {BenchToolRunner.LookAtScreen}", some);
        Assert.DoesNotContain("only reads", some);

        string reads = Prompt([], ToolApprovalMode.AutoRead, BenchToolRunner.Send);
        Assert.Contains("With send off, this conversation only reads", reads);

        Assert.DoesNotContain("Switched off", Prompt([], ToolApprovalMode.AutoRead));
    }

    [Fact]
    public void The_instruments_open_are_named_as_the_tools_name_them()
    {
        string prompt = Prompt([Open(Generator, "192.168.1.20"), Open(Meter, "192.168.1.21", busy: true)], ToolApprovalMode.AutoRead);

        Assert.Contains("The 2 instruments open now:", prompt);
        Assert.Contains("- gen: Siglent Technologies SDG2042X at 192.168.1.20", prompt);
        Assert.Contains("- dmm: Siglent Technologies SDM3065X at 192.168.1.21 (a script is running on it)", prompt);
    }

    [Fact]
    public void An_empty_bench_is_said_with_no_tool_to_open_one()
    {
        Assert.Contains("No instrument is open yet", Prompt([], ToolApprovalMode.AutoRead));
    }

    [Fact]
    public void Past_the_most_named_the_rest_are_left_to_list_instruments()
    {
        var bench = new List<BenchInstrument>();
        for (int i = 0; i < AssistantPrompt.MostNamed + 3; i++) bench.Add(Open(Meter, $"192.168.1.{100 + i}"));

        string prompt = Prompt(bench, ToolApprovalMode.AutoRead);

        Assert.Contains("…and 3 more, which list_instruments names.", prompt);
        Assert.DoesNotContain($"192.168.1.{100 + AssistantPrompt.MostNamed}", prompt);
    }

    [Fact]
    public void The_rungs_are_listed_from_the_one_that_asks_most_and_labelled_the_same_in_both_builds()
    {
        Assert.Equal([ToolApprovalMode.Ask, ToolApprovalMode.AutoRead, ToolApprovalMode.AutoReadWrite], AssistantPrompt.Rungs);
        Assert.Equal("Ask before every step", AssistantPrompt.RungLabel(ToolApprovalMode.Ask));
        Assert.Equal("Ask before changes", AssistantPrompt.RungLabel(ToolApprovalMode.AutoRead));
        Assert.Equal("Change without asking", AssistantPrompt.RungLabel(ToolApprovalMode.AutoReadWrite));
    }

    // ------------------------------------------------------------------- what the windows say

    [Fact]
    public void The_context_line_names_the_instruments_as_their_tabs_do()
    {
        Assert.StartsWith("No instrument is open.", AssistantPrompt.Context([]));
        Assert.StartsWith("Working on the one instrument open: SDM3065X (192.168.1.21).",
            AssistantPrompt.Context([Open(Meter, "192.168.1.21")]));
        Assert.StartsWith("Working on the 2 instruments open: SDG2042X (192.168.1.20), Instrument (192.168.1.30).",
            AssistantPrompt.Context([Open(Generator, "192.168.1.20"), Open("", "192.168.1.30")]));
    }

    [Fact]
    public void A_calls_arguments_read_on_one_line_instrument_and_command_first()
    {
        Assert.Equal("dmm · MEAS:VOLT:DC? · undocumented: true",
            AssistantPrompt.CallLine("""{"undocumented": true, "command": "MEAS:VOLT:DC?", "instrument": "dmm"}"""));
        Assert.Equal("kind: \"sequence\"",
            AssistantPrompt.CallLine("""{"kind": "sequence", "script": "PRINT 1"}"""));
        Assert.Equal("not json", AssistantPrompt.CallLine("not json"));
        Assert.EndsWith("…", AssistantPrompt.CallLine($$"""{"command": "{{new string('x', 400)}}"}"""));
    }

    [Fact]
    public void A_cards_state_says_what_happened_to_the_call()
    {
        var waiting = new ChatTurn { Kind = ChatTurn.Tool, Changes = true, Gate = new TaskCompletionSource<bool>() };
        Assert.True(AssistantPrompt.Waiting(waiting));
        Assert.Equal("waits for you: this changes the instrument", AssistantPrompt.CallState(waiting));

        Assert.Equal("ran without asking", AssistantPrompt.CallState(new ChatTurn { Kind = ChatTurn.Tool, Decision = true, Auto = true }));
        Assert.Equal("approved", AssistantPrompt.CallState(new ChatTurn { Kind = ChatTurn.Tool, Decision = true }));
        Assert.Equal("declined", AssistantPrompt.CallState(new ChatTurn { Kind = ChatTurn.Tool, Decision = false }));

        var answered = new ChatTurn { Kind = ChatTurn.Tool, Gate = new TaskCompletionSource<bool>() };
        answered.Gate.SetResult(true);
        Assert.False(AssistantPrompt.Waiting(answered));
        Assert.Equal("", AssistantPrompt.CallState(answered));
    }

    [Fact]
    public void A_proposal_goes_to_a_sequence_or_to_the_instrument_it_names()
    {
        Assert.Equal((true, (string?)null), AssistantPrompt.ProposalTarget("""{"kind": "sequence", "script": "PRINT 1"}"""));
        Assert.Equal((false, "gen"), AssistantPrompt.ProposalTarget("""{"kind": "instrument", "instrument": "gen", "script": "x"}"""));
        Assert.Equal((false, (string?)null), AssistantPrompt.ProposalTarget("not json"));
        Assert.Equal((false, (string?)null), AssistantPrompt.ProposalTarget("""{"kind": 3}"""));
    }

    // ------------------------------------------------------------------- the bench in a line

    [Fact]
    public void The_bench_line_names_each_instrument_by_its_alias_and_model()
    {
        string line = AssistantPrompt.BenchLine([Open(Generator, "192.168.1.20"), Open(Meter, "192.168.1.21")]);

        Assert.Equal("gen SDG2042X, dmm SDM3065X", line);
    }

    [Fact]
    public void An_empty_bench_is_an_empty_line()
    {
        Assert.Equal("", AssistantPrompt.BenchLine([]));
    }

    [Fact]
    public void An_instrument_with_no_identity_is_named_by_its_alias_alone()
    {
        string line = AssistantPrompt.BenchLine([Open("", "192.168.1.30")]);

        Assert.DoesNotContain("  ", line);
        Assert.False(string.IsNullOrWhiteSpace(line));
    }

    // ------------------------------------------------------------------- a chat opened again

    [Fact]
    public void A_chat_opened_on_the_bench_it_was_about_says_only_when()
    {
        string line = AssistantPrompt.ReopenedLine("dmm SDM3065X", "dmm SDM3065X", Today);

        Assert.Equal("This chat was opened again on 7 November 2026 at 17:56.", line);
    }

    [Fact]
    public void A_chat_opened_on_another_bench_says_both()
    {
        string line = AssistantPrompt.ReopenedLine("dmm SDM3065X", "gen SDG2042X", Today);

        Assert.Equal("This chat was opened again on 7 November 2026 at 17:56. It was about dmm SDM3065X; open now: gen SDG2042X.", line);
    }

    [Fact]
    public void A_chat_about_nothing_or_opened_on_nothing_says_so_in_words()
    {
        Assert.EndsWith("It was about no instrument; open now: gen SDG2042X.", AssistantPrompt.ReopenedLine("", "gen SDG2042X", Today));
        Assert.EndsWith("It was about dmm SDM3065X; open now: none.", AssistantPrompt.ReopenedLine("dmm SDM3065X", "", Today));
    }

    [Fact]
    public void The_reopened_line_goes_to_the_model_with_the_warning_about_old_names()
    {
        var chat = new AiChat();
        chat.Restore(
            [new ChatTurn { Kind = ChatTurn.User, Text = "What does the dmm read?" }, new ChatTurn { Kind = ChatTurn.Assistant, Text = "4.99876E+00 V." }],
            AssistantPrompt.ReopenedLine("dmm SDM3065X", "", Today));

        List<LlmMessage> sent = chat.BuildConversation();

        Assert.Equal(3, sent.Count);
        Assert.Contains("open now: none.", sent[2].Text);
        Assert.Contains(AiChat.ReopenedWarning, sent[2].Text);
    }
}
