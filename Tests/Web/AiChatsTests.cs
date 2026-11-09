using System.Text;
using System.Text.Json.Nodes;
using LabEquipmentController.Web.Bench;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabEquipmentController.Tests;

/// <summary>
/// The page's kept assistant chats, on its server (<c>/api/ai/chats</c>): kept with their files
/// beside <c>ai.json</c>, listed the one kept last first, read back as they were written, thrown
/// away, and refused when what is sent is not a chat, names another, or names no file.
/// </summary>
public sealed class AiChatsTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "lec-chats-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch { /* a temp folder is not worth a failure */ }
    }

    [Fact]
    public async Task A_chat_is_kept_beside_the_settings_and_read_back_whole()
    {
        AiChats chats = Chats();
        SavedChat chat = Chat("What does the meter read?", new DateTime(2026, 11, 7, 18, 0, 0));
        chat.Turns[0].Files.Add(new SavedFile { Name = "screen.png", MediaType = "image/png", Data = [1, 2, 3] });

        var (status, _) = await Run(c => chats.SaveAsync(chat.Id, c.Request, CancellationToken.None), ChatStore.WriteChat(chat));
        Assert.Equal(204, status);
        Assert.True(File.Exists(Path.Combine(_data, "chats", FileChatStore.ListFileName)));

        var (read, body) = await Run(_ => chats.LoadAsync(chat.Id));
        Assert.Equal(200, read);
        SavedChat again = ChatStore.ReadChat(body)!;
        Assert.Equal("What does the meter read?", again.Title);
        Assert.Equal("dmm SDM3065X", again.Bench);
        Assert.Equal([1, 2, 3], again.Turns[0].Files[0].Data);
    }

    [Fact]
    public async Task The_list_names_the_chat_kept_last_first()
    {
        AiChats chats = Chats();
        SavedChat older = Chat("First", new DateTime(2026, 11, 6, 9, 0, 0));
        SavedChat newer = Chat("Second", new DateTime(2026, 11, 7, 9, 0, 0));
        await Run(c => chats.SaveAsync(newer.Id, c.Request, CancellationToken.None), ChatStore.WriteChat(newer));
        await Run(c => chats.SaveAsync(older.Id, c.Request, CancellationToken.None), ChatStore.WriteChat(older));

        var (status, body) = await Run(_ => chats.ListAsync());

        Assert.Equal(200, status);
        Assert.Equal(["Second", "First"], ChatStore.ReadList(body).Select(c => c.Title));
    }

    [Fact]
    public async Task A_chat_thrown_away_is_gone_from_the_list_and_answers_not_found()
    {
        AiChats chats = Chats();
        SavedChat chat = Chat("Gone soon", DateTime.Now);
        await Run(c => chats.SaveAsync(chat.Id, c.Request, CancellationToken.None), ChatStore.WriteChat(chat));

        var (deleted, _) = await Run(_ => chats.DeleteAsync(chat.Id));
        var (read, _) = await Run(_ => chats.LoadAsync(chat.Id));
        var (_, list) = await Run(_ => chats.ListAsync());

        Assert.Equal(204, deleted);
        Assert.Equal(404, read);
        Assert.Empty(ChatStore.ReadList(list));
    }

    [Fact]
    public async Task What_is_not_a_chat_or_names_another_is_refused_in_the_relays_error_shape()
    {
        AiChats chats = Chats();
        SavedChat chat = Chat("Mine", DateTime.Now);

        var (notAChat, said) = await Run(c => chats.SaveAsync(chat.Id, c.Request, CancellationToken.None), "not json at all");
        var (another, _) = await Run(c => chats.SaveAsync("someone-else", c.Request, CancellationToken.None), ChatStore.WriteChat(chat));

        Assert.Equal(400, notAChat);
        Assert.Equal("That is not a chat.", (string?)JsonNode.Parse(said)!["error"]!["message"]);
        Assert.Equal(400, another);
        Assert.False(Directory.Exists(Path.Combine(_data, "chats")) && Directory.EnumerateFiles(Path.Combine(_data, "chats"), "chat-*").Any());
    }

    [Fact]
    public async Task An_id_that_would_leave_the_folder_names_no_file_and_is_refused()
    {
        AiChats chats = Chats();
        SavedChat chat = Chat("Escape", DateTime.Now);
        chat.Id = @"..\..\settings";

        var (status, _) = await Run(c => chats.SaveAsync(chat.Id, c.Request, CancellationToken.None), ChatStore.WriteChat(chat));
        var (read, _) = await Run(_ => chats.LoadAsync(@"..\..\ai"));

        Assert.Equal(400, status);
        Assert.Equal(404, read);
        Assert.False(File.Exists(Path.Combine(_data, "settings.json")));
    }

    // ------------------------------------------------------------------- helpers

    /// <summary>A transcript of single-instrument scripts is a poor thing to carry on in DEVICE and WITH, and neither belongs in the assistant's list.</summary>
    [Fact]
    public async Task The_script_writers_chats_are_kept_apart_from_the_assistants_and_from_each_other()
    {
        AiChats chats = Chats();
        SavedChat assistant = Chat("About the bench", new DateTime(2026, 11, 7, 9, 0, 0));
        SavedChat script = Chat("A single-instrument script", new DateTime(2026, 11, 7, 10, 0, 0));
        SavedChat sequence = Chat("A multi-instrument script", new DateTime(2026, 11, 7, 11, 0, 0));

        await Run(c => chats.SaveAsync(assistant.Id, c.Request, CancellationToken.None), ChatStore.WriteChat(assistant));
        await Run(c => chats.SaveAsync(script.Id, c.Request, CancellationToken.None, "script"), ChatStore.WriteChat(script));
        await Run(c => chats.SaveAsync(sequence.Id, c.Request, CancellationToken.None, "sequence"), ChatStore.WriteChat(sequence));

        Assert.Equal("About the bench", Assert.Single(ChatStore.ReadList((await Run(_ => chats.ListAsync())).Body)).Title);
        Assert.Equal("A single-instrument script", Assert.Single(ChatStore.ReadList((await Run(_ => chats.ListAsync("script"))).Body)).Title);
        Assert.Equal("A multi-instrument script", Assert.Single(ChatStore.ReadList((await Run(_ => chats.ListAsync("sequence"))).Body)).Title);

        Assert.Equal(404, (await Run(_ => chats.LoadAsync(script.Id))).Status);
        Assert.Equal(200, (await Run(_ => chats.LoadAsync(script.Id, "script"))).Status);

        Assert.Equal(204, (await Run(_ => chats.DeleteAsync(script.Id, "script"))).Status);
        Assert.Empty(ChatStore.ReadList((await Run(_ => chats.ListAsync("script"))).Body));
        Assert.Single(ChatStore.ReadList((await Run(_ => chats.ListAsync())).Body));
    }

    [Fact]
    public async Task A_kind_of_chat_there_is_no_such_thing_as_is_refused()
    {
        var (status, body) = await Run(_ => Chats().ListAsync("../../secrets"));

        Assert.Equal(400, status);
        Assert.Contains("kind of chat", body);
    }

    private AiChats Chats() => new(new AiSettingsStore(
        new AiOptions(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LEC_DATA"] = _data }).Build(),
        NullLogger<AiSettingsStore>.Instance));

    private static SavedChat Chat(string title, DateTime updated) => new()
    {
        Id = SavedChat.NewId(),
        Title = title,
        Bench = "dmm SDM3065X",
        Created = updated,
        Updated = updated,
        Turns = [new SavedTurn { Kind = ChatTurn.User, Text = title }, new SavedTurn { Kind = ChatTurn.Assistant, Text = "An answer." }],
    };

    private static async Task<(int Status, string Body)> Run(Func<HttpContext, Task<IResult>> endpoint, string? json = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider(),
        };
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json ?? ""));
        context.Response.Body = new MemoryStream();

        IResult result = await endpoint(context);
        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }
}
