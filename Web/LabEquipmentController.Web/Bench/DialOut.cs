using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LabEquipmentController.Web.Client.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.WebUtilities;

namespace LabEquipmentController.Web.Bench;

/// <summary>
/// The server as a bench that <em>dials out</em>: it opens the connection to its host and holds it,
/// rather than waiting to be called.
/// </summary>
/// <remarks>
/// Switched on by <c>LEC_DIAL_URL</c> and <c>LEC_DIAL_TOKEN</c> together, and off without them —
/// the standalone app and service mode are both exactly as they were.
///
/// It exists for the case service mode cannot serve: a bench on a factory LAN, behind NAT and a
/// firewall, and a host in a datacenter that has no address to dial. The questions still run the
/// same way round — the host asks and the bench answers — and only the dialing is reversed.
///
/// The token is the host's, not this server's: the host issues it, names this bench in it, and it
/// is the whole of what this server sends. There is nothing to compare here, which is why this
/// class holds no comparison.
/// </remarks>
public sealed class DialOutMode
{
    public const string UrlVariable = "LEC_DIAL_URL";
    public const string TokenVariable = "LEC_DIAL_TOKEN";

    public DialOutMode(IConfiguration configuration)
        : this(configuration[UrlVariable], configuration[TokenVariable]) { }

    public DialOutMode(string? url, string? token)
    {
        Url = (url ?? "").Trim();
        Token = (token ?? "").Trim();
    }

    /// <summary>The hub this server dials, absolute: the host's own address and path.</summary>
    public string Url { get; }

    /// <summary>What it dials with. Issued by the host, and worth one bench on one instance.</summary>
    public string Token { get; }

    /// <summary>
    /// Both are set, so this server dials out. One without the other is a half-configured bench and
    /// not a mode: it would dial nowhere, or dial with nothing, and either is worth saying plainly
    /// rather than failing quietly (see <see cref="DialOutService"/>).
    /// </summary>
    public bool Enabled => Url.Length > 0 && Token.Length > 0;

    /// <summary>Why it will not dial, or null when it will.</summary>
    public string? Refusal
    {
        get
        {
            if (Url.Length == 0 && Token.Length == 0) return null;
            if (Url.Length == 0) return $"{TokenVariable} is set and {UrlVariable} is not, so there is nowhere to dial.";
            if (Token.Length == 0) return $"{UrlVariable} is set and {TokenVariable} is not, so there is nothing to dial with.";
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri)) return $"{UrlVariable} is not an absolute address.";
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return $"{UrlVariable} has to be an http or https address.";
            return null;
        }
    }
}

/// <summary>
/// One request the host puts down the held connection: an HTTP request with the transport taken
/// out of it.
/// </summary>
/// <remarks>
/// Named as the host names it. The two halves live in different repositories and have to agree on
/// this shape, and the same word in both is the cheapest way to keep them agreeing.
///
/// <strong>The wire names are written down rather than inferred.</strong> The host spells them out
/// with <c>JsonPropertyName</c>; leaving this side to whatever naming policy the hub protocol happens
/// to default to would mean the two agree by coincidence, and a serializer option set on either side
/// — by anyone, for any reason — would part them silently, with nothing failing until a bench in a
/// customer's building stopped understanding its host. Both sides say <c>id</c>, and both are pinned
/// by a test that reads the serialized JSON.
/// </remarks>
public sealed record BenchAsk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("body")] string? Body);

/// <summary>What this bench said back, named after the ask it answers. Pinned as <see cref="BenchAsk"/> is.</summary>
/// <remarks>
/// <strong>An answer too long for one message goes as several</strong>: <c>part</c> of
/// <c>parts</c>, numbered from 0 and sent in order, each with the same id and status and the next
/// stretch of the body, which the host puts back together. One message is part 0 of 1.
///
/// A hub takes messages up to a size of its own choosing — SignalR's default is 32 KB, and
/// Treeality's is 8 MB — and one read of a run's record has no such bound: twenty thousand rows of
/// sixteen columns came to 11 MB, and a read answers up to 8 MiB each of lines and rows. Until
/// 2026-09-23 an answer like that made the host close the
/// connection, this bench dial back in, and the host ask again, for as long as it followed the
/// run, which never ended for it. See <see cref="DialOutService.Parts"/> for how long a part is.
/// </remarks>
public sealed record BenchSaid(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("part")] int Part = 0,
    [property: JsonPropertyName("parts")] int Parts = 1);

