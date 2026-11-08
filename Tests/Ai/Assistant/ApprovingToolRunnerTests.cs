using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The gate between the model and the bench, and what tells a run that is getting somewhere from
/// one that is stuck. GDS Viewer tests these through its whole conversation; the conversation
/// here is not written yet, so they are tested where they are — with a runner that records what
/// it was asked to run, so what is checked is what reached the bench, not what the gate said.
/// </summary>
public class ApprovingToolRunnerTests
{
    private const string NoArguments = """{"type":"object","properties":{}}""";

    /// <summary>A bench runner whose changing tool is holstered until a gate switches it on — as a real one is.</summary>
    private sealed class Holstered : ILlmToolRunner, IEditCapableToolRunner
    {
        private readonly ScriptedTools _bench = new();

        public bool AllowEdits { get; set; }

        public IReadOnlyList<LlmTool> Tools => AllowEdits ? _bench.Tools : _bench.Tools.Where(tool => !tool.Changes).ToList();

        public List<(string Name, string ArgumentsJson)> Calls => _bench.Calls;

        public Task<LlmToolOutput> RunToolAsync(string name, string argumentsJson, CancellationToken ct)
            => !AllowEdits && name == "set_output"
                ? Task.FromResult(LlmToolOutput.Refused("Changes are not offered here."))
                : _bench.RunToolAsync(name, argumentsJson, ct);
    }

    /// <summary>A bench with a tool of each kind: one that reads, one that changes, and one that proposes.</summary>
    private static ScriptedTools Bench() => new()
    {
        Tools =
        [
            new LlmTool { Name = "read_meter", Description = "Reads the meter.", InputSchemaJson = NoArguments },
            new LlmTool { Name = "set_output", Description = "Switches the output.", InputSchemaJson = NoArguments, Changes = true },
            new LlmTool { Name = "propose_script", Description = "Puts a script in the editor.", InputSchemaJson = NoArguments, Proposes = true },
        ],
    };

    /// <summary>An approver that lets through what the rung already answered, and holds everything else until the test says.</summary>
    private sealed class Person
    {
        public List<ToolRunRequest> Shown { get; } = [];

        public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> Approve(ToolRunRequest request, CancellationToken ct)
        {
            lock (Shown) Shown.Add(request);
            return request.AutoApproved ? Task.FromResult(true) : Answer.Task;
        }
    }

    // ------------------------------------------------------------------------------ the rungs

    [Theory]
    [InlineData(ToolApprovalMode.Ask, false, true)]
    [InlineData(ToolApprovalMode.Ask, true, true)]
    [InlineData(ToolApprovalMode.AutoRead, false, false)]
    [InlineData(ToolApprovalMode.AutoRead, true, true)]
    [InlineData(ToolApprovalMode.AutoReadWrite, false, false)]
    [InlineData(ToolApprovalMode.AutoReadWrite, true, false)]
    [InlineData((ToolApprovalMode)42, false, true)]
    public void Each_rung_says_what_still_has_to_be_asked(ToolApprovalMode mode, bool changes, bool expected)
        => Assert.Equal(expected, ApprovingToolRunner.NeedsApproval(mode, changes));

    /// <summary>A gate built without a rung starts on the middle one, where a read runs and a change waits.</summary>
    [Fact]
    public void A_gate_starts_on_the_middle_rung()
        => Assert.Equal(ToolApprovalMode.AutoRead, new ApprovingToolRunner(Bench(), (_, _) => Task.FromResult(true)).Mode);

