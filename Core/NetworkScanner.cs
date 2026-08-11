using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>How we talk SCPI to an instrument.</summary>
public enum InstrumentTransport
{
    /// <summary>Plain TCP socket (Rigol 5555, Keysight/Siglent scopes 5025, ...).</summary>
    RawSocket,

    /// <summary>VXI-11 over ONC RPC, discovered via the portmapper on port 111.</summary>
    Vxi11,
}

/// <summary>A local IPv4 interface we can scan from.</summary>
public sealed class LocalInterface
{
    public string Name { get; }
    public IPAddress Address { get; }
    public IPAddress Mask { get; }

    /// <summary>
    /// True when the interface has a default gateway — i.e. a real LAN. Virtual
    /// switches (Hyper-V's "Default Switch", WSL, VPN taps) typically have none.
    /// </summary>
    public bool HasGateway { get; }

    /// <summary>Usable host addresses on this subnet (excludes network + broadcast).</summary>
    public int HostCount { get; }

    /// <summary>Subnet size in CIDR bits, e.g. 24 for 255.255.255.0.</summary>
    public int PrefixLength { get; }

    public LocalInterface(string name, IPAddress address, IPAddress mask, bool hasGateway)
    {
        Name = name;
        Address = address;
        Mask = mask;
        HasGateway = hasGateway;

        uint maskBits = ToMaskUInt(mask);
        PrefixLength = System.Numerics.BitOperations.PopCount(maskBits);

        uint hostBits = ~maskBits;
        HostCount = hostBits > 1 ? (int)(hostBits - 1) : 0;
    }

    // CIDR rather than a dotted mask: this string has to fit the combo box.
    public override string ToString() => $"{Address}/{PrefixLength}  ({Name})  —  {HostCount} hosts";

    private static uint ToMaskUInt(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}

/// <summary>A discovered instrument.</summary>
public sealed class ScpiDevice
{
    public required IPAddress Address { get; init; }

    /// <summary>Raw-socket port, or 111 (the portmapper) for VXI-11 devices.</summary>
    public required int Port { get; init; }

    public InstrumentTransport Transport { get; init; } = InstrumentTransport.RawSocket;

    /// <summary>Raw *IDN? response, or empty if the device didn't answer.</summary>
    public string Identity { get; init; } = "";

    public string Endpoint => $"{Address}:{Port}";

    public string TransportName => Transport == InstrumentTransport.Vxi11 ? "VXI-11" : "Raw socket";
}

/// <summary>
/// Discovers SCPI instruments on a subnet over two transports:
///
///   * Raw socket — TCP-connect to a candidate port and send "*IDN?".
///     Vendors differ: 5025 is the LXI/VISA convention (Keysight, Siglent
///     scopes, Tektronix, R&amp;S), Rigol uses 5555, and Fluke's 8845A/8846A
///     bench meters listen on 3490 and nothing else.
///
///   * VXI-11 — for instruments with no raw socket at all (e.g. Siglent
///     SDG2042X). Detected via the RPC portmapper on port 111. Note that plain
///     Linux hosts run rpcbind on 111 too, so the VXI-11 handshake itself is
///     what separates an instrument from an NFS server.
/// </summary>
public static class NetworkScanner
{
    /// <summary>
    /// Default scan list: three raw-socket ports plus VXI-11 (111).
    ///
    /// 3490 is here for the Fluke 8845A/8846A, which answer on that port and no other —
    /// without it a Fluke meter is simply invisible to a scan, and the catalog shipped
    /// for it may as well not exist.
    /// </summary>
    public static readonly int[] CommonScpiPorts =
        { 5025, 5555, 3490, Vxi11Client.PortmapperPort };

    /// <summary>
    /// Enumerate usable (up, non-loopback) local IPv4 interfaces, best candidate first.
    ///
    /// Ordering matters: the UI selects the first entry by default, and a Hyper-V
    /// "Default Switch" (192.168.224.1/20 = 4094 hosts, no instruments) would
    /// otherwise be picked ahead of the real 254-host LAN. Real LANs — the ones
    /// with a default gateway — come first, then smaller subnets.
    /// </summary>
    public static List<LocalInterface> GetLocalInterfaces()
    {
        var result = new List<LocalInterface>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            IPInterfaceProperties props = ni.GetIPProperties();

            bool hasGateway = props.GatewayAddresses.Any(g =>
                g.Address is { AddressFamily: AddressFamily.InterNetwork } &&
                !g.Address.Equals(IPAddress.Any));

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                IPAddress mask = ua.IPv4Mask;
                if (mask == null || mask.Equals(IPAddress.Any)) continue;

                result.Add(new LocalInterface(ni.Name, ua.Address, mask, hasGateway));
            }
        }

