namespace LabEquipmentController.Cli;

/// <summary>One instrument address, as typed on a command line.</summary>
/// <remarks>
/// The GUI has an address box and two radio buttons; a command line has one string, so it
/// has to carry the transport too. Four spellings are accepted, and the VISA one is
/// delegated to <see cref="VisaResource"/> so the CLI and the app agree about what
/// "TCPIP0::host::inst0::INSTR" means rather than each having an opinion.
/// </remarks>
public sealed record Endpoint(string Host, InstrumentTransport Transport, int Port, string DeviceName)
{
    public const int DefaultRawPort = 5025;

    public static bool TryParse(string? text, out Endpoint endpoint, out string error)
    {
        endpoint = new Endpoint("", InstrumentTransport.RawSocket, DefaultRawPort, "inst0");
        error = "";
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) { error = "No address given."; return false; }

        // A full VISA resource string.
        if (VisaResource.TryParse(text, out var visa))
        {
            endpoint = new Endpoint(visa.Host, visa.Transport, visa.Port, visa.DeviceName);
            return true;
        }

        // vxi://host — the short way to ask for VXI-11 without writing a resource string.
        if (text.StartsWith("vxi://", StringComparison.OrdinalIgnoreCase))
        {
            string h = text["vxi://".Length..].Trim().TrimEnd('/');
            if (h.Length == 0) { error = "vxi:// needs a host."; return false; }
            endpoint = new Endpoint(h, InstrumentTransport.Vxi11, Vxi11Client.PortmapperPort, "inst0");
            return true;
        }

        if (text.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            text = text["tcp://".Length..].Trim().TrimEnd('/');

        // host:port, or a bare host. An IPv6 literal is bracketed, and its colons are not
        // port separators — [::1]:5025 splits at the last colon, ::1 does not split at all.
        string host = text;
        int port = DefaultRawPort;
        int colon = text.LastIndexOf(':');
        bool bracketed = text.StartsWith('[');
        if (colon > 0 && (bracketed || text.IndexOf(':') == colon))
        {
            string tail = text[(colon + 1)..];
            if (int.TryParse(tail, out int p))
            {
                if (p is < 1 or > 65535) { error = $"'{tail}' is not a port number."; return false; }
                host = text[..colon];
                port = p;
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
        var transport = port == Vxi11Client.PortmapperPort
            ? InstrumentTransport.Vxi11
            : InstrumentTransport.RawSocket;

        endpoint = new Endpoint(host, transport, port, "inst0");
        return true;
    }

    /// <summary>Open a client for this endpoint. Serialized, as every caller in this project is.</summary>
    public IInstrumentClient CreateClient(int timeoutMs)
    {
        IInstrumentClient inner = Transport == InstrumentTransport.Vxi11
            ? new Vxi11Client(Host, DeviceName)
            : new ScpiClient(Host, Port);
        inner.TimeoutMs = timeoutMs;
        return new SerializedInstrumentClient(inner);
    }

    public override string ToString() => Transport == InstrumentTransport.Vxi11
        ? $"{Host} (VXI-11)"
        : $"{Host}:{Port} (raw socket)";
}
