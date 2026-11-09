using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>Where a live link is: not started, waiting for <c>lec mcp</c> to link it, linked, or ended by somebody.</summary>
public enum LiveLinkState
{
    Off,
    Waiting,
    Linked,
    Ended,
}

/// <summary>One call that came over the link, and what the page answered, for the card's list of them.</summary>
public sealed class LiveLinkCall
{
    public required string Name { get; init; }

    public required string ArgumentsJson { get; init; }

    /// <summary>The answer, or null while it is being worked out.</summary>
    public string? Text { get; set; }

    public bool IsError { get; set; }
}

/// <summary>
/// The bench page's end of a live link to <c>lec mcp</c>, from GDS Viewer: a code to show, a
/// WebSocket to 127.0.0.1, and every call that comes down it answered on the instruments open on
/// this bench. See <c>TabLink</c> in the command-line tool for the other end and what is said
/// between them.
///
/// **The page calls, because a page cannot listen.** Started, it dials the port until something
/// answers (<c>lec mcp</c> listens from when its Claude window starts it), says hello with the
/// code, is told it is waiting, and waits. Nothing it is sent is acted on until it is told it is
/// linked, which is the server saying the person gave a model that code.
///
/// **A link that ends stays ended.** The code is spent and the next link is a new Start, with a
/// new one: a page that went back to waiting by itself after a link would be a page offering
/// itself to whatever said hello next.
///
/// **Calls are answered one at a time, in the order they come.** What answers them is handed in
/// as a function, so this is testable against the real server without a browser.
/// </summary>
public sealed class LiveLink : IAsyncDisposable
{
    /// <summary>The port <c>lec mcp</c> listens on unless started with <c>--link-port</c>. Not GDS Viewer's, so both can be up at once.</summary>
    public const int DefaultPort = 43717;

    /// <summary>How many calls the card lists. The oldest go first.</summary>
    public const int MostCalls = 200;

    /// <summary>
    /// The words <c>lec mcp</c> closes a waiting page with when it is one too many: a page closed
    /// with them ends rather than dialing again, or nine pages would take turns at being turned away.
    /// </summary>
    public const string TurnedAwayReason = "Too many pages were waiting.";

    private static readonly TimeSpan FirstPause = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongestPause = TimeSpan.FromSeconds(8);

    /// <summary>The most a call from the server may be. A call is a tool's name and its arguments.</summary>
    private const int MostCall = 4 * 1024 * 1024;

    private readonly Func<string, string, CancellationToken, Task<LlmToolOutput>> _answer;

    /// <summary>One message out at a time: an answer going out and a Stop's goodbye must not cross.</summary>
    private readonly SemaphoreSlim _sending = new(1, 1);

    /// <summary>The run in progress, which a Start replaces and a Stop ends. Each run checks it is still the one before it says anything.</summary>
    private CancellationTokenSource? _running;

    private ClientWebSocket? _socket;

    /// <param name="answer">Answers one call, a tool's name and its arguments as JSON, on this bench.</param>
    public LiveLink(Func<string, string, CancellationToken, Task<LlmToolOutput>> answer)
    {
        _answer = answer;
    }

    public LiveLinkState State { get; private set; } = LiveLinkState.Off;

    /// <summary>The code to tell the model, as it is shown: two groups of four digits.</summary>
    public string Code { get; private set; } = "";

    public int Port { get; private set; } = DefaultPort;

    /// <summary>The MCP client the link is to, as <c>lec mcp</c> said it introduced itself: "Claude Code", say.</summary>
    public string Client { get; private set; } = "";

    /// <summary>Why the last link ended.</summary>
    public string Ending { get; private set; } = "";

    /// <summary>
    /// What is odd about the wait, while there is something: nothing on the port has said it is
    /// <c>lec mcp</c> within <see cref="HeardWithin"/> of the hello. Anything else on the port
    /// that takes a WebSocket and keeps quiet would otherwise be waited on for ever.
    /// </summary>
    public string Notice { get; private set; } = "";

