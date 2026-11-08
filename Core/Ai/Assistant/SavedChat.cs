using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace LabEquipmentController;

/// <summary>
/// **A conversation as it is kept between visits** — the assistant window's chats list, as the
/// Claude app has one and GDS Viewer after it: every question, answer and call, so a chat can be
/// opened again and carried on, read back or written out as text. Kept by an
/// <see cref="IChatStore"/> — on the desktop, a file a chat under %AppData% (<see cref="FileChatStore"/>).
///
/// **Its own shape rather than the window's turns written out**: a turn holds things that only mean
/// anything while the window is up — the gate a waiting card is let go by, a file's place in the box —
/// and the names written here are pinned, so a chat kept by an earlier version still reads. See
/// <see cref="SavedTurn"/>.
/// </summary>
public sealed class SavedChat
{
    /// <summary>How long a title is before it is cut — about a row of the chats list.</summary>
    public const int MostTitleCharacters = 80;

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>What the list calls it: the first question, cut to a row.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>
    /// The bench it was about when it was last kept, said in a line by the window — its instruments by
    /// the names the tools gave them, say — for the list to show, and for the line a reopened chat
    /// starts with to say when the bench open now is another. Where GDS Viewer keeps the name of the
    /// layout that was open.
    /// </summary>
    [JsonPropertyName("bench")]
    public string Bench { get; set; } = "";

    [JsonPropertyName("created")]
    public DateTime Created { get; set; }

    [JsonPropertyName("updated")]
    public DateTime Updated { get; set; }

    [JsonPropertyName("turns")]
    public List<SavedTurn> Turns { get; set; } = [];

    /// <summary>A new id, unique enough for one person's chats wherever they are kept, and safe in a file's name.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>The row the list shows for it.</summary>
    public ChatSummary Summary() => new() { Id = Id, Title = Title, Bench = Bench, Updated = Updated };

    /// <summary>
    /// What a conversation is called: its first question, on one line and cut to
    /// <see cref="MostTitleCharacters"/>. "New chat" when nothing has been asked yet, which the list
    /// never shows, since a chat is kept from its first question on.
    /// </summary>
    public static string TitleFor(IEnumerable<ChatTurn> turns)
    {
        ChatTurn? first = turns.FirstOrDefault(turn => turn.Kind == ChatTurn.User);
        if (first is null) return "New chat";

        string flat = string.Join(" ", first.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= MostTitleCharacters ? flat : flat[..MostTitleCharacters].TrimEnd() + "...";
    }

    /// <summary>A conversation's turns, kept. The window's own are left as they are.</summary>
    public static List<SavedTurn> Keep(IEnumerable<ChatTurn> turns) => [.. turns.Select(SavedTurn.From)];

    /// <summary>The turns again, to put back into a conversation — see <see cref="AiChat.Restore"/>.</summary>
    public List<ChatTurn> Unkept() => [.. Turns.Select(turn => turn.ToTurn())];
}

/// <summary>One row of the chats list: enough to name a chat and order it, without reading every turn of every chat.</summary>
public sealed class ChatSummary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("bench")]
    public string Bench { get; set; } = "";

    [JsonPropertyName("updated")]
    public DateTime Updated { get; set; }
}

/// <summary>
/// One <see cref="ChatTurn"/> as it is kept. A card that was still waiting when it was kept comes back
/// answered no — nothing can let it go once the window that held it is gone — which is what a stop
/// does to one, and on a bench the safe reading: a send nobody approved was never sent.
/// </summary>
public sealed class SavedTurn
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = ChatTurn.Note;

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("tool")]
    public string ToolName { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string ArgumentsJson { get; set; } = "";

    [JsonPropertyName("changes")]
    public bool Changes { get; set; }

    [JsonPropertyName("proposes")]
    public bool Proposes { get; set; }

    [JsonPropertyName("auto")]
    public bool Auto { get; set; }

    [JsonPropertyName("decision")]
    public bool? Decision { get; set; }

    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("resultIsError")]
    public bool ResultIsError { get; set; }

    [JsonPropertyName("png")]
    public string? Png { get; set; }

    /// <summary>The script a proposal put forward, kept so its card can still put it in the editor.</summary>
    [JsonPropertyName("proposal")]
    public string? Proposal { get; set; }

    [JsonPropertyName("files")]
    public List<SavedFile> Files { get; set; } = [];

    [JsonPropertyName("usage")]
    public LlmUsage? Usage { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("cost")]
    public decimal? Cost { get; set; }

    public static SavedTurn From(ChatTurn turn)
    {
        var kept = new SavedTurn
        {
            Kind = turn.Kind,
            Text = turn.Text,
            ToolName = turn.ToolName,
            ArgumentsJson = turn.ArgumentsJson,
            Changes = turn.Changes,
            Proposes = turn.Proposes,
            Auto = turn.Auto,
            Decision = turn.Kind == ChatTurn.Tool ? turn.Decision ?? false : turn.Decision,
            Result = turn.Result,
            ResultIsError = turn.ResultIsError,
            Png = turn.Png,
            Proposal = turn.Proposal,
            Usage = turn.Usage?.Copy(),
            Model = turn.Model,
            Cost = turn.Cost,
        };

        foreach (AiAttachment file in turn.Attachments)
            kept.Files.Add(new SavedFile { Name = file.Name, MediaType = file.MediaType, Data = file.Data, Excluded = file.Excluded });

        return kept;
    }

    public ChatTurn ToTurn()
    {
        var turn = new ChatTurn
        {
            Kind = Kind,
            Text = Text,
            ToolName = ToolName,
            ArgumentsJson = ArgumentsJson,
            Changes = Changes,
            Proposes = Proposes,
            Auto = Auto,
            Decision = Decision,
            Result = Result,
            ResultIsError = ResultIsError,
            Png = Png,
            Proposal = Proposal,
            Usage = Usage?.Copy(),
            Model = Model,
            Cost = Cost,
        };

        foreach (SavedFile file in Files)
            turn.Attachments.Add(new AiAttachment(file.Name, file.MediaType, file.Data) { Excluded = file.Excluded });

        return turn;
    }
}

/// <summary>
/// A file a question was sent with, **kept with its bytes**, so it goes with the questions after it
/// as it did before — a chat whose screenshot was only named would be carried on without it, and the
/// model would be asked about a picture it can no longer see.
/// </summary>
public sealed class SavedFile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string MediaType { get; set; } = "";

    [JsonPropertyName("data")]
    public byte[] Data { get; set; } = [];

    [JsonPropertyName("excluded")]
    public bool Excluded { get; set; }
}
