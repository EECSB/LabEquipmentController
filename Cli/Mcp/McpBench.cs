namespace LabEquipmentController.Cli;

/// <summary>
/// The instruments <c>lec mcp</c> was started for, as the assistant's bench.
///
/// The addresses on the command line are the whole of what a model can reach — the MCP
/// guide's "the folder is the sandbox", for a bench. There is no tool to dial another address:
/// what a model names can come from anywhere, a line in a datasheet included, and an
/// instrument somebody else is using is one network hop from any address it could make up.
///
/// An instrument that was off when the client started the server is tried again when the
/// instruments are next listed, at most every fifteen seconds: Claude Code starts its servers
/// when it starts, which is often before the bench is switched on.
/// </summary>
public sealed class McpBench : IAsyncDisposable
{
    /// <summary>How long a listing waits before trying an instrument that did not answer again.</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(15);

    private readonly IReadOnlyList<Endpoint> _addresses;
    private readonly int _timeoutMs;
    private readonly TextWriter _log;
    private readonly Dictionary<Endpoint, InstrumentSession> _open = [];
    private readonly Dictionary<Endpoint, DateTime> _lastTried = [];
    private readonly SemaphoreSlim _connecting = new(1, 1);

    public McpBench(IReadOnlyList<Endpoint> addresses, int timeoutMs, TextWriter log)
    {
        _addresses = addresses;
        _timeoutMs = timeoutMs;
        _log = log;
        // Read under the same lock the dialling writes under: a client may call a tool while a
        // listing is still dialling.
        Sessions = new SessionBench(() =>
        {
            lock (_open) return [.. _addresses.Where(_open.ContainsKey).Select(a => _open[a])];
        });
    }

    /// <summary>The open sessions, as the tools see them.</summary>
    public SessionBench Sessions { get; }

    /// <summary>The addresses it was started for, as typed.</summary>
    public IReadOnlyList<Endpoint> Addresses => _addresses;

    /// <summary>
    /// Dials every address that is not open and has not been tried within <see cref="RetryEvery"/>,
    /// all at once, saying on standard error what came of each — never on standard output, which
    /// is the protocol's.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        await _connecting.WaitAsync(ct);
        try
        {
            DateTime now = DateTime.UtcNow;
            List<Endpoint> due;
            lock (_open)
            {
                due = [.. _addresses.Where(a => !_open.ContainsKey(a)
                                             && (!_lastTried.TryGetValue(a, out DateTime last) || now - last >= RetryEvery))];
            }
            foreach (Endpoint a in due) _lastTried[a] = now;

            var dialled = await Task.WhenAll(due.Select(async a => (a, session: await TryOpenAsync(a, ct))));
            lock (_open)
            {
                foreach (var (a, session) in dialled)
                {
                    if (session is not null) _open[a] = session;
                }
            }
        }
        finally
        {
            _connecting.Release();
        }
    }

    private async Task<InstrumentSession?> TryOpenAsync(Endpoint address, CancellationToken ct)
    {
        IInstrumentClient client = address.CreateClient(_timeoutMs);
        try
        {
            await client.ConnectAsync(ct);
            string identity = (await client.QueryAsync("*IDN?", ct)).Trim();
            await _log.WriteAsync($"{address}: {identity}\n");
            return new InstrumentSession(client, identity, InstrumentProfile.ForIdentity(identity), _timeoutMs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            client.Dispose();
            await _log.WriteAsync($"{address}: did not connect ({ex.Message}). It is tried again when the instruments are listed.\n");
            return null;
        }
    }

    /// <summary>Hands each front panel back and closes the connections, as the GUI does on exit.</summary>
    public async ValueTask DisposeAsync()
    {
        List<InstrumentSession> open;
        lock (_open)
        {
            open = [.. _open.Values];
            _open.Clear();
        }
        foreach (InstrumentSession session in open)
            await session.CloseAsync();
        _connecting.Dispose();
    }
}

/// <summary>
/// <see cref="McpBench"/>'s sessions as the tools' bench, trying the instruments that were not
/// answering before each listing.
/// </summary>
internal sealed class RetryingBench(McpBench bench) : IAssistantBench
{
    public async Task<IReadOnlyList<BenchInstrument>> ListAsync(CancellationToken ct)
    {
        await bench.ConnectAsync(ct);
        return await bench.Sessions.ListAsync(ct);
    }

    public Task<CommandReference?> CatalogAsync(BenchInstrument instrument, CancellationToken ct)
        => bench.Sessions.CatalogAsync(instrument, ct);

    public Task<string> QueryAsync(BenchInstrument instrument, string command, CancellationToken ct)
        => bench.Sessions.QueryAsync(instrument, command, ct);

    public Task SendAsync(BenchInstrument instrument, string command, CancellationToken ct)
        => bench.Sessions.SendAsync(instrument, command, ct);

    public Task<byte[]> CaptureScreenAsync(BenchInstrument instrument, CancellationToken ct)
        => bench.Sessions.CaptureScreenAsync(instrument, ct);
}
