using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// Where the assistant's chats are kept, for a window to list, open, keep and throw away. The chat
/// half of GDS Viewer's <c>IAiService</c>: the models half is LEC's AI connections, which both builds
/// already keep. The desktop's is <see cref="FileChatStore"/>; a build that keeps them elsewhere —
/// the page, with its server — writes one over the same <see cref="ChatStore"/> text.
/// </summary>
public interface IChatStore
{
    /// <summary>The chats kept, the one changed last first. Never null.</summary>
    Task<List<ChatSummary>> ListAsync();

    /// <summary>A chat by its id, or null when there is none kept under it — deleted from another window, say — in which case the list stops naming it.</summary>
    Task<SavedChat?> LoadAsync(string id);

    /// <summary>Keeps a chat, new or changed, puts it at the top of the list, and raises <see cref="Changed"/>.</summary>
    Task SaveAsync(SavedChat chat);

    /// <summary>Throws a chat away, and raises <see cref="Changed"/>.</summary>
    Task DeleteAsync(string id);

    /// <summary>Raised after a chat is kept or thrown away, so a list shows it — from whichever thread kept it.</summary>
    event Func<Task>? Changed;
}

/// <summary>
/// How a kept chat and the list of them are written, for any store: JSON under the names
/// <see cref="SavedChat"/> pins, by reflection as the rest of Core writes its JSON — the catalogs load
/// that way in the trimmed web page too.
///
/// **Text that will not read is no chats, or no chat**, rather than an error: a list that throws takes
/// the window with it, where a missing chat is one line in it gone.
/// </summary>
public static class ChatStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>The list in the order it is shown: changed last first.</summary>
    public static List<ChatSummary> Ordered(IEnumerable<ChatSummary> list) => [.. list.OrderByDescending(each => each.Updated)];

    /// <summary>The list out of its stored text.</summary>
    public static List<ChatSummary> ReadList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<ChatSummary>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string WriteList(List<ChatSummary> list) => JsonSerializer.Serialize(list, Options);

    /// <summary>A chat out of its stored text, or null when there is none or it will not read.</summary>
    public static SavedChat? ReadChat(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<SavedChat>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string WriteChat(SavedChat chat) => JsonSerializer.Serialize(chat, Options);
}

/// <summary>
/// The chats kept in a folder: one file a chat, and a list beside them — the desktop's, under
/// <see cref="DefaultFolder"/>.
///
/// **A list apart from the chats**, as GDS Viewer keeps them: the list is read whenever the window
/// shows it, and reading every chat whole to name it would read every screenshot in every one. **The
/// chat is written before the list that names it**, so a write cut short leaves a chat the list does
/// not name rather than a row that opens nothing — and a row whose chat is gone anyway is dropped when
/// it is opened.
///
/// **Every file is written beside its target and moved over it**, so a write cut short — the machine
/// off mid-save — leaves the old file whole rather than half of a new one, which would read as no chats
/// at all. One call at a time in this process; two processes keeping chats in one folder could each
/// miss the other's newest row, never a chat.
/// </summary>
public sealed class FileChatStore : IChatStore
{
    public const string ListFileName = "chats.json";

    private const string ChatFilePrefix = "chat-";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileChatStore(string folder) => Folder = folder;

    /// <summary>%AppData%\LabEquipmentController\chats, beside the settings file.</summary>
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LabEquipmentController", "chats");

    public string Folder { get; }

    public event Func<Task>? Changed;

    public async Task<List<ChatSummary>> ListAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ReadListAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SavedChat?> LoadAsync(string id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            SavedChat? chat = PathOf(id) is string path && File.Exists(path)
                ? ChatStore.ReadChat(await File.ReadAllTextAsync(path).ConfigureAwait(false))
                : null;

            if (chat is null) await DropAsync(id).ConfigureAwait(false);
            return chat;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(SavedChat chat)
    {
        string path = PathOf(chat.Id)
            ?? throw new ArgumentException($"A chat's id names its file, and \"{chat.Id}\" cannot: letters, digits, - and _ only.", nameof(chat));

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Folder);
            await WriteBesideAsync(path, ChatStore.WriteChat(chat)).ConfigureAwait(false);

            List<ChatSummary> list = await ReadListAsync().ConfigureAwait(false);
            list.RemoveAll(each => each.Id == chat.Id);
            list.Insert(0, chat.Summary());
            await WriteBesideAsync(ListPath, ChatStore.WriteList(ChatStore.Ordered(list))).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        await RaiseChangedAsync().ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DropAsync(id).ConfigureAwait(false);
            if (PathOf(id) is string path && File.Exists(path)) File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }

        await RaiseChangedAsync().ConfigureAwait(false);
    }

    private string ListPath => Path.Combine(Folder, ListFileName);

    /// <summary>
    /// The file a chat is kept in, or null for an id that cannot name one. An id is only ever this
    /// class's own <see cref="SavedChat.NewId"/> in practice, but it is read back out of a file, and a
    /// file can say anything — "..\..\settings" included.
    /// </summary>
    private string? PathOf(string? id)
        => id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? Path.Combine(Folder, ChatFilePrefix + id + ".json")
            : null;

    private async Task<List<ChatSummary>> ReadListAsync()
        => File.Exists(ListPath) ? ChatStore.ReadList(await File.ReadAllTextAsync(ListPath).ConfigureAwait(false)) : [];

    private async Task DropAsync(string id)
    {
        List<ChatSummary> list = await ReadListAsync().ConfigureAwait(false);
        if (list.RemoveAll(each => each.Id == id) > 0)
            await WriteBesideAsync(ListPath, ChatStore.WriteList(list)).ConfigureAwait(false);
    }

    /// <summary>Writes <paramref name="text"/> to a file of its own beside <paramref name="path"/>, then moves it over the target in one step.</summary>
    private static async Task WriteBesideAsync(string path, string text)
    {
        string beside = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(beside, text).ConfigureAwait(false);
            File.Move(beside, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(beside); } catch { /* best effort: the write's own failure is the one to report */ }
            throw;
        }
    }

    private async Task RaiseChangedAsync()
    {
        if (Changed is not Func<Task> changed) return;

        foreach (Func<Task> listener in changed.GetInvocationList().Cast<Func<Task>>())
            await listener().ConfigureAwait(false);
    }
}
