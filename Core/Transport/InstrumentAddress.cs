using System;

namespace LabEquipmentController;

/// <summary>
/// One instrument address, in any of the spellings a person might reasonably type:
///
///   TCPIP0::192.168.1.19::inst0::INSTR   a VISA resource string (delegated to <see cref="VisaResource"/>)
///   vxi://192.168.1.19                   VXI-11, without writing a resource string
///   tcp://192.168.1.19:5025              a raw socket, said out loud
///   192.168.1.19:5025                    host and port
///   192.168.1.19                         a bare host, on whichever port the caller defaults to
///   serial://COM3?baud=115200            an RS-232 port and its line settings
///   ASRL3::INSTR                         the same thing as VISA spells it
///
/// This lives in Core because all three front ends have to agree about what a typed
/// address means. They did not: the CLI and the web server each carried their own copy of
/// this parser — identical, which is the only reason nobody noticed the duplication — and
/// the desktop carried neither, so its Address box refused <c>vxi://…</c> though UI-SPEC
/// §3.3 describes that box, in both builds, as taking one. Two implementations of one rule
/// are one rule and one guess; three, one of which is a gap, is worse.
/// </summary>
/// <param name="Host">Hostname or IP, unbracketed — or the port name for a serial address.</param>
/// <param name="Transport">What to open: a raw socket, VXI-11, or a serial port.</param>
/// <param name="Port">
/// The TCP port — the raw socket's, or 111 for the VXI-11 portmapper. Zero and meaningless
/// for serial, whose settings are in <see cref="InstrumentAddress.Serial"/>.
/// </param>
/// <param name="DeviceName">The VXI-11 logical device ("inst0" unless a resource string said otherwise).</param>
public sealed record InstrumentAddress(
    string Host, InstrumentTransport Transport, int Port, string DeviceName)
{
    /// <summary>The port a bare host gets when nothing else decides it.</summary>
    public const int DefaultRawPort = 5025;

    /// <summary>
    /// The text said which transport to use — a VISA resource string, <c>vxi://</c> or
    /// <c>tcp://</c>. A caller that would otherwise second-guess the transport (the desktop
    /// prefers a discovered row's, so a bare address reaches an instrument the way the scan
    /// found it) must not do so here: the user named it.
    /// </summary>
    public bool TransportNamed { get; init; }

    /// <summary>
    /// The text carried a port of its own, rather than taking the default. Only meaningful
    /// when <see cref="TransportNamed"/> is false; a named form always settles its own port.
    /// </summary>
    public bool PortNamed { get; init; }

    /// <summary>
    /// Baud rate, framing, flow control and terminator, when <see cref="Transport"/> is
    /// <see cref="InstrumentTransport.Serial"/>; null otherwise. Never null when it is —
    /// an address that names no settings gets <see cref="SerialSettings.Default"/>, because
    /// 9600-8-N-1 is a decision as much as any other and belongs where it can be read.
    /// </summary>
    public SerialSettings? Serial { get; init; }

    /// <summary>
    /// Read an address, or say why it cannot be read.
    /// </summary>
    /// <param name="defaultRawPort">
    /// The port a bare host takes. 5025 by convention, but the desktop passes the first
    /// entry from its own SCPI Port(s) box, so that typing a bare address means the same
    /// thing there as pressing Scan does.
    /// </param>
    public static bool TryParse(
        string? text, out InstrumentAddress address, out string error,
        int defaultRawPort = DefaultRawPort)
    {
        address = new InstrumentAddress("", InstrumentTransport.RawSocket, defaultRawPort, "inst0");
        error = "";

        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) { error = "No address given."; return false; }

        // A full VISA resource string, as NI-MAX and Connection Expert report it.
        if (VisaResource.TryParse(text, out VisaResource visa))
        {
            address = new InstrumentAddress(visa.Host, visa.Transport, visa.Port, visa.DeviceName)
            {
                TransportNamed = true,
                PortNamed = true,
                // ASRL carries no line settings; VISA keeps those as session attributes.
                Serial = visa.Transport == InstrumentTransport.Serial ? SerialSettings.Default : null,
            };
            return true;
        }

        // It named a VISA interface and did not parse as one. That is not a hostname to try
        // and resolve — someone who typed GPIB0::9::INSTR meant an instrument, and telling
        // them there is no such host answers a question they did not ask. The transports
        // this app does not have are refused here by name, with the reason (SPEC §17).
        if (VisaResource.NamesAnInterface(text, out string iface))
        {
            error = iface switch
            {
                "GPIB" => "GPIB is not supported: reaching a GPIB card needs its manufacturer's own driver.",
                "USB" => "USB-TMC is not supported. This app speaks over the network and over RS-232.",
                "TCPIP" => $"'{text}' is not a resource string that can be read. Expected TCPIP0::host::inst0::INSTR, or TCPIP0::host::5025::SOCKET.",
                "ASRL" => $"'{text}' is not a resource string that can be read. Expected ASRL3::INSTR, or ASRL::COM3::INSTR.",
                _ => $"{iface} is not supported. This app speaks over the network and over RS-232.",
            };
            return false;
        }

        // vxi://host — the short way to ask for VXI-11. A trailing path is the logical
        // device behind a gateway: vxi://host/gpib0,9 is the instrument at GPIB address 9.
        if (text.StartsWith("vxi://", StringComparison.OrdinalIgnoreCase))
        {
            string rest = text["vxi://".Length..].Trim().TrimEnd('/');
            if (rest.Length == 0) { error = "vxi:// needs a host."; return false; }

            string device = "inst0";
            int slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                device = rest[(slash + 1)..].Trim();
                rest = rest[..slash].Trim();
                if (rest.Length == 0) { error = "vxi:// needs a host."; return false; }
                if (device.Length == 0) device = "inst0";
            }

            address = new InstrumentAddress(
                rest, InstrumentTransport.Vxi11, Vxi11Client.PortmapperPort, device)
            {
                TransportNamed = true,
                PortNamed = true,
            };
            return true;
        }

        // serial://COM3, serial:///dev/ttyUSB0 — with the line settings as a query, since
        // baud and parity have to match before one character gets through and there is
        // nothing on the wire to negotiate them with.
        if (text.StartsWith("serial://", StringComparison.OrdinalIgnoreCase))
        {
            string rest = text["serial://".Length..].Trim();

            string query = "";
            int question = rest.IndexOf('?');
            if (question >= 0)
            {
                query = rest[(question + 1)..];
                rest = rest[..question].Trim();
            }

            rest = rest.TrimEnd('/');
            if (rest.Length == 0) { error = "serial:// needs a port name, e.g. serial://COM3."; return false; }

            // serial://dev/ttyUSB0 is serial:///dev/ttyUSB0 with a slash lost to counting.
            // No real port is named "dev/…", so putting it back cannot take a good address
            // and make it wrong.
            if (rest.StartsWith("dev/", StringComparison.Ordinal)) rest = "/" + rest;

            if (!SerialSettings.TryParse(query, out SerialSettings settings, out error)) return false;

            address = new InstrumentAddress(rest, InstrumentTransport.Serial, 0, "")
            {
                TransportNamed = true,
                PortNamed = true,
                Serial = settings,
            };
            return true;
        }

        bool namedTransport = false;
        if (text.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            text = text["tcp://".Length..].Trim().TrimEnd('/');
            namedTransport = true;
            if (text.Length == 0) { error = "tcp:// needs a host."; return false; }
        }

        // host:port, or a bare host. An IPv6 literal's own colons are not port separators:
        // a bracketed one is split only after its closing bracket, so [::1]:5025 splits and
        // [::1] does not, and an unbracketed address splits only when its single colon can
        // be nothing else. Reading the last colon of [fe80::1] as a port separator is how
        // this used to answer "'1]' is not a port number" for a perfectly good address.
        string host = text;
        int port = defaultRawPort;
        bool portNamed = false;
        bool bracketed = text.StartsWith('[');

        int colon;
        if (bracketed)
        {
            int close = text.IndexOf(']');
            if (close < 0) { error = "No closing ']' in the address."; return false; }
            colon = text.IndexOf(':', close);
        }
        else
        {
            colon = text.LastIndexOf(':');
            if (colon > 0 && text.IndexOf(':') != colon) colon = -1;   // several colons: a bare IPv6
        }

        if (colon > 0)
        {
            string tail = text[(colon + 1)..];
            if (int.TryParse(tail, out int p))
            {
                if (p is < 1 or > 65535) { error = $"'{tail}' is not a port number."; return false; }
                host = text[..colon];
                port = p;
                portNamed = true;
            }
            else if (bracketed)
            {
                error = $"'{tail}' is not a port number.";
                return false;
            }
        }

        host = host.Trim('[', ']').Trim();
        if (host.Length == 0) { error = "No host in the address."; return false; }

        // Port 111 is the RPC portmapper, which is how VXI-11 is reached — writing it out
        // is the same request as vxi://host, and reading it as a raw socket would connect
        // to the portmapper and then wait forever for a SCPI reply it will never send.
        InstrumentTransport transport = port == Vxi11Client.PortmapperPort
            ? InstrumentTransport.Vxi11
            : InstrumentTransport.RawSocket;

        address = new InstrumentAddress(host, transport, port, "inst0")
        {
            TransportNamed = namedTransport,
            PortNamed = portNamed,
        };
        return true;
    }

    /// <summary>
    /// This address written back out in one canonical form — what tells one instrument from
    /// another, and what someone can type in to reach this one again.
    ///
    /// VXI-11 keeps its scheme, because <c>host:111</c> is the portmapper rather than an
    /// instrument, and carries its device name only when it is not the default. A raw
    /// socket is always host and port, spelled out even when the port is the usual 5025,
    /// since the whole job here is telling two apart. Serial keeps its scheme too — a bare
    /// <c>COM3</c> is a legal hostname and would be read as one — and carries only the line
    /// settings that are not the usual ones.
    /// </summary>
    public string Canonical
    {
        get
        {
            switch (Transport)
            {
                case InstrumentTransport.Vxi11:
                    return string.IsNullOrWhiteSpace(DeviceName)
                           || DeviceName.Equals("inst0", StringComparison.OrdinalIgnoreCase)
                        ? $"vxi://{Host}"
                        : $"vxi://{Host}/{DeviceName}";

                case InstrumentTransport.Serial:
                    string query = (Serial ?? SerialSettings.Default).Query;
                    return query.Length == 0 ? $"serial://{Host}" : $"serial://{Host}?{query}";

                default:
                    return $"{Host}:{Port}";
            }
        }
    }

    /// <summary>
    /// Open a client for this address — the one place that knows which transport goes with
    /// which spelling. Returned unwrapped: whether exchanges are serialized is the caller's
    /// policy, and every front end in this repository wraps the result in a
    /// <see cref="SerializedInstrumentClient"/> on the next line.
    /// </summary>
    /// <param name="timeoutMs">Connect, read and write timeout, in milliseconds.</param>
    public IInstrumentClient CreateClient(int timeoutMs = 5000)
    {
        IInstrumentClient client = Transport switch
        {
            InstrumentTransport.Vxi11 => new Vxi11Client(Host, string.IsNullOrWhiteSpace(DeviceName) ? "inst0" : DeviceName),
            InstrumentTransport.Serial => new SerialInstrumentClient(Host, Serial ?? SerialSettings.Default),
            _ => new ScpiClient(Host, Port),
        };
        client.TimeoutMs = timeoutMs;
        return client;
    }

    /// <summary>What to show a person: the canonical spelling and the transport behind it.</summary>
    public override string ToString() => $"{Canonical} ({Transport.DisplayName()})";
}
