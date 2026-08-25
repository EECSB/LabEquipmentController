using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;

namespace LabEquipmentController;

/// <summary>
/// The serial ports this machine has. Names only — this lists, it does not probe, and the
/// distinction is the whole reason serial has no place in <c>NetworkScanner</c>.
///
/// A subnet sweep asks every address the same harmless question and believes the answers.
/// A serial port cannot be asked anything until the baud rate, framing and flow control
/// already match, so a sweep would have to try combinations of them; and the thing on the
/// other end may be a modem, a printer, a GPS or a 3D printer, for which an unexpected
/// <c>*IDN?</c> at the wrong speed is not the harmless connect a TCP probe is. So the list
/// is offered and the choice is the user's — which is what SPEC §17 means by discovery not
/// transferring.
/// </summary>
public static class SerialPorts
{
    /// <summary>
    /// Every port name the operating system reports, sorted so <c>COM2</c> comes before
    /// <c>COM10</c> rather than after it. Empty if the platform has no serial ports to
    /// report at all, which is the case in a browser.
    /// </summary>
    public static IReadOnlyList<string> Names()
    {
        string[] names;
        try { names = SerialPort.GetPortNames(); }
        catch (PlatformNotSupportedException) { return Array.Empty<string>(); }
        catch (Exception) { return Array.Empty<string>(); }

        return Sort(names);
    }

    /// <summary>
    /// The order they are offered in: like with like, and by number within that, so COM2
    /// comes before COM10 rather than after it — which plain text sorting gets wrong, and
    /// gets wrong exactly where a bench has enough adapters for it to matter.
    /// </summary>
    internal static IReadOnlyList<string> Sort(IEnumerable<string> names)
        => names.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(Stem, StringComparer.OrdinalIgnoreCase)
                .ThenBy(TrailingNumber)
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>Where a port name's trailing digits begin, or its length if it has none.</summary>
    private static int DigitsStart(string name)
    {
        int i = name.Length;
        while (i > 0 && char.IsAsciiDigit(name[i - 1])) i--;
        return i;
    }

    /// <summary>The name without its trailing digits — "COM", "/dev/ttyUSB" — so like sorts with like.</summary>
    private static string Stem(string name) => name[..DigitsStart(name)];

    /// <summary>The digits on the end of a port name, so COM2 comes before COM10.</summary>
    private static int TrailingNumber(string name)
    {
        int i = DigitsStart(name);
        return i < name.Length && int.TryParse(name[i..], out int n) ? n : int.MaxValue;
    }
}
