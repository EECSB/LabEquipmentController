using System.Buffers;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LabEquipmentController.Cli;

/// <summary>
/// Puts the bench tools on the Model Context Protocol: their list, and their calls answered.
///
/// Transport and nothing else. The tools, their schemas and every answer are
/// <see cref="BenchToolRunner"/>'s, so what Claude Code is told through <c>lec mcp</c> is word
/// for word what the app's assistant window is told — the AI guide's "the same tools, word for
/// word", which is what stops the two drifting apart.
///
/// The SDK's low-level handlers rather than its attributed methods, as GDS Viewer's <c>gds mcp</c>
/// does it: those build a tool out of a C# method, and these tools already exist, with schemas
/// written from the same parameter list their arguments are read against.
/// </summary>
public static class McpServing
{
    /// <summary>The live link's tools, offered beside the bench's while it is on.</summary>
    public const string LinkTab = "link_tab";
    public const string UnlinkTab = "unlink_tab";

    /// <summary>How long link_tab waits for a page when none has said hello yet.</summary>
    public static readonly TimeSpan LinkWait = TimeSpan.FromSeconds(20);

    /// <summary>How long a call sent to a page is waited for: a screen capture and a wait of a minute both fit.</summary>
    public static readonly TimeSpan CallWithin = TimeSpan.FromMinutes(2);

