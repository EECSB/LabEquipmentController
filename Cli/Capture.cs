using System.Globalization;
using System.Text;

namespace LabEquipmentController.Cli;

/// <summary>Pulling pictures and traces off an instrument, and writing them down.</summary>
public static class Capture
{
    /// <summary>
    /// What kind of image an instrument just handed back, read from the bytes themselves.
    /// </summary>
    /// <remarks>
    /// The format is the instrument's decision, not ours: the Rigol's <c>:DISPlay:DATA?</c>
    /// returns a BMP, a Tektronix told <c>SAVe:IMAGe:FILEFormat PNG</c> returns a PNG, and
    /// some R&amp;S analyzers only document BMP at all. So the extension is sniffed rather
    /// than assumed — naming a BMP ".png" produces a file that half the world's tools
    /// refuse to open and the other half open while reporting the wrong format.
    ///
    /// Converting between them is deliberately not done. The only in-box converter is
    /// System.Drawing, which is Windows-only, and pulling in a native imaging package to
    /// re-encode a screenshot would cost this CLI its reason to exist.
    /// </remarks>
    public static string ExtensionFor(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return ".png";
        if (data.Length >= 2 && data[0] == 0x42 && data[1] == 0x4D) return ".bmp";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return ".jpg";
        if (data.Length >= 6 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return ".gif";
        if (data.Length >= 4 && data[0] == 0x49 && data[1] == 0x49 && data[2] == 0x2A) return ".tif";
        if (data.Length >= 4 && data[0] == 0x4D && data[1] == 0x4D && data[3] == 0x2A) return ".tif";
        return ".bin";
    }

    /// <summary>
    /// Give the file the extension the bytes deserve, keeping whatever name was asked for.
    /// A user who wrote "shot.png" and whose scope sent a BMP gets "shot.bmp" and is told.
    /// </summary>
    public static string PathFor(string requested, ReadOnlySpan<byte> data)
    {
        string ext = ExtensionFor(data);
        string current = Path.GetExtension(requested);
        if (current.Equals(ext, StringComparison.OrdinalIgnoreCase)) return requested;
        // .jpeg and .jpg are the same picture; do not rename over that.
        if (ext == ".jpg" && current.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) return requested;
        return Path.ChangeExtension(requested, ext);
    }

    /// <summary>A capture as CSV: the same two columns the GUI's export writes.</summary>
    public static string ToCsv(WaveformCapture capture) => capture.ToCsv();

    /// <summary>A capture as an SVG trace.</summary>
    public static string ToSvg(WaveformCapture capture, string title)
    {
        var pts = capture.Samples.Select(s => (s.Time, s.Voltage)).ToList();
        return Plot.Svg(title, "Time (s)", "Voltage (V)", [new Plot.Series("Channel", pts)]);
    }

    /// <summary>
    /// Read a CSV back into a table, for plotting a file recorded earlier. Handles the
    /// quoting rules the export writes — a description containing a comma is one field,
    /// and splitting on commas alone would turn it into two.
    /// </summary>
    public static (List<string> Headers, List<IReadOnlyList<string>> Rows) ReadCsv(string text)
    {
        var rows = new List<IReadOnlyList<string>>();
        var headers = new List<string>();
        var field = new StringBuilder();
        var row = new List<string>();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    if (headers.Count == 0) headers.AddRange(row); else rows.Add(row.ToArray());
                    row = new List<string>();
                    break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (headers.Count == 0) headers.AddRange(row); else rows.Add(row.ToArray());
        }
        return (headers, rows);
    }

    /// <summary>Parse "500ms", "2s", "1m" or a bare number of milliseconds.</summary>
    public static bool TryParseInterval(string? text, out int milliseconds)
    {
        milliseconds = 0;
        text = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text)) return false;

        double scale = 1;
        if (text.EndsWith("ms", StringComparison.Ordinal)) { text = text[..^2]; }
        else if (text.EndsWith("s", StringComparison.Ordinal)) { text = text[..^1]; scale = 1000; }
        else if (text.EndsWith("m", StringComparison.Ordinal)) { text = text[..^1]; scale = 60_000; }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return false;
        double ms = n * scale;
        if (ms < 1 || ms > int.MaxValue) return false;
        milliseconds = (int)ms;
        return true;
    }
}
