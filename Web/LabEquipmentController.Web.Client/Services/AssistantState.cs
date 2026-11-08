namespace LabEquipmentController.Web.Client.Services;

/// <summary>
/// The AI assistant as it stands between openings of its window: the conversation, which chat it
/// is kept as, which panels are open, the words left in the box, and what asking, compacting and
/// keeping do. The desktop keeps all of it by hiding its window rather than closing it; a tool
/// window here is taken out of the page when it shuts, so what it holds lives here instead, for
/// as long as the tab does, and an answer still coming when the window shuts is still kept when it
/// lands. Only New Chat starts another conversation.
///
/// The window (<c>Tools/Assistant.razor</c>) draws this and asks it to act; what it does is the
/// desktop's <c>AssistantForm</c>, step for step.
/// </summary>
public sealed class AssistantState
{
    public const string Where = "the bench's web page";

    private readonly WebAiService _ai;
    private readonly WebBench _bench;

    public AssistantState(WebAiService ai, BenchClient bench)
    {
        _ai = ai;
        _bench = new WebBench(bench);
        Tools = new BenchToolRunner(_bench) { Off = Chat.ToolsOff };
    }

    public AiChat Chat { get; } = new();

    /// <summary>The bench's tools, with the ones switched off in the window's picker refused.</summary>
    public BenchToolRunner Tools { get; }

    /// <summary>The kept chat on screen, once its first question has been asked; null before that.</summary>
    public string? ChatId { get; private set; }

    /// <summary>When the chat on screen began.</summary>
    public DateTime Created { get; private set; }

    /// <summary>
    /// A count of the chats put on screen, so an answer that lands after another chat was opened
    /// is not kept under that one's id.
    /// </summary>
    public int Shown { get; private set; }

    /// <summary>Open or shut is the person's to choose, and holds across conversations.</summary>
    public bool ChatsOpen { get; set; } = true;

    public bool FilesOpen { get; set; }

    /// <summary>Whether the cards a compaction folded away are shown.</summary>
    public bool ShowFolded { get; set; }

    /// <summary>The words in the box, kept through the window shutting.</summary>
    public string Draft { get; set; } = "";

    /// <summary>The effort chosen for the next question: "" for the model's default. Never saved.</summary>
    public string Effort { get; set; } = "";

    /// <summary>Whether the window has been opened before in this tab: the first opening fills the box with a question that works.</summary>
    public bool OpenedBefore { get; set; }

    /// <summary>What the status line along the window's foot says.</summary>
    public string Status { get; set; } = "Ready.";

    /// <summary>Raised when something here changes that the window draws: the status, the chat kept, the chat on screen.</summary>
    public event Action? Changed;

    /// <summary>The instruments open now, as the tools list them.</summary>
    public Task<IReadOnlyList<BenchInstrument>> BenchAsync() => _bench.ListAsync(CancellationToken.None);

    /// <summary>
    /// Asks the question and waits for the answer, then keeps the chat. A question that failed
    /// gave its files back to the box (<see cref="AiChat.GaveFilesBack"/>), and the window puts its
    /// words back with them; that is the window's to do, so the outcome is handed back.
    /// </summary>
    public async Task<ChatOutcome> SendAsync(string text, AiConnection connection)
    {
        ILlmProvider provider = _ai.CreateProvider(connection, Effort);
        IReadOnlyList<BenchInstrument> bench = await BenchAsync();
        string system = AssistantPrompt.Build(bench, Chat.Mode, Chat.ToolsOff, Where, DateTime.Now);
        int shown = Shown;

        Say("Asking " + connection.EffectiveName + "…");
        ChatOutcome outcome = await Chat.SendAsync(text, provider, system, Tools);
        Say(outcome switch
        {
            ChatOutcome.Stopped => "Stopped.",
            ChatOutcome.Failed => "The question failed; the reason is in the conversation.",
            ChatOutcome.Paused => "Paused at the limit on rounds. Continue carries on.",
            _ => SpentLine(),
        });
        if (outcome != ChatOutcome.NotSent) await KeepAsync(shown);
        return outcome;
    }

