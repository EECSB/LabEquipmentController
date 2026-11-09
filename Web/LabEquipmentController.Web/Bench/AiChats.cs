namespace LabEquipmentController.Web.Bench;

/// <summary>
/// The page's kept chats, on this server beside <c>ai.json</c>: one bench, one set of chats, as it
/// has one set of connections and one key. The desktop's own <see cref="FileChatStore"/> underneath,
/// in <c>chats</c> under the data folder, so off a container, where that folder is the desktop's,
/// the two builds on one machine keep the same chats; in a container they live on its volume and
/// survive it being replaced.
///
/// **The script writer's are kept apart**, in a folder under that one for each of its two languages:
/// a transcript of single-instrument scripts is a poor thing to carry on in DEVICE and WITH, and
/// neither belongs in the assistant's list. A request names which with <c>?kind=</c>; none is the
/// assistant's.
///
/// The page sends a chat as <see cref="ChatStore"/> writes one and is answered the same way, so
/// what crosses the wire is what lands on disk, under the names <see cref="SavedChat"/> pins.
/// </summary>
public sealed class AiChats(AiSettingsStore settings)
{
    public FileChatStore Store { get; } = new(Path.Combine(settings.DataDirectory, "chats"));

    private readonly FileChatStore _scripts = new(Path.Combine(settings.DataDirectory, "chats", "scripts"));
    private readonly FileChatStore _sequences = new(Path.Combine(settings.DataDirectory, "chats", "sequences"));

    private FileChatStore? Of(string? kind) => kind switch
    {
        null or "" => Store,
        "script" => _scripts,
        "sequence" => _sequences,
        _ => null,
    };

    /// <summary>The list, the chat kept last first.</summary>
    public async Task<IResult> ListAsync(string? kind = null)
        => Of(kind) is FileChatStore store
            ? Results.Text(ChatStore.WriteList(await store.ListAsync()), "application/json")
            : NoSuchKind(kind);

    /// <summary>One chat, or 404 when none is kept under that id, which drops it from the list.</summary>
    public async Task<IResult> LoadAsync(string id, string? kind = null)
    {
        if (Of(kind) is not FileChatStore store) return NoSuchKind(kind);

        return await store.LoadAsync(id) is SavedChat chat
            ? Results.Text(ChatStore.WriteChat(chat), "application/json")
            : Results.NotFound();
    }

    /// <summary>
    /// Keeps a chat, new or changed. Refused when the body is not a chat, when its id is not the
    /// one in the address, and when the id could not name a file: an id comes back out of a page,
    /// and a page can say anything.
    /// </summary>
    public async Task<IResult> SaveAsync(string id, HttpRequest request, CancellationToken ct, string? kind = null)
    {
        if (Of(kind) is not FileChatStore store) return NoSuchKind(kind);

        string json;
        using (var reader = new StreamReader(request.Body))
            json = await reader.ReadToEndAsync(ct);

        if (ChatStore.ReadChat(json) is not SavedChat chat)
            return Refused("That is not a chat.");
        if (chat.Id != id)
            return Refused("The chat's id is not the one in the address.");

        try
        {
            await store.SaveAsync(chat);
        }
        catch (ArgumentException e)
        {
            return Refused(e.Message);
        }
        return Results.NoContent();
    }

    public async Task<IResult> DeleteAsync(string id, string? kind = null)
    {
        if (Of(kind) is not FileChatStore store) return NoSuchKind(kind);

        await store.DeleteAsync(id);
        return Results.NoContent();
    }

    private static IResult NoSuchKind(string? kind) => Refused($"\"{kind}\" is not a kind of chat this bench keeps.");

    private static IResult Refused(string why)
        => Results.Json(new { error = new { message = why } }, statusCode: StatusCodes.Status400BadRequest);
}