/// <summary>
/// What a host may ask for down a dial-out connection, and what this bench does about it.
/// </summary>
/// <remarks>
/// <strong>A named list, and not a proxy for the whole API.</strong> The envelope carries a method
/// and a path, so forwarding it into the server's own routes would have been fewer lines — and it
/// would have handed whoever is at the other end of the connection every route this server has,
/// including connecting to an arbitrary address on the customer's LAN, scanning it, and writing
/// this server's settings. A host that reaches the bench at an address is checked by <em>its</em>
/// whitelist before a byte leaves it; a bench that dials out has nothing of the host's between it
/// and its instruments, so <strong>the list of what may be asked is this bench's own whitelist</strong>
/// and it lives here.
///
/// What is on it is what running a released procedure takes: read a script, see the instruments,
/// bind them, start, follow, stop. Nothing else, and a path that is not on it is refused with the
/// path named in this server's log.
///
/// Each entry calls the same method the matching API route calls, so the two cannot answer
/// differently — and the JSON is written with the web defaults the routes are serialized with, for
/// the same reason. <strong>That includes failing the same way:</strong> a call on the list that
/// throws answers 500, as its route would, because an exception left to escape is sent nowhere at
/// all and the host learns only that this bench went quiet.
/// </remarks>
public sealed class DialOutCalls(BenchService bench, RunService runs, ILogger<DialOutCalls> log)
{
    /// <summary>What the API routes are serialized with, so an answer down the connection is the same answer.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string RefusedMessage = "This bench does not answer that over a dial-out connection.";

    public BenchSaid Answer(BenchAsk ask)
    {
        string method = (ask.Method ?? "").Trim().ToUpperInvariant();
        string path = Normalize(ask.Path);

        try
        {
            if (method == "GET" && path == "/api/sessions")
                return Ok(ask, bench.Sessions());

            if (method == "POST" && path == "/api/sequence/declares")
                return Ok(ask, SequenceAnswers.Declares(Sequence(ask).Script));

            if (method == "POST" && path == "/api/sequence/requirements")
            {
                var req = Sequence(ask);
                return Ok(ask, bench.BindSequence(req.Script, req.Bindings));
            }

            if (method == "POST" && path == "/api/runs/sequence")
                return Ok(ask, runs.StartSequence(Sequence(ask)));

            // The same start under an id the host chose, which is what makes a start whose answer
            // is lost on this connection one the host can still follow and stop.
            if (method == "PUT" && Below(path, "/api/runs/", out string starting))
                return Ok(ask, runs.StartSequence(Sequence(ask), starting));

            if (method == "GET" && Below(path, "/api/runs/", out string reading))
            {
                // From where the host left off, as the route reads it — and a value that is not a
                // whole number is the route's 400 here too.
                if (ReadFrom(ask.Path) is not { } from) return new BenchSaid(ask.Id, StatusCodes.Status400BadRequest, null);

                return runs.Record(reading, from.Output, from.Rows) is { } record
                    ? Ok(ask, record)
                    : new BenchSaid(ask.Id, StatusCodes.Status404NotFound, null);
            }

            if (method == "POST" && Below(path, "/api/runs/", "/stop", out string stopping))
                return new BenchSaid(ask.Id, runs.Stop(stopping) ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound, null);
        }
        catch (JsonException)
        {
            // The host sent something this server could not read as the shape that path takes. Its
            // own word for a request it cannot parse is 400, and so is this.
            return new BenchSaid(ask.Id, StatusCodes.Status400BadRequest, null);
        }
        catch (Exception ex)
        {
            // Anything else the call itself threw. Over HTTP the framework turns this into a 500
            // and the caller hears back at once; over a dial-out connection nothing does, so an
            // exception that escapes here is sent nowhere and the host waits out its own timeout
            // and reports a bench that did not answer — a network story for a body this bench
            // refused. Answering 500 keeps the two entry points saying the same thing, which is
            // what the list below is written around. The detail stays in this server's log.
            log.LogError(ex, "This bench could not answer {Method} {Path} over its dial-out connection.", method, path);

            return new BenchSaid(ask.Id, StatusCodes.Status500InternalServerError, null);
        }

        // Said in the log and not on the wire: whatever is at the other end holds a token this
        // bench's host issued, and the person who needs to know which path was refused is the one
        // reading this server's log.
        log.LogWarning("A host asked this bench for {Method} {Path} over its dial-out connection, which is not one of the things it answers there.", method, path);

        return new BenchSaid(ask.Id, StatusCodes.Status403Forbidden, JsonSerializer.Serialize(new { error = RefusedMessage }, Json));
    }

