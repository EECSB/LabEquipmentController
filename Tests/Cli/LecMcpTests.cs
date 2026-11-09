using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LabEquipmentController.Cli;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LabEquipmentController.Tests;

/// <summary>
/// <c>lec mcp</c>: the bench tools on the Model Context Protocol, against an instrument on a
/// loopback socket. In process through the SDK's own client, and once as the real process a
/// client starts, with every line of its standard output read as protocol.
/// </summary>
public sealed class LecMcpTests : IDisposable
{
    private const string Generator = "Siglent Technologies,SDG2042X,SDG2XCAD1R0001,2.01.01.35R3B2";

    private readonly FakeScpiListener _instrument = new(Generator);

    public void Dispose() => _instrument.Dispose();

    [Fact]
    public async Task The_tools_are_the_windows_own_and_send_is_marked_destructive()
    {
        await using var served = await Serve(readOnly: false);

        IList<McpClientTool> tools = await served.Client.ListToolsAsync();

        Assert.Equal(BenchToolRunner.All.Select(t => t.Name).Order(), tools.Select(t => t.Name).Order());
        McpClientTool send = tools.Single(t => t.Name == BenchToolRunner.Send);
        Assert.True(send.ProtocolTool.Annotations?.DestructiveHint);
        Assert.False(send.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tools.Single(t => t.Name == BenchToolRunner.Query).ProtocolTool.Annotations?.ReadOnlyHint);
    }

    [Fact]
    public async Task A_call_reaches_the_instrument_and_its_answer_comes_back_as_words()
    {
        await using var served = await Serve(readOnly: false);

        CallToolResult listed = await served.Client.CallToolAsync(BenchToolRunner.ListInstruments, new Dictionary<string, object?>());
        Assert.Contains("- gen: Siglent Technologies SDG2042X", Text(listed));

        CallToolResult sent = await served.Client.CallToolAsync(BenchToolRunner.Send, new Dictionary<string, object?> { ["command"] = "C1:OUTP ON" });
        Assert.NotEqual(true, sent.IsError);
        Assert.Contains("Sent C1:OUTP ON to gen", Text(sent));
        Assert.Contains("C1:OUTP ON", _instrument.Received);
    }

    [Fact]
    public async Task A_refusal_is_an_answer_with_isError_and_not_a_protocol_error()
    {
        await using var served = await Serve(readOnly: false);

        CallToolResult refused = await served.Client.CallToolAsync(BenchToolRunner.Query, new Dictionary<string, object?> { ["command"] = "C1:OUTP ON" });

        Assert.True(refused.IsError);
        Assert.Contains("is not a query", Text(refused));
        Assert.DoesNotContain("C1:OUTP ON", _instrument.Received);
    }

    [Fact]
    public async Task Read_only_offers_no_send_and_says_so_at_the_handshake()
    {
        await using var served = await Serve(readOnly: true);

        IList<McpClientTool> tools = await served.Client.ListToolsAsync();

        Assert.DoesNotContain(tools, t => t.Name == BenchToolRunner.Send);
        Assert.Contains("started read-only", served.Client.ServerInstructions);

        CallToolResult refused = await served.Client.CallToolAsync(BenchToolRunner.Send, new Dictionary<string, object?> { ["command"] = "C1:OUTP ON" });
        Assert.True(refused.IsError);
        Assert.DoesNotContain("C1:OUTP ON", _instrument.Received);
    }

    [Fact]
    public async Task A_proposed_script_comes_back_whole_after_the_answer()
    {
        await using var served = await Serve(readOnly: false);

        CallToolResult proposed = await served.Client.CallToolAsync(BenchToolRunner.ProposeScript, new Dictionary<string, object?>
        {
            ["kind"] = "instrument",
            ["script"] = "C1:OUTP ON\nDELAY 100\nC1:OUTP OFF",
        });

        Assert.Equal(2, proposed.Content.Count);
        Assert.Contains("lec run", ((TextContentBlock)proposed.Content[0]).Text);
        Assert.Equal("C1:OUTP ON\nDELAY 100\nC1:OUTP OFF", ((TextContentBlock)proposed.Content[1]).Text);
        Assert.DoesNotContain("C1:OUTP OFF", _instrument.Received);
    }

    /// <summary>
    /// The real thing: <c>lec mcp</c> started as its own process, the way a client starts it, and
    /// spoken to in newline-delimited JSON-RPC. Every line it writes to standard output has to be a
    /// protocol message — a stray line of prose there is a message the client cannot parse, and
    /// nothing in process can see one.
    /// </summary>
    [Fact]
    public async Task As_a_process_it_writes_nothing_but_protocol_to_standard_output()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add(LecInItsOwnFolder());
        start.ArgumentList.Add("mcp");
        start.ArgumentList.Add($"127.0.0.1:{_instrument.Port}");

