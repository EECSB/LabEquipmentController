using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// One serial port as the scan card lists it: always a port, sometimes an identity.
///
/// The difference from <see cref="ScpiDevice"/> is the whole difference between the two
/// scans. An IP address that does not answer is not a row — there is nothing there. A
/// serial port that does not answer is still a port, still on this machine, and still
/// connectable at settings the scan did not try, so it stays listed with an empty identity
/// rather than vanishing.
/// </summary>
public sealed class SerialDevice
{
    /// <summary>The operating system's name for the port — <c>COM3</c>, <c>/dev/ttyUSB0</c>.</summary>
    public required string PortName { get; init; }

    /// <summary>
    /// The line settings that got an answer, or the first ones tried when nothing did.
    /// Carried into the address so picking a row keeps the baud rate that worked.
    /// </summary>
    public SerialSettings Settings { get; init; } = SerialSettings.Default;

    /// <summary>The <c>*IDN?</c> reply, or empty when the port was listed rather than asked.</summary>
    public string Identity { get; init; } = "";

    /// <summary>
    /// The baud rates the scan tried, in order. Empty means the port was listed and never
    /// opened — which is the state every row is in until Scan is pressed.
    /// </summary>
    public IReadOnlyList<int> Tried { get; init; } = Array.Empty<int>();

    /// <summary>
    /// Something came back that was not an identity — bytes at the wrong framing, or a
    /// device that is not an instrument. Worth saying out loud: it means the port is alive
    /// and the settings are wrong, which is the one case a bare "no reply" would send
    /// someone looking at the cable instead of the baud rate.
    /// </summary>
    public bool Noise { get; init; }

    /// <summary>True once Scan has opened this port, whatever came of it.</summary>
    public bool Probed => Tried.Count > 0;

    /// <summary>True when the port answered with something that reads as an identity.</summary>
    public bool Answered => Identity.Length > 0;

    /// <summary>Always "Serial" — the column exists so the two scans produce the same table.</summary>
    public string TransportName => InstrumentTransport.Serial.DisplayName();

    /// <summary>The line settings as an engineer writes them: <c>9600-8-N-1</c>.</summary>
    public string SettingsText => Settings.ToString();

    /// <summary>
    /// The port written the way it would be typed back in, carrying whatever settings
    /// answered — <c>serial://COM3?baud=115200</c>. The same job
    /// <see cref="ScpiDevice.TypedAddress"/> does, and for the same reason: a row that
    /// fills the Address box has to fill it with something that connects.
    /// </summary>
    public string TypedAddress =>
        new InstrumentAddress(PortName, InstrumentTransport.Serial, 0, "") { Serial = Settings }.Canonical;

    /// <summary>
    /// What the Identity column shows. Three states, because "listed" and "asked and got
    /// nothing" are different facts and a blank cell for both would hide the difference.
    /// </summary>
    public string IdentityText
    {
        get
        {
            if (Answered) return Identity;
            if (!Probed) return "";

            string rates = string.Join(", ", Tried);
            return Noise
                ? $"(answered at {rates}, but not with an identity — wrong line settings?)"
                : $"(no reply at {rates})";
        }
    }
}

/// <summary>
/// The serial half of the scan card: list the ports, and — only when asked — open each one
/// and see whether an instrument answers <c>*IDN?</c>.
///
/// This is not the subnet sweep with different addresses, and the difference is worth being
/// precise about, because SPEC §17 spent a while arguing that discovery does not transfer
/// to serial:
///
///   * <see cref="List"/> opens nothing. It is what the card shows the moment it switches
///     to Serial, and it is enough to pick a port and connect. Nothing on anyone's bench
///     is touched by looking at this list.
///
///   * <see cref="ScanAsync"/> opens ports, and only ever because somebody pressed Scan.
///     It tries the baud rates it was given, in the order it was given them, and stops on
///     the first that answers. It does not sweep parity, data bits or flow control: that is
///     a combinatorial search against hardware that cannot say "wrong number", and it is
///     the thing §17 refuses.
///
/// The honest caveat, which the UI repeats: opening a port asserts DTR and RTS, and the
/// thing on the other end may not be an instrument. A scan sends five short ASCII strings
/// to whatever is there. That is a decision for the person at the bench, which is why it is
/// a button and not something that happens on arrival.
/// </summary>
public static class SerialScanner
{
    /// <summary>
    /// The rates worth trying, commonest first among the ones that are not 9600.
    ///
    /// 9600 leads because it is what IEEE 488.2-era instruments ship with and what their
    /// manuals print beside the connector; 115200 is second because it is what everything
    /// built since is set to. The list is the exact serial counterpart of
    /// <see cref="NetworkScanner.CommonScpiPorts"/> — the handful of answers that cover
    /// almost every instrument, offered so nobody has to know them.
    /// </summary>
    public static readonly int[] CommonBaudRates = { 9600, 115200, 19200, 38400, 57600 };

    /// <summary>
    /// How many ports are opened at once. Small on purpose: the count is nearly always
    /// under five, and a USB-serial driver asked to open eight handles at the same instant
    /// is a worse bet than one asked to open four twice.
    /// </summary>
    private const int MaxConcurrentPorts = 4;

