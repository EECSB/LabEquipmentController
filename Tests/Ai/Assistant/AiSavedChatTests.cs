using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The assistant's chats as they are kept between visits: a conversation written out and read back
/// whole, the list a chats panel shows, a kept chat put back to be carried on — with the line that
/// says it was and the warning the model is sent that the instrument names were another session's —
/// and the desktop's folder of them.
///
/// What would hurt silently is a chat that reads back different from how it was written — a
/// question's files gone, so the next question no longer sends them; a proposal's script gone, so
/// its card can no longer put it in the editor — or a kept list that will not read and takes the
/// window with it. GDS Viewer's AiSavedChatTests, with the store it keeps in the browser made a
/// folder of files.
/// </summary>
public sealed class AiSavedChatTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lec-chats-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private static List<ChatTurn> Conversation()
    {
        var question = new ChatTurn { Kind = ChatTurn.User, Text = "Switch on channel 1\nand write me a script" };
        question.Attachments.Add(new AiAttachment("notes.txt", "text/plain", "C1 only"u8.ToArray()) { Excluded = true });

        return
        [
            question,
            new ChatTurn
            {
                Kind = ChatTurn.Tool,
                ToolName = "send",
                ArgumentsJson = """{"instrument":"gen","command":"C1:OUTP ON"}""",
                Changes = true,
                Decision = true,
                Result = "Sent C1:OUTP ON to gen. Its error queue is empty.",
            },
            new ChatTurn
            {
                Kind = ChatTurn.Tool,
                ToolName = "look_at_screen",
                ArgumentsJson = "{}",
                Auto = true,
                Decision = true,
                Result = "The screen of scope.",
                Png = "iVBORw0KGgo=",
            },
            new ChatTurn
            {
                Kind = ChatTurn.Tool,
                ToolName = "propose_script",
                ArgumentsJson = """{"kind":"instrument","instrument":"gen","script":"C1:OUTP ON\nDELAY 500"}""",
                Proposes = true,
                Auto = true,
                Decision = true,
                Result = "A draft for gen's script, 2 lines.",
                Proposal = "C1:OUTP ON\nDELAY 500",
            },
            new ChatTurn
            {
                Kind = ChatTurn.Assistant,
                Text = "Done.",
                Usage = new LlmUsage { InputTokens = 1000, OutputTokens = 50, Requests = 2 },
                Model = "claude-sonnet-5",
                Cost = 0.0025m,
            },
        ];
    }

    private static SavedChat Kept(List<ChatTurn> turns, DateTime? updated = null) => new()
    {
        Id = SavedChat.NewId(),
        Title = SavedChat.TitleFor(turns),
        Bench = "gen (SDG2042X), scope (DS2202)",
        Created = new DateTime(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc),
        Updated = updated ?? new DateTime(2026, 11, 7, 9, 5, 0, DateTimeKind.Utc),
        Turns = SavedChat.Keep(turns),
    };

    // --------------------------------------------------------------------------- the shape

    [Fact]
    public void A_chat_reads_back_as_it_was_written_with_its_pictures_files_proposals_and_bill()
    {
        SavedChat kept = Kept(Conversation());
        SavedChat? read = ChatStore.ReadChat(ChatStore.WriteChat(kept));

        Assert.NotNull(read);
        Assert.Equal(kept.Id, read.Id);
        Assert.Equal("Switch on channel 1 and write me a script", read.Title);
        Assert.Equal("gen (SDG2042X), scope (DS2202)", read.Bench);
        Assert.Equal(kept.Updated, read.Updated);

        List<ChatTurn> turns = read.Unkept();
        Assert.Equal(new[] { ChatTurn.User, ChatTurn.Tool, ChatTurn.Tool, ChatTurn.Tool, ChatTurn.Assistant }, turns.Select(turn => turn.Kind));

        AiAttachment file = Assert.Single(turns[0].Attachments);
        Assert.Equal("notes.txt", file.Name);
        Assert.Equal("text/plain", file.MediaType);
        Assert.Equal("C1 only"u8.ToArray(), file.Data);
        Assert.True(file.Excluded);

        ChatTurn send = turns[1];
        Assert.Equal("send", send.ToolName);
        Assert.Equal("""{"instrument":"gen","command":"C1:OUTP ON"}""", send.ArgumentsJson);
        Assert.True(send.Changes);
        Assert.False(send.Auto);
        Assert.True(send.Decision);
        Assert.StartsWith("Sent C1:OUTP ON", send.Result);

        Assert.Equal("iVBORw0KGgo=", turns[2].Png);

        ChatTurn proposal = turns[3];
        Assert.True(proposal.Proposes);
        Assert.Equal("C1:OUTP ON\nDELAY 500", proposal.Proposal);

        ChatTurn answer = turns[4];
        Assert.Equal(1000, answer.Usage!.InputTokens);
        Assert.Equal(2, answer.Usage.Requests);
        Assert.True(answer.Usage.Reported);
        Assert.Equal("claude-sonnet-5", answer.Model);
        Assert.Equal(0.0025m, answer.Cost);
    }

    /// <summary>A card still waiting when it was kept has nothing to let it go afterwards: it comes back declined, as a stop leaves one — a send nobody approved was never sent.</summary>
    [Fact]
    public void A_card_kept_while_it_waited_comes_back_answered_no()
    {
        var waiting = new ChatTurn { Kind = ChatTurn.Tool, ToolName = "send", Changes = true, Gate = new TaskCompletionSource<bool>() };

        ChatTurn read = SavedTurn.From(waiting).ToTurn();

        Assert.False(read.Decision);
        Assert.Null(read.Gate);
    }

    /// <summary>The names a chat is written under are pinned: renamed, a chat kept by an earlier version would read as empty.</summary>
    [Fact]
    public void The_names_a_chat_is_written_under_are_pinned()
    {
        JsonObject chat = JsonNode.Parse(ChatStore.WriteChat(Kept(Conversation())))!.AsObject();

        Assert.Equal(new[] { "id", "title", "bench", "created", "updated", "turns" }, chat.Select(pair => pair.Key));
        Assert.Equal(
            new[] { "kind", "text", "tool", "arguments", "changes", "proposes", "auto", "decision", "result", "resultIsError", "png", "proposal", "files", "usage", "model", "cost" },
            chat["turns"]![0]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(new[] { "name", "type", "data", "excluded" }, chat["turns"]![0]!["files"]![0]!.AsObject().Select(pair => pair.Key));

        // Kept with its bytes, as base64.
        Assert.Equal(Convert.ToBase64String("C1 only"u8.ToArray()), (string?)chat["turns"]![0]!["files"]![0]!["data"]);
    }

    [Fact]
    public void A_title_is_the_first_question_on_one_line_cut_to_a_row()
    {
        List<ChatTurn> turns =
        [
            new ChatTurn { Kind = ChatTurn.Note, Text = "A note first" },
            new ChatTurn { Kind = ChatTurn.User, Text = "  What   does\n the meter read?  " },
            new ChatTurn { Kind = ChatTurn.User, Text = "And the supply?" },
        ];

        Assert.Equal("What does the meter read?", SavedChat.TitleFor(turns));
        Assert.Equal("New chat", SavedChat.TitleFor([]));

        string title = SavedChat.TitleFor([new ChatTurn { Kind = ChatTurn.User, Text = new string('a', 200) }]);
        Assert.Equal(SavedChat.MostTitleCharacters + 3, title.Length);
        Assert.EndsWith("...", title);
    }

    [Fact]
    public void The_list_reads_back_changed_last_first_and_text_that_will_not_read_is_no_chats()
    {
        var older = new ChatSummary { Id = "a", Title = "Older", Updated = new DateTime(2026, 11, 6) };
        var newer = new ChatSummary { Id = "b", Title = "Newer", Updated = new DateTime(2026, 11, 7) };

        List<ChatSummary> read = ChatStore.ReadList(ChatStore.WriteList(ChatStore.Ordered([older, newer])));

        Assert.Equal(new[] { "b", "a" }, read.Select(each => each.Id));
        Assert.Empty(ChatStore.ReadList(null));
        Assert.Empty(ChatStore.ReadList("not json"));
        Assert.Null(ChatStore.ReadChat("{"));
        Assert.Null(ChatStore.ReadChat(""));
    }

    // ------------------------------------------------------------------------- put back

    /// <summary>
    /// **Put back and carried on**: the turns are the window's again, with one line saying it was
    /// opened — not two, however often — and the model is sent all of it, the line and the warning
    /// about instrument names with it. A note would have cut it off there, as the bench changing does;
    /// a reopened chat is opened to be carried on.
    /// </summary>
    [Fact]
    public void A_kept_chat_put_back_says_so_once_and_the_model_is_sent_all_of_it_with_the_warning()
    {
        var chat = new AiChat();

        chat.Restore(Kept(Conversation()).Unkept(), "Opened again.");

        Assert.Equal(6, chat.Turns.Count);
        Assert.Equal(ChatTurn.Reopened, chat.Turns[^1].Kind);

        chat.Restore(SavedChat.Keep(chat.Turns).Select(turn => turn.ToTurn()), "Opened again with another bench open - it was about gen and scope.");

        Assert.Equal(6, chat.Turns.Count);
        Assert.Equal("Opened again with another bench open - it was about gen and scope.", chat.Turns[^1].Text);
        Assert.Equal(0, chat.FoldedBefore);

        List<LlmMessage> sent = chat.BuildConversation();
        string said = string.Join("\n", sent.Select(message => message.Text));

        Assert.Contains("Switch on channel 1", said);
        Assert.Contains("Done.", said);
        Assert.Contains("You called send", said);
        Assert.Contains("You proposed a script of 2 lines for gen", said);
        Assert.Contains("it was about gen and scope", said);
        Assert.Contains(AiChat.ReopenedWarning, said);
        Assert.Equal(LlmMessage.UserRole, sent[^1].Role);
    }

    /// <summary>The warning says what is true of instrument names: they are given from what is open, so the same name may now be another instrument, or none.</summary>
    [Fact]
    public void The_warning_says_to_list_the_instruments_again()
    {
        Assert.Contains("may no longer be open or name the same instrument", AiChat.ReopenedWarning);
        Assert.Contains("list the instruments again before using one", AiChat.ReopenedWarning);
    }

    /// <summary>Put back only to be written out as text, it says nothing about being opened.</summary>
    [Fact]
    public void A_kept_chat_put_back_with_nothing_to_say_has_no_line_and_its_transcript_reads_as_it_did()
    {
        var chat = new AiChat();

        chat.Restore(Kept(Conversation()).Unkept(), null);

        Assert.Equal(5, chat.Turns.Count);

        string text = chat.Transcript("gen, scope", new DateTime(2026, 11, 7, 9, 10, 0)).Replace("\r\n", "\n");

        Assert.Contains("YOU\nSwitch on channel 1", text);
        Assert.Contains("ASSISTANT\nDone.", text);
        Assert.DoesNotContain("Opened again", text);
    }

    /// <summary>A kept question's files are kept with their bytes, so a chat carried on sends them with its next question as it did before.</summary>
    [Fact]
    public async Task A_kept_chat_carried_on_sends_its_files_again()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var question = new ChatTurn { Kind = ChatTurn.User, Text = "What is on the scope?" };
        question.Attachments.Add(new AiAttachment("screen.png", "image/png", png));
        List<ChatTurn> turns = [question, new ChatTurn { Kind = ChatTurn.Assistant, Text = "A sine." }];

        SavedChat? read = ChatStore.ReadChat(ChatStore.WriteChat(Kept(turns)));
        var chat = new AiChat();
        chat.Restore(read!.Unkept(), "Opened again.");

        var standIn = new AiStandIn(AiStandIn.AnthropicText("Its amplitude is 2 V."));
        var connection = new AiConnection { Provider = AiProvider.Anthropic };
        await chat.SendAsync("And its amplitude?", new AnthropicProvider(standIn.Client(), new DirectLlmEndpoint(connection, "k"), connection), "system", null)
                  .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Contains(Convert.ToBase64String(png), standIn.Requests[0].Body.ToJsonString());
    }

    // ------------------------------------------------------------------- the desktop's folder

    /// <summary>
    /// Chats kept in a folder read back as they were, the list newest first, and every save and delete
    /// is announced — each file written beside its target and moved over it, so none is left behind.
    /// </summary>
    [Fact]
    public async Task Chats_kept_in_a_folder_read_back_newest_first_and_say_when_they_change()
    {
        var store = new FileChatStore(_folder);
        int changed = 0;
        store.Changed += () =>
        {
            Interlocked.Increment(ref changed);
            return Task.CompletedTask;
        };

        Assert.Empty(await store.ListAsync());

        SavedChat first = Kept(Conversation(), new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc));
        SavedChat second = Kept([new ChatTurn { Kind = ChatTurn.User, Text = "What does the meter read?" }], new DateTime(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc));

        await store.SaveAsync(second);
        await store.SaveAsync(first);

        List<ChatSummary> list = await store.ListAsync();
        Assert.Equal(new[] { second.Id, first.Id }, list.Select(each => each.Id));
        Assert.Equal("What does the meter read?", list[0].Title);
        Assert.Equal("gen (SDG2042X), scope (DS2202)", list[1].Bench);
        Assert.Equal(2, changed);

        SavedChat? read = await store.LoadAsync(first.Id);
        Assert.NotNull(read);
        Assert.Equal(5, read.Turns.Count);
        Assert.Equal("C1:OUTP ON\nDELAY 500", read.Unkept()[3].Proposal);

        // Kept again under the same id: one row, its new title.
        first.Title = "Renamed";
        await store.SaveAsync(first);
        Assert.Equal(new[] { "Renamed", "What does the meter read?" }, (await store.ListAsync()).OrderBy(each => each.Title, StringComparer.Ordinal).Select(each => each.Title));
        Assert.Equal(2, (await store.ListAsync()).Count);

        Assert.True(File.Exists(Path.Combine(_folder, FileChatStore.ListFileName)));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public async Task Deleting_a_chat_takes_its_file_and_its_row()
    {
        var store = new FileChatStore(_folder);
        SavedChat chat = Kept(Conversation());

        await store.SaveAsync(chat);
        await store.DeleteAsync(chat.Id);

        Assert.Empty(await store.ListAsync());
        Assert.Null(await store.LoadAsync(chat.Id));
        Assert.Equal(new[] { FileChatStore.ListFileName }, Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    /// <summary>A row whose chat is gone — deleted by hand, or by another window — opens nothing, and is dropped from the list when it is tried.</summary>
    [Fact]
    public async Task A_row_whose_chat_is_gone_is_dropped_when_it_is_opened()
    {
        var store = new FileChatStore(_folder);
        SavedChat chat = Kept(Conversation());

        await store.SaveAsync(chat);
        foreach (string file in Directory.GetFiles(_folder, "chat-*.json")) File.Delete(file);

        Assert.Single(await store.ListAsync());
        Assert.Null(await store.LoadAsync(chat.Id));
        Assert.Empty(await store.ListAsync());
    }

    /// <summary>A chat file that will not read is no chat, and a list that will not read is no chats — neither takes the window with it.</summary>
    [Fact]
    public async Task Files_that_will_not_read_are_no_chats_rather_than_an_error()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, FileChatStore.ListFileName), "half a list [");
        var store = new FileChatStore(_folder);

        Assert.Empty(await store.ListAsync());

        SavedChat chat = Kept(Conversation());
        await store.SaveAsync(chat);
        File.WriteAllText(Directory.GetFiles(_folder, "chat-*.json").Single(), "{ not a chat");

        Assert.Null(await store.LoadAsync(chat.Id));
    }

    /// <summary>An id names a file, and is read back out of one: one that would reach outside the folder is refused, and never read.</summary>
    [Fact]
    public async Task An_id_that_cannot_name_a_file_in_the_folder_is_refused()
    {
        var store = new FileChatStore(_folder);
        SavedChat chat = Kept(Conversation());
        chat.Id = @"..\..\settings";

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(chat));
        Assert.Null(await store.LoadAsync(@"..\..\settings"));
        Assert.Null(await store.LoadAsync(""));
    }

    [Fact]
    public void The_desktop_keeps_its_chats_beside_its_settings()
        => Assert.Equal(Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath)!, "chats"), FileChatStore.DefaultFolder);
}
