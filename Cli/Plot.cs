using System.Globalization;
using System.Text;

namespace LabEquipmentController.Cli;

/// <summary>
/// Draws a line chart as SVG.
/// </summary>
/// <remarks>
/// Hand-written rather than drawn with a library, and SVG rather than PNG, for one reason:
/// the CLI runs on Linux, macOS and Windows, and the two ways to rasterise a chart from
/// .NET are System.Drawing (Windows-only — the exact thing this project is not) and a
/// native imaging package (a real dependency, per-RID native binaries, published three
/// times over). An SVG is text. It opens in every browser, scales without pixelating,
/// prints into a report, and costs nothing to produce.
///
/// The GUI's plot panel is the richer one — pick axes, tick several series, switch to log.
/// This is the headless equivalent: enough to see whether a sweep worked before spending
/// an afternoon on the numbers, which is the same thing that panel is for.
/// </remarks>
public static class Plot
{
    private const int Width = 900, Height = 480;
    private const int Left = 78, Right = 24, Top = 28, Bottom = 56;

    private static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>An axis label rounded to something a person would write on graph paper.</summary>
    private static string Tick(double v)
    {
        double a = Math.Abs(v);
        if (a != 0 && (a < 1e-3 || a >= 1e6)) return v.ToString("0.###e+0", CultureInfo.InvariantCulture);
        return v.ToString(a >= 100 ? "0.##" : "0.####", CultureInfo.InvariantCulture);
    }

    /// <summary>Escape the five characters that would otherwise end an SVG attribute or element.</summary>
    internal static string Esc(string? s) => (s ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");

    public sealed record Series(string Name, IReadOnlyList<(double X, double Y)> Points);

    /// <summary>
    /// Render one or more series. Colours come from a fixed six-entry palette, chosen to
    /// stay distinguishable in greyscale and to the ~8% of men with red-green colour
    /// blindness — a plot of two traces is useless if two traces look the same.
    /// </summary>
    public static string Svg(string title, string xLabel, string yLabel, IReadOnlyList<Series> series)
    {
        string[] palette = ["#1f77b4", "#d62728", "#2ca02c", "#9467bd", "#ff7f0e", "#17becf"];

        var all = series.SelectMany(s => s.Points).ToList();
        double xMin = all.Count > 0 ? all.Min(p => p.X) : 0, xMax = all.Count > 0 ? all.Max(p => p.X) : 1;
        double yMin = all.Count > 0 ? all.Min(p => p.Y) : 0, yMax = all.Count > 0 ? all.Max(p => p.Y) : 1;
        // A flat trace has no range to scale against; give it one so the line lands mid-plot
        // instead of dividing by zero or pinning to an edge.
        if (xMax - xMin < double.Epsilon) { xMin -= 0.5; xMax += 0.5; }
        if (yMax - yMin < double.Epsilon) { yMin -= 0.5; yMax += 0.5; }
        double padY = (yMax - yMin) * 0.05;
        yMin -= padY; yMax += padY;

        double PlotX(double x) => Left + (x - xMin) / (xMax - xMin) * (Width - Left - Right);
        double PlotY(double y) => Height - Bottom - (y - yMin) / (yMax - yMin) * (Height - Top - Bottom);

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {Width} {Height}\" width=\"{Width}\" height=\"{Height}\" font-family=\"system-ui, sans-serif\">\n");
        // An explicit white ground: an SVG with no background is transparent, and a
        // transparent plot dropped into a dark-themed viewer is black lines on black.
        sb.Append($"<rect width=\"{Width}\" height=\"{Height}\" fill=\"#ffffff\"/>\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{Width / 2}\" y=\"18\" text-anchor=\"middle\" font-size=\"14\" fill=\"#111\">{Esc(title)}</text>\n");

        // Grid and axis labels: five lines each way is enough to read a value off and few
        // enough not to fight the trace.
        for (int i = 0; i <= 5; i++)
        {
            double fy = yMin + (yMax - yMin) * i / 5.0;
            double py = PlotY(fy);
            sb.Append(CultureInfo.InvariantCulture,
                $"<line x1=\"{Left}\" y1=\"{N(py)}\" x2=\"{Width - Right}\" y2=\"{N(py)}\" stroke=\"#e6e6e6\"/>\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{Left - 8}\" y=\"{N(py + 4)}\" text-anchor=\"end\" font-size=\"11\" fill=\"#444\">{Esc(Tick(fy))}</text>\n");

            double fx = xMin + (xMax - xMin) * i / 5.0;
            double px = PlotX(fx);
            sb.Append(CultureInfo.InvariantCulture,
                $"<line x1=\"{N(px)}\" y1=\"{Top}\" x2=\"{N(px)}\" y2=\"{Height - Bottom}\" stroke=\"#f0f0f0\"/>\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{N(px)}\" y=\"{Height - Bottom + 18}\" text-anchor=\"middle\" font-size=\"11\" fill=\"#444\">{Esc(Tick(fx))}</text>\n");
        }

        sb.Append(CultureInfo.InvariantCulture,
            $"<rect x=\"{Left}\" y=\"{Top}\" width=\"{Width - Left - Right}\" height=\"{Height - Top - Bottom}\" fill=\"none\" stroke=\"#999\"/>\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{Width / 2}\" y=\"{Height - 12}\" text-anchor=\"middle\" font-size=\"12\" fill=\"#111\">{Esc(xLabel)}</text>\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<text x=\"16\" y=\"{Height / 2}\" text-anchor=\"middle\" font-size=\"12\" fill=\"#111\" transform=\"rotate(-90 16 {Height / 2})\">{Esc(yLabel)}</text>\n");

        for (int s = 0; s < series.Count; s++)
        {
            var pts = series[s].Points;
            if (pts.Count == 0) continue;
            string colour = palette[s % palette.Length];
            var d = new StringBuilder();
            for (int i = 0; i < pts.Count; i++)
                d.Append(i == 0 ? 'M' : 'L').Append(N(PlotX(pts[i].X))).Append(' ').Append(N(PlotY(pts[i].Y))).Append(' ');
            sb.Append(CultureInfo.InvariantCulture,
                $"<path d=\"{d.ToString().Trim()}\" fill=\"none\" stroke=\"{colour}\" stroke-width=\"1.4\"/>\n");

            if (series.Count > 1)
            {
                int ly = Top + 14 + s * 16;
                sb.Append(CultureInfo.InvariantCulture,
                    $"<line x1=\"{Left + 12}\" y1=\"{ly}\" x2=\"{Left + 36}\" y2=\"{ly}\" stroke=\"{colour}\" stroke-width=\"2\"/>\n");
                sb.Append(CultureInfo.InvariantCulture,
                    $"<text x=\"{Left + 42}\" y=\"{ly + 4}\" font-size=\"11\" fill=\"#111\">{Esc(series[s].Name)}</text>\n");
            }
        }

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Build series from a recorded table: the first column is the x axis, every other
    /// numeric column becomes a trace. Non-numeric cells are skipped rather than treated
    /// as zero — a blank reading is missing data, and plotting it at zero invents a value.
    /// </summary>
    public static IReadOnlyList<Series> FromTable(IReadOnlyList<string> headers,
                                                  IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var series = new List<Series>();
        for (int c = 1; c < headers.Count; c++)
        {
            var pts = new List<(double, double)>();
            foreach (var row in rows)
            {
                if (row.Count <= c) continue;
                if (!double.TryParse(row[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)) continue;
                if (!double.TryParse(row[c], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) continue;
                pts.Add((x, y));
            }
            if (pts.Count > 0) series.Add(new Series(headers[c], pts));
        }
        return series;
    }
}
