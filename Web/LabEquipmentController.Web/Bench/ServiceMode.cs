using System.Security.Cryptography;
using System.Text;

namespace LabEquipmentController.Web.Bench;

/// <summary>
/// The server as a bench for a host, rather than an app for the person at the keyboard.
/// </summary>
/// <remarks>
/// Switched on by <c>LEC_SERVICE_TOKEN</c>, and off without it — the standalone behaviour,
/// unchanged. In service mode every API route and the hub take that token as a bearer; opening
/// the page no longer closes the bench, because a host's people open it while a measurement is
/// running; an instrument a run holds refuses everything else until the run ends; and the AI
/// features take their connection from the host with each request, rather than from a key of
/// this server's own that everyone who can reach the page would share.
///
/// <c>LEC_PATH_BASE</c> is independent of the token. It serves the page and the API below a path,
/// which is how a host proxies this server at a path of its own; the page's one absolute
/// reference, its <c>&lt;base href&gt;</c>, is rewritten to match, and everything else it asks for
/// is relative to that.
///
/// The token is compared in constant time. Not because anyone is expected to time a bench on a
/// private network, but because the comparison is one line either way and the right line costs
/// nothing.
/// </remarks>
public sealed class ServiceMode
{
    public const string TokenVariable = "LEC_SERVICE_TOKEN";
    public const string PathBaseVariable = "LEC_PATH_BASE";

    public const string Challenge = "This bench takes a bearer token on every API route and on the hub.";
    public const string HeldMessage = "This instrument is held by a run, and takes nothing else until the run ends.";
    public const string HostConnectionsMessage =
        "The host that runs this bench supplies the AI connection with each request; nothing is set here.";
    public const string NoHostConnectionMessage =
        "The host did not send an AI connection with this request, and this bench keeps none of its own.";
    public const string NotAServiceMessage =
        "This server takes its AI connection from its own settings, not from a request.";

    private readonly byte[] _token;

    private readonly bool _dials;

    public ServiceMode(IConfiguration configuration)
        : this(configuration[TokenVariable], configuration[PathBaseVariable], configuration[DialOutMode.UrlVariable]) { }

    public ServiceMode(string? token, string? pathBase = null, string? dialUrl = null)
    {
        Token = (token ?? "").Trim();
        _token = Encoding.UTF8.GetBytes(Token);
        PathBase = NormalizePathBase(pathBase);
        _dials = (dialUrl ?? "").Trim().Length > 0;
    }

    public string Token { get; }

    /// <summary>A token is configured: the API and the hub take it as a bearer.</summary>
    public bool Enabled => Token.Length > 0;

    /// <summary>
    /// A host drives this server, whichever way round the dialing goes: it calls with a token
    /// (<see cref="Enabled"/>), or this server dials it (<see cref="DialOutMode"/>).
    /// </summary>
    /// <remarks>
    /// What follows from it is the same either way, and is nothing to do with the bearer: a run the
    /// host started holds its instruments against the page, and opening the page lets go of the idle
    /// ones rather than all of them. A dial-out bench that forgot this would have its host's
    /// measurement stopped by somebody opening its console to watch it.
    ///
    /// Not what decides where the AI connection comes from — that is <see cref="Enabled"/>, because
    /// it arrives inside a request a host proxies, and nothing proxies a bench that dials out.
    /// </remarks>
    public bool Driven => Enabled || _dials;

    /// <summary>Where the app is served from: empty for the root, or a path with a leading slash and no trailing one.</summary>
    public string PathBase { get; }

    /// <summary>
    /// The path as configured, however it was written: <c>bench</c>, <c>/bench</c> and <c>/bench/</c>
    /// are all <c>/bench</c>, and a bare slash or nothing at all is the root.
    /// </summary>
    public static string NormalizePathBase(string? raw)
    {
        string path = (raw ?? "").Trim().TrimEnd('/');
        if (path.Length == 0) return "";
        return path.StartsWith('/') ? path : "/" + path;
    }

    /// <summary>
    /// Whether a request carries the token: as <c>Authorization: Bearer</c>, or as <c>access_token</c>
    /// in the query, which is how SignalR's own client carries one on a WebSocket, where there are
    /// no headers to set. With no token configured everything is admitted, which is the standalone app.
    /// </summary>
    public bool Admits(HttpRequest request)
    {
        if (!Enabled) return true;

        string? presented = null;
        string authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            presented = authorization["Bearer ".Length..].Trim();
        else if (request.Query.TryGetValue("access_token", out var fromQuery))
            presented = fromQuery.ToString();

        if (presented is null) return false;
        byte[] given = Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(given, _token);
    }
}
