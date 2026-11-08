using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// How much the model may do on the bench on its own.
///
/// A ladder, not a set of switches: each rung includes the one below it. What moves between rungs
/// is what still stops for a person, never what the model may ask for — it can always ask to
/// change something, and on every rung but the top one that ask becomes a card somebody has to
/// answer.
///
/// **A conversation starts on the middle rung**, <see cref="AutoRead"/>, and no rung is remembered
/// between visits, so a restart never finds the top one on. The values run up the ladder, so the
/// zero a field starts at is the strictest of them rather than the most trusting.
/// </summary>
public enum ToolApprovalMode
{
    /// <summary>Nothing runs until a person says so, reads included. One card at a time.</summary>
    Ask,

    /// <summary>Reads run the moment the model asks; a change still waits for a person. Where a conversation starts.</summary>
    AutoRead,

    /// <summary>
    /// Reads and changes both run unattended. Each is still shown as a card as it happens — but on
    /// a bench there is no undo for an output switched on, so this is the rung to be sure of.
    /// </summary>
    AutoReadWrite,
}

/// <summary>What the model asked to run, in the form a person is asked to approve.</summary>
public sealed class ToolRunRequest
{
    public ToolRunRequest(string toolName, string argumentsJson, bool changes, ToolApprovalMode mode, bool proposes = false)
    {
        ToolName = toolName;
        ArgumentsJson = argumentsJson;
        Proposes = proposes;
        Changes = changes && !proposes;
        AutoApproved = proposes || !ApprovingToolRunner.NeedsApproval(mode, Changes);
    }

    public string ToolName { get; }

    public string ArgumentsJson { get; }

    /// <summary>Whether the call changes an instrument rather than only reading one. Never for a proposal, which changes nothing.</summary>
    public bool Changes { get; }

    /// <summary>
    /// Whether the call puts something forward for the person to take — a script for the editor —
    /// rather than doing anything. See <see cref="LlmTool.Proposes"/>.
    /// </summary>
    public bool Proposes { get; }

    /// <summary>
    /// Whether the rung in force — or the call being a proposal — has already answered this. It is
    /// still shown, since a call that runs unwatched is exactly the one worth seeing, but as
    /// something that happened rather than something to decide.
    /// </summary>
    public bool AutoApproved { get; }
}

/// <summary>
/// Stands between the model and the bench and decides, per call, whether it runs now or waits.
///
/// **Every tool is offered on every rung, changes included.** Withholding the changing tools was
/// worse than gating them, GraphDBViewer found: the model answers that it is unable to make
/// changes and stops, when the useful thing is to ask for the change and let somebody approve it.
/// So the rung decides only what runs unattended, and everything else becomes a card.
///
/// **The rung is read at every call**, not once per answer: a switch to the top rung in the middle
/// of an answer lets the next call through at once, and a switch to a stricter one stops the calls
/// after it — read once, GDS Viewer found a switch to "change without asking" went on asking until
/// the answer was done.
///
/// **A proposal never waits**, on any rung. A tool that proposes (<see cref="LlmTool.Proposes"/>)
/// changes nothing: what it puts forward reaches the editor when the person presses the button on
/// its card, and runs when they press Run — SPEC §11c's two deliberate acts. Asking first as well
/// would be a third, about something that has not happened yet.
///
/// A wrapper rather than a flag inside the runner, and nothing in either provider knows about it:
/// the tool loop already awaits <see cref="ILlmToolRunner.RunToolAsync"/>, so waiting on a person
/// is that await taking longer.
/// </summary>
public sealed class ApprovingToolRunner : ILlmToolRunner
{
    /// <summary>What the model is told when a person declines. Phrased as a result, since that is what it is.</summary>
    public const string DeclinedMessage =
        "The person did not approve that call, so it was not run. Do not run it again. Answer with what you "
      + "already know, or ask what they would like instead.";

    private readonly ILlmToolRunner _inner;
    private readonly Func<ToolRunRequest, CancellationToken, Task<bool>>? _approve;
    private readonly Func<ToolRunRequest, LlmToolOutput?, Task>? _completed;
    private readonly Func<ToolApprovalMode> _mode;

