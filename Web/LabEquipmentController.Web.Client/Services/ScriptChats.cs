using LabEquipmentController.Web.Client.Contracts;

namespace LabEquipmentController.Web.Client.Services;

/// <summary>
/// The AI script writer's conversations, held outside the window that shows them.
///
/// Outside because the window does not outlive one answer: Use This Script closes it, the
/// same as the desktop's <c>ScriptAiForm</c>. A history kept in the component would be a
/// chat of exactly one turn — visible right up until the moment you took a draft and needed
/// it — so it lives here, for as long as the tab does, and the desktop keeps a static of its
/// own for the same reason.
///
/// Kept in the browser rather than on the server, too. The server has no notion of a window,
/// and two script editors open at once are two conversations; a server-side history would
/// splice them into one. What that costs is a transcript on the wire with every request,
/// which is also exactly what the model is being sent, so the window can show the figure
/// honestly.
///
/// Keyed on the language, because the two editors speak different ones: a transcript of
/// single-instrument scripts is a poor thing to hand a model being asked for DEVICE and WITH.
/// </summary>
public sealed class ScriptChats
{
    private readonly Dictionary<bool, List<AiTurn>> _byLanguage = [];

    /// <summary>The conversation for one language — the live list, not a copy.</summary>
    public List<AiTurn> For(bool isSequence)
    {
        if (!_byLanguage.TryGetValue(isSequence, out List<AiTurn>? turns))
            _byLanguage[isSequence] = turns = [];
        return turns;
    }

    /// <summary>
    /// How much transcript goes with every request, in characters.
    ///
    /// Not tokens — the browser cannot count those, and no two providers count them the same
    /// way — but it is the only honest number available here and it moves with them. A count
    /// of turns on its own does not answer "should I clear this?": two turns carrying
    /// two-hundred-line scripts are worth more than ten carrying four.
    /// </summary>
    public static int Size(IReadOnlyList<AiTurn> turns)
        => turns.Sum(t => t.Request.Length + t.Script.Length + t.Notes.Length
                        + t.Undocumented.Sum(u => u.Length));
}
