using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Net;
using LabEquipmentController.Web.Client.Contracts;
using LabEquipmentController.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

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
/// Sessions outlive a browser circuit but not the browser. Closing a laptop lid, reloading,
/// or losing the connection for a moment must not drop an instrument mid-sweep - a sweep that
/// survives a refresh is the main thing the web version has over the desktop one - but a bench
/// nobody is looking at any more is a set of instruments held open for nothing, and the next
/// person to open the app finds a bench of connections to instruments that may since have been
/// switched off. So the bench is let go a short while after the last page closes. See Watching.
/// </remarks>
public sealed class BenchService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ILogger<BenchService> _log;
    private readonly IHubContext<BenchHub> _hub;

    /// <summary>
    /// How many runs are driving each session, by id.
    ///
    /// Counted rather than a flag, because two things can hold the same instrument at once — a
    /// script started from one console and a readout polling in another window — and a flag
    /// cleared by whichever finished first would unlock a link that is still being driven.
    /// Absent means nought; nothing is ever stored as nought.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _driving = new();

    /// <summary>
    /// How long the bench outlives the last page that was watching it.
    ///
    /// Long enough to cover a reload - which disconnects and reconnects, and on a cold cache has
    /// to fetch the whole runtime again before it can say hello - and short enough that the app
    /// opened again later starts on an empty bench rather than on yesterday's connections.
    /// </summary>
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(20);

    private readonly Lock _presence = new();
    private int _watching;
    private CancellationTokenSource? _closing;

    /// <summary>The server as a host's bench, or null for the standalone app. See <see cref="ServiceMode"/>.</summary>
    private readonly ServiceMode? _service;

    public BenchService(ILogger<BenchService> log, IHubContext<BenchHub> hub, ServiceMode? service = null)
        => (_log, _hub, _service) = (log, hub, service);

    private bool Service => _service?.Enabled == true;

    /// <summary>
    /// In service mode an instrument a run holds is held against everything, not only against its
    /// console. The console locks itself in the page; a host's request, or a second person's, does
    /// not, and a command that lands between two steps of a measurement changes what it measured.
    /// </summary>
    private bool Held(string id) => Service && IsDriven(id);

    /// <summary>
    /// One open conversation with one instrument.
    /// </summary>
    /// <param name="Host">The machine it is on. Two instruments can share this.</param>
    /// <param name="Address">
    /// Where the instrument is, written the way you would type it back in — host and port, or
    /// a vxi:// address with its device name where that is not the default. This is the thing
    /// that identifies a session, not the host: a GPIB or serial gateway is one address with
    /// several instruments behind it, on different ports or different device names.
    /// </param>
    public sealed record Session(
        string Id,
        string Host,
        string Address,
        InstrumentTransport Transport,
        string Identity,
        InstrumentFamily Family,
        InstrumentProfile Profile,
        /// <summary>
        /// Serialized, always: a connection carries one conversation at a time, and this is
        /// the gate that makes that true. Typed as the concrete class rather than the
        /// interface because what it holds beyond the interface — the queue of commands
        /// waiting their turn — is shown to the user.
        /// </summary>
        SerializedInstrumentClient Client);

    // ------------------------------------------------------------------ interfaces

    public IReadOnlyList<LocalInterfaceDto> Interfaces() =>
        NetworkScanner.GetLocalInterfaces()
            .Select(i => new LocalInterfaceDto(
                i.Name, i.Address.ToString(), i.PrefixLength, i.HostCount, i.HasGateway,
                WholeRange(i.Address, i.Mask, i.PrefixLength)))
            .ToList();

    /// <summary>
    /// Every host on an adapter's subnet, written the way someone would type it into the range
    /// box themselves: 192.168.1.1-192.168.1.254 for a machine at 192.168.1.28 on a /24.
    ///
    /// First and last <em>host</em>, so the network and broadcast addresses are left out — they
    /// are not instruments, and leaving them out is what an empty box has always done. A /31 is
    /// a point-to-point link and a /32 is one host: both ends are usable there and there is
    /// nothing to leave out, which is the same rule Core's HostRange applies to a CIDR.
    /// </summary>
    private static string WholeRange(IPAddress address, IPAddress mask, int prefixLength)
    {
        byte[] a = address.GetAddressBytes(), m = mask.GetAddressBytes();
        if (a.Length != 4 || m.Length != 4) return address.ToString();

        uint host = ToUInt32(a), bits = ToUInt32(m);
        uint network = host & bits, broadcast = network | ~bits;

        (uint first, uint last) = prefixLength >= 31
            ? (network, broadcast)
            : (network + 1, broadcast - 1);

        return Dotted(first) + "-" + Dotted(last);

        static uint ToUInt32(byte[] b) => ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

        static string Dotted(uint v) =>
            new IPAddress(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }).ToString();
    }

    // ------------------------------------------------------------------------ scan

    /// <summary>
    /// Sweep the chosen interface for instruments.
    /// </summary>
    /// <param name="events">
    /// Called as the sweep proceeds, or null to hear only the final report. A plain delegate
    /// rather than an <see cref="IProgress{T}"/> on purpose: <c>Progress&lt;T&gt;</c> posts
    /// each report to the thread pool, and two reports posted in order can arrive out of it —
    /// which for a progress count means a bar that walks backwards. This is called on the
    /// scanner's own thread, in order, and the caller is expected to do something cheap.
    /// </param>
    public async Task<ScanReport> ScanAsync(ScanRequest req, Action<ScanEvent>? events, CancellationToken ct)
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

        // Said in words by the half that worked it out. "5025,111" typed into a box is not
        // the same claim as "port(s) 5025, 111", and blank is not the same claim as "the
        // whole subnet" until something has decided which subnet — someone who narrowed a
        // scan and found nothing has to be able to see that they narrowed it.
        string detail = $"{hosts.Count:N0} address(es) on port(s) {string.Join(", ", ports)}"
                      + (capped ? " (cut short at 65,536)" : "");

        events?.Invoke(new ScanEvent(ScanStage.Started, hosts.Count, 0, null, detail, null));

        var found = events is null ? null
            : new Immediate<ScpiDevice>(d => events(new ScanEvent(ScanStage.Found, hosts.Count, 0, Map(d), null, null)));
        var probed = events is null ? null
            : new Immediate<int>(n => events(new ScanEvent(ScanStage.Probed, hosts.Count, n, null, null, null)));

        try
        {
            var devices = await NetworkScanner.ScanAsync(
                hosts, ports, req.TimeoutMs, req.TimeoutMs, probed, ct, found);
            return new ScanReport(devices.Select(Map).ToList(), hosts.Count, capped, null);
        }
        catch (OperationCanceledException)
        {
            // Whatever answered before the stop has already been reported and is already on
            // screen. Partial results are useful, so nothing here takes them away.
            return new ScanReport([], hosts.Count, capped, "Scan stopped.");
        }
    }

    /// <summary>
    /// Every serial port the server has, as unprobed rows. Opens nothing — this is what the
    /// card shows the moment it switches to Serial, and it is already enough to pick a port
    /// and connect (SPEC §17).
    /// </summary>
    public IReadOnlyList<DeviceDto> SerialPortRows() =>
        SerialScanner.List().Select(Map).ToList();

    /// <summary>
    /// The serial half of the scan card: open the chosen ports and ask each for its identity.
    ///
    /// Reports through the same <see cref="ScanEvent"/> stream the subnet sweep uses, because
    /// it is the same three facts — how much there is, how far along, and a row the moment it
    /// is known. Where it differs is that every port reports, answered or not: a serial port
    /// that says nothing is still a port on the server and still connectable at settings this
    /// did not try, so it stays in the list rather than being left out of it.
    /// </summary>
    public async Task<ScanReport> ScanSerialAsync(SerialScanRequest req, Action<ScanEvent>? events, CancellationToken ct)
    {
        var available = SerialPorts.Names();

        List<string> ports;
        if (req.Port is { Length: > 0 } one)
        {
            // Named rather than chosen from the list: the browser is a page away from the
            // machine holding the port, and the list it was shown may be a minute old.
            if (!available.Any(p => string.Equals(p, one, StringComparison.OrdinalIgnoreCase)))
                return new ScanReport([], 0, false, $"The server has no serial port called '{one}'.");
            ports = [one];
        }
        else
        {
            ports = available.ToList();
        }

        if (ports.Count == 0)
            return new ScanReport([], 0, false, "The server has no serial ports. In Docker they have to be passed in with `devices:` — see the compose file.");

        var bauds = SerialScanner.ParseBaudRates(req.Bauds);
        if (bauds.Count == 0) bauds.AddRange(SerialScanner.CommonBaudRates);

        string detail = $"{ports.Count} port(s) at {string.Join(", ", bauds)}";
        events?.Invoke(new ScanEvent(ScanStage.Started, ports.Count, 0, null, detail, null));

        var found = events is null ? null
            : new Immediate<SerialDevice>(d => events(new ScanEvent(ScanStage.Found, ports.Count, 0, Map(d), null, null)));
        var probed = events is null ? null
            : new Immediate<int>(n => events(new ScanEvent(ScanStage.Probed, ports.Count, n, null, null, null)));

        try
        {
            var devices = await SerialScanner.ScanAsync(ports, bauds, SerialIdnTimeoutMs, probed, ct, found);
            return new ScanReport(devices.Select(Map).ToList(), ports.Count, false, null);
        }
        catch (OperationCanceledException)
        {
            return new ScanReport([], ports.Count, false, "Scan stopped.");
        }
    }

    /// <summary>
    /// Long enough for a slow instrument to compose an identity at 9600, short enough that
    /// five rates against a dead port is a wait rather than a hang. Fixed rather than taken
    /// from the request, exactly as the desktop's is: this bounds discovery, not instrument
    /// communication, and the two want very different numbers.
    /// </summary>
    private const int SerialIdnTimeoutMs = 1500;

    /// <summary>
    /// An <see cref="IProgress{T}"/> that reports on the thread that called it, rather than
    /// posting to the thread pool as <see cref="Progress{T}"/> does. The scanner reports from
    /// its own tasks and there is no UI thread here to get back to; what there is instead is
    /// an ordering requirement, which posting would lose.
    /// </summary>
    private sealed class Immediate<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static DeviceDto Map(ScpiDevice d) =>
        new(d.Address.ToString(), d.Port, d.TransportName, d.Identity, d.TypedAddress);

    /// <summary>
    /// A serial port as the same four columns. The port number is zero because a serial port
    /// has none — the second column carries the line settings instead, which is the whole
    /// reason <see cref="DeviceDto.Settings"/> exists.
    /// </summary>
    private static DeviceDto Map(SerialDevice d) =>
        new(d.PortName, 0, d.TransportName, d.IdentityText, d.TypedAddress, d.SettingsText, d.Answered);

    // -------------------------------------------------------------------- sessions

    public IReadOnlyList<SessionDto> Sessions() => _sessions.Values.Select(Describe).ToList();

    public SessionDto? Session_(string id) => _sessions.TryGetValue(id, out var s) ? Describe(s) : null;

    internal Session? Raw(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    /// <summary>Every open session, as it is held rather than as the browser is shown it.</summary>
    internal IReadOnlyList<Session> Raw() => _sessions.Values.ToList();

    /// <summary>
    /// What each DEVICE line of a script is bound to on this bench: the table over the editor.
    /// </summary>
    /// <remarks>
    /// Worked out here, by the rule the desktop's device strip is filled by
    /// (<see cref="SequenceBinding"/>), so the two builds cannot bind one script two ways. The
    /// browser half holds no instrument logic by design, and a copy of the rule there would be
    /// one rule and one guess. What the page has picked by hand comes up with the script and is
    /// taken as given; a pick of a session that has since closed is not a pick of anything.
    /// </remarks>
    public IReadOnlyList<SequenceRequirement> BindSequence(string script, IReadOnlyDictionary<string, string>? picks)
    {
        var picked = new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, id) in picks ?? new Dictionary<string, string>())
            if (_sessions.TryGetValue(id, out var s)) picked[alias] = s;

        return SequenceBinding.Bind(SequenceRunner.Requirements(script), _sessions.Values.ToList(),
                                    s => s.Identity, s => s.Host, picked)
            .Select(b => new SequenceRequirement(b.Alias, b.Model, b.Instrument?.Id, Note(b)))
            .ToList();
    }

    /// <summary>Why a row has nothing, and here, what to do about it: the table has a picker.</summary>
    private static string? Note(DeviceBinding<Session> b)
        => b.State == DeviceBindingState.Ambiguous ? b.Reason + ": pick one" : b.Reason;

    // ----------------------------------------------------------------- driving a link

    /// <summary>True while a script or a readout is driving this session.</summary>
    public bool IsDriven(string id) => _driving.TryGetValue(id, out int n) && n > 0;

    /// <summary>
    /// Take one or more instruments for a run, and give them back when the returned handle is
    /// disposed.
    ///
    /// This is the web's <c>InstrumentSession.IsBusy</c>. On the desktop that flag is set in
    /// exactly two places — a script starting and a readout beginning to poll — and the console
    /// greys itself out while it is set, because a command typed into the console between two
    /// steps of a sweep changes what the sweep measured. Everything here is serialized, so such
    /// a command cannot corrupt a reply; it interleaves, which is the part that matters.
    ///
    /// Held on the server rather than in the page that started the run, because the bench is
    /// one shared workspace: a console open in a second tab is watching the same instrument and
    /// has the same reason not to be typed into.
    /// </summary>
    public IDisposable Drive(params string[] sessionIds)
    {
        // Distinct, because a sequence can bind two aliases to one instrument, and a hold taken
        // twice would need giving back twice.
        var held = sessionIds.Distinct(StringComparer.Ordinal).Where(_sessions.ContainsKey).ToArray();
        foreach (var id in held) Took(id, +1);
        return new Hold(this, held);
    }

    /// <summary>Move one session's count, and say so if it crossed nought.</summary>
    private void Took(string id, int delta)
    {
        int now = _driving.AddOrUpdate(id, Math.Max(0, delta), (_, n) => Math.Max(0, n + delta));
        if (now == 0) _driving.TryRemove(id, out _);

        // Only on the crossing. A second run starting on an already-driven instrument changes
        // nothing anyone can see, and an event per hold would have the console redraw for it.
        if ((delta > 0 && now == 1) || (delta < 0 && now == 0))
            _ = _hub.Clients.All.SendAsync("Driven", id, now > 0);
    }

    private sealed class Hold(BenchService bench, string[] ids) : IDisposable
    {
        private bool _given;

        public void Dispose()
        {
            // A run's finally can be reached twice — once by the run ending and once by the
            // enumerator being disposed — and giving back a hold that was already given back
            // would unlock an instrument something else is still driving.
            if (_given) return;
            _given = true;
            foreach (var id in ids) bench.Took(id, -1);
        }
    }

    public async Task<SessionDto> ConnectAsync(ConnectRequest req, CancellationToken ct)
    {
        InstrumentAddress target = ParseAddress(req.Address);
        return await OpenAsync(target, () => target.CreateClient(req.TimeoutMs), ct);
    }

    /// <summary>
    /// Open a session on <paramref name="target"/>, over a client from <paramref name="create"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ConnectAsync"/> hands this the address's own transport. A test hands it a fake
    /// instrument, which is how a run is driven through this class and <see cref="RunService"/>
    /// end to end with no bench behind them. A factory rather than a client because it is only
    /// called once the address is known to be free: nothing is made, let alone dialled, for an
    /// instrument that already has a session.
    /// </remarks>
    internal async Task<SessionDto> OpenAsync(InstrumentAddress target, Func<IInstrumentClient> create, CancellationToken ct)
    {
        string address = Endpoint(target);

        // One session per instrument. Reconnecting to something already open would put a
        // second socket on an instrument that answers one conversation at a time.
        //
        // Per instrument, not per host: this used to match on the host and the transport
        // alone, which is right for a box with one instrument in it and wrong for every
        // gateway. A GPIB-Ethernet or serial gateway is a single address with several
        // instruments behind it, told apart by port or by VXI-11 device name — and matching
        // on the host meant the second one you connected silently handed you the first one's
        // session, so you would drive the wrong instrument while its name sat in the tab.
        var existing = _sessions.Values.FirstOrDefault(s =>
            string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return Describe(existing);

        var client = new SerializedInstrumentClient(create());

        try
        {
            await client.ConnectAsync(ct);
            string idn = (await client.QueryAsync("*IDN?", ct)).Trim();
            var family = InstrumentProfile.FamilyForIdentity(idn);
            var profile = InstrumentProfile.ForIdentity(idn);

            var session = new Session(Guid.NewGuid().ToString("N"), target.Host, address, target.Transport, idn, family, profile, client);
            _sessions[session.Id] = session;

            // What is waiting on this connection, pushed as it changes. The desktop's console
            // draws the same queue from the same event; here it has a wire to cross, and the
            // wire is the hub because there is nothing to poll for — the queue changes when
            // the instrument finishes answering, not when a browser asks.
            //
            // Fire and forget, and to everyone: the bench is one shared workspace, and a
            // console open in a second tab is watching the same instrument.
            client.PendingChanged += (_, _) =>
                _ = _hub.Clients.All.SendAsync("Queued", session.Id, client.Queued);
            _log.LogInformation("Connected {Address} as {Family}", address, family);
            BenchChanged();
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
        BenchChanged();
        return true;
    }

    /// <summary>
    /// The bench is not what it was: something connected, or something went.
    /// </summary>
    /// <remarks>
    /// The queue and the lock were pushed and the list itself was not, which left a page holding
    /// a tab strip from whenever it last asked. That is a console onto a session the server no
    /// longer has — it draws, it can be typed into, and every press fails — and it is exactly the
    /// state opening the app afresh exists to prevent, arrived at from the other direction.
    ///
    /// The message carries nothing: what the list now is, is a question with an answer already,
    /// and a page told to go and read it cannot be told a stale version of it.
    ///
    /// Fire and forget, and to everyone: the bench is one shared workspace.
    /// </remarks>
    private void BenchChanged() => _ = _hub.Clients.All.SendAsync("Bench");

    // ------------------------------------------------------------------- readout

    /// <summary>
    /// Poll one measurement on a timer, yielding each reading as it arrives.
    ///
    /// A meter answers one reading per query, so the only way to see a trend — a drifting
    /// supply, a warming thermistor, a settling reference — is to ask repeatedly. The desktop
    /// app's readout window does exactly this; here the asking stays on the server rather than
    /// being a browser sending a command every second, because the server is what holds the
    /// socket and a page that is scrolled, backgrounded or on a slow link would otherwise be
    /// deciding the sample interval.
    ///
    /// Cancelling the stream — which is what closing the pane does — trips this token and the
    /// polling stops. The instrument is left alone, not left running.
    /// </summary>
    public async IAsyncEnumerable<ReadingDto> ReadoutAsync(
        string id, string query, int intervalMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var s))
        {
            yield return new ReadingDto(0, 0, 0, 0, 0, 0, 0, "No such session — it may have been closed.");
            yield break;
        }

        if (Held(id))
        {
            yield return new ReadingDto(0, 0, 0, 0, 0, 0, 0, ServiceMode.HeldMessage);
            yield break;
        }

        // The desktop's box allows the same range, and reads it afresh each time round so a
        // change takes effect at once. Here the client restarts the stream instead, which is
        // the same thing seen from further away.
        intervalMs = Math.Clamp(intervalMs, 100, 60_000);

        // Polling holds the link, so the console for this instrument locks itself out for as
        // long as it does. Given back when the enumerator is disposed, which is what closing
        // the pane, pressing Stop, or the browser going away all come to.
        using var _hold = Drive(id);

        var series = new ReadingSeries();
        var clock = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            string reply = "";
            string? failed = null;
            try { reply = await s.Client.AskAsync(query, ct); }
            catch (OperationCanceledException) { yield break; }
            catch (Exception ex) { failed = ex.Message; }

            if (failed is not null)
            {
                // A meter that has stopped answering is the end of the run, not a gap in it.
                var (lo, hi, avg) = series.Statistics();
                yield return new ReadingDto(clock.Elapsed.TotalSeconds, 0,
                    series.Count, series.TotalTaken, lo, hi, avg, failed);
                yield break;
            }

            double at = clock.Elapsed.TotalSeconds;
            if (ReadingSeries.TryParseReading(reply, out double value))
            {
                series.Add(at, value);
                var (min, max, mean) = series.Statistics();
                yield return new ReadingDto(at, value, series.Count, series.TotalTaken, min, max, mean, null);
            }
            else
            {
                // Said, not thrown: an instrument in the wrong mode answers something that is
                // not a number, and the next poll may well be fine.
                var (min, max, mean) = series.Statistics();
                yield return new ReadingDto(at, 0, series.Count, series.TotalTaken, min, max, mean,
                    "Could not read a number from: " + reply.Trim());
            }

            try { await Task.Delay(intervalMs, ct); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    public async Task<CommandReply> SendAsync(string id, string text, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var s))
            return new CommandReply(text, null, false, 0, "No such session — it may have been closed.");
        if (Held(id))
            return new CommandReply(text, null, ScpiClient.IsQuery(text), 0, ServiceMode.HeldMessage);

        bool isQuery = ScpiClient.IsQuery(text);
        var clock = Stopwatch.StartNew();
        try
        {
            if (isQuery)
            {
                string reply = (await s.Client.AskAsync(text, ct)).Trim();
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

    /// <summary>
    /// Ask the instrument to list its own commands.
    ///
    /// Core's <see cref="CommandDiscovery"/>, which is the same code the desktop console runs:
    /// SCPI-99's <c>SYSTem:HELP:HEADers?</c>, and a look at what came back to tell a real header
    /// dump from an error line or a bare zero. Most budget instruments do not implement it, so a
    /// failure here is the ordinary case and is reported as one rather than thrown.
    /// </summary>
    public async Task<DiscoveryReply> DiscoverAsync(string id, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var s))
            return new DiscoveryReply(CommandDiscovery.Query, false, 0, "");
        if (Held(id))
            return new DiscoveryReply(CommandDiscovery.Query, false, 0, "");

        var found = await CommandDiscovery.DiscoverAsync(s.Client, ct);
        return new DiscoveryReply(CommandDiscovery.Query, found.Success, found.Count, found.HeaderList);
    }

    // --------------------------------------------------------------------- capture

    /// <summary>
    /// Capture one or more channels, against one time axis.
    ///
    /// The reading is Core's <see cref="ChannelCaptures"/>, which the desktop app uses too: one
    /// channel at a time because the connection carries one conversation at a time, a channel that
    /// cannot be read failing on its own while the others are still returned, and a trace whose
    /// length disagrees with the time base read as "no such channel" rather than handed over as a
    /// flat line across one that is not there. This turns those reads into what the browser draws.
    ///
    /// The timeout is raised once around the lot rather than per channel: a full record is about a
    /// megabyte and four of them are four, and the user's timeout is set for a query.
    /// </summary>
    public async Task<WaveformSetDto> WaveformAsync(string id, IReadOnlyList<int> channels, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var s))
            return new WaveformSetDto([], [], 0, "No such session.");
        if (Held(id))
            return new WaveformSetDto([], [], 0, ServiceMode.HeldMessage);
        if (!s.Profile.SupportsWaveformCapture)
            return new WaveformSetDto([], [], 0, $"No waveform-transfer dialect is documented for {s.Profile.Name}.");

        int was = s.Client.TimeoutMs;
        s.Client.TimeoutMs = Math.Max(was, 15000);
        try
        {
            IReadOnlyList<ChannelRead> reads = await ChannelCaptures.ReadAsync(
                s.Client, s.Profile.WaveformDialect, channels, ct);

            // The first channel that answered sets the axis. Every other one is sampled against
            // the same time base, so a copy of the same numbers per channel would be a megabyte
            // of agreement.
            ChannelRead? first = reads.FirstOrDefault(r => r.Ok);
            IReadOnlyList<double> time = first?.Capture is { } axis
                ? axis.Samples.Select(x => x.Time).ToList()
                : [];

            var traces = reads
                .Select(r => new TraceDto(
                    r.Channel,
                    r.Capture is { } got
                        ? got.Samples.Select(x => x.Voltage).ToList()
                        : (IReadOnlyList<double>)[],
                    r.Error))
                .ToList();

            // Every one of them failing is the capture failing, and the first reason is the reason.
            string? failed = traces.Count > 0 && traces.All(t => t.Error is not null)
                ? traces[0].Error
                : null;

            return new WaveformSetDto(time, traces, first?.Capture?.XIncrement ?? 0, failed);
        }
        finally { s.Client.TimeoutMs = was; }
    }

    public async Task<ScreenshotDto> ScreenshotAsync(string id, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var s))
            return new ScreenshotDto("", "", 0, "", "No such session.");
        if (Held(id))
            return new ScreenshotDto("", "", 0, "", ServiceMode.HeldMessage);
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
    /// A parsed address written back out in one canonical form — which is what tells one
    /// instrument from another, and what someone can type in to reach this one again.
    /// <see cref="InstrumentAddress.Canonical"/> decides the spelling, so the string a
    /// session is keyed on is one the address box will take back.
    /// </summary>
    internal static string Endpoint(InstrumentAddress address) => address.Canonical;

    /// <summary>
    /// A bare host, host:port, vxi://host, tcp://host, serial://COM3, or a VISA resource
    /// string — read by the one parser in Core, so this server, the CLI and the desktop app
    /// agree about what a typed address means instead of each carrying its own copy. They
    /// did not: this was one of two identical copies, and the desktop had neither.
    /// </summary>
    /// <remarks>
    /// The whole address is handed on rather than the four fields it used to be taken apart
    /// into, because a serial address carries baud rate and framing as well and a tuple that
    /// dropped them would connect at 9600 to an instrument set to 115200 — which does not
    /// fail, it answers with rubbish.
    /// </remarks>
    internal static InstrumentAddress ParseAddress(string? text)
    {
        if (!InstrumentAddress.TryParse(text, out InstrumentAddress a, out string error))
            throw new ArgumentException(error);

        return a;
    }

    private SessionDto Describe(Session s)
    {
        var catalog = CommandReference.ForFamily(s.Family);
        return new SessionDto(
            s.Id, s.Address,
            s.Transport.DisplayName(),
            s.Client.Description,
            s.Identity, s.Family.ToString(), s.Profile.Name,
            s.Profile.Commands.Select(c => new QuickCommandDto(c.Label, c.Command)).ToList(),
            s.Profile.SupportsWaveformCapture,
            s.Profile.ScreenCaptureCommand is { Length: > 0 },
            s.Profile.ReadoutFunctions.Select(r => new ReadoutDto(r.Label, r.Query, r.Unit)).ToList(),
            catalog?.Instrument, catalog?.Commands.Count ?? 0,
            // So a console that opens onto a busy instrument shows what is already waiting,
            // rather than an empty strip until the next thing finishes.
            s.Client.Queued,
            // And starts locked if something is already driving it, rather than offering a
            // command box that the next event will take away.
            IsDriven(s.Id));
    }

    // ------------------------------------------------------------- who is watching

    /// <summary>
    /// A page has the bench open. Cancels a pending close: someone came back.
    /// </summary>
    public void Watching()
    {
        lock (_presence)
        {
            _watching++;
            _closing?.Cancel();
            _closing?.Dispose();
            _closing = null;
        }
    }

    /// <summary>
    /// A page has gone. If it was the last one, let the bench go after <see cref="Linger"/>.
    /// </summary>
    /// <remarks>
    /// Counted rather than flagged: consoles opened in browser tabs of their own are watching the
    /// same bench, and the bench is only unwatched when the last of them has gone.
    /// </remarks>
    public void Unwatching()
    {
        CancellationToken ct;
        lock (_presence)
        {
            _watching = Math.Max(0, _watching - 1);
            if (_watching > 0) return;

            _closing?.Cancel();
            _closing?.Dispose();
            _closing = new CancellationTokenSource();
            ct = _closing.Token;
        }

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(Linger, ct); }
            catch (OperationCanceledException) { return; }   // someone came back

            _log.LogInformation("No page has the bench open; letting {Count} instrument(s) go.",
                _sessions.Count);
            if (Service) await CloseIdleAsync();
            else await CloseAllAsync();
        }, CancellationToken.None);
    }

    /// <summary>
    /// The app has been opened, and whether this document is a reload rather than an opening.
    /// </summary>
    /// <remarks>
    /// <b>Opening the app closes the bench.</b> Nothing is connected until the person at the
    /// keyboard connects it. MainForm takes every connection with it when it goes, and a build
    /// that hands whoever opens it next the last window’s sessions is offering a console onto an
    /// instrument that may since have been switched off, moved or unplugged — and one that now
    /// answers to a different address is worse than one that does not answer at all.
    ///
    /// A reload is the exception, and the only one: a sweep that outlives a refresh is the main
    /// thing this build has over the desktop. Which of the two a document is comes from the
    /// browser and is decided in index.html — the navigation type, <i>and</i> how long ago the
    /// document before it went, because a restored tab brings its sessionStorage back with it
    /// and a mark alone cannot tell a reopening from a refresh.
    ///
    /// <b>Not guarded on whether anything else is watching.</b> It was, and that was the second
    /// half of the same fault: with any page still up — a tab left open, one the browser
    /// restored, one whose socket had not yet been reaped — the opening did nothing at all and
    /// the app came up on the last bench again. A detached console is not caught by this because
    /// it never asks: it is a window the app itself opened onto a session that already exists,
    /// and opening one is not opening the app.
    ///
    /// <see cref="Linger"/> stays for the case nobody comes back at all.
    ///
    /// <b>In service mode an instrument a run holds is kept</b>, here and when the last page goes:
    /// the bench belongs to a host then, whose people open the page while a measurement is running,
    /// and the measurement is what the bench is for. The idle ones still go.
    /// </remarks>
    public async Task PageOpenedAsync(bool resumed)
    {
        if (resumed || _sessions.IsEmpty) return;

        _log.LogInformation(
            "The app was opened; letting {Count} instrument(s) from the last window go.",
            _sessions.Count);
        if (Service) await CloseIdleAsync();
        else await CloseAllAsync();
    }

    /// <summary>Disconnect every instrument on the bench, properly.</summary>
    public async Task CloseAllAsync()
    {
        foreach (var id in _sessions.Keys.ToList()) await DisconnectAsync(id);
    }

    /// <summary>Disconnect every instrument no run is driving, and leave the driven ones to their runs.</summary>
    public async Task CloseIdleAsync()
    {
        foreach (var id in _sessions.Keys.ToList())
            if (!IsDriven(id)) await DisconnectAsync(id);
    }

    public ValueTask DisposeAsync() => new(CloseAllAsync());
}
