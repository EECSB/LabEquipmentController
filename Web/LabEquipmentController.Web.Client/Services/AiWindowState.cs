namespace LabEquipmentController.Web.Client.Services;

/// <summary>
/// An AI window as it stands between openings: the conversation, which chat it is kept as, which
/// panels are open, the words left in the box, and what keeping and compacting do. The desktop
/// keeps all of it by hiding its window rather than closing it; a tool window here is taken out of
/// the page when it shuts, so what it holds lives here instead, for as long as the tab does, and an
/// answer still coming when the window shuts is still kept when it lands. Only New Chat starts
/// another conversation.
///
/// One window draws it (<c>Tools/Assistant.razor</c>), for the bench's AI assistant
/// (<see cref="AssistantState"/>) and for a script editor's Write a Script with AI
/// (<see cref="ScriptWriterState"/>). What differs is who is asked and where the chats are kept.
/// </summary>
public abstract class AiWindowState
{
    protected AiWindowState(WebAiService ai, BenchClient bench, IChatStore store)
    {
        Ai = ai;
        Bench = new WebBench(bench);
        Store = store;
    }

    protected WebAiService Ai { get; }

    protected WebBench Bench { get; }

    /// <summary>The instruments open, in a line, as they last read.</summary>
    protected string BenchLine { get; private set; } = "";

    /// <summary>Where this window's chats are kept: apart for each kind of window.</summary>
    public IChatStore Store { get; }

    public AiChat Chat { get; } = new();

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

    public bool FilesOpen { get; set; } = true;

    /// <summary>Whether the cards a compaction folded away are shown.</summary>
    public bool ShowFolded { get; set; }

    /// <summary>The words in the box, kept through the window shutting.</summary>
    public string Draft { get; set; } = "";

    /// <summary>The effort chosen for the next question: "" for the model's default. Never saved.</summary>
    public string Effort { get; set; } = "";

    /// <summary>Whether the next question runs in fast mode, where the model picked offers it. Never saved.</summary>
    public bool Fast { get; set; }

    /// <summary>Whether what the next question costs and what has been spent are on a line under the box. The (i) takes it off.</summary>
    public bool MeterOn { get; set; } = true;

    /// <summary>The one line over the box that says what just happened, when anything did that the conversation does not show: "" for none.</summary>
    public string Notice { get; private set; } = "";

    /// <summary>Raised when something here changes that the window draws: the notice, the chat kept, the chat on screen.</summary>
    public event Action? Changed;

    /// <summary>The instruments open now, as the tools list them.</summary>
    public async Task<IReadOnlyList<BenchInstrument>> BenchAsync()
    {
        IReadOnlyList<BenchInstrument> bench = await Bench.ListAsync(CancellationToken.None);
        BenchLine = AssistantPrompt.BenchLine(bench);
        return bench;
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
        ILlmProvider provider = Ai.CreateProvider(connection, levels.Count > 0 ? levels[0] : "");
        int shown = Shown;

        Say("");
        ChatOutcome outcome = await Chat.CompactAsync(provider);
        if (outcome == ChatOutcome.Stopped) Say("Compacting was stopped. The conversation is as it was.");
        else if (outcome == ChatOutcome.Failed) Say("Compacting failed. The conversation is as it was.");
        if (outcome != ChatOutcome.NotSent) await KeepAsync(shown);
    }

    /// <summary>
    /// Keeps the chat on screen, from its first question on, after every answer and every
    /// compaction; not when another chat was put on screen while the answer came.
    /// </summary>
    protected async Task KeepAsync(int shown)
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
            await Store.SaveAsync(chat);
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
        Say("");
    }

    /// <summary>
    /// Opens a kept chat and carries it on, with a line saying when it was opened again and, when
    /// the bench is another now, what it was about. Null when it opened; otherwise why not.
    /// </summary>
    public async Task<string?> OpenAsync(string id)
    {
        if (Chat.Generating) return "Not while an answer is coming. Stop it first.";
        if (id == ChatId) return null;

        SavedChat? kept = await Store.LoadAsync(id);
        if (kept is null) return "That chat is gone: it was deleted, or its file will not read.";
        if (Chat.Generating) return "Not while an answer is coming. Stop it first.";

        Shown++;
        string benchNow = AssistantPrompt.BenchLine(await BenchAsync());
        Chat.Restore(kept.Unkept(), AssistantPrompt.ReopenedLine(kept.Bench, benchNow, DateTime.Now));
        ChatId = kept.Id;
        Created = kept.Created == default ? DateTime.Now : kept.Created;
        ShowFolded = false;
        Say("");
        return null;
    }

    /// <summary>Throws a kept chat away; the chat on screen deleted is the chat on screen gone, and a new one takes its place.</summary>
    public async Task DeleteAsync(string id)
    {
        await Store.DeleteAsync(id);
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

        SavedChat? kept = await Store.LoadAsync(id);
        if (kept is null) return null;
        // Put back into a conversation of its own only to be written out: no line, nothing sent.
        var other = new AiChat();
        other.Restore(kept.Unkept(), null);
        return (kept.Title, other.Transcript(kept.Title, DateTime.Now));
    }

    /// <summary>Says what just happened in the line over the box; "" takes it away.</summary>
    public void Say(string notice)
    {
        Notice = notice;
        Changed?.Invoke();
    }
}