    /// <summary>
    /// Compacts the conversation on the model picked, at its lowest effort: a briefing needs care
    /// with names rather than the model's best reasoning, and the connection is the one somebody
    /// saved; another is not theirs to be sent to.
    /// </summary>
    public async Task CompactAsync(AiConnection connection)
    {
        if (Chat.WhyNotCompact() is string cannot)
        {
            Say(cannot);
            return;
        }

        IReadOnlyList<string> levels = AiModels.EffortLevels(connection);
        ILlmProvider provider = _ai.CreateProvider(connection, levels.Count > 0 ? levels[0] : "");
        int shown = Shown;

        Say("Compacting the conversation…");
        ChatOutcome outcome = await Chat.CompactAsync(provider);
        Say(outcome switch
        {
            ChatOutcome.Answered => "Compacted: the briefing stands in for what came before it. " + SpentLine(),
            ChatOutcome.Stopped => "Compacting was stopped. The conversation is as it was.",
            ChatOutcome.Failed => "Compacting failed. The conversation is as it was.",
            _ => Status,
        });
        if (outcome != ChatOutcome.NotSent) await KeepAsync(shown);
    }

    /// <summary>
    /// Keeps the chat on screen, from its first question on, after every answer and every
    /// compaction; not when another chat was put on screen while the answer came.
    /// </summary>
    private async Task KeepAsync(int shown)
    {
        if (shown != Shown) return;

        IReadOnlyList<ChatTurn> turns = Chat.Turns;
        if (!turns.Any(t => t.Kind == ChatTurn.User)) return;

        ChatId ??= SavedChat.NewId();
        if (Created == default) Created = DateTime.Now;
        var chat = new SavedChat
        {
            Id = ChatId,
            Title = SavedChat.TitleFor(turns),
            Bench = AssistantPrompt.BenchLine(await BenchAsync()),
            Created = Created,
            Updated = DateTime.Now,
            Turns = SavedChat.Keep(turns),
        };
        try
        {
            await _ai.Chats.SaveAsync(chat);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            Say("The chat could not be kept: " + e.Message);
        }
    }

    /// <summary>A new conversation: what the window holds rather than a chat (the files waiting, the tools switched off, what was spent) stays.</summary>
    public void NewChat()
    {
        if (Chat.Generating) return;
        Shown++;
        Chat.Clear();
        ChatId = null;
        Created = default;
        ShowFolded = false;
        Say("New conversation. " + SpentLine());
    }

    /// <summary>
    /// Opens a kept chat and carries it on, with a line saying when it was opened again and, when
    /// the bench is another now, what it was about. Null when it opened; otherwise why not.
    /// </summary>
    public async Task<string?> OpenAsync(string id)
    {
        if (Chat.Generating) return "Not while an answer is coming. Stop it first.";
        if (id == ChatId) return null;

        SavedChat? kept = await _ai.Chats.LoadAsync(id);
        if (kept is null) return "That chat is gone: it was deleted, or its file will not read.";
        if (Chat.Generating) return "Not while an answer is coming. Stop it first.";

        Shown++;
        string benchNow = AssistantPrompt.BenchLine(await BenchAsync());
        Chat.Restore(kept.Unkept(), AssistantPrompt.ReopenedLine(kept.Bench, benchNow, DateTime.Now));
        ChatId = kept.Id;
        Created = kept.Created == default ? DateTime.Now : kept.Created;
        ShowFolded = false;
        Say("Opened: " + kept.Title);
        return null;
    }

    /// <summary>Throws a kept chat away; the chat on screen deleted is the chat on screen gone, and a new one takes its place.</summary>
    public async Task DeleteAsync(string id)
    {
        await _ai.Chats.DeleteAsync(id);
        if (id == ChatId) NewChat();
    }

    /// <summary>A chat as text, for its row's Save: the one on screen as it stands, another as it was kept. Null when it is gone.</summary>
    public async Task<(string Title, string Text)?> TranscriptAsync(string? id)
    {
        if (id is null || id == ChatId)
        {
            string title = SavedChat.TitleFor(Chat.Turns);
            return (title, Chat.Transcript(title, DateTime.Now));
        }

        SavedChat? kept = await _ai.Chats.LoadAsync(id);
        if (kept is null) return null;
        // Put back into a conversation of its own only to be written out: no line, nothing sent.
        var other = new AiChat();
        other.Restore(kept.Unkept(), null);
        return (kept.Title, other.Transcript(kept.Title, DateTime.Now));
    }

    public string SpentLine()
    {
        LlmUsage used = Chat.Used;
        if (!used.Reported) return "Ready.";
        string tokens = $"{AiPricing.Count(used.InputTokens)} tokens in, {AiPricing.Count(used.OutputTokens)} out";
        return Chat.Cost is decimal dollars
            ? $"Spent since the page opened: {tokens}, {AiPricing.AboutMoney(dollars)}."
            : $"Spent since the page opened: {tokens}.";
    }

    public void Say(string status)
    {
        Status = status;
        Changed?.Invoke();
    }
}