    private static readonly Tool LinkTabTool = new()
    {
        Name = LinkTab,
        Description = "Links these tools to a bench page, so they work the instruments the person has open in it, as they watch. "
                    + "The person starts it on the bench page, under MCP server in AI settings under the gear, which shows a code: ask them for it. "
                    + "Waits up to 20 seconds for the page. While linked, every tool here goes to that page's instruments, until unlink_tab.",
        InputSchema = Element("""{"type":"object","properties":{"code":{"type":"string","description":"The eight digits the bench page shows under MCP server in its AI settings, like 4821 7356."}},"required":["code"]}"""),
        Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false, OpenWorldHint = false },
    };

    private static readonly Tool UnlinkTabTool = new()
    {
        Name = UnlinkTab,
        Description = "Ends the link to the bench page. The tools work the instruments on the command line again.",
        InputSchema = Element("""{"type":"object","properties":{}}"""),
        Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false, OpenWorldHint = false },
    };

    /// <summary>The server's name and instructions, and the two handlers that are all it answers.</summary>
    public static McpServerOptions Options(BenchToolRunner tools, string instructions, TabLink? link = null)
    {
        List<Tool> listed = [.. tools.Tools.Select(ToProtocol)];
        if (link is not null) listed.AddRange([LinkTabTool, UnlinkTabTool]);
        var turn = new SemaphoreSlim(1, 1);
        string version = typeof(McpServing).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "lec", Title = "lec - lab instruments over SCPI", Version = version },
            ServerInstructions = instructions,
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (request, cancel) => ValueTask.FromResult(new ListToolsResult { Tools = listed }),
                CallToolHandler = async (request, cancel) => await CallAsync(tools, link, turn, request.Server.ClientInfo?.Name ?? "", request.Params, cancel),
            },
        };
    }

    /// <summary>Serves the tools on a transport until the client closes it or <paramref name="ct"/> fires.</summary>
    public static async Task RunAsync(BenchToolRunner tools, string instructions, ITransport transport, CancellationToken ct, TabLink? link = null)
    {
        await using var server = McpServer.Create(transport, Options(tools, instructions, link));
        await server.RunAsync(ct);
    }

    /// <summary>
    /// One tool as the protocol describes it, with what it does said in the annotations a client
    /// reads to decide what to ask the person first.
    ///
    /// <c>send</c> is destructive: a command to an instrument has no undo, which is the very case
    /// the hint is for. Everything else reads. Closed world, all of them: nothing reaches past
    /// the instruments on the command line.
    /// </summary>
    private static Tool ToProtocol(LlmTool tool) => new()
    {
        Name = tool.Name,
        Description = tool.Description,
        InputSchema = Element(tool.InputSchemaJson),
        Annotations = new ToolAnnotations
        {
            ReadOnlyHint = !tool.Changes,
            DestructiveHint = tool.Changes,
            OpenWorldHint = false,
        },
    };

    /// <summary>
    /// Answers one call. What went wrong is said as an answer with <c>isError</c> set, never as a
    /// protocol error, so the model reads it and tries another way rather than the client
    /// reporting a broken server.
    /// </summary>
    private static async Task<CallToolResult> CallAsync(BenchToolRunner tools, TabLink? link, SemaphoreSlim turn, string client,
                                                        CallToolRequestParams? asked, CancellationToken ct)
    {
        if (asked is null) return Answer("A tool call has to say which tool.", true);

        await turn.WaitAsync(ct);
        try
        {
            return await RouteAsync(tools, link, client, asked, ct);
        }
        finally
        {
            turn.Release();
        }
    }

    /// <summary>
    /// One call, to the page while one is linked and to the command line's instruments otherwise.
    /// **What the model works on is said, never assumed**: when the page goes, the next call is
    /// refused with why rather than run on instruments it was not thinking of.
    /// </summary>
    private static async Task<CallToolResult> RouteAsync(BenchToolRunner tools, TabLink? link, string client, CallToolRequestParams asked, CancellationToken ct)
    {
        if (link is not null)
        {
            try
            {
                if (asked.Name == LinkTab) return await LinkAsync(link, client, asked, ct);
                if (asked.Name == UnlinkTab) return await UnlinkAsync(link);
            }
            catch (LinkRefusal refusal)
            {
                return Answer(refusal.Message, true);
            }

            if (link.TakeEnding() is string ended)
            {
                return Answer($"{ended} So {asked.Name} was not run. From the next call the tools work the instruments on the command line again; "
                            + "to work on the page, ask the person to press Start under MCP server in its AI settings again and give you the new code.", true);
            }

            // A tool --read-only left out stays out, whichever instruments it would have reached.
            if (link.IsLinked && tools.Tools.Any(t => t.Name == asked.Name))
            {
                try
                {
                    TabAnswer answer = await link.CallAsync(asked.Name, Element(ArgumentsOf(asked.Arguments)), CallWithin, ct);
                    return Answer(answer.Text, answer.IsError, answer.Png, answer.Proposal);
                }
                catch (LinkRefusal refusal)
                {
                    return Answer(refusal.Message, true);
                }
            }
        }

        try
        {
            LlmToolOutput output = await tools.RunToolAsync(asked.Name, ArgumentsOf(asked.Arguments), ct);
            return Answer(output.Text, output.IsError, output.Png, output.Proposal);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Answer($"{asked.Name} failed: {ex.Message}", true);
        }
    }

    /// <summary>Links the page that offered the code, and says what it has open so the model starts from where the person is.</summary>
    private static async Task<CallToolResult> LinkAsync(TabLink link, string client, CallToolRequestParams asked, CancellationToken ct)
    {
        string code = asked.Arguments is { } given && given.TryGetValue("code", out JsonElement value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText()
            : "";

        await link.LinkAsync(code, client, LinkWait, ct);

        var said = new System.Text.StringBuilder("Linked to the bench page. The tools work the instruments open in it from now on, as the person watches, until unlink_tab.");

        // Its refusal is not the link's: the page is linked by now, whatever this does.
        try
        {
            TabAnswer listed = await link.CallAsync(BenchToolRunner.ListInstruments, Element("{}"), CallWithin, ct);
            if (!listed.IsError) said.Append("\n\n").Append(listed.Text);
        }
        catch (LinkRefusal refusal)
        {
            said.Append($"\n\nlist_instruments did not answer yet: {refusal.Message}");
        }

        return Answer(said.ToString(), false);
    }

    private static async Task<CallToolResult> UnlinkAsync(TabLink link)
        => Answer(await link.UnlinkAsync()
            ? "The link to the bench page is ended. The tools work the instruments on the command line again."
            : "No bench page was linked. The tools work the instruments on the command line.", false);

    /// <summary>
    /// The words, then the screen when a tool took one — which the model is shown, not told
    /// about — then a proposed script whole, there being no Use button on this side to hand it to.
    /// </summary>
    private static CallToolResult Answer(string text, bool isError, string? png = null, string? proposal = null)
    {
        var content = new List<ContentBlock> { new TextContentBlock { Text = text } };
        if (png is not null) content.Add(ImageContentBlock.FromBytes(Convert.FromBase64String(png), "image/png"));
        if (proposal is not null) content.Add(new TextContentBlock { Text = proposal });
        return new CallToolResult { Content = content, IsError = isError };
    }

    /// <summary>The arguments as the one JSON object the tools read, or an empty one when the call sent none.</summary>
    private static string ArgumentsOf(IDictionary<string, JsonElement>? given)
    {
        if (given is null) return "{}";

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in given)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
