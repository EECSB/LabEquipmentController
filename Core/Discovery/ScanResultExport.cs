using System.Collections.Generic;
using System.Linq;

namespace LabEquipmentController;

/// <summary>Serializes discovered instruments to CSV (RFC 4180).</summary>
public static class ScanResultExport
{
    /// <summary>
    /// The header row, and the names of the four columns the list is drawn with. The window
    /// and the file say the same words in the same order, which is the point: a column called
    /// Protocol on screen and Transport in the file is two names for one thing.
    /// </summary>
    public static IReadOnlyList<string> Columns { get; } = ["IP Address", "Port", "Protocol", "Identity"];

    /// <summary>
    /// The same four columns for a serial scan, named the way that list names them. Two
    /// headers rather than one because the first two columns genuinely hold different
    /// things — a port name and its line settings, not an address and a number — and a file
    /// headed "IP Address" over a column of COM3 would be a worse lie than a second header.
    /// </summary>
    public static IReadOnlyList<string> SerialColumns { get; } = ["Port", "Settings", "Protocol", "Identity"];

    /// <summary>Build a CSV document (with header row) from the given devices.</summary>
    public static string ToCsv(IEnumerable<ScpiDevice> devices)
        => ToCsv(devices.Select(d => (d.Address.ToString(), d.Port, d.TransportName, d.Identity)));

    /// <summary>The serial list, written with <see cref="SerialColumns"/> over it.</summary>
    public static string ToCsv(IEnumerable<SerialDevice> devices)
        => ToCsv(SerialColumns,
                 devices.Select(d => (d.PortName, d.SettingsText, d.TransportName, d.IdentityText)));

    /// <summary>
    /// The same document from rows that have already been reduced to strings, which is what
    /// the browser has: the web build's list crosses the wire as a DTO and the ScpiDevice it
    /// came from is long gone by the time anyone presses Export. One writer either way, so
    /// the quoting rule and the header cannot drift between the two front ends.
    /// </summary>
    public static string ToCsv(IEnumerable<(string Address, int Port, string Protocol, string Identity)> rows)
        => ToCsv(Columns,
                 rows.Select(r => (r.Address, r.Port.ToString(), r.Protocol, r.Identity)));

    /// <summary>
    /// Four columns of text under whichever header they belong to. The one writer both
    /// scans and both front ends go through, so the quoting rule cannot drift between them.
    /// </summary>
    public static string ToCsv(IReadOnlyList<string> columns,
                               IEnumerable<(string A, string B, string C, string D)> rows)
        => CsvWriter.Table(columns, rows.Select(r => (IReadOnlyList<string>)[r.A, r.B, r.C, r.D]));
}
