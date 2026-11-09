using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LabEquipmentController.Cli;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LabEquipmentController.Tests;

/// <summary>
/// The live link, both ends of it over a real socket: the page's <see cref="LiveLink"/> and
/// <c>lec mcp</c>'s <see cref="TabLink"/>, and the tools of the MCP server going to the page while
/// one is linked. No browser: the page's end is written so it can be driven from here.
/// </summary>
public sealed class TabLinkTests
{
    private static int FreePort()
    {
        var finding = new TcpListener(IPAddress.Loopback, 0);
        finding.Start();
        int port = ((IPEndPoint)finding.LocalEndpoint).Port;
        finding.Stop();
        return port;
    }

    private static async Task Until(Func<bool> met, string what)
    {
        for (int i = 0; i < 400; i++)
        {
            if (met()) return;
            await Task.Delay(25);
        }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    private static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static LiveLink Page(List<string> calls) => new((name, arguments, _) =>
    {
        calls.Add($"{name} {arguments}");
        return Task.FromResult(new LlmToolOutput { Text = "dmm answered: 42" });
    });

    /// <summary>Starts a page and a server on one port, and waits until the page is in line.</summary>
    private static async Task<(TabLink Tab, LiveLink Page, List<string> Calls)> Waiting()
    {
        int port = FreePort();
        var tab = new TabLink(port);
        Assert.True(tab.TryListen(out string why), why);

        var calls = new List<string>();
        LiveLink page = Page(calls);
        page.Start(port);
        await Until(() => tab.Waiting == 1, "the page to say hello");
        return (tab, page, calls);
    }

    [Fact]
    public async Task A_page_is_linked_by_its_code_and_answers_the_calls_sent_to_it()
    {
        var (tab, page, calls) = await Waiting();
        await using (tab)
        await using (page)
        {
            Assert.Equal(LiveLinkState.Waiting, page.State);

            await tab.LinkAsync(page.Code, "Claude Code", TimeSpan.FromSeconds(5), default);
            await Until(() => page.State == LiveLinkState.Linked, "the page to hear it is linked");
            Assert.Equal("Claude Code", page.Client);

            TabAnswer answer = await tab.CallAsync("query", Json("""{"command":"*IDN?"}"""), TimeSpan.FromSeconds(5), default);

            Assert.Equal("dmm answered: 42", answer.Text);
            Assert.False(answer.IsError);
            Assert.Equal("""query {"command":"*IDN?"}""", Assert.Single(calls));
            LiveLinkCall call = Assert.Single(page.Calls);
            Assert.Equal("dmm answered: 42", call.Text);
        }
    }

    /// <summary>The likeliest mistake is a digit read wrong, so with a page waiting it is said at once.</summary>
    [Fact]
    public async Task A_wrong_code_is_refused_at_once_and_links_nothing()
    {
        var (tab, page, _) = await Waiting();
        await using (tab)
        await using (page)
        {
            string wrong = page.Code.StartsWith('1') ? "2222 2222" : "1111 1111";

            var refused = await Assert.ThrowsAsync<LinkRefusal>(() => tab.LinkAsync(wrong, "Claude", TimeSpan.FromSeconds(20), default));

            Assert.Contains("with another code", refused.Message);
            Assert.False(tab.IsLinked);
            Assert.Equal(LiveLinkState.Waiting, page.State);
        }
    }

    [Theory]
    [InlineData("48217356")]
    [InlineData("4821 7356")]
    [InlineData("4821-7356")]
    public void A_code_is_its_digits_however_it_was_written(string written)
        => Assert.Equal("48217356", TabLink.Digits(written));

    [Fact]
    public void A_code_is_two_groups_of_four_digits()
        => Assert.Matches(@"^\d{4} \d{4}$", LiveLink.NewCode());

    /// <summary>Nothing is answered before the link is made: until then the page is only offering itself.</summary>
    [Fact]
    public async Task A_page_that_stops_ends_the_link_and_the_model_is_told_once()
    {
        var (tab, page, _) = await Waiting();
        await using (tab)
        await using (page)
        {
            await tab.LinkAsync(page.Code, "Claude", TimeSpan.FromSeconds(5), default);
            await Until(() => page.State == LiveLinkState.Linked, "the link");

            await page.StopAsync();
            await Until(() => !tab.IsLinked, "the server to hear the page go");

            Assert.Contains("closed the link", tab.TakeEnding());
            Assert.Null(tab.TakeEnding());
            Assert.Equal(LiveLinkState.Off, page.State);
        }
    }

    /// <summary>A link that ended stays ended: a page that went back to waiting would be offering itself to whatever said hello next.</summary>
    [Fact]
    public async Task A_link_the_server_ends_stays_ended_and_says_why()
    {
        var (tab, page, _) = await Waiting();
        await using (tab)
        await using (page)
        {
            await tab.LinkAsync(page.Code, "Claude", TimeSpan.FromSeconds(5), default);
            await Until(() => page.State == LiveLinkState.Linked, "the link");

            Assert.True(await tab.UnlinkAsync());
            await Until(() => page.State == LiveLinkState.Ended, "the page to hear it ended");

            Assert.Equal("Claude ended the link.", page.Ending);
            await Task.Delay(100);
            Assert.Equal(0, tab.Waiting);
        }
    }

    // ------------------------------------------------------------------- through the protocol

    private sealed record Served(McpClient Client, CancellationTokenSource Stopping, Task Serving) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            Stopping.Cancel();
            try { await Serving; } catch (OperationCanceledException) { }
        }
    }

    private sealed class NoBench : IAssistantBench
    {
        public Task<IReadOnlyList<BenchInstrument>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<BenchInstrument>>([]);
        public Task<CommandReference?> CatalogAsync(BenchInstrument instrument, CancellationToken ct) => Task.FromResult<CommandReference?>(null);
        public Task<string> QueryAsync(BenchInstrument instrument, string command, CancellationToken ct) => Task.FromResult("");
        public Task SendAsync(BenchInstrument instrument, string command, CancellationToken ct) => Task.CompletedTask;
        public Task<byte[]> CaptureScreenAsync(BenchInstrument instrument, CancellationToken ct) => Task.FromResult(Array.Empty<byte>());
    }

    private static async Task<Served> Serve(TabLink link, bool readOnly = false)
    {
        var tools = new BenchToolRunner(new NoBench()) { AllowEdits = !readOnly };
        var toServer = new Pipe();
        var toClient = new Pipe();
        var stopping = new CancellationTokenSource();
        Task serving = McpServing.RunAsync(tools, Commands.McpInstructions([], readOnly, linking: true),
            new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "lec"), stopping.Token, link);
        McpClient client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
        return new Served(client, stopping, serving);
    }

    private static string Text(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    [Fact]
    public async Task While_a_page_is_linked_the_tools_go_to_it_and_after_unlink_they_do_not()
    {
        var (tab, page, calls) = await Waiting();
        await using (tab)
        await using (page)
        await using (Served served = await Serve(tab))
        {
            CallToolResult linked = await served.Client.CallToolAsync(McpServing.LinkTab, new Dictionary<string, object?> { ["code"] = page.Code });
            Assert.Contains("Linked to the bench page.", Text(linked));
            Assert.Contains("dmm answered: 42", Text(linked));
            Assert.StartsWith(BenchToolRunner.ListInstruments, Assert.Single(calls));

            CallToolResult asked = await served.Client.CallToolAsync(BenchToolRunner.Query, new Dictionary<string, object?> { ["command"] = "*IDN?" });
            Assert.Equal("dmm answered: 42", Text(asked));
            Assert.Equal(2, calls.Count);

            CallToolResult ended = await served.Client.CallToolAsync(McpServing.UnlinkTab);
            Assert.Contains("is ended", Text(ended));

            await served.Client.CallToolAsync(BenchToolRunner.ListInstruments);
            Assert.Equal(2, calls.Count);
        }
    }

    /// <summary>What the model works on is said, never assumed: the call after the page went is refused with why.</summary>
    [Fact]
    public async Task The_first_call_after_the_page_went_is_refused_with_why()
    {
        var (tab, page, calls) = await Waiting();
        await using (tab)
        await using (page)
        await using (Served served = await Serve(tab))
        {
            await served.Client.CallToolAsync(McpServing.LinkTab, new Dictionary<string, object?> { ["code"] = page.Code });
            await page.StopAsync();
            await Until(() => !tab.IsLinked, "the server to hear the page go");

            CallToolResult refused = await served.Client.CallToolAsync(BenchToolRunner.ListInstruments);
            Assert.True(refused.IsError);
            Assert.Contains("closed the link", Text(refused));
            Assert.Contains("was not run", Text(refused));

            CallToolResult next = await served.Client.CallToolAsync(BenchToolRunner.ListInstruments);
            Assert.NotEqual(true, next.IsError);
            Assert.Single(calls);
        }
    }

    /// <summary>A tool --read-only left out stays out, whichever instruments it would have reached.</summary>
    [Fact]
    public async Task Read_only_keeps_send_away_from_a_linked_page()
    {
        var (tab, page, calls) = await Waiting();
        await using (tab)
        await using (page)
        await using (Served served = await Serve(tab, readOnly: true))
        {
            await served.Client.CallToolAsync(McpServing.LinkTab, new Dictionary<string, object?> { ["code"] = page.Code });
            int before = calls.Count;

            CallToolResult sent = await served.Client.CallToolAsync(BenchToolRunner.Send, new Dictionary<string, object?> { ["command"] = "*RST" });

            Assert.True(sent.IsError);
            Assert.Equal(before, calls.Count);
        }
    }

    [Fact]
    public async Task The_link_tools_are_listed_beside_the_benchs_when_the_link_is_on()
    {
        await using var tab = new TabLink(FreePort());
        await using Served served = await Serve(tab);

        IList<McpClientTool> tools = await served.Client.ListToolsAsync();

        Assert.Contains(tools, t => t.Name == McpServing.LinkTab);
        Assert.Contains(tools, t => t.Name == McpServing.UnlinkTab);
        Assert.Contains(tools, t => t.Name == BenchToolRunner.Query);
    }
}