    /// <summary>
    /// Read a written list of baud rates — <c>9600, 115200</c> — the way the scan card's
    /// box holds them. Separators are comma, semicolon or space; anything unreadable is
    /// dropped rather than refused, and duplicates collapse. An empty result means the
    /// caller should fall back to <see cref="CommonBaudRates"/>.
    ///
    /// The exact shape of the desktop's port-list box, deliberately: the two boxes sit in
    /// the same place on the same row and one replaces the other, so they had better read
    /// the same way.
    /// </summary>
    public static List<int> ParseBaudRates(string? text)
    {
        var rates = new List<int>();
        if (string.IsNullOrWhiteSpace(text)) return rates;

        foreach (string part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out int baud) && baud > 0 && !rates.Contains(baud))
                rates.Add(baud);
        }

        return rates;
    }

    /// <summary>
    /// Every port this machine reports, as unprobed rows. Opens nothing.
    /// </summary>
    public static List<SerialDevice> List(IEnumerable<string>? ports = null)
        => (ports is null ? SerialPorts.Names() : SerialPorts.Sort(ports))
           .Select(p => new SerialDevice { PortName = p })
           .ToList();

    /// <summary>
    /// Ask each port for its identity, at each baud rate in turn until one answers.
    ///
    /// Every port comes back, answered or not — see <see cref="SerialDevice"/> for why.
    /// <paramref name="progress"/> counts ports finished, and <paramref name="deviceFound"/>
    /// fires per port as it finishes, so a list can fill in rather than appear at the end.
    /// </summary>
    public static async Task<List<SerialDevice>> ScanAsync(
        IReadOnlyList<string> ports,
        IReadOnlyList<int> bauds,
        int idnTimeoutMs = 1500,
        IProgress<int>? progress = null,
        CancellationToken ct = default,
        IProgress<SerialDevice>? deviceFound = null)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(bauds);

        var rates = bauds.Count > 0 ? bauds : CommonBaudRates;
        var results = new SerialDevice[ports.Count];

        int done = 0;
        using var gate = new SemaphoreSlim(MaxConcurrentPorts);

        var work = ports.Select(async (port, index) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                SerialDevice found = await ProbeAsync(port, rates, idnTimeoutMs, ct).ConfigureAwait(false);
                results[index] = found;
                deviceFound?.Report(found);
            }
            finally
            {
                gate.Release();
                progress?.Report(Interlocked.Increment(ref done));
            }
        });

        await Task.WhenAll(work).ConfigureAwait(false);
        return results.Where(r => r is not null).ToList();
    }

    /// <summary>
    /// One port, each rate in turn. Stops at the first identity; remembers whether anything
    /// came back at all, so a wrong baud rate can be reported as a wrong baud rate.
    /// </summary>
    private static async Task<SerialDevice> ProbeAsync(
        string port, IReadOnlyList<int> bauds, int idnTimeoutMs, CancellationToken ct)
    {
        var tried = new List<int>();
        bool noise = false;

        foreach (int baud in bauds)
        {
            ct.ThrowIfCancellationRequested();
            tried.Add(baud);

            var settings = SerialSettings.Default with { BaudRate = baud };
            string reply;

            try
            {
                using var client = new SerialInstrumentClient(port, settings) { TimeoutMs = idnTimeoutMs };
                await client.ConnectAsync(ct).ConfigureAwait(false);
                reply = await client.QueryAsync("*IDN?", ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // A port that will not open — held by another program, or gone since the
                // list was taken — is not a failure of the scan. It is a row with no
                // identity, which is what every unprobed row already looks like. Trying
                // the next rate would only fail the same way, but the loop is cheap and
                // stopping early would need a second kind of result to explain itself.
                continue;
            }

            if (LooksLikeIdentity(reply))
            {
                return new SerialDevice
                {
                    PortName = port,
                    Settings = settings,
                    Identity = reply.Trim(),
                    Tried = tried,
                };
            }

            if (reply.Trim().Length > 0) noise = true;
        }

        return new SerialDevice
        {
            PortName = port,
            Settings = SerialSettings.Default with { BaudRate = bauds[0] },
            Tried = tried,
            Noise = noise,
        };
    }

    /// <summary>
    /// Whether a reply reads as an <c>*IDN?</c> answer rather than as bytes arriving at the
    /// wrong speed.
    ///
    /// This check has no counterpart in the network scan, and needs none: TCP either
    /// delivers the sender's bytes or delivers nothing. A UART at the wrong baud rate
    /// delivers *different* bytes — framing errors read as plausible-looking characters —
    /// so accepting whatever arrived would put mojibake in the Identity column and call it
    /// an instrument.
    ///
    /// Two tests, both cheap. Everything printable, which kills the usual NUL/0xFF spray of
    /// a rate mismatch; and at least one comma, because IEEE 488.2 defines the reply as four
    /// comma-separated fields and every instrument in the catalogs obeys that. A real
    /// instrument answering without a comma is reported as noise rather than as an identity
    /// — the wrong way round is worse, because it is silent.
    /// </summary>
    internal static bool LooksLikeIdentity(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return false;

        string text = reply.Trim();
        if (text.Length < 3) return false;
        if (!text.Contains(',')) return false;

        foreach (char c in text)
        {
            if (c is '\t' or '\r' or '\n') continue;
            if (c is < ' ' or > '~') return false;
        }

        return true;
    }
}
