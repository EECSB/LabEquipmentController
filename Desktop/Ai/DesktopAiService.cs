using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// What the desktop's assistant window stands on (<see cref="IAiService"/>): the connections and
/// keys AI Connection keeps (<see cref="AiBookStore"/>, the keys under DPAPI), the model called
/// straight from the app with the key it holds, and the chats in a folder under %AppData%.
///
/// One for the app, made by the main window: AI Connection applied there is
/// <see cref="ReloadAsync"/> here, which tells the assistant to pick again.
/// </summary>
internal sealed class DesktopAiService : IAiService
{
    // One client for the process, with no clock of its own: each request carries five minutes.
    private static readonly HttpClient Http = LlmProviders.NewHttpClient();

    private static DesktopAiService? _app;

    /// <summary>The app's one, for the assistant and the two script writers, which share a picked connection and its keys.</summary>
    public static DesktopAiService App => _app ??= new DesktopAiService(new FileChatStore(FileChatStore.DefaultFolder));

    private AiConnections _book;
    private IReadOnlyDictionary<string, string> _keys;

    public DesktopAiService(IChatStore chats)
    {
        Chats = chats;
        (_book, _keys) = AiBookStore.Load();
    }

    public string KeptWhere => "on this computer";

    public IChatStore Chats { get; }

    public event Func<Task>? ConnectionsChanged;

    public Task<AiConnections> LoadConnectionsAsync() => Task.FromResult(_book.Clone());

    public Task SelectAsync(string id)
    {
        if (_book.Select(id)) AiBookStore.Select(id);
        return Task.CompletedTask;
    }

    public string? WhyNotAsk(AiConnection connection)
        => connection.Provider == AiProvider.OpenAiCompatible || Key(connection).Length > 0
            ? null
            : "No API key is set on " + connection.EffectiveName + ". Add one under Tools ▸ AI Connection…";

    public ILlmProvider CreateProvider(AiConnection connection, string effort)
        => LlmProviders.Create(Http, new DirectLlmEndpoint(connection, Key(connection)), connection, effort);

    /// <summary>AI Connection was applied: read what it saved, and tell the window.</summary>
    public async Task ReloadAsync()
    {
        (_book, _keys) = AiBookStore.Load();
        if (ConnectionsChanged is Func<Task> changed) await changed();
    }

    /// <summary>The key kept for a connection, for what calls the model some other way than <see cref="CreateProvider"/>: the script writer.</summary>
    public string KeyFor(AiConnection connection) => Key(connection);

    /// <summary>This service with its chats in a folder of their own, as a script writer's are.</summary>
    public IAiService KeptIn(string folder)
        => new ChatsElsewhere(this, new FileChatStore(Path.Combine(FileChatStore.DefaultFolder, folder)));

    private sealed class ChatsElsewhere(DesktopAiService app, IChatStore chats) : IAiService
    {
        public string KeptWhere => app.KeptWhere;

        public IChatStore Chats => chats;

        public event Func<Task>? ConnectionsChanged
        {
            add => app.ConnectionsChanged += value;
            remove => app.ConnectionsChanged -= value;
        }

        public Task<AiConnections> LoadConnectionsAsync() => app.LoadConnectionsAsync();

        public Task SelectAsync(string id) => app.SelectAsync(id);

        public string? WhyNotAsk(AiConnection connection) => app.WhyNotAsk(connection);

        public ILlmProvider CreateProvider(AiConnection connection, string effort) => app.CreateProvider(connection, effort);
    }

    private string Key(AiConnection connection)
        => _keys.TryGetValue(connection.Id, out string? key) ? key : "";
}
