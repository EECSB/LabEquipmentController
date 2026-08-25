using System.Net.Http.Json;

namespace LabEquipmentController.Web.Client.Services;

/// <summary>
/// Whether the server is answering, and one place for the whole app to hear about it.
///
/// The page and the server are two programs. The page keeps running when the server stops —
/// that is what a WebAssembly app is — so the first anyone hears of a server that has gone is
/// a call failing, and until now that call was on its own: each one either handled the failure
/// or took the page down with it.
///
/// This watches every call instead. <see cref="ServerWatchHandler"/> sits in the HTTP pipeline
/// and tells this what happened; anything that wants to know listens here. A component asking
/// for instruments should not also be the thing that decides the server has gone.
/// </summary>
public sealed class ServerWatch
{
    private readonly Func<HttpClient> _http;

    /// <param name="http">
    /// Resolved when it is needed rather than taken now: the client this watches is built *with*
    /// this watch in its pipeline, so asking for one here while being constructed for the other
    /// would be a circle. By the time anything retries, both exist.
    /// </param>
    public ServerWatch(Func<HttpClient> http) => _http = http;

    /// <summary>True once a call has failed to reach the server at all.</summary>
    public bool Unreachable { get; private set; }

    /// <summary>What the failure said, for the box that reports it.</summary>
    public string? Reason { get; private set; }

    /// <summary>True while <see cref="RetryAsync"/> is asking.</summary>
    public bool Checking { get; private set; }

    public event Action? Changed;

    /// <summary>
    /// A call could not reach the server.
    ///
    /// Only the first one is worth saying anything about: when a server goes away every call in
    /// flight fails at once, and four identical reports of one outage is noise.
    /// </summary>
    internal void Lost(string reason)
    {
        if (Unreachable) return;
        Unreachable = true;
        Reason = reason;
        Changed?.Invoke();
    }

    /// <summary>A call got through, so whatever was wrong is over.</summary>
    internal void Reached()
    {
        if (!Unreachable) return;
        Unreachable = false;
        Reason = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Ask again, on purpose.
    ///
    /// The cheapest call the API has, so this says whether the server is there rather than
    /// whether some particular thing works. Success clears the state through the same handler
    /// every other call goes through — there is no second path to being reachable.
    /// </summary>
    public async Task<bool> RetryAsync()
    {
        if (Checking) return false;

        Checking = true;
        Changed?.Invoke();
        try
        {
            _ = await _http().GetFromJsonAsync<List<Contracts.SessionDto>>("api/sessions");
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Checking = false;
            Changed?.Invoke();
        }
    }
}

/// <summary>
/// Watches every call for the one failure that means the server is gone.
///
/// In the pipeline rather than at the call sites, because the call sites are the wrong place to
/// notice: there are dozens, they are added all the time, and the one that happens to be first
/// when a server dies is whichever the user pressed. A handler sees them all.
///
/// It reports and rethrows. Whatever asked still gets its exception and can still say something
/// local about it — a command that could not be sent still belongs in the console's log — and
/// this is only about the one thing that is true of all of them.
/// </summary>
public sealed class ServerWatchHandler : DelegatingHandler
{
    private readonly ServerWatch _watch;

    public ServerWatchHandler(ServerWatch watch) => _watch = watch;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await base.SendAsync(request, cancellationToken);

            // Reached, whatever it answered. A 404 or a 500 is the server disagreeing with the
            // request, which is a different thing from there being no server.
            _watch.Reached();
            return response;
        }
        catch (HttpRequestException ex)
        {
            // The browser's fetch fails this way for a refused connection, a DNS failure and a
            // dropped network alike — all of them "nothing answered", none of them distinguishable
            // from here. TypeError: Failed to fetch is what it says in the console.
            _watch.Lost(ex.Message);
            throw;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A timeout, not a cancellation: nobody asked for this to stop, so the server did not
            // answer in time. Same conclusion from the page's side.
            _watch.Lost("The server did not answer in time.");
            throw;
        }
    }
}