    private static BenchSaid Ok<T>(BenchAsk ask, T body)
        => new(ask.Id, StatusCodes.Status200OK, JsonSerializer.Serialize(body, Json));

    private static SequenceRunRequest Sequence(BenchAsk ask)
        => JsonSerializer.Deserialize<SequenceRunRequest>(ask.Body ?? "", Json)
           ?? throw new JsonException("The body was null.");

    /// <summary>The path alone, without a query and without a trailing slash, and never empty.</summary>
    private static string Normalize(string? path)
    {
        string only = (path ?? "").Trim();

        int query = only.IndexOfAny(['?', '#']);
        if (query >= 0) only = only[..query];

        if (only.Length > 1) only = only.TrimEnd('/');
        return only.Length == 0 ? "/" : only;
    }

    /// <summary>
    /// Where a read of a run starts: the route's own <c>outputFrom</c> and <c>rowsFrom</c>, from the
    /// query on the path, each 0 when it is absent — or null when one is there and is not a whole
    /// number, which the route would refuse.
    /// </summary>
    private static (int Output, int Rows)? ReadFrom(string? path)
    {
        string whole = path ?? "";

        int query = whole.IndexOf('?');
        if (query < 0) return (0, 0);

        int fragment = whole.IndexOf('#', query);
        var asked = QueryHelpers.ParseQuery(fragment < 0 ? whole[query..] : whole[query..fragment]);

        int? At(string name)
        {
            if (!asked.TryGetValue(name, out var said) || said.ToString().Length == 0) return 0;
            return int.TryParse(said.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int at) ? at : null;
        }

        return At("outputFrom") is { } output && At("rowsFrom") is { } rows ? (output, rows) : null;
    }

    /// <summary>A path under a prefix, with what follows it — <c>/api/runs/abc</c> is <c>abc</c>.</summary>
    private static bool Below(string path, string prefix, out string rest)
    {
        rest = "";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;

        rest = Uri.UnescapeDataString(path[prefix.Length..]);
        return rest.Length > 0 && !rest.Contains('/');
    }

    /// <summary>The same, for a path that ends in a verb: <c>/api/runs/abc/stop</c> is <c>abc</c>.</summary>
    private static bool Below(string path, string prefix, string suffix, out string rest)
    {
        rest = "";
        if (!path.EndsWith(suffix, StringComparison.Ordinal)) return false;

        return Below(path[..^suffix.Length], prefix, out rest);
    }
}

