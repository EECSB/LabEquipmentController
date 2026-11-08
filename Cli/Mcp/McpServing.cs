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
    /// <summary>The server's name and instructions, and the two handlers that are all it answers.</summary>
    public static McpServerOptions Options(BenchToolRunner tools, string instructions)
    {
        List<Tool> listed = [.. tools.Tools.Select(ToProtocol)];
        string version = typeof(McpServing).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "lec", Title = "lec - lab instruments over SCPI", Version = version },
            ServerInstructions = instructions,
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (request, cancel) => ValueTask.FromResult(new ListToolsResult { Tools = listed }),
                CallToolHandler = async (request, cancel) => await CallAsync(tools, request.Params, cancel),
            },
        };
    }

    /// <summary>Serves the tools on a transport until the client closes it or <paramref name="ct"/> fires.</summary>
    public static async Task RunAsync(BenchToolRunner tools, string instructions, ITransport transport, CancellationToken ct)
    {
        await using var server = McpServer.Create(transport, Options(tools, instructions));
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
    private static async Task<CallToolResult> CallAsync(BenchToolRunner tools, CallToolRequestParams? asked, CancellationToken ct)
    {
        if (asked is null) return Answer("A tool call has to say which tool.", true);

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
