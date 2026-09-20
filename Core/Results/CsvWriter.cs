using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LabEquipmentController;

/// <summary>
/// Recorded rows as a CSV document — the one writer every Save CSV in the project goes
/// through, so the quoting rule cannot drift between the desktop app and the browser.
///
/// It has drifted before, which is why this exists. An instrument's reply is not a number as
/// often as you would like: a Siglent generator answers <c>C1:OUTP?</c> with
/// <c>C1:OUTP OFF,LOAD,HZ,PLRT,NOR</c>, and every <c>*IDN?</c> in the world carries three
/// commas. Recorded into a column and written out unquoted, one reading became five columns
/// and the file no longer described the table it came from.
///
/// RFC 4180 throughout: CRLF between rows — which is what the CSV standard says and what a
/// spreadsheet on any platform expects — and a field quoted only when it holds a comma, a
/// quote or a line break, so a column of plain numbers stays a column of plain numbers.
/// </summary>
public static class CsvWriter
{
    /// <summary>A header row and the rows under it.</summary>
    public static string Table(IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        if (columns.Count > 0) sb.Append(Row(columns));
        foreach (IReadOnlyList<string> row in rows) sb.Append(Row(row));
        return sb.ToString();
    }

    /// <summary>One row, terminated. Empty of fields is still a line, as an empty row is.</summary>
    public static string Row(IEnumerable<string> fields)
        => string.Join(',', fields.Select(Field)) + "\r\n";

    /// <summary>
    /// One field, quoted when it needs to be and left alone when it does not. Embedded quotes
    /// are doubled, which is how CSV escapes them.
    /// </summary>
    public static string Field(string? value)
    {
        string text = value ?? "";
        return text.IndexOfAny([',', '"', '\n', '\r']) < 0
            ? text
            : '"' + text.Replace("\"", "\"\"") + '"';
    }
}