        using var server = Process.Start(start)!;
        var errors = new ConcurrentQueue<string>();
        server.ErrorDataReceived += (_, line) => { if (line.Data is not null) errors.Enqueue(line.Data); };
        server.BeginErrorReadLine();

        try
        {
            async Task<JsonElement> Ask(int id, string method, string parameters)
            {
                await server.StandardInput.WriteLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{parameters}}}");
                await server.StandardInput.FlushAsync();
                string? line = await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Assert.NotNull(line);
                using var message = JsonDocument.Parse(line!);
                Assert.Equal("2.0", message.RootElement.GetProperty("jsonrpc").GetString());
                Assert.Equal(id, message.RootElement.GetProperty("id").GetInt32());
                return message.RootElement.GetProperty("result").Clone();
            }

            JsonElement hello = await Ask(1, "initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"0"}}""");
            Assert.Equal("lec", hello.GetProperty("serverInfo").GetProperty("name").GetString());
            await server.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            JsonElement listed = await Ask(2, "tools/list", "{}");
            Assert.Equal(BenchToolRunner.All.Count + 2, listed.GetProperty("tools").GetArrayLength()); // and link_tab and unlink_tab

            JsonElement answered = await Ask(3, "tools/call", """{"name":"query","arguments":{"command":"*IDN?"}}""");
            Assert.Contains(Generator, answered.GetProperty("content")[0].GetProperty("text").GetString());

            // Closing its input is how a client says it is done: it hands the front panel back, ends,
            // and says nothing more.
            server.StandardInput.Close();
            string rest = await server.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal("", rest);
            Assert.Equal(0, server.ExitCode);
            Assert.Contains(errors, line => line.StartsWith("lec mcp: serving 1 address", StringComparison.Ordinal));
        }
        finally
        {
            if (!server.HasExited) server.Kill();
        }
    }

    // ------------------------------------------------------------------- helpers

    private async Task<Served> Serve(bool readOnly)
    {
        Assert.True(Cli.Endpoint.TryParse($"127.0.0.1:{_instrument.Port}", out var address, out _));
        var bench = new McpBench([address], 3000, TextWriter.Null);
        await bench.ConnectAsync(default);
        var tools = new BenchToolRunner(new RetryingBench(bench)) { AllowEdits = !readOnly, HowProposalsAreTaken = "Give it to the person to run with lec run." };

        var toServer = new Pipe();
        var toClient = new Pipe();
        var stopping = new CancellationTokenSource();
        Task serving = McpServing.RunAsync(tools, Commands.McpInstructions([address], readOnly),
            new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "lec"), stopping.Token);
        McpClient client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
        return new Served(client, bench, stopping, serving);
    }

    /// <summary>
    /// lec.dll where its own build put it, beside every assembly it loads. The copy in this test
    /// project's folder is not runnable: the web project brings ASP.NET's shared framework into
    /// this one, which then leaves out the logging assembly the MCP SDK needs, and lec's own
    /// runtime config does not name that framework — so it died there loading it, and never said
    /// a word on standard output.
    /// </summary>
    private static string LecInItsOwnFolder()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string framework = here.Name;
        string configuration = here.Parent!.Name;
        string root = here.Parent.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(root, "Cli", "bin", configuration, framework, "lec.dll");
    }

    private static string Text(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private sealed record Served(McpClient Client, McpBench Bench, CancellationTokenSource Stopping, Task Serving) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            Stopping.Cancel();
            try { await Serving; } catch (OperationCanceledException) { }
            await Bench.DisposeAsync();
        }
    }
}

/// <summary>
/// A raw-socket SCPI instrument on loopback: newline-terminated, any number of connections,
/// answering *IDN? with its identity, the error query with no error and any other query with a
/// number — and keeping every line it was sent other than the error query.
/// </summary>
internal sealed class FakeScpiListener : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _identity;
    private readonly ConcurrentQueue<string> _received = new();

    public FakeScpiListener(string identity)
    {
        _identity = identity;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    public IReadOnlyList<string> Received => [.. _received];

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                while (await reader.ReadLineAsync(_stop.Token) is string line)
                {
                    string command = line.Trim();
                    if (command.Length == 0) continue;
                    if (!command.Equals("SYST:ERR?", StringComparison.OrdinalIgnoreCase) && command != "*IDN?")
                        _received.Enqueue(command);
                    if (!command.Contains('?')) continue;

                    string reply = command switch
                    {
                        "*IDN?" => _identity,
                        _ when command.Equals("SYST:ERR?", StringComparison.OrdinalIgnoreCase) => "0,\"No error\"",
                        _ => "1.234",
                    };
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(reply + "\n"), _stop.Token);
                }
            }
            catch { /* closed */ }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
