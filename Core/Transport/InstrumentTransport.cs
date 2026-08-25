namespace LabEquipmentController;

/// <summary>
/// How we talk SCPI to an instrument.
///
/// This lived in <c>NetworkScanner.cs</c> while every member of it was a network, which
/// stopped being true when <see cref="Serial"/> arrived: a scanner is a poor place to keep
/// the name of the one transport it cannot scan. Same namespace, so nothing outside this
/// repository can tell it moved.
/// </summary>
public enum InstrumentTransport
{
    /// <summary>Plain TCP socket (Rigol 5555, Keysight/Siglent scopes 5025, ...).</summary>
    RawSocket,

    /// <summary>VXI-11 over ONC RPC, discovered via the portmapper on port 111.</summary>
    Vxi11,

    /// <summary>
    /// SCPI over an RS-232 serial port — <c>COM3</c>, <c>/dev/ttyUSB0</c>. Appended rather
    /// than inserted: this enum is stored by value in settings and reported over the web
    /// API, so the two above have to keep the numbers they already have.
    /// </summary>
    Serial,
}

/// <summary>What to call a transport in front of a person.</summary>
public static class InstrumentTransports
{
    /// <summary>
    /// The name shown in a device list, a console header or a CLI line. One copy, because
    /// the scanner, the web API and the CLI each used to spell it out with a ternary — fine
    /// while there were two of them, a bug waiting for a third.
    /// </summary>
    public static string DisplayName(this InstrumentTransport transport) => transport switch
    {
        InstrumentTransport.Vxi11 => "VXI-11",
        InstrumentTransport.Serial => "Serial",
        _ => "Raw socket",
    };
}
