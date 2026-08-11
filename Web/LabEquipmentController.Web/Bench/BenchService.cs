using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using LabEquipmentController.Web.Client.Contracts;

namespace LabEquipmentController.Web.Bench;

/// <summary>
/// Every open instrument connection in the process, and the only door to them.
/// </summary>
/// <remarks>
/// A singleton, because a connection is not per-browser: two people with the page open are
/// looking at one bench, and the second one must not get a second socket to an instrument
/// that permits a single conversation. That is the same reasoning behind
/// <see cref="SerializedInstrumentClient"/> one level down — this class stops two *sessions*
/// racing, that one stops two *calls* racing.
///
/// Sessions are held until closed rather than tied to a browser circuit. Closing a laptop
/// lid should not drop an instrument mid-sweep, and a sweep that survives a refresh is the
/// main thing the web version has over the desktop one.
/// </remarks>
public sealed class BenchService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ILogger<BenchService> _log;

    public BenchService(ILogger<BenchService> log) => _log = log;

    public sealed record Session(
        string Id,
        string Address,
        InstrumentTransport Transport,
        string Identity,
        InstrumentFamily Family,
        InstrumentProfile Profile,
        IInstrumentClient Client);

    // ------------------------------------------------------------------ interfaces

    public IReadOnlyList<LocalInterfaceDto> Interfaces() =>
        NetworkScanner.GetLocalInterfaces()
            .Select(i => new LocalInterfaceDto(i.Name, i.Address.ToString(), i.PrefixLength, i.HostCount, i.HasGateway))
            .ToList();

    // ------------------------------------------------------------------------ scan

    public async Task<ScanReport> ScanAsync(ScanRequest req, IProgress<DeviceDto>? found, CancellationToken ct)
    {
        var interfaces = NetworkScanner.GetLocalInterfaces();
        if (interfaces.Count == 0)
            return new ScanReport([], 0, false, "No usable network interface. In Docker this usually means the container is on a bridge network rather than the host's — see the compose file.");

        LocalInterface chosen =
            (req.InterfaceAddress is { Length: > 0 } want
                ? interfaces.FirstOrDefault(i => i.Address.ToString() == want)
                : null)
            ?? interfaces.FirstOrDefault(i => i.HasGateway)
            ?? interfaces[0];

        var ports = new List<int>();
        foreach (string p in (req.Ports ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(p.Trim(), out int n) || n is < 1 or > 65535)
                return new ScanReport([], 0, false, $"'{p.Trim()}' is not a port number.");
            ports.Add(n);
        }
        if (ports.Count == 0) ports.AddRange(NetworkScanner.CommonScpiPorts);

        List<IPAddress> hosts;
        bool capped;
        if (req.Range is { Length: > 0 } spec)
        {
            if (!HostRange.TryParse(spec, chosen.Address, out var range, out string error) || range is null)
                return new ScanReport([], 0, false, error.Length > 0 ? error : $"'{spec}' is not an address range.");
            hosts = range.Enumerate(65536, out capped);
        }
        else
        {
            hosts = NetworkScanner.EnumerateHosts(chosen.Address, chosen.Mask, 65536, out capped);
        }

        var progress = found is null ? null : new Progress<ScpiDevice>(d => found.Report(Map(d)));
        try
        {
            var devices = await NetworkScanner.ScanAsync(
                hosts, ports, req.TimeoutMs, req.TimeoutMs, null, ct, progress);
            return new ScanReport(devices.Select(Map).ToList(), hosts.Count, capped, null);
        }
        catch (OperationCanceledException)
        {
            return new ScanReport([], hosts.Count, capped, "Scan cancelled.");
        }
    }

    private static DeviceDto Map(ScpiDevice d) =>
        new(d.Address.ToString(), d.Port, d.TransportName, d.Identity);

    // -------------------------------------------------------------------- sessions

    public IReadOnlyList<SessionDto> Sessions() => _sessions.Values.Select(Describe).ToList();

    public SessionDto? Session_(string id) => _sessions.TryGetValue(id, out var s) ? Describe(s) : null;

    internal Session? Raw(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    public async Task<SessionDto> ConnectAsync(ConnectRequest req, CancellationToken ct)
    {
        var (host, transport, port, device) = ParseAddress(req.Address);

        // One session per address. Reconnecting to something already open would open a
        // second socket to an instrument that answers one conversation at a time.
        var existing = _sessions.Values.FirstOrDefault(s =>
            string.Equals(s.Address, host, StringComparison.OrdinalIgnoreCase) && s.Transport == transport);
        if (existing is not null) return Describe(existing);

        IInstrumentClient inner = transport == InstrumentTransport.Vxi11
            ? new Vxi11Client(host, device)
            : new ScpiClient(host, port);
        inner.TimeoutMs = req.TimeoutMs;
        IInstrumentClient client = new SerializedInstrumentClient(inner);

        try
        {
            await client.ConnectAsync(ct);
            string idn = (await client.QueryAsync("*IDN?", ct)).Trim();
            var family = InstrumentProfile.FamilyForIdentity(idn);
            var profile = InstrumentProfile.ForIdentity(idn);

            var session = new Session(Guid.NewGuid().ToString("N"), host, transport, idn, family, profile, client);
            _sessions[session.Id] = session;
            _log.LogInformation("Connected {Address} as {Family}", host, family);
            return Describe(session);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<bool> DisconnectAsync(string id)
    {
        if (!_sessions.TryRemove(id, out var s)) return false;
        // Hand the front panel back before dropping the socket, exactly as the desktop app
        // does — an instrument left in remote mode ignores its own knobs.
        try { await s.Client.ReturnToLocalAsync(); } catch { /* best effort */ }
        s.Client.Dispose();
        return true;
    }

    public async Task<CommandReply> SendAsync(string id, string text, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var s))
            return new CommandReply(text, null, false, 0, "No such session — it may have been closed.");

        bool isQuery = ScpiClient.IsQuery(text);
        var clock = Stopwatch.StartNew();
        try
        {
            if (isQuery)
            {
                string reply = (await s.Client.QueryAsync(text, ct)).Trim();
                return new CommandReply(text, reply, true, clock.Elapsed.TotalSeconds, null);
            }
            await s.Client.SendAsync(text, ct);
            return new CommandReply(text, null, false, clock.Elapsed.TotalSeconds, null);
        }
        catch (Exception ex)
        {
            return new CommandReply(text, null, isQuery, clock.Elapsed.TotalSeconds, ex.Message);
        }
    }

    // --------------------------------------------------------------------- capture

    public async Task<WaveformDto> WaveformAsync(string id, int channel, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var s))
            return new WaveformDto([], [], 0, "No such session.");
        if (!s.Profile.SupportsWaveformCapture)
            return new WaveformDto([], [], 0, $"No waveform-transfer dialect is documented for {s.Profile.Name}.");

        try
        {
            int was = s.Client.TimeoutMs;
            s.Client.TimeoutMs = Math.Max(was, 15000);
            try
            {
                var capture = await WaveformReader.ReadAsync(s.Client, s.Profile.WaveformDialect, channel, ct);
                return new WaveformDto(
                    capture.Samples.Select(x => x.Time).ToList(),
                    capture.Samples.Select(x => x.Voltage).ToList(),
                    capture.XIncrement, null);
            }
            finally { s.Client.TimeoutMs = was; }
        }
        catch (Exception ex) { return new WaveformDto([], [], 0, ex.Message); }
    }

    public async Task<ScreenshotDto> ScreenshotAsync(string id, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var s))
            return new ScreenshotDto("", "", 0, "", "No such session.");
        string? cmd = s.Profile.ScreenCaptureCommand;
        if (string.IsNullOrEmpty(cmd))
            return new ScreenshotDto("", "", 0, "", $"No screen-capture command is documented for {s.Profile.Name}.");

        try
        {
            int was = s.Client.TimeoutMs;
            s.Client.TimeoutMs = Math.Max(was, 20000);
            try
            {
                foreach (string setup in s.Profile.ScreenCaptureSetup)
                    await s.Client.SendAsync(setup, ct);
                byte[] data = await s.Client.QueryBinaryAsync(cmd, ct);
                if (data.Length == 0) return new ScreenshotDto("", "", 0, cmd, "The instrument returned no image data.");
                return new ScreenshotDto(ImageType(data), Convert.ToBase64String(data), data.Length, cmd, null);
            }
            finally { s.Client.TimeoutMs = was; }
        }
        catch (Exception ex) { return new ScreenshotDto("", "", 0, cmd, ex.Message); }
    }

    /// <summary>
    /// The format is the instrument's choice — a Rigol sends BMP, a Tektronix set to PNG
    /// sends PNG — so it is read from the bytes. A browser shown the wrong MIME type
    /// renders a broken-image icon and says nothing about why.
    /// </summary>
    internal static string ImageType(ReadOnlySpan<byte> d) =>
        d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 ? "image/png"
        : d.Length >= 2 && d[0] == 0x42 && d[1] == 0x4D ? "image/bmp"
        : d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF ? "image/jpeg"
        : d.Length >= 3 && d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46 ? "image/gif"
        : "application/octet-stream";

    // --------------------------------------------------------------------- helpers

    /// <summary>
    /// A bare host, host:port, vxi://host, or a VISA resource string — the same spellings
    /// the CLI accepts, delegated to Core so all three front ends agree.
    /// </summary>
    internal static (string Host, InstrumentTransport Transport, int Port, string Device) ParseAddress(string? text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("No address given.");

        if (VisaResource.TryParse(text, out var visa))
            return (visa.Host, visa.Transport, visa.Port, visa.DeviceName);

        if (text.StartsWith("vxi://", StringComparison.OrdinalIgnoreCase))
        {
            string h = text["vxi://".Length..].Trim().TrimEnd('/');
            if (h.Length == 0) throw new ArgumentException("vxi:// needs a host.");
            return (h, InstrumentTransport.Vxi11, Vxi11Client.PortmapperPort, "inst0");
        }
        if (text.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            text = text["tcp://".Length..].Trim().TrimEnd('/');

        string host = text;
        int port = 5025;
        int colon = text.LastIndexOf(':');
        bool bracketed = text.StartsWith('[');
        if (colon > 0 && (bracketed || text.IndexOf(':') == colon))
        {
            string tail = text[(colon + 1)..];
            if (int.TryParse(tail, out int p))
            {
                if (p is < 1 or > 65535) throw new ArgumentException($"'{tail}' is not a port number.");
                host = text[..colon];
                port = p;
            }
            else if (bracketed) throw new ArgumentException($"'{tail}' is not a port number.");
        }
        host = host.Trim('[', ']').Trim();
        if (host.Length == 0) throw new ArgumentException("No host in the address.");

        // Port 111 is the RPC portmapper, which is how VXI-11 is reached; read as a raw
        // socket it connects and then waits for a reply that never comes.
        var transport = port == Vxi11Client.PortmapperPort ? InstrumentTransport.Vxi11 : InstrumentTransport.RawSocket;
        return (host, transport, port, "inst0");
    }

    private static SessionDto Describe(Session s)
    {
        var catalog = CommandReference.ForFamily(s.Family);
        return new SessionDto(
            s.Id, s.Address,
            s.Transport == InstrumentTransport.Vxi11 ? "VXI-11" : "Raw socket",
            s.Identity, s.Family.ToString(), s.Profile.Name,
            s.Profile.Commands.Select(c => new QuickCommandDto(c.Label, c.Command)).ToList(),
            s.Profile.SupportsWaveformCapture,
            s.Profile.ScreenCaptureCommand is { Length: > 0 },
            s.Profile.ReadoutFunctions.Select(r => new ReadoutDto(r.Label, r.Query, r.Unit)).ToList(),
            catalog?.Instrument, catalog?.Commands.Count ?? 0);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList()) await DisconnectAsync(id);
    }
}