    /// <summary>A gate on one rung, for a caller with no way to change it mid-answer. The middle rung unless told otherwise.</summary>
    /// <param name="inner">The runner the calls go to once they are let through.</param>
    /// <param name="approve">
    /// Shows the call and answers it. A request whose <see cref="ToolRunRequest.AutoApproved"/> is
    /// set has been answered already, and should be shown rather than waited on. Awaited otherwise,
    /// so it may take as long as the person does.
    /// </param>
    /// <param name="completed">Told what the tool answered, or null when it was declined, so the same card can show it.</param>
    /// <param name="mode">The rung, for every call.</param>
    public ApprovingToolRunner(ILlmToolRunner inner,
                               Func<ToolRunRequest, CancellationToken, Task<bool>>? approve,
                               Func<ToolRunRequest, LlmToolOutput?, Task>? completed = null,
                               ToolApprovalMode mode = ToolApprovalMode.AutoRead)
        : this(inner, approve, completed, () => mode)
    {
    }

    /// <summary>A gate whose rung is asked for at every call, so a rung chosen while a run is going is the one its next call meets.</summary>
    /// <param name="inner">The runner the calls go to once they are let through.</param>
    /// <param name="approve">Shows the call and answers it — see the other constructor.</param>
    /// <param name="completed">Told what the tool answered, or null when it was declined.</param>
    /// <param name="mode">Asked for the rung in force at every call, rather than once.</param>
    public ApprovingToolRunner(ILlmToolRunner inner,
                               Func<ToolRunRequest, CancellationToken, Task<bool>>? approve,
                               Func<ToolRunRequest, LlmToolOutput?, Task>? completed,
                               Func<ToolApprovalMode> mode)
    {
        _inner = inner;
        _approve = approve;
        _completed = completed;
        _mode = mode;

        // The changing tools stay holstered until something asks for them, and this is the
        // something: a runner reached through a gate can offer them, because the gate is what
        // makes them safe.
        if (inner is IEditCapableToolRunner editable) editable.AllowEdits = true;
    }

    /// <summary>The rung in force now, which is what the next call is put to.</summary>
    public ToolApprovalMode Mode => _mode();

    /// <summary>Whether a call of this kind still has to be put to a person on a rung. An unknown rung asks, which is the safe reading of it.</summary>
    public static bool NeedsApproval(ToolApprovalMode mode, bool changes) => mode switch
    {
        ToolApprovalMode.AutoReadWrite => false,
        ToolApprovalMode.AutoRead => changes,
        _ => true,
    };

    /// <summary>Every tool the runner has. What the rung changes is which of them stop for a person.</summary>
    public IReadOnlyList<LlmTool> Tools => _inner.Tools;

    public async Task<LlmToolOutput> RunToolAsync(string name, string argumentsJson, CancellationToken ct)
    {
        LlmTool? tool = Find(name);
        var request = new ToolRunRequest(name, argumentsJson, tool?.Changes == true, Mode, tool?.Proposes == true);

        // No approver is a misconfiguration, not permission. Refusing is the safe reading of it —
        // on the trusting rungs too, where the approver is also how what ran gets shown.
        if (_approve is null) return LlmToolOutput.Declined(DeclinedMessage);

        if (!await _approve(request, ct).ConfigureAwait(false))
        {
            if (_completed is not null) await _completed(request, null).ConfigureAwait(false);
            return LlmToolOutput.Declined(DeclinedMessage);
        }

        LlmToolOutput output = await _inner.RunToolAsync(name, argumentsJson, ct).ConfigureAwait(false);

        if (_completed is not null) await _completed(request, output).ConfigureAwait(false);
        return output;
    }

    /// <summary>
    /// The tool called. A name that is no tool changes nothing — the runner will say so — so it is
    /// taken as a read rather than put to a person as though it were a change.
    /// </summary>
    private LlmTool? Find(string name)
    {
        foreach (LlmTool tool in _inner.Tools)
        {
            if (tool.Name == name) return tool;
        }
        return null;
    }
}
