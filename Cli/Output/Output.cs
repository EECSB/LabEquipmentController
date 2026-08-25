using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LabEquipmentController.Cli;

/// <summary>How results reach the terminal, a pipe, or a file.</summary>
/// <remarks>
/// Kept apart from the commands that produce the data so the shapes can be tested without
/// a network: a scan that formats its table wrongly is as broken as one that finds nothing,
/// and only one of those two failures needs an instrument to reproduce.
/// </remarks>
public static class Output
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// A plain column table. No box drawing and no ANSI: this output gets piped into grep
    /// and pasted into issues at least as often as it is read on a terminal, and a Windows
    /// console that has not had virtual-terminal mode turned on prints escape codes
    /// literally.
    /// </summary>
    public static string Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (headers.Count == 0) return "";
        var width = new int[headers.Count];
        for (int c = 0; c < headers.Count; c++) width[c] = headers[c].Length;
        foreach (var row in rows)
            for (int c = 0; c < headers.Count && c < row.Count; c++)
                width[c] = Math.Max(width[c], (row[c] ?? "").Length);

        var sb = new StringBuilder();
        AppendRow(sb, headers, width);
        // A rule under the headers, of the same widths, so a wrapped terminal still shows
        // where the columns were meant to fall.
        AppendRow(sb, headers.Select((_, c) => new string('-', width[c])).ToList(), width);
        foreach (var row in rows) AppendRow(sb, row, width);
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> cells, int[] width)
    {
        for (int c = 0; c < width.Length; c++)
        {
            string cell = c < cells.Count ? cells[c] ?? "" : "";
            sb.Append(cell);
            // No trailing whitespace on the last column: it is invisible until it lands in
            // a diff, and these tables get committed to issues and READMEs.
            if (c < width.Length - 1) sb.Append(new string(' ', width[c] - cell.Length + 2));
        }
        sb.Append('\n');
    }

    /// <summary>RFC 4180: quote when the value contains a comma, a quote or a newline.</summary>
    /// <remarks>
    /// No headers means no header line — not an empty one. Streaming calls this twice, once
    /// with the headers and no rows and then once per row with no headers, and an
    /// unconditional header line put a blank line in front of every reading. Any tool
    /// reading that CSV sees a row of one empty field between each real one.
    /// </remarks>
    public static string Csv(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        if (headers.Count > 0)
            sb.Append(string.Join(",", headers.Select(Escape))).Append("\r\n");
        foreach (var row in rows)
            sb.Append(string.Join(",", row.Select(Escape))).Append("\r\n");
        return sb.ToString();

        static string Escape(string? cell)
        {
            cell ??= "";
            return cell.IndexOfAny([',', '"', '\r', '\n']) >= 0
                ? '"' + cell.Replace("\"", "\"\"") + '"'
                : cell;
        }
    }

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Json);

    /// <summary>Render one result set in whichever shape the options asked for.</summary>
    public static string Render(ParsedCommand cmd, IReadOnlyList<string> headers,
                                IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (cmd.Has("json"))
            return ToJson(rows.Select(r => headers
                .Select((h, i) => (h, v: i < r.Count ? r[i] : ""))
                .ToDictionary(x => x.h, x => x.v)).ToList());
        if (cmd.Has("csv")) return Csv(headers, rows);
        return Table(headers, rows);
    }

    /// <summary>Seconds since the run started, formatted the way the CSV export does.</summary>
    public static string Seconds(double s) => s.ToString("0.000", CultureInfo.InvariantCulture);
}