        return result
            .OrderByDescending(i => i.HasGateway)
            .ThenBy(i => i.HostCount)
            .ToList();
    }

    /// <summary>
    /// Build the list of host addresses on the subnet that owns <paramref name="ip"/>,
    /// excluding the network and broadcast addresses. Capped at
    /// <paramref name="maxHosts"/> so a huge mask (e.g. /16) can't spawn 65k probes.
    /// </summary>
    public static List<IPAddress> EnumerateHosts(IPAddress ip, IPAddress mask, int maxHosts, out bool capped)
    {
        uint ipu = ToUInt(ip);
        uint masku = ToUInt(mask);
        uint network = ipu & masku;
        uint broadcast = network | ~masku;

        var hosts = new List<IPAddress>();
        capped = false;

        for (uint a = network + 1; a < broadcast; a++)
        {
            hosts.Add(FromUInt(a));
            if (hosts.Count >= maxHosts)
            {
                capped = a + 1 < broadcast;
                break;
            }
        }

        return hosts;
    }

    /// <summary>
    /// Probe every host in parallel. Ports other than 111 are probed as raw
    /// sockets; including 111 in <paramref name="ports"/> enables VXI-11 discovery.
    /// <paramref name="progress"/> reports hosts completed so far.
    /// </summary>
    /// <param name="connectTimeoutMs">
    /// Short per-connection timeout — keeps the sweep fast across many dead hosts.
    /// </param>
    /// <param name="idnTimeoutMs">
    /// Longer timeout for the *IDN? exchange once a port is actually open. Only the
    /// handful of responsive hosts pay it, so slow instruments (Rigol needs ~2 s)
    /// still get identified.
    /// </param>
    public static async Task<List<ScpiDevice>> ScanAsync(
        IReadOnlyList<IPAddress> hosts,
        IReadOnlyList<int> ports,
        int connectTimeoutMs,
        int idnTimeoutMs,
        IProgress<int>? progress,
        CancellationToken ct,
        IProgress<ScpiDevice>? deviceFound = null)
    {
        var rawPorts = ports.Where(p => p != Vxi11Client.PortmapperPort).ToList();
        bool scanVxi11 = ports.Contains(Vxi11Client.PortmapperPort);

        var found = new ConcurrentBag<ScpiDevice>();
        int hostsDone = 0;

        // Report at most ~200 times per scan. Reporting per-host would post 4094
        // messages to the UI thread on a /20, swamping the message queue and
        // delaying user input (the Stop button) behind the backlog.
        int reportEvery = Math.Max(1, hosts.Count / 200);

        using var throttler = new SemaphoreSlim(128);

        var tasks = hosts.Select(async host =>
        {
            await throttler.WaitAsync(ct).ConfigureAwait(false);
            var hits = new List<ScpiDevice>();
            try
            {
                // Ports are probed one at a time, never concurrently: instruments like
                // the Rigol accept only a single connection and wedge if you open two.
                foreach (int port in rawPorts)
                {
                    ct.ThrowIfCancellationRequested();
                    var dev = await ProbeRawAsync(host, port, connectTimeoutMs, ct).ConfigureAwait(false);
                    if (dev != null) hits.Add(dev);
                }

                // VXI-11 supplies the identity (the raw probe deliberately doesn't). For a
                // device on both transports (the Rigol) MergeByAddress then keeps the
                // VXI-11 row, which is also the transport we want to drive it over.
                if (scanVxi11)
                {
                    ct.ThrowIfCancellationRequested();
                    var dev = await ProbeVxi11Async(host, connectTimeoutMs, idnTimeoutMs, ct).ConfigureAwait(false);
                    if (dev != null) hits.Add(dev);
                }
            }
            catch
            {
                // Ignore per-host failures — unreachable/filtered hosts are expected.
                // (Cancellation is handled by the caller via the token.)
            }
            finally
            {
                throttler.Release();

                // Merge this host's probes into one row and surface it immediately, so the
                // caller can show instruments as they turn up instead of leaving the list
                // empty until the whole sweep finishes (a big subnet takes a while, and an
                // empty list for a minute reads as a hung app).
                if (hits.Count > 0)
                {
                    ScpiDevice merged = MergeByAddress(hits);
                    found.Add(merged);
                    deviceFound?.Report(merged);
                }

                // Nothing is reported once Stop has been pressed. The remaining hosts still
                // unwind — they abandon their connects and fall through here in a fraction of
                // a second — but each one used to post its number, so the bar sprinted to the
                // end after the button had already gone back to "Scan". A bar racing forward
                // is the plainest possible statement that the scan is still running, which is
                // exactly what the user was told had stopped.
                int done = Interlocked.Increment(ref hostsDone);
                if (!ct.IsCancellationRequested
                    && (done % reportEvery == 0 || done == hosts.Count))
                    progress?.Report(done);
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Collapse to one row per address so the list shows instruments, not endpoints.
        return found
            .GroupBy(d => ToUInt(d.Address))
            .Select(g => MergeByAddress(g.ToList()))
            .OrderBy(d => ToUInt(d.Address))
            .ToList();
    }

    /// <summary>
    /// Merge all probe results for one address into a single device: prefer VXI-11 when
    /// the instrument offers it, and attach an identity from whichever probe read one.
    ///
    /// VXI-11 is preferred over the raw socket because it is a framed, explicitly-read
    /// protocol with proper clear/local operations. The Rigol in particular has a broken
    /// raw socket (a permanent one-reply-behind lag on port 5555), but works correctly
    /// over VXI-11 — so a device reachable both ways should be driven over VXI-11.
    /// </summary>
    private static ScpiDevice MergeByAddress(List<ScpiDevice> sameAddress)
    {
        ScpiDevice chosen = sameAddress
            .OrderBy(d => d.Transport == InstrumentTransport.Vxi11 ? 0 : 1)
            .ThenByDescending(d => !string.IsNullOrWhiteSpace(d.Identity))
            .First();

        string identity = sameAddress
            .Select(d => d.Identity)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";

        if (identity == chosen.Identity) return chosen;

        return new ScpiDevice
        {
            Address = chosen.Address,
            Port = chosen.Port,
            Transport = chosen.Transport,
            Identity = identity,
        };
    }

    // ------------------------------------------------------------- raw socket

    private static async Task<ScpiDevice?> ProbeRawAsync(
        IPAddress host, int port, int connectTimeoutMs, CancellationToken ct)
    {
        // Port check only — deliberately NOT sending *IDN? here.
        //
        // A slow instrument (the Rigol needs ~2-3 s) wouldn't answer within any window
        // short enough to keep the scan fast, and a *IDN? whose reply we abandon poisons
        // the instrument: it holds that unread response and hands it back on the next
        // connection, desyncing every future query. So during discovery we just confirm
        // the raw socket is open; identity is filled in by the VXI-11 probe (via
        // MergeByAddress) if the device offers it, and always by Connect afterwards.
        using var tcp = new TcpClient();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(connectTimeoutMs);
        try
        {
            await tcp.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);
        }
        catch
        {
            return null; // closed, filtered, or timed out — not an instrument
        }

        return new ScpiDevice
        {
            Address = host,
            Port = port,
            Transport = InstrumentTransport.RawSocket,
            Identity = "",
        };
    }

    // ----------------------------------------------------------------- VXI-11

    private static async Task<ScpiDevice?> ProbeVxi11Async(
        IPAddress host, int connectTimeoutMs, int idnTimeoutMs, CancellationToken ct)
    {
        // Cheap gate: is the portmapper even listening?
        using (var probe = new TcpClient())
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(connectTimeoutMs);
            try
            {
                await probe.ConnectAsync(host, Vxi11Client.PortmapperPort, connectCts.Token).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        // Port 111 is open — but ordinary Linux boxes run rpcbind there too.
        // Only a real VXI-11 instrument completes the handshake, so let it filter.
        try
        {
            using var client = new Vxi11Client(host.ToString()) { TimeoutMs = idnTimeoutMs };
            await client.ConnectAsync(ct).ConfigureAwait(false);

            string identity = "";
            try
            {
                identity = await client.QueryAsync("*IDN?", ct).ConfigureAwait(false);
            }
            catch
            {
                // Linked but wouldn't identify — still worth listing.
            }

            return new ScpiDevice
            {
                Address = host,
                Port = Vxi11Client.PortmapperPort,
                Transport = InstrumentTransport.Vxi11,
                Identity = identity,
            };
        }
        catch
        {
            return null; // rpcbind without VXI-11 registered, or handshake failed
        }
    }

    // ---------------------------------------------------------------- helpers

    private static uint ToUInt(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress FromUInt(uint v)
        => new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
}
