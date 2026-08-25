namespace LabEquipmentController.Cli;

/// <summary>One instrument address, as typed on a command line, and a client to reach it.</summary>
/// <remarks>
/// The GUI has an address box and two radio buttons; a command line has one string, so it
/// has to carry the transport too. Reading it is <see cref="InstrumentAddress"/>'s job, in
/// Core, so the CLI, the desktop app and the web server agree about what a typed address
/// means rather than each having an opinion — they did not, and the desktop's box refused
/// a spelling UI-SPEC §3.3 says it takes.
///
/// What is left here is one rule that is genuinely this program's: every connection it
/// opens is serialized. The address is held whole rather than copied field by field —
/// this used to be four fields, which was lossless while every address was a host and a
/// port, and would quietly drop the baud rate of the first serial one.
/// </remarks>
public sealed record Endpoint(InstrumentAddress Address)
{
    public const int DefaultRawPort = InstrumentAddress.DefaultRawPort;

    public string Host => Address.Host;
    public InstrumentTransport Transport => Address.Transport;
    public int Port => Address.Port;
    public string DeviceName => Address.DeviceName;

    /// <summary>The line settings, for a serial address; null for the LAN transports.</summary>
    public SerialSettings? Serial => Address.Serial;

    public static bool TryParse(string? text, out Endpoint endpoint, out string error)
    {
        endpoint = new Endpoint(new InstrumentAddress("", InstrumentTransport.RawSocket, DefaultRawPort, "inst0"));

        if (!InstrumentAddress.TryParse(text, out InstrumentAddress a, out error, DefaultRawPort))
            return false;

        endpoint = new Endpoint(a);
        return true;
    }

    /// <summary>Open a client for this endpoint. Serialized, as every caller in this project is.</summary>
    public IInstrumentClient CreateClient(int timeoutMs)
        => new SerializedInstrumentClient(Address.CreateClient(timeoutMs));

    public override string ToString() => Transport switch
    {
        InstrumentTransport.Vxi11 => $"{Host} (VXI-11)",
        InstrumentTransport.Serial => $"{Host} (serial, {Serial ?? SerialSettings.Default})",
        _ => $"{Host}:{Port} (raw socket)",
    };
}
