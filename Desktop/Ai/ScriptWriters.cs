using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// What a script editor tells the Write a Script with AI window: the language, the instruments the
/// model may address, how to read the editor as it stands now, and where a script goes.
/// </summary>
/// <param name="Editor">The editor that opened the window, which can be shut while the window stays up.</param>
/// <param name="Asking">Makes the model for one request: the connection, the effort, the script and the output to send with it.</param>
internal sealed record ScriptWriterJob(
    Form Editor,
    bool IsSequence,
    IReadOnlyList<ScriptContextInstrument> Instruments,
    Func<string> CurrentScript,
    Func<string> RecentOutput,
    Action<string> Use,
    Func<AiConnection, string, string?, string?, ILlmProvider> Asking);

/// <summary>
/// The two Write a Script with AI windows, one for each language, as the assistant's: made the first
/// time they are asked for and hidden rather than closed after that, so a conversation outlives its
/// window. Keyed on the language because a transcript of single-instrument scripts is a poor thing
/// to hand a model being asked for DEVICE and WITH.
/// </summary>
internal static class ScriptWriters
{
    private const string Title = "Write a Script with AI";

    private static readonly Dictionary<bool, AssistantForm> Windows = [];

    private static Form? _main;
    private static SessionRegistry? _sessions;
    private static Action? _settings;

    /// <summary>What the windows need from the main window, which is theirs to live under.</summary>
    public static void Configure(Form main, SessionRegistry sessions, Action settings)
    {
        _main = main;
        _sessions = sessions;
        _settings = settings;
    }

    /// <summary>
    /// Shows the writer for an editor, or says why it cannot be shown: nothing to spend, or no
    /// instrument whose catalog to write from.
    /// </summary>
    public static void Open(Form editor, IReadOnlyList<ScriptContextInstrument> instruments, bool isSequence,
                            Func<string> currentScript, Func<string> recentOutput, Action<string> use)
    {
        (AiConnections book, IReadOnlyDictionary<string, string> keys) = AiBookStore.Load();
        if (book.Selected is null || keys.Count == 0)
        {
            MessageBox.Show(editor,
                "No AI connection is set up yet.\n\nAdd one under Tools ▸ AI Connection: you supply your "
              + "own provider and key, and the key is stored encrypted for your Windows account.",
                Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (instruments.Count == 0)
        {
            MessageBox.Show(editor,
                "Connect an instrument first.\n\nThe model is given that instrument's command catalog to "
              + "write from; without one it would be inventing SCPI, which is the one thing this app does not do.",
                Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var job = new ScriptWriterJob(editor, isSequence, instruments, currentScript, recentOutput, use,
            (connection, effort, script, output) => new DesktopScriptWriter(
                connection, DesktopAiService.App.KeyFor(connection), effort, instruments, isSequence, script, output));

        if (Windows.TryGetValue(isSequence, out AssistantForm? window) && !window.IsDisposed)
        {
            window.Aim(job);
            if (!window.Visible) window.Show();
            if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
            window.BringToFront();
            window.Activate();
            return;
        }

        window = new AssistantForm(
            DesktopAiService.App.KeptIn(isSequence ? "sequences" : "scripts"),
            _sessions ?? new SessionRegistry(), (_, _, _) => { }, job);
        if (editor.Icon != null) window.Icon = editor.Icon;
        if (_settings is not null) window.SettingsRequested += _settings;
        Windows[isSequence] = window;
        window.Show(_main);
    }
}

/// <summary>
/// The script writer as a model the conversation can ask: <see cref="AiChat"/> holds the turns, the
/// files, what was spent and the Stop, and <see cref="ScriptAuthor"/> builds the prompt and checks
/// what comes back. An answer goes back as <see cref="WrittenScript"/>'s text, which is what a
/// conversation keeps.
/// </summary>
internal sealed class DesktopScriptWriter(
    AiConnection connection, string apiKey, string effort,
    IReadOnlyList<ScriptContextInstrument> instruments, bool isSequence,
    string? currentScript, string? recentOutput) : ILlmProvider
{
    public string Model => connection.EffectiveModel;

    public LlmUsage Spent { get; } = new();

    public LlmPrice? Price { get; } = AiPricing.For(connection, DateOnly.FromDateTime(DateTime.Now));

    public async Task<LlmResult> CompleteAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation, CancellationToken ct)
    {
        var (asked, history, files) = ScriptAuthor.ReadConversation(conversation);

        AiConnection asking = connection.Clone();
        asking.Effort = ScriptAuthor.EffortFrom(effort);

        try
        {
            AuthoredScript written = await new ScriptAuthor(new AiClient()).WriteAsync(
                asked, instruments, isSequence, asking, apiKey, currentScript, recentOutput, history, files, ct);
            Spent.Add(written.Usage);
            return LlmResult.Success(new WrittenScript(written.Script, written.Notes, written.Undocumented).ToText());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LlmResult.Failure(ex is AiException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>There are no tools to offer: the writer's one way of answering is a script.</summary>
    public Task<LlmResult> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation,
                                                  ILlmToolRunner tools, CancellationToken ct,
                                                  int maxRounds = LlmProviders.DefaultToolRounds)
        => CompleteAsync(systemPrompt, conversation, ct);
}
