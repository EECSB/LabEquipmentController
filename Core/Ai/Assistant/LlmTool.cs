using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// A tool the assistant's model may call, described the way every provider can be told about
/// one.
///
/// The names across this folder are GDS Viewer's and GraphDBViewer's — <c>ILlmProvider</c>,
/// <c>ILlmToolRunner</c>, <c>ApprovingToolRunner</c>, <c>AiChat</c> — so the three projects
/// read as one design and a fix found in one can be carried to the others by name.
/// </summary>
public sealed class LlmTool
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>JSON Schema, as text, for the object of arguments it takes.</summary>
    public required string InputSchemaJson { get; init; }

    /// <summary>
    /// Whether calling it changes an instrument rather than only reading one.
    ///
    /// What the approval rung reads: a change is what waits for a person on the middle rung,
    /// where a read runs at once. On a bench that matters more than in an editor — there is no
    /// undo for an output switched on into a circuit, so the card a change waits on is the only
    /// place it can be stopped.
    /// </summary>
    public bool Changes { get; init; }

    /// <summary>
    /// Whether calling it puts something forward rather than doing it — a script for the
    /// editor. It changes nothing and never waits: the button on its card is the person's say,
    /// which is SPEC §11c's rule that nothing a model writes runs without two deliberate acts.
    /// </summary>
    public bool Proposes { get; init; }
}

/// <summary>A call the model asked for. The id goes back with the answer, so the model knows which call it answers.</summary>
public sealed class LlmToolCall
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string ArgumentsJson { get; init; }
}

/// <summary>
/// What a tool answered: the words the model reads, and a picture when the tool took one.
///
/// Richer than a string for the reasons GDS Viewer gave. A picture of an instrument's screen
/// is something the model should see rather than read about, and both provider families can
/// take one back — Anthropic inside the tool's result, the OpenAI-compatible family in a user
/// turn after it. And a refusal is said as one: a model told a call failed tries something
/// else rather than building on it.
/// </summary>
public sealed class LlmToolOutput
{
    public required string Text { get; init; }

    /// <summary>Whether the call was refused or failed, with <see cref="Text"/> saying why.</summary>
    public bool IsError { get; init; }

    /// <summary>
    /// Whether a person declined the call, so it never ran. Not an error to the model, which is
    /// asked not to try another way to the same end; but no progress either, which
    /// <see cref="ToolProgress"/> could not tell from the words alone — so a model asking for
    /// one change after another, each answered no, ran to the cap on rounds. The providers never
    /// read it.
    /// </summary>
    public bool IsDeclined { get; init; }

    /// <summary>A PNG for the model to look at, as base64, or null for an answer in words alone.</summary>
    public string? Png { get; init; }

    /// <summary>
    /// What a proposing tool put forward for the person to take — a script, for the editor —
    /// shown on its card beside the button that takes it. Never sent back to the model, which
    /// wrote it and is told in <see cref="Text"/> what became of it.
    /// </summary>
    public string? Proposal { get; init; }

    /// <summary>A call a person declined: a result to the model, and no progress to the run.</summary>
    public static LlmToolOutput Declined(string text) => new() { Text = text ?? "", IsDeclined = true };

    /// <summary>A refusal, in words that say what to do about it.</summary>
    public static LlmToolOutput Refused(string text) => new() { Text = text ?? "", IsError = true };
}

/// <summary>
/// Runs the tools the model calls.
///
/// Implemented over the bench and handed to a provider's loop, so the provider code knows
/// nothing about instruments.
/// </summary>
public interface ILlmToolRunner
{
    /// <summary>The tools offered to the model.</summary>
    IReadOnlyList<LlmTool> Tools { get; }

    /// <summary>Runs one call and says what came of it. Never throws for anything the model did.</summary>
    Task<LlmToolOutput> RunToolAsync(string name, string argumentsJson, CancellationToken ct);
}

/// <summary>
/// A runner that owns tools which change an instrument, and can be told whether to offer them.
///
/// Off is the default and stays off unless something deliberately turns it on, so a caller that
/// knows nothing about changes cannot hand the model one by accident.
/// <see cref="ApprovingToolRunner"/> is what turns it on, because the gate it puts in front of
/// every change is what makes offering them safe.
/// </summary>
public interface IEditCapableToolRunner
{
    /// <summary>Whether the tools that change an instrument are offered, and honoured when one is called.</summary>
    bool AllowEdits { get; set; }
}
