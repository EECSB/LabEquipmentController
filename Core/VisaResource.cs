using System;

namespace LabEquipmentController;

/// <summary>
/// A parsed VISA-style TCPIP resource string, e.g.
///   TCPIP0::192.168.1.19::inst0::INSTR   → VXI-11 (device "inst0")
///   TCPIP0::192.168.1.19::5025::SOCKET   → raw socket on port 5025
///
/// We parse these ourselves (no VISA runtime) so a user can paste the canonical
/// string an instrument reports in NI-MAX / Keysight Connection Expert. Only the
/// TCPIP interface is supported — this app is LAN-only.
/// </summary>
public sealed class VisaResource
{
    public required string Host { get; init; }
    public required InstrumentTransport Transport { get; init; }

    /// <summary>Raw-socket TCP port, or the VXI-11 portmapper port (111) for INSTR.</summary>
    public int Port { get; init; }

    /// <summary>VXI-11 logical device name (INSTR resources); "inst0" by default.</summary>
    public string DeviceName { get; init; } = "inst0";

    public static bool TryParse(string? text, out VisaResource resource)
    {
        resource = null!;
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("::")) return false;

        string[] parts = text.Split("::", StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;

        // Interface: TCPIP, optionally with a board index (TCPIP0). LAN only.
        if (!parts[0].StartsWith("TCPIP", StringComparison.OrdinalIgnoreCase)) return false;

        string host = parts[1];
        if (host.Length == 0) return false;

        string last = parts[^1];
        bool isSocket = last.Equals("SOCKET", StringComparison.OrdinalIgnoreCase);
        bool isInstr = last.Equals("INSTR", StringComparison.OrdinalIgnoreCase);

        // Tokens between the host and the resource class (INSTR/SOCKET, if present).
        int classIdx = (isSocket || isInstr) ? parts.Length - 1 : parts.Length;
        string[] middle = parts[2..classIdx];

        if (isSocket)
        {
            // SOCKET requires a port as the token before ::SOCKET.
            if (middle.Length == 0 ||
                !int.TryParse(middle[^1], out int port) || port is <= 0 or > 65535)
                return false;

            resource = new VisaResource
            {
                Host = host,
                Transport = InstrumentTransport.RawSocket,
                Port = port,
            };
            return true;
        }

        // INSTR (explicit or the default class): a VXI-11 instrument.
        resource = new VisaResource
        {
            Host = host,
            Transport = InstrumentTransport.Vxi11,
            Port = Vxi11Client.PortmapperPort,
            DeviceName = middle.Length > 0 ? middle[0] : "inst0",
        };
        return true;
    }

    /// <summary>The canonical resource string for a discovered device.</summary>
    public static string Format(InstrumentTransport transport, string host, int port) =>
        transport == InstrumentTransport.Vxi11
            ? $"TCPIP0::{host}::inst0::INSTR"
            : $"TCPIP0::{host}::{port}::SOCKET";

    public override string ToString() =>
        Transport == InstrumentTransport.Vxi11
            ? $"TCPIP0::{Host}::{DeviceName}::INSTR"
            : $"TCPIP0::{Host}::{Port}::SOCKET";
}
