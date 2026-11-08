namespace LabEquipmentController.Web.Bench;

/// <summary>
/// The page's kept assistant chats, on this server beside <c>ai.json</c>: one bench, one set of
/// chats, as it has one set of connections and one key. The desktop's own
/// <see cref="FileChatStore"/> underneath, in <c>chats</c> under the data folder, so off a
/// container, where that folder is the desktop's, the two builds on one machine keep the same
/// chats; in a container they live on its volume and survive it being replaced.
///
/// The page sends a chat as <see cref="ChatStore"/> writes one and is answered the same way, so
/// what crosses the wire is what lands on disk, under the names <see cref="SavedChat"/> pins.
/// </summary>
public sealed class AiChats(AiSettingsStore settings)
{
    public FileChatStore Store { get; } = new(Path.Combine(settings.DataDirectory, "chats"));

    /// <summary>The list, the chat kept last first.</summary>
    public async Task<IResult> ListAsync()
        => Results.Text(ChatStore.WriteList(await Store.ListAsync()), "application/json");

    /// <summary>One chat, or 404 when none is kept under that id, which drops it from the list.</summary>
    public async Task<IResult> LoadAsync(string id)
        => await Store.LoadAsync(id) is SavedChat chat
            ? Results.Text(ChatStore.WriteChat(chat), "application/json")
            : Results.NotFound();

    /// <summary>
    /// Keeps a chat, new or changed. Refused when the body is not a chat, when its id is not the
    /// one in the address, and when the id could not name a file: an id comes back out of a page,
    /// and a page can say anything.
    /// </summary>
    public async Task<IResult> SaveAsync(string id, HttpRequest request, CancellationToken ct)
    {
        string json;
        using (var reader = new StreamReader(request.Body))
            json = await reader.ReadToEndAsync(ct);

        if (ChatStore.ReadChat(json) is not SavedChat chat)
            return Refused("That is not a chat.");
        if (chat.Id != id)
            return Refused("The chat's id is not the one in the address.");

        try
        {
            await Store.SaveAsync(chat);
        }
        catch (ArgumentException e)
        {
            return Refused(e.Message);
        }
        return Results.NoContent();
    }

    public async Task<IResult> DeleteAsync(string id)
    {
        await Store.DeleteAsync(id);
        return Results.NoContent();
    }

    private static IResult Refused(string why)
        => Results.Json(new { error = new { message = why } }, statusCode: StatusCodes.Status400BadRequest);
}
