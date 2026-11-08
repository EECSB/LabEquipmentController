using System;
using System.Collections.Generic;
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

    private string Key(AiConnection connection)
        => _keys.TryGetValue(connection.Id, out string? key) ? key : "";
}