    /// <summary>How long after the hello something has to answer before <see cref="Notice"/> says nothing has.</summary>
    public TimeSpan HeardWithin { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The calls this link has answered, oldest first.</summary>
    public List<LiveLinkCall> Calls { get; } = [];

    /// <summary>What is open on this bench, said in the hello so <c>lec mcp</c> can tell the model what it linked.</summary>
    public Func<string>? Subject { get; set; }

    /// <summary>Raised whenever anything shown changes: the state, the client, a call or its answer.</summary>
    public event Func<Task>? Changed;

    /// <summary>Starts waiting for <c>lec mcp</c> on <paramref name="port"/> with a new code, ending whatever link there was.</summary>
    public void Start(int port)
    {
        _running?.Cancel();

        var mine = new CancellationTokenSource();
        _running = mine;

        Port = port;
        Code = NewCode();
        Client = "";
        Ending = "";
        Notice = "";
        State = LiveLinkState.Waiting;
        Calls.Clear();

        _ = RunAsync(mine);
    }

    /// <summary>Ends the link, or the wait for one, saying goodbye so <c>lec mcp</c> knows the page went rather than fell over.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? was = _running;
        _running = null;

        if (_socket is ClientWebSocket open && open.State == WebSocketState.Open)
            await GoodbyeAsync(open, "Stopped in the page.").ConfigureAwait(false);

        was?.Cancel();

        if (State == LiveLinkState.Off) return;

        State = LiveLinkState.Off;
        Client = "";
        await RaiseChangedAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>A new code: eight digits from a cryptographic source, shown in two fours.</summary>
    public static string NewCode()
    {
        string digits = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture);
        return digits[..4] + " " + digits[4..];
    }

    // ------------------------------------------------------------------- the run

    /// <summary>
    /// Dials until something answers, then serves it until it closes. Before a link that is only
    /// waiting again (the server went, or turned the page away); after one it is the end.
    /// </summary>
    private async Task RunAsync(CancellationTokenSource mine)
    {
        CancellationToken cancel = mine.Token;
        TimeSpan pause = FirstPause;

        while (!cancel.IsCancellationRequested)
        {
            var trying = new ClientWebSocket();
            string? closing = null;

            try
            {
                await trying.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/"), cancel).ConfigureAwait(false);

                _socket = trying;
                pause = FirstPause;

                await SendAsync(trying, HelloMessage(), cancel).ConfigureAwait(false);

                closing = await ServeAsync(trying, mine).ConfigureAwait(false);
            }
            catch (Exception) when (!cancel.IsCancellationRequested)
            {
                // Nothing is on the port yet, or it went: dial again.
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
            finally
            {
                if (ReferenceEquals(_socket, trying)) _socket = null;
                trying.Dispose();
            }

            if (cancel.IsCancellationRequested || !ReferenceEquals(_running, mine)) return;

            if (State == LiveLinkState.Linked)
            {
                State = LiveLinkState.Ended;
                Client = "";
                Ending = closing is { Length: > 0 } ? closing : "lec mcp went away: the Claude window that started it closed, or restarted it.";
                _running = null;
                await RaiseChangedAsync().ConfigureAwait(false);
                return;
            }

            // Let go for being one page too many: the end, rather than the ninth page again.
            if (closing == TurnedAwayReason)
            {
                State = LiveLinkState.Ended;
                Ending = $"{closing} Press Start again when fewer pages are waiting on lec mcp, or press Stop in one of them.";
                _running = null;
                await RaiseChangedAsync().ConfigureAwait(false);
                return;
            }

            try
            {
                await Task.Delay(pause, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, LongestPause.Ticks));
        }
    }

    private string HelloMessage() => new JsonObject
    {
        ["type"] = "hello",
        ["app"] = "Lab Equipment Controller",
        ["code"] = Code.Replace(" ", "", StringComparison.Ordinal),
        ["file"] = Subject?.Invoke() ?? "",
    }.ToJsonString();

    /// <summary>The server's messages until it closes, and the reason it gave for closing if it gave one.</summary>
    private async Task<string?> ServeAsync(ClientWebSocket open, CancellationTokenSource mine)
    {
        // The first message is waited for with a clock on it, see Notice. The receive goes on past
        // the clock rather than being given up: cancelling a receive aborts the socket.
        Task<string?> receiving = ReceiveAsync(open, mine.Token);

        if (await Task.WhenAny(receiving, Task.Delay(HeardWithin, mine.Token)).ConfigureAwait(false) != receiving && ReferenceEquals(_running, mine))
        {
            Notice = $"Nothing on port {Port} has said it is lec mcp in {HeardWithin.TotalSeconds:0} seconds, and anything else on the port never will. Still waiting.";
            await RaiseChangedAsync().ConfigureAwait(false);
        }

        while (true)
        {
            string? message = await receiving.ConfigureAwait(false);
            if (message is null) return open.CloseStatusDescription;

            // Something answered, whatever it said: the wait is an ordinary one again.
            if (Notice.Length > 0)
            {
                Notice = "";
                await RaiseChangedAsync().ConfigureAwait(false);
            }

            await HandleAsync(open, message, mine).ConfigureAwait(false);
            receiving = ReceiveAsync(open, mine.Token);
        }
    }

    private async Task HandleAsync(ClientWebSocket open, string message, CancellationTokenSource mine)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !ReferenceEquals(_running, mine)) return;

