using LabEquipmentController.Web.Client.Contracts;

namespace LabEquipmentController.Web.Client.Services;

/// <summary>
/// The script writer's windows between openings, one for each of the two languages.
///
/// Outside the window because the window does not outlive one answer: Use This Script closes it, the
/// same as on the desktop. Keyed on the language, because the two editors speak
/// different ones: a transcript of single-instrument scripts is a poor thing to hand a model being
/// asked for DEVICE and WITH, so each language has its conversation and its kept chats.
/// </summary>
public sealed class ScriptChats(WebAiService ai, BenchClient bench, HttpClient http)
{
    private ScriptWriterState? _scripts;
    private ScriptWriterState? _sequences;

    public ScriptWriterState For(bool isSequence) => isSequence
        ? _sequences ??= new ScriptWriterState(ai, bench, new WebChatStore(http, "sequence"))
        : _scripts ??= new ScriptWriterState(ai, bench, new WebChatStore(http, "script"));
}

/// <summary>What a script editor tells the Write a Script with AI window it opens.</summary>
/// <param name="IsSequence">Whether the editor speaks the multi-instrument language.</param>
/// <param name="Session">The console it was opened from; null for the multi-instrument editor, which offers the bench.</param>
/// <param name="CurrentScript">What is in the editor.</param>
/// <param name="RecentOutput">What the last run printed.</param>
/// <param name="Picks">The multi-instrument editor's binding table, as picked by hand: alias to session id.</param>
public sealed record ScriptWriterContext(
    bool IsSequence, string? Session, string? CurrentScript, string? RecentOutput,
    IReadOnlyDictionary<string, string>? Picks);

/// <summary>What one request to the writer is about, as the window's switches stand when it is sent.</summary>
public sealed record ScriptWriterAsk(
    IReadOnlyList<string> SessionIds, bool IsSequence, string? CurrentScript, string? RecentOutput,
    string? EditorScript, IReadOnlyDictionary<string, string>? Picks);

/// <summary>Write a Script with AI between openings: its conversation, asked of the server's script writer.</summary>
public sealed class ScriptWriterState(WebAiService ai, BenchClient bench, IChatStore store)
    : AiWindowState(ai, bench, store)
{
    private readonly BenchClient _client = bench;

    public async Task<ChatOutcome> SendAsync(string text, AiConnection connection, ScriptWriterAsk ask)
    {
        var writer = new ScriptWriterModel(_client, connection, Effort, ask);
        int shown = Shown;

        Say("");
        ChatOutcome outcome = await Chat.SendAsync(text, writer, "", null);
        if (outcome != ChatOutcome.NotSent) await KeepAsync(shown);
        return outcome;
    }
}

/// <summary>
/// The server's script writer, as a model the conversation can ask.
///
/// The conversation (<see cref="AiChat"/>) holds the turns, the files, what was spent and the Stop;
/// what it asks through this is the server, which has the catalogs, builds the prompt, checks what
/// comes back and holds the key. An answer comes back as <see cref="WrittenScript"/>'s text, which is
/// what the conversation keeps, and what goes back as the conversation so far.
/// </summary>
public sealed class ScriptWriterModel(BenchClient bench, AiConnection connection, string effort, ScriptWriterAsk ask) : ILlmProvider
{
    public string Model => connection.EffectiveModel;

    public LlmUsage Spent { get; } = new();

    public LlmPrice? Price { get; } = AiPricing.For(connection, DateOnly.FromDateTime(DateTime.Now));

    public async Task<LlmResult> CompleteAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation, CancellationToken ct)
    {
        var (asked, history, files) = Read(conversation);

        AiScriptReply reply;
        try
        {
            reply = await bench.AiScriptAsync(new AiScriptRequest(
                asked, ask.SessionIds, ask.IsSequence, ask.CurrentScript, ask.RecentOutput, history,
                ask.EditorScript, ask.Picks, Files: files, Effort: effort),
                BenchClient.ScriptWait(connection.TimeoutSeconds), ct);
        }
        catch (HttpRequestException e)
        {
            // The browser's fetch says only "TypeError: Failed to fetch" for a server that is not
            // there, which is true and tells nobody what to do.
            return LlmResult.Failure($"Could not reach the server ({e.Message}).");
        }

        if (reply.Error is not null) return LlmResult.Failure(reply.Error);

        Spent.Add(reply.Usage);
        return LlmResult.Success(new WrittenScript(reply.Script, reply.Notes, reply.Undocumented).ToText());
    }

    /// <summary>There are no tools to offer: the writer's one way of answering is a script.</summary>
    public Task<LlmResult> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation,
                                                  ILlmToolRunner tools, CancellationToken ct,
                                                  int maxRounds = LlmProviders.DefaultToolRounds)
        => CompleteAsync(systemPrompt, conversation, ct);

    /// <summary>The conversation as the writer takes it, in the shape the server's request does (<see cref="ScriptAuthor.ReadConversation"/>).</summary>
    public static (string Asked, List<AiTurn> History, List<AiScriptFile> Files) Read(IReadOnlyList<LlmMessage> conversation)
    {
        var (asked, history, files) = ScriptAuthor.ReadConversation(conversation);
        return (asked,
                [.. history.Select(t => new AiTurn(t.Request, t.Script, t.Notes, t.Undocumented ?? []))],
                [.. files.Select(f => new AiScriptFile(f.Name, f.MediaType, Convert.ToBase64String(f.Data)))]);
    }
}
