using LabEquipmentController.Web.Client.Contracts;

namespace LabEquipmentController.Web.Bench;

/// <summary>
/// The curve the browser draws, worked out here.
///
/// The arithmetic is Core's <see cref="ResultPlot"/> — the same code the desktop's plot uses —
/// because reading a number out of whatever the instrument said, choosing an axis range and
/// placing ticks a person would have chosen is not something to write twice and have drift.
/// The browser cannot call Core directly: the library carries thirty-six catalogs and
/// twenty-four thousand commands, and none of that belongs in a download whose purpose is to
/// draw a line.
///
/// So the page sends its table and its choices, and gets back geometry: every point and every
/// tick already expressed as a fraction of its axis, 0 at the origin and 1 at the far end.
/// Mapping a value onto an axis is the one part that differs between a linear and a log scale,
/// and it stays on this side of the wire.
/// </summary>
public static class PlotService
{
    /// <summary>The desktop's six series colours, in its order.</summary>
    private static readonly string[] Palette =
        ["#005ac8", "#c85a00", "#008c5a", "#9628a0", "#b41e3c", "#5a5a5a"];

    public static PlotReply Build(PlotRequest req)
    {
        var rows = req.Rows.Select(r => new SequenceRow(r.Values)).ToList();

        var plottable = new List<int>();
        for (int i = 0; i < req.Columns.Count; i++)
            if (rows.Count == 0 || HasValues(rows, i))
                plottable.Add(i);

        // Null means "choose for me": the first column across, everything else that has
        // anything to draw up. Columns are known before the first row is, and a column with
        // nothing in it yet is empty rather than textual, so before any rows arrive
        // everything is fair game — which is what `plottable` says in that case.
        IReadOnlyList<int> wanted = req.YColumns
            ?? plottable.Where(i => i != req.XColumn).ToList();

        var series = ResultPlot.Build(rows, req.Columns, req.XColumn, wanted);

        bool canLogX = series.Count > 0
            && ResultPlot.CanBeLogarithmic(series.SelectMany(s => s.Points.Select(p => p.X)));
        bool canLogY = series.Count > 0
            && ResultPlot.CanBeLogarithmic(series.SelectMany(s => s.Points.Select(p => p.Y)));

        // Asked for and refused is not an error — a run whose readings pass through zero
        // simply has no log axis, and the switch goes dead rather than flattening the curve
        // onto one edge.
        var x = ResultPlot.Axis(series.SelectMany(s => s.Points.Select(p => p.X)), req.LogX && canLogX);
        var y = ResultPlot.Axis(series.SelectMany(s => s.Points.Select(p => p.Y)), req.LogY && canLogY);

        var drawn = series
            .Select((s, i) => new PlotSeriesDto(
                s.Name,
                Palette[i % Palette.Length],
                s.Points
                    .Select(p => new PlotPointDto(ResultPlot.Fraction(x, p.X), ResultPlot.Fraction(y, p.Y)))
                    .ToList()))
            .ToList();

        string? unit = MeasurementUnit.Guess(
            wanted.Count > 0 && wanted[0] < req.Columns.Count ? req.Columns[wanted[0]] : null,
            CommandValues(req.Columns, rows));

        return new PlotReply(
            drawn,
            // Labelled as a clock when that is what the column holds. Decided from the raw
            // strings, because by the time they are numbers a timestamp and a count of
            // seconds look identical.
            Axis(x, IsClock(rows, req.XColumn)),
            Axis(y, clock: false),
            canLogX, canLogY,
            wanted.ToList(),
            plottable,
            req.XColumn >= 0 && req.XColumn < req.Columns.Count ? req.Columns[req.XColumn] : "",
            unit ?? "");
    }

    /// <summary>An axis as the page needs it: ticks placed along it, and already labelled.</summary>
    private static PlotAxisDto Axis(PlotAxis axis, bool clock)
    {
        // How far apart the ticks are is what decides how many decimals their labels need. A
        // log axis steps by decades, so its own rounding already distinguishes them.
        double step = axis.Logarithmic || axis.Ticks.Count < 2
            ? 0
            : Math.Abs(axis.Ticks[1] - axis.Ticks[0]);

        var ticks = axis.Ticks
            .Select(t => (Value: t, At: ResultPlot.Fraction(axis, t)))
            .Where(t => t.At is >= 0 and <= 1)
            .Select(t => new PlotTickDto(
                t.At,
                clock ? ResultPlot.FormatClock(t.Value, step) : ResultPlot.Format(t.Value, step)))
            .ToList();

        return new PlotAxisDto(axis.Min, axis.Max, axis.Logarithmic, ticks);
    }

    private static bool HasValues(IReadOnlyList<SequenceRow> rows, int column)
        => ResultPlot.HasPlottableValues(
            rows.Where(r => column < r.Values.Count).Select(r => (string?)r.Values[column]));

    /// <summary>
    /// Whether every value in a column that has one is a clock. All of them, not most: one
    /// plain number among the timestamps means the column is something else, and labelling
    /// the axis as a time would be a confident lie about what it shows.
    /// </summary>
    private static bool IsClock(IReadOnlyList<SequenceRow> rows, int column)
    {
        if (column < 0) return false;

        bool any = false;
        foreach (SequenceRow row in rows)
        {
            if (column >= row.Values.Count) continue;
            string v = row.Values[column];
            if (string.IsNullOrWhiteSpace(v)) continue;
            if (!ResultPlot.TryParseClock(v, out _)) return false;
            any = true;
        }
        return any;
    }

    /// <summary>
    /// The values of a column named "Command", if the table has one — what a console records
    /// beside each reading, and the best clue to what the reading is of.
    /// </summary>
    private static IEnumerable<string>? CommandValues(IReadOnlyList<string> columns, IReadOnlyList<SequenceRow> rows)
    {
        int column = -1;
        for (int i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i], "Command", StringComparison.OrdinalIgnoreCase))
            {
                column = i;
                break;
            }

        if (column < 0) return null;
        return rows.Where(r => column < r.Values.Count).Select(r => r.Values[column]).ToList();
    }
}
