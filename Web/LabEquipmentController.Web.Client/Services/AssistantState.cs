namespace LabEquipmentController.Web.Client.Services;

/// <summary>
/// The AI assistant's window between openings (<see cref="AiWindowState"/>), and what only the
/// assistant has: the bench's tools, and this tab's end of the live link to lec mcp. What it does
/// with a question is the desktop's <c>AssistantForm</c>, step for step.
/// </summary>
public sealed class AssistantState : AiWindowState
{
    public const string Where = "the bench's web page";

    private readonly AiPreferences _preferences;

    public AssistantState(WebAiService ai, BenchClient bench, AiPreferences preferences)
        : base(ai, bench, ai.Chats)
    {
        _preferences = preferences;
        Tools = new BenchToolRunner(Bench) { Off = Chat.ToolsOff };

        // The same tools over the link, saying a proposal is the client's to hand on: there is no
        // card here for it to wait on. No second approval gate, as the guide has it: the client the
        // model runs in asks the person before each call.
        var linked = new BenchToolRunner(Bench)
        {
            HowProposalsAreTaken = "Nothing here runs it: the script follows this answer, for you to give to the person, who can paste "
                                 + "it into a script editor on the bench page and press Run there - tell them what it does and what to check before they do.",
        };
        Link = new LiveLink(linked.RunToolAsync) { Subject = () => BenchLine };
    }

    /// <summary>This tab's end of the live link to lec mcp: up for as long as the tab is, whatever windows are shut.</summary>
    public LiveLink Link { get; }

    /// <summary>The bench's tools, with the ones switched off in the window's picker refused.</summary>
    public BenchToolRunner Tools { get; }

    /// <summary>
    /// Asks the question and waits for the answer, then keeps the chat. A question that failed
    /// gave its files back to the box (<see cref="AiChat.GaveFilesBack"/>), and the window puts its
    /// words back with them; that is the window's to do, so the outcome is handed back.
    /// </summary>
    public async Task<ChatOutcome> SendAsync(string text, AiConnection connection)
    {
        ILlmProvider provider = Ai.CreateProvider(connection, Effort, Fast);
        IReadOnlyList<BenchInstrument> bench = await BenchAsync();
        string system = AssistantPrompt.Build(bench, Chat.Mode, Chat.ToolsOff, Where, DateTime.Now);
        int shown = Shown;

        Say("");
        Chat.MaxToolRounds = await _preferences.ToolRoundsAsync();
        ChatOutcome outcome = await Chat.SendAsync(text, provider, system, Tools);
        if (outcome != ChatOutcome.NotSent) await KeepAsync(shown);
        return outcome;
    }
}
