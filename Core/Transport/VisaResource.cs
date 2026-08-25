using System;

namespace LabEquipmentController;

/// <summary>
/// A parsed VISA-style resource string, e.g.
///   TCPIP0::192.168.1.19::inst0::INSTR   → VXI-11 (device "inst0")
///   TCPIP0::192.168.1.19::5025::SOCKET   → raw socket on port 5025
///   ASRL3::INSTR                         → the serial port VISA calls ASRL3
///
/// We parse these ourselves (no VISA runtime) so a user can paste the canonical
/// string an instrument reports in NI-MAX / Keysight Connection Expert. TCPIP and
/// ASRL are the interfaces this app has transports for; GPIB and USB resource
/// strings are refused rather than half-read (SPEC §17).
/// </summary>
public sealed class VisaResource
{
    /// <summary>Hostname or IP — or, for an ASRL resource, the serial port's name.</summary>
    public required string Host { get; init; }

    public required InstrumentTransport Transport { get; init; }

    /// <summary>Raw-socket TCP port, or the VXI-11 portmapper port (111) for INSTR. Zero for ASRL.</summary>
    public int Port { get; init; }

    /// <summary>VXI-11 logical device name (INSTR resources); "inst0" by default.</summary>
    public string DeviceName { get; init; } = "inst0";

    public static bool TryParse(string? text, out VisaResource resource)
    {
        resource = null!;
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("::")) return false;

        string[] parts = text.Split("::", StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;

        if (parts[0].StartsWith("ASRL", StringComparison.OrdinalIgnoreCase))
            return TryParseSerial(parts, out resource);

        // Interface: TCPIP, optionally with a board index (TCPIP0).
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

    /// <summary>
    /// The VISA interface a string names, whether or not the rest of it parses — so that a
    /// resource string this app cannot use is answered with the reason rather than being
    /// mistaken for a hostname. <c>GPIB0::9::INSTR</c> is not a machine on the network, and
    /// "no such host" is the wrong thing to say about it.
    /// </summary>
    /// <remarks>
    /// Only the part before the first <c>::</c> is looked at, with any board index dropped.
    /// An IPv6 literal reaches this too — <c>fe80::1</c> has a <c>::</c> in it — and does not
    /// match, because what is in front of the colons is not the name of an interface.
    /// </remarks>
    internal static bool NamesAnInterface(string text, out string name)
    {
        name = "";

        int sep = text.IndexOf("::", StringComparison.Ordinal);
        if (sep <= 0) return false;

        string head = text[..sep].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').ToUpperInvariant();

        if (head is not ("TCPIP" or "ASRL" or "GPIB" or "USB" or "PXI" or "VXI" or "FIREWIRE"))
            return false;

        name = head;
        return true;
    }

    /// <summary>
    /// An ASRL resource: VISA's name for a serial port.
    ///
    ///   ASRL3::INSTR             the third one — COM3, as NI-MAX and Connection Expert
    ///                            number them, those being the tools these strings get
    ///                            copied out of
    ///   ASRL::COM3::INSTR        the same, said explicitly
    ///   ASRL::/dev/ttyUSB0::INSTR  which is the only way to say it off Windows, where
    ///                            "the third serial port" is not a name anything agrees on
    ///
    /// Line settings are not part of a resource string — VISA carries them as session
    /// attributes — so an ASRL address here means the defaults until something says
    /// otherwise, and <c>serial://COM3?baud=115200</c> is the spelling that can.
    /// </summary>
    private static bool TryParseSerial(string[] parts, out VisaResource resource)
    {
        resource = null!;

        string last = parts[^1];
        bool hasClass = last.Equals("INSTR", StringComparison.OrdinalIgnoreCase);
        if (!hasClass && parts.Length > 2) return false;   // ASRL::COM3::SOCKET is not a thing

        string[] middle = parts[1..(hasClass ? ^1 : ^0)];
        string index = parts[0]["ASRL".Length..].Trim();

        string name;
        if (middle.Length > 0 && middle[0].Length > 0)
        {
            name = middle[0];                                    // an explicit port name
        }
        else if (index.Length > 0 && int.TryParse(index, out int n) && n > 0)
        {
            name = $"COM{n}";
        }
        else
        {
            return false;
        }

        resource = new VisaResource
        {
            Host = name,
            Transport = InstrumentTransport.Serial,
            Port = 0,
        };
        return true;
    }

    /// <summary>The canonical resource string for a discovered device.</summary>
    public static string Format(InstrumentTransport transport, string host, int port) =>
        transport switch
        {
            InstrumentTransport.Vxi11 => $"TCPIP0::{host}::inst0::INSTR",
            InstrumentTransport.Serial => $"ASRL::{host}::INSTR",
            _ => $"TCPIP0::{host}::{port}::SOCKET",
        };

    public override string ToString() =>
        Transport switch
        {
            InstrumentTransport.Vxi11 => $"TCPIP0::{Host}::{DeviceName}::INSTR",
            // The explicit form, always: ASRL3 is only COM3 by a convention that stops at
            // the edge of Windows, and a resource string is for pasting elsewhere.
            InstrumentTransport.Serial => $"ASRL::{Host}::INSTR",
            _ => $"TCPIP0::{Host}::{Port}::SOCKET",
        };
}