            string? type = Text(root, "type");

            if (type == "linked")
            {
                Client = Text(root, "client") ?? "";
                State = LiveLinkState.Linked;
                await RaiseChangedAsync().ConfigureAwait(false);
                return;
            }

            // Nothing is answered before the link is made: until then the page is only offering itself.
            if (type != "call" || State != LiveLinkState.Linked) return;
            if (!root.TryGetProperty("id", out JsonElement number) || !number.TryGetInt32(out int id)) return;

            string name = Text(root, "name") ?? "";
            string arguments = root.TryGetProperty("arguments", out JsonElement given) && given.ValueKind == JsonValueKind.Object
                ? given.GetRawText()
                : "{}";

            await CallAsync(open, id, name, arguments, mine.Token).ConfigureAwait(false);
        }
    }

    /// <summary>One call: listed, answered by the bench, and the answer sent back. A failure here is an answer that says so.</summary>
    private async Task CallAsync(ClientWebSocket open, int id, string name, string arguments, CancellationToken cancel)
    {
        var call = new LiveLinkCall { Name = name, ArgumentsJson = arguments };
        Calls.Add(call);
        if (Calls.Count > MostCalls) Calls.RemoveAt(0);
        await RaiseChangedAsync().ConfigureAwait(false);

        LlmToolOutput output;
        try
        {
            output = await _answer(name, arguments, cancel).ConfigureAwait(false);
        }
        catch (Exception failed) when (failed is not OperationCanceledException)
        {
            output = new LlmToolOutput { Text = $"{name} failed in the page: {failed.Message}", IsError = true };
        }

        call.Text = output.Text;
        call.IsError = output.IsError;
        await RaiseChangedAsync().ConfigureAwait(false);

        var reply = new JsonObject
        {
            ["type"] = "answer",
            ["id"] = id,
            ["text"] = output.Text,
            ["isError"] = output.IsError,
            ["png"] = output.Png,
            ["proposal"] = output.Proposal,
        };

        await SendAsync(open, reply.ToJsonString(), cancel).ConfigureAwait(false);
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // ------------------------------------------------------------------- the socket

    private async Task SendAsync(ClientWebSocket open, string message, CancellationToken cancel)
    {
        await _sending.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await open.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancel).ConfigureAwait(false);
        }
        finally
        {
            _sending.Release();
        }
    }

    private async Task GoodbyeAsync(ClientWebSocket open, string reason)
    {
        if (!await _sending.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false)) return;
        try
        {
            using var hurry = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await open.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, hurry.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A socket that cannot say goodbye is one that is going anyway.
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>One whole message as text, or null when the server closed.</summary>
    private static async Task<string?> ReceiveAsync(ClientWebSocket open, CancellationToken cancel)
    {
        var buffer = new ArrayBufferWriter<byte>();
        while (true)
        {
            ValueWebSocketReceiveResult result = await open.ReceiveAsync(buffer.GetMemory(16 * 1024), cancel).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;

            buffer.Advance(result.Count);
            if (buffer.WrittenCount > MostCall) throw new InvalidDataException($"A message over {MostCall} bytes.");
            if (result.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private async Task RaiseChangedAsync()
    {
        if (Changed is null) return;
        foreach (Delegate listener in Changed.GetInvocationList())
            await ((Func<Task>)listener)().ConfigureAwait(false);
    }
}