    /// <summary>On the middle rung a read runs at once — shown, not waited on — and a change waits for a person.</summary>
    [Fact]
    public async Task On_the_middle_rung_a_read_runs_and_a_change_waits()
    {
        ScriptedTools bench = Bench();
        var person = new Person();
        var gate = new ApprovingToolRunner(bench, person.Approve);

        LlmToolOutput read = await gate.RunToolAsync("read_meter", "{}", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Task<LlmToolOutput> change = gate.RunToolAsync("set_output", "{}", CancellationToken.None);

        Assert.Equal("4.998 V", read.Text);
        Assert.True(person.Shown[0].AutoApproved);
        Assert.False(person.Shown[1].AutoApproved);
        Assert.True(person.Shown[1].Changes);
        Assert.False(change.IsCompleted);
        Assert.Equal(new[] { "read_meter" }, bench.Calls.Select(call => call.Name));

        person.Answer.SetResult(true);
        await change.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(new[] { "read_meter", "set_output" }, bench.Calls.Select(call => call.Name));
    }

    /// <summary>
    /// The rung is read at every call: a switch made between two calls of one answer is in force
    /// for the second — read once, GDS Viewer found a switch went on asking until the answer was done.
    /// </summary>
    [Fact]
    public async Task The_rung_is_read_at_every_call()
    {
        var mode = ToolApprovalMode.Ask;
        var shown = new List<ToolRunRequest>();
        var gate = new ApprovingToolRunner(Bench(), (request, _) => { shown.Add(request); return Task.FromResult(true); }, null, () => mode);

        await gate.RunToolAsync("set_output", "{}", CancellationToken.None);
        mode = ToolApprovalMode.AutoReadWrite;
        await gate.RunToolAsync("set_output", "{}", CancellationToken.None);
        mode = ToolApprovalMode.AutoRead;
        await gate.RunToolAsync("set_output", "{}", CancellationToken.None);

        Assert.Equal(new[] { false, true, false }, shown.Select(request => request.AutoApproved));
    }

    /// <summary>
    /// A proposal changes nothing and never waits, on any rung — the bottom one included. The button
    /// on its card is the person's say; asking first as well would be a third act, about nothing yet.
    /// </summary>
    [Theory]
    [InlineData(ToolApprovalMode.Ask)]
    [InlineData(ToolApprovalMode.AutoRead)]
    [InlineData(ToolApprovalMode.AutoReadWrite)]
    public async Task A_proposal_never_waits_on_any_rung(ToolApprovalMode mode)
    {
        ScriptedTools bench = Bench();
        var person = new Person();
        var gate = new ApprovingToolRunner(bench, person.Approve, null, mode);

        // The person never answers: a proposal that waited would never come back.
        await gate.RunToolAsync("propose_script", "{}", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        ToolRunRequest shown = Assert.Single(person.Shown);
        Assert.True(shown.AutoApproved);
        Assert.True(shown.Proposes);
        Assert.False(shown.Changes);
        Assert.Equal("propose_script", Assert.Single(bench.Calls).Name);
    }

    /// <summary>A tool marked both ways is taken at its word as a proposal: it changes nothing, so nothing waits.</summary>
    [Fact]
    public void A_proposal_is_never_a_change_whatever_else_it_says()
    {
        var request = new ToolRunRequest("propose_script", "{}", changes: true, ToolApprovalMode.Ask, proposes: true);

        Assert.False(request.Changes);
        Assert.True(request.AutoApproved);
    }

    // ------------------------------------------------------------------ declining, and no gate

    /// <summary>A decline is an answer to the call: the bench is not touched, the card hears it was declined, and the model is told — as a result, not an error.</summary>
    [Fact]
    public async Task A_declined_change_is_not_made_and_the_model_is_told()
    {
        ScriptedTools bench = Bench();
        var heard = new List<LlmToolOutput?>();
        var gate = new ApprovingToolRunner(bench, (_, _) => Task.FromResult(false),
                                           (_, output) => { heard.Add(output); return Task.CompletedTask; },
                                           ToolApprovalMode.AutoRead);

        LlmToolOutput output = await gate.RunToolAsync("set_output", "{}", CancellationToken.None);

        Assert.Empty(bench.Calls);
        Assert.Equal(ApprovingToolRunner.DeclinedMessage, output.Text);
        Assert.True(output.IsDeclined);
        Assert.False(output.IsError);
        Assert.Null(Assert.Single(heard));
    }

    [Fact]
    public async Task An_approved_call_runs_and_its_card_hears_what_it_answered()
    {
        ScriptedTools bench = Bench();
        var heard = new List<(ToolRunRequest, LlmToolOutput?)>();
        var gate = new ApprovingToolRunner(bench, (_, _) => Task.FromResult(true),
                                           (request, output) => { heard.Add((request, output)); return Task.CompletedTask; },
                                           ToolApprovalMode.Ask);

        LlmToolOutput output = await gate.RunToolAsync("set_output", """{"on":true}""", CancellationToken.None);

        var (request, answer) = Assert.Single(heard);
        Assert.Equal("set_output", request.ToolName);
        Assert.Equal("""{"on":true}""", request.ArgumentsJson);
        Assert.Same(output, answer);
        Assert.Single(bench.Calls);
    }

    /// <summary>No approver is a misconfiguration, not permission: nothing runs, on the most trusting rung too.</summary>
    [Fact]
    public async Task With_no_approver_nothing_runs()
    {
        ScriptedTools bench = Bench();
        var gate = new ApprovingToolRunner(bench, null, null, ToolApprovalMode.AutoReadWrite);

        LlmToolOutput output = await gate.RunToolAsync("read_meter", "{}", CancellationToken.None);

        Assert.Equal(ApprovingToolRunner.DeclinedMessage, output.Text);
        Assert.True(output.IsDeclined);
        Assert.Empty(bench.Calls);
    }

    /// <summary>A name that is no tool changes nothing, so it is put to the rung as a read, and the runner says it is no tool.</summary>
    [Fact]
    public async Task A_name_that_is_no_tool_is_taken_as_a_read()
    {
        var shown = new List<ToolRunRequest>();
        var gate = new ApprovingToolRunner(Bench(), (request, _) => { shown.Add(request); return Task.FromResult(true); });

        await gate.RunToolAsync("format_disk", "{}", CancellationToken.None);

        Assert.False(Assert.Single(shown).Changes);
        Assert.True(shown[0].AutoApproved);
    }

    /// <summary>The changing tools are not offered, nor honoured, until the gate is in front of them — and every tool is offered through it, whatever the rung.</summary>
    [Fact]
    public async Task Without_the_gate_no_change_is_offered_or_made()
    {
        var bench = new Holstered();

        Assert.DoesNotContain(bench.Tools, tool => tool.Changes);
        Assert.True((await bench.RunToolAsync("set_output", "{}", CancellationToken.None)).IsError);
        Assert.Empty(bench.Calls);

        var gate = new ApprovingToolRunner(bench, (_, _) => Task.FromResult(true), null, ToolApprovalMode.Ask);

        Assert.True(bench.AllowEdits);
        Assert.Contains(gate.Tools, tool => tool.Name == "set_output" && tool.Changes);
    }

    // ---------------------------------------------------------------------------- progress

    private static (LlmToolCall, LlmToolOutput) Ran(string name, string arguments, LlmToolOutput output)
        => (new LlmToolCall { Id = "call", Name = name, ArgumentsJson = arguments }, output);

    private static LlmToolOutput Said(string text, string? png = null) => new() { Text = text, Png = png };

    [Fact]
    public void Rounds_of_refusals_are_stuck_on_the_third()
    {
        var progress = new ToolProgress();
        IReadOnlyList<(LlmToolCall, LlmToolOutput)> refused = [Ran("set_output", "{}", LlmToolOutput.Refused("No."))];

        Assert.False(progress.IsStuckAfter(refused));
        Assert.False(progress.IsStuckAfter(refused));
        Assert.True(progress.IsStuckAfter(refused));
    }

    [Fact]
    public void A_round_that_gets_somewhere_starts_the_count_again()
    {
        var progress = new ToolProgress();
        IReadOnlyList<(LlmToolCall, LlmToolOutput)> refused = [Ran("set_output", "{}", LlmToolOutput.Refused("No."))];

        progress.IsStuckAfter(refused);
        progress.IsStuckAfter(refused);
        Assert.False(progress.IsStuckAfter([Ran("read_meter", "{}", Said("4.998 V"))]));
        Assert.False(progress.IsStuckAfter(refused));
        Assert.False(progress.IsStuckAfter(refused));
        Assert.True(progress.IsStuckAfter(refused));
    }

    /// <summary>The same call answered the same is no progress; the same call answered differently — a reading that moved, a screen that changed — is.</summary>
    [Fact]
    public void A_repeat_is_no_progress_and_a_new_answer_is()
    {
        var progress = new ToolProgress();

        Assert.False(progress.IsStuckAfter([Ran("read_meter", "{}", Said("4.998 V"))]));
        Assert.False(progress.IsStuckAfter([Ran("read_meter", "{}", Said("4.998 V"))]));
        Assert.False(progress.IsStuckAfter([Ran("read_meter", "{}", Said("5.001 V"))]));
        Assert.False(progress.IsStuckAfter([Ran("capture_screen", "{}", Said("The screen.", "AAAA"))]));
        Assert.False(progress.IsStuckAfter([Ran("capture_screen", "{}", Said("The screen.", "BBBB"))]));
        Assert.False(progress.IsStuckAfter([Ran("capture_screen", "{}", Said("The screen.", "BBBB"))]));
        Assert.False(progress.IsStuckAfter([Ran("read_meter", "{}", Said("4.998 V"))]));
        Assert.True(progress.IsStuckAfter([Ran("read_meter", "{}", Said("5.001 V"))]));
    }

    /// <summary>A declined call is a result to the model, but it did not run: no more progress than a refusal.</summary>
    [Fact]
    public void A_declined_call_is_no_progress()
    {
        var progress = new ToolProgress();

        for (int volts = 1; volts <= 2; volts++)
            Assert.False(progress.IsStuckAfter([Ran("set_output", $$"""{"volts":{{volts}}}""", LlmToolOutput.Declined(ApprovingToolRunner.DeclinedMessage))]));

        Assert.True(progress.IsStuckAfter([Ran("set_output", """{"volts":3}""", LlmToolOutput.Declined(ApprovingToolRunner.DeclinedMessage))]));
    }
}