/// <summary>
/// Holds the connection to the host and answers what comes down it, for as long as the server runs.
/// </summary>
/// <remarks>
/// <strong>SignalR's client, not a socket of this server's own.</strong> The case this mode exists
/// for is a bench behind a customer's outbound proxy, which is exactly where a raw upgrade gets
/// refused; reconnection, keep-alives and a fallback transport are three things worth not owning.
/// The host's half is a SignalR hub for the same reason.
///
/// <strong>It answers with <c>SendAsync</c> and not <c>InvokeAsync</c>.</strong> The answer is
/// written from inside the handler that received the ask, and waiting there for a result would be
/// waiting on the loop that is running the handler.
///
/// <strong>A dropped connection is not a lost run.</strong> Nothing here touches a run: the bench
/// goes on measuring whether or not a host is listening, and the host asks again by the run id it
/// already has — which is the same arrangement that survives a host restarting.
/// </remarks>
public sealed class DialOutService(
    DialOutMode mode,
    DialOutCalls calls,
    ILogger<DialOutService> log) : BackgroundService
{
    /// <summary>How long to wait before dialing again when the connection could not be made at all.</summary>
    private static readonly TimeSpan TryAgainAfter = TimeSpan.FromSeconds(30);

    /// <summary>The hub method the host calls on this bench, and the one this bench answers with.</summary>
    public const string AskMethod = "Ask";
    public const string SaidMethod = "Said";

    private HubConnection? _connection;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (mode.Refusal is { } refusal)
        {
            // Half-configured, which is worth one line in the log rather than silence: somebody set
            // one of the two variables and believes this bench is dialing.
            log.LogError("This bench will not dial out: {Refusal}", refusal);
            return;
        }

        if (!mode.Enabled) return;

        var connection = new HubConnectionBuilder()
            .WithUrl(mode.Url, options => options.AccessTokenProvider = () => Task.FromResult<string?>(mode.Token))
            .WithAutomaticReconnect(new KeepTrying())
            .Build();

        _connection = connection;

        connection.On<BenchAsk>(AskMethod, async ask =>
        {
            BenchSaid said = calls.Answer(ask);
            try
            {
                foreach (BenchSaid part in Parts(said))
                    await connection.SendAsync(SaidMethod, part, stoppingToken);
            }
            catch (Exception ex) { log.LogWarning(ex, "The answer to {Ask} could not be sent; the host will ask again.", ask.Id); }
        });

        connection.Reconnecting += _ =>
        {
            log.LogWarning("The connection to the host dropped. Anything running here goes on running.");
            return Task.CompletedTask;
        };

        connection.Reconnected += _ =>
        {
            log.LogInformation("Reconnected to the host.");
            return Task.CompletedTask;
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await connection.StartAsync(stoppingToken);
                log.LogInformation("Dialed in to {Url}. This bench takes its instructions from there.", mode.Url);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The host may not be up yet, or may be up and refusing this token. Both are worth
                // saying and neither is worth stopping the server for: everything else this bench
                // does works without a host.
                log.LogWarning(ex, "Could not dial {Url}; trying again in {Seconds} seconds.", mode.Url, TryAgainAfter.TotalSeconds);
                try { await Task.Delay(TryAgainAfter, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (_connection is not null) await _connection.DisposeAsync();
    }

    /// <summary>
    /// How long one part of an answer may be, in characters: 256 K.
    /// </summary>
    /// <remarks>
    /// Written as JSON inside the hub's own message, a character takes at most six bytes — a quote
    /// is two, and anything outside ASCII is escaped as <c>\uXXXX</c> by the serializer's default
    /// encoder — so the largest part comes to 1.5 MB on the wire, and a host whose hub takes
    /// messages of 2 MB receives any answer, whatever its body holds. Small enough, too, that a
    /// part crosses a slow line in seconds, and the host's wait for the next one starts again with each.
    /// </remarks>
    internal const int PartLength = 256 * 1024;

    /// <summary>
    /// An answer as the messages that carry it (<see cref="BenchSaid"/>): itself when its body fits
    /// in one, and otherwise the body cut into stretches of at most <paramref name="length"/>
    /// characters, numbered, in order.
    /// </summary>
    internal static IEnumerable<BenchSaid> Parts(BenchSaid said, int length = PartLength)
    {
        string body = said.Body ?? "";

        if (body.Length <= length)
        {
            yield return said;
            yield break;
        }

        var cuts = new List<(int Start, int Length)>();

        for (int start = 0; start < body.Length;)
        {
            int take = Math.Min(length, body.Length - start);

            // Never between the two halves of a character outside the basic plane: each half on its
            // own is not a character, and a serializer may write it as something else entirely.
            // A part one character long has nowhere to move the cut to, and must not end up empty.
            if (take > 1 && start + take < body.Length && char.IsHighSurrogate(body[start + take - 1])) take--;

            cuts.Add((start, take));
            start += take;
        }

        for (int i = 0; i < cuts.Count; i++)
            yield return new BenchSaid(said.Id, said.Status, body.Substring(cuts[i].Start, cuts[i].Length), i, cuts.Count);
    }

    /// <summary>
    /// Reconnection that does not give up.
    /// </summary>
    /// <remarks>
    /// SignalR's own policy tries four times over half a minute and then stops, which suits a browser
    /// tab and not a bench on a factory floor: the thing it is waiting for may be a line that comes
    /// back on Monday. Quick at first, because most drops are a moment long, then steady.
    /// </remarks>
    private sealed class KeepTrying : IRetryPolicy
    {
        private static readonly TimeSpan[] First =
            [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

        private static readonly TimeSpan Steady = TimeSpan.FromSeconds(30);

        public TimeSpan? NextRetryDelay(RetryContext context)
            => context.PreviousRetryCount < First.Length ? First[context.PreviousRetryCount] : Steady;
    }
}
