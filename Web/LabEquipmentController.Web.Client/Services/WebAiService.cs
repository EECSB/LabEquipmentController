using System.Net.Http.Json;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabEquipmentController.Web.Client.Contracts;

namespace LabEquipmentController.Web.Client.Services;

/// <summary>
/// What the page's assistant window stands on (<see cref="IAiService"/>): the server's
/// connections, keyless, the model asked through the server's relay, which holds the key and pins
/// the address and model (<see cref="RelayLlmEndpoint"/>), and the chats the server keeps
/// (<see cref="WebChatStore"/>). The desktop's <c>DesktopAiService</c> is the same four things
/// from the settings file, DPAPI and a folder.
///
/// Scoped, as the window's conversation is: one for the tab. AI settings applied in this tab is
/// <see cref="RaiseConnectionsChangedAsync"/>, which tells the window to pick again.
/// </summary>
public sealed class WebAiService(BenchClient bench, AiHttp ai, WebChatStore chats) : IAiService
{
    /// <summary>The connections as the server last described them, for whether each has a key.</summary>
    private IReadOnlyList<AiConnectionDto> _described = [];

    /// <summary>Whether this server takes its connections from a host with each request (service mode).</summary>
    private bool _hosted;

    public string KeptWhere => "by the bench's server";

    public IChatStore Chats => chats;

    public event Func<Task>? ConnectionsChanged;

    public async Task<AiConnections> LoadConnectionsAsync()
    {
        AiStatus status = await bench.AiStatusAsync();
        _described = status.Connections;
        _hosted = status.HostConnections;

        var book = new AiConnections();
        foreach (AiConnectionDto described in status.Connections) book.Items.Add(described.ToConnection());
        book.SelectedId = status.SelectedId;
        return book;
    }

    public async Task SelectAsync(string id)
    {
        await bench.AiSelectAsync(id);
        await RaiseConnectionsChangedAsync();
    }

    /// <summary>False when the models are a host's to give, picked and not edited here.</summary>
    public bool ModelsEditable => !_hosted;

    /// <summary>The microphone's recording, as WAV, written out by a provider that takes audio, with the words to expect.</summary>
    public async Task<(string? Text, string? Problem)> TranscribeAsync(AiConnection connection, byte[] wav, string vocabulary, CancellationToken ct)
    {
        using HttpResponseMessage response = await ai.Http.PostAsJsonAsync(
            $"api/ai/transcribe/{Uri.EscapeDataString(connection.Id)}", new AiTranscribeRequest(Convert.ToBase64String(wav), vocabulary), ct);
        AiTranscribeReply? reply = await response.Content.ReadFromJsonAsync<AiTranscribeReply>(ct);
        return reply is null ? (null, "No reply from the server.") : (reply.Text, reply.Problem);
    }

    public string? WhyNotAsk(AiConnection connection)
    {
        if (_hosted)
            return "This bench takes its AI connection from its host with each request, and the assistant cannot ask through one yet.";
        if (_described.FirstOrDefault(d => d.Id == connection.Id) is not AiConnectionDto described)
            return "That connection is gone. Pick another, or add one under AI settings…";
        return described.Configured || connection.Provider == AiProvider.OpenAiCompatible
            ? null
            : $"No API key is set on {connection.EffectiveName}. Add one under AI settings…";
    }

    public ILlmProvider CreateProvider(AiConnection connection, string effort) => CreateProvider(connection, effort, fast: false);

    /// <summary>The same, in fast mode where the model picked offers it.</summary>
    public ILlmProvider CreateProvider(AiConnection connection, string effort, bool fast)
        => LlmProviders.Create(ai.Http, new RelayLlmEndpoint(connection.Id), connection, effort, fast);

    /// <summary>AI settings was applied: the window reads the connections again.</summary>
    public async Task RaiseConnectionsChangedAsync()
    {
        if (ConnectionsChanged is Func<Task> changed) await changed();
    }

    /// <summary>Raised when an AI window asks for AI settings, which are under the gear wherever the window was opened from.</summary>
    public event Func<Task>? SettingsAsked;

    public async Task AskForSettingsAsync()
    {
        if (SettingsAsked is Func<Task> asked) await asked();
    }
}

/// <summary>
/// The chats the bench's server keeps (<c>/api/ai/chats</c>, the server's <c>AiChats</c>), sent
/// and read as <see cref="ChatStore"/> writes them, so the page's chat is the desktop's on disk.
/// </summary>
/// <param name="http">The page's client.</param>
/// <param name="kind">Which of the server's sets of chats: null for the assistant's, or the script writer's <c>script</c> or <c>sequence</c>.</param>
public sealed class WebChatStore(HttpClient http, string? kind = null) : IChatStore
{
    private readonly string _ofKind = kind is null ? "" : $"?kind={Uri.EscapeDataString(kind)}";

    public event Func<Task>? Changed;

    public async Task<List<ChatSummary>> ListAsync()
        => ChatStore.ReadList(await http.GetStringAsync("api/ai/chats" + _ofKind));

    public async Task<SavedChat?> LoadAsync(string id)
    {
        using HttpResponseMessage response = await http.GetAsync(Address(id));
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await Ensure(response);
        return ChatStore.ReadChat(await response.Content.ReadAsStringAsync());
    }

    public async Task SaveAsync(SavedChat chat)
    {
        using var content = new StringContent(ChatStore.WriteChat(chat), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await http.PutAsync(Address(chat.Id), content);
        await Ensure(response);
        await RaiseAsync();
    }

    public async Task DeleteAsync(string id)
    {
        using HttpResponseMessage response = await http.DeleteAsync(Address(id));
        await Ensure(response);
        await RaiseAsync();
    }

    private string Address(string id) => $"api/ai/chats/{Uri.EscapeDataString(id)}{_ofKind}";

    /// <summary>A refusal said in the server's own words, which are in its body as <c>{"error":{"message":…}}</c>.</summary>
    private static async Task Ensure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        string said = await response.Content.ReadAsStringAsync();
        string? why = null;
        try
        {
            why = JsonNode.Parse(said)?["error"]?["message"]?.GetValue<string>();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // Not the server's shape: a proxy's page, say. The status is what there is to go on.
        }
        throw new InvalidOperationException(why is { Length: > 0 }
            ? why
            : $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    private async Task RaiseAsync()
    {
        if (Changed is Func<Task> changed) await changed();
    }
}
