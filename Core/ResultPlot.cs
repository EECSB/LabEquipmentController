using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LabEquipmentController;

/// <summary>One plotted point.</summary>
public readonly record struct PlotPoint(double X, double Y);

/// <param name="Name">The column this came from, for the legend.</param>
public sealed record PlotSeries(string Name, IReadOnlyList<PlotPoint> Points);

/// <summary>
/// A drawn axis: where it starts and ends, and where the labelled ticks go.
/// </summary>
/// <param name="Logarithmic">
/// When true <see cref="Min"/> and <see cref="Max"/> are still real values, not logs — the
/// caller maps through <see cref="ResultPlot.Fraction"/>, which knows the difference.
/// </param>
public sealed record PlotAxis(double Min, double Max, bool Logarithmic, IReadOnlyList<double> Ticks)
{
    public bool IsEmpty => Max <= Min;
}

/// <summary>
/// Turns a table of recorded results into something drawable.
///
/// A swept measurement produces a table, and a table of forty rows is not a frequency
/// response — the shape is the answer, and the shape is only visible as a curve. This is the
/// arithmetic behind that: reading the numbers back out of strings the instrument sent,
/// choosing axis ranges, and placing ticks a person would have chosen.
///
/// Deliberately UI-free so it can be tested. The drawing is in <c>ResultPlotPanel</c>.
/// </summary>
public static class ResultPlot
{
    /// <summary>
    /// Read a number out of whatever the instrument said.
    ///
    /// Replies are not tidy: a Rigol answers <c>1.549479e-01</c>, a Siglent might answer
    /// <c>1.5V</c>, and a value recorded straight from a FOR variable is plain digits. Some
    /// instruments append units, some prepend a '+'. Anything with a number at the front is
    /// worth plotting; anything else is a gap in the series rather than a zero, because a
    /// zero would be a data point that never happened.
    /// </summary>
    /// <summary>
    /// A clock — "20:14:03", "20:14:03.125", or "14:03" — as seconds since midnight.
    ///
    /// A timestamp is the natural thing to put in a results table and the wrong thing to hand
    /// a plotter, which needs a number. Reading it here lets one string be both: the table
    /// shows the time the reading was taken, the axis gets a value it can space points along,
    /// and the CSV carries the clock rather than an offset from something the file does not
    /// mention.
    ///
    /// <para>
    /// Seconds since midnight, so a run that crosses midnight plots its last readings to the
    /// left of its first. A bench session at exactly midnight is the one case this gets wrong,
    /// and it is cheaper to say so than to carry a date through every row to prevent it.
    /// </para>
    /// </summary>
    public static bool TryParseClock(string? text, out double secondsOfDay)
    {
        secondsOfDay = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Three fields exactly. "09:00" is nine hours to one reader and nine minutes to the
        // next, and there is no way to tell from the string which was meant — so it is not
        // read at all, rather than read one way and silently plotted the other.
        string[] parts = text.Trim().Split(':');
        if (parts.Length != 3) return false;

        double total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            // Only the last field may carry a fraction; hours and minutes are whole.
            bool last = i == parts.Length - 1;
            NumberStyles styles = last ? NumberStyles.Float : NumberStyles.Integer;
            if (!double.TryParse(parts[i], styles, CultureInfo.InvariantCulture, out double v))
                return false;

            // 61 seconds is a typo, not a time. Refusing keeps this from quietly accepting
            // things that merely contain colons. Hours are left unbounded: a run that has
            // been going 30 hours is a reasonable thing to timestamp.
            if (v < 0 || (i > 0 && v >= 60)) return false;
            total = total * 60 + v;
        }

        secondsOfDay = total;
        return true;
    }

    /// <summary>
    /// A tick label for an axis of clock values, carrying sub-second digits only when the
    /// ticks are closer together than a second — which is what a fast sample rate produces.
    /// </summary>
    public static string FormatClock(double secondsOfDay, double step)
    {
        // The axis pads its range, so a tick can sit just outside the day. Wrap it back.
        double s = secondsOfDay % 86400.0;
        if (s < 0) s += 86400.0;

        int decimals = step is <= 0 or >= 1
            ? 0
            : Math.Clamp((int)Math.Ceiling(-Math.Log10(step)), 0, 3);

        string format = decimals == 0 ? @"hh\:mm\:ss" : @"hh\:mm\:ss\." + new string('f', decimals);
        return TimeSpan.FromSeconds(s).ToString(format, CultureInfo.InvariantCulture);
    }

    public static bool TryParseValue(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim();

        // A clock is a value, just not a decimal one. Tried first: the leading-run rule below
        // would otherwise read "20:14:03" as its hour and put every reading from the same hour
        // on the same X.
        if (TryParseClock(s, out value)) return true;

        // The longest leading run that still parses. Walking down from the full string means
        // "1.5e-3V" gives up only the 'V', and "1.5e" falls back to "1.5" rather than failing.
        for (int end = s.Length; end > 0; end--)
        {
            if (double.TryParse(s[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                // A colon means the leading run was a field of something, not a value with a
                // unit after it. A well-formed clock was already taken above, so a colon here
                // means something that only looks like one — "1:2:3:4", or a field that did
                // not validate. Reading its first number would be a guess, and no point beats
                // a wrong point.
                if (end < s.Length && s[end] == ':') return false;

                // A bare engineering suffix multiplies what came before it: "100k", "2.5M".
                if (end < s.Length && Suffix(s[end]) is double factor)
                    value *= factor;

                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
        }

        return false;
    }

    /// <summary>
    /// Is there anything in this column a plot could draw?
    ///
    /// A results table mixes readings with the things that produced them. A console records
    /// the command beside each value, so a column holds <c>C1:BSWV?</c> all the way down;
    /// <see cref="Build"/> drops every one of those rows and hands back an empty series, and
    /// an empty series is a legend entry, a colour and a checked box that draw nothing. Worth
    /// knowing before offering the column rather than after.
    ///
    /// <para>
    /// One parsable value is enough. A sweep whose first reading came back "ERR" is still a
    /// sweep, and requiring all of them would drop a column for one bad point — the opposite
    /// mistake, and the more annoying one, since the reading it hides is real.
    /// </para>
    /// </summary>
    public static bool HasPlottableValues(IEnumerable<string?> values)
    {
        foreach (string? v in values)
            if (TryParseValue(v, out _)) return true;
        return false;
    }

    /// <summary>SI multipliers as an instrument or a script writes them. 'M' is mega, 'm' is milli.</summary>
    private static double? Suffix(char c) => c switch
    {
        'p' => 1e-12,
        'n' => 1e-9,
        'u' or 'µ' => 1e-6,
        'm' => 1e-3,
        'k' or 'K' => 1e3,
        'M' => 1e6,
        'G' => 1e9,
        _ => null,
    };

    /// <summary>
    /// Build one series per chosen Y column, against the chosen X column.
    ///
    /// A row is used only when both its X and that series' Y read as numbers. Rows are
    /// dropped per series rather than for the table as a whole: one instrument returning
    /// "ERR" for one reading should not delete the other instrument's curve.
    /// </summary>
    public static IReadOnlyList<PlotSeries> Build(
        IReadOnlyList<SequenceRow> rows,
        IReadOnlyList<string> columnNames,
        int xColumn,
        IEnumerable<int> yColumns)
    {
        var series = new List<PlotSeries>();
        if (rows.Count == 0 || xColumn < 0) return series;

        foreach (int y in yColumns)
        {
            if (y < 0 || y == xColumn) continue;

            var points = new List<PlotPoint>();
            foreach (SequenceRow row in rows)
            {
                if (xColumn >= row.Values.Count || y >= row.Values.Count) continue;
                if (!TryParseValue(row.Values[xColumn], out double xv)) continue;
                if (!TryParseValue(row.Values[y], out double yv)) continue;
                points.Add(new PlotPoint(xv, yv));
            }

            if (points.Count > 0)
                series.Add(new PlotSeries(NameOf(columnNames, y), points));
        }

        return series;
    }

    private static string NameOf(IReadOnlyList<string> names, int index)
        => index >= 0 && index < names.Count && names[index].Length > 0
            ? names[index]
            : $"Column {index + 1}";

    /// <summary>
    /// An axis covering <paramref name="values"/>, with ticks at round numbers.
    /// </summary>
    /// <param name="log">
    /// Log scale. Refused rather than fudged when anything is zero or negative: a log axis
    /// through zero has no meaning, and silently dropping those points would hide data.
    /// </param>
    public static PlotAxis Axis(IEnumerable<double> values, bool log)
    {
        double[] all = values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToArray();
        if (all.Length == 0) return new PlotAxis(0, 0, false, Array.Empty<double>());

        double min = all.Min(), max = all.Max();

        if (log && min > 0) return LogAxis(min, max);

        // A flat series still deserves an axis — otherwise a constant reading draws on the
        // edge of the plot or not at all.
        if (max - min < double.Epsilon)
        {
            double pad = Math.Abs(max) > 1e-12 ? Math.Abs(max) * 0.1 : 1;
            min -= pad;
            max += pad;
        }
        else
        {
            double pad = (max - min) * 0.06;
            min -= pad;
            max += pad;
        }

        return new PlotAxis(min, max, false, LinearTicks(min, max));
    }

    /// <summary>Can these values go on a log axis at all?</summary>
    public static bool CanBeLogarithmic(IEnumerable<double> values)
        => values.Any() && values.All(v => v > 0 && !double.IsNaN(v) && !double.IsInfinity(v));

    private static PlotAxis LogAxis(double min, double max)
    {
        double lo = Math.Pow(10, Math.Floor(Math.Log10(min)));
        double hi = Math.Pow(10, Math.Ceiling(Math.Log10(max)));
        if (hi <= lo) hi = lo * 10;

        // A tick per decade. Any more and a five-decade sweep is unreadable.
        var ticks = new List<double>();
        for (double t = lo; t <= hi * 1.0000001; t *= 10) ticks.Add(t);

        return new PlotAxis(lo, hi, true, ticks);
    }

    /// <summary>
    /// Ticks at 1, 2 or 5 times a power of ten — the steps a person picks when drawing an
    /// axis by hand, and the reason a grid reads as values rather than as decoration.
    /// </summary>
    public static IReadOnlyList<double> LinearTicks(double min, double max, int target = 6)
    {
        var ticks = new List<double>();
        double span = max - min;
        if (span <= 0 || double.IsNaN(span) || double.IsInfinity(span)) return ticks;

        double rough = span / target;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double normalised = rough / magnitude;

        double step = normalised switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 5,
            _ => 10,
        } * magnitude;

        for (double t = Math.Ceiling(min / step) * step; t <= max + step * 1e-9; t += step)
            ticks.Add(Math.Abs(t) < step * 1e-9 ? 0 : t);   // kill -0 and float dust at zero

        return ticks;
    }

    /// <summary>
    /// Where a value sits along an axis, 0 at the minimum and 1 at the maximum. Log axes
    /// interpolate in the exponent, which is the whole reason to have one.
    /// </summary>
    public static double Fraction(PlotAxis axis, double value)
    {
        if (axis.IsEmpty) return 0;

        if (!axis.Logarithmic) return (value - axis.Min) / (axis.Max - axis.Min);
        if (value <= 0) return 0;

        double lo = Math.Log10(axis.Min), hi = Math.Log10(axis.Max);
        return hi <= lo ? 0 : (Math.Log10(value) - lo) / (hi - lo);
    }

    /// <summary>
    /// A tick label short enough to fit under an axis. Engineering notation where it helps —
    /// "34.8 M" reads as a frequency in a way "34800000" does not.
    /// </summary>
    public static string Format(double value)
    {
        if (value == 0) return "0";

        double abs = Math.Abs(value);
        (double scale, string unit) = abs switch
        {
            >= 1e9 => (1e9, "G"),
            >= 1e6 => (1e6, "M"),
            >= 1e3 => (1e3, "k"),
            >= 1 => (1.0, ""),
            >= 1e-3 => (1e-3, "m"),
            >= 1e-6 => (1e-6, "µ"),
            >= 1e-9 => (1e-9, "n"),
            _ => (1e-12, "p"),
        };

        double scaled = value / scale;
        string text = Math.Abs(scaled) >= 100 ? scaled.ToString("0", CultureInfo.InvariantCulture)
                    : Math.Abs(scaled) >= 10 ? scaled.ToString("0.#", CultureInfo.InvariantCulture)
                    : scaled.ToString("0.##", CultureInfo.InvariantCulture);

        return unit.Length == 0 ? text : text + " " + unit;
    }

    /// <summary>
    /// A tick label carrying enough decimals to tell it from the tick next to it.
    ///
    /// <see cref="Format(double)"/> rounds to a couple of significant figures, which is right
    /// for a value on its own and wrong for an axis: readings that differ in the fourth
    /// decimal all round to the same text, and the axis becomes a column of identical labels
    /// beside a curve that is plainly going somewhere. The spacing between ticks is what says
    /// how much precision the reader needs, so it is what decides.
    /// </summary>
    public static string Format(double value, double step)
    {
        if (step <= 0 || double.IsNaN(step) || double.IsInfinity(step)) return Format(value);
        if (value == 0) return "0";

        double abs = Math.Abs(value);
        (double scale, string unit) = abs switch
        {
            >= 1e9 => (1e9, "G"),
            >= 1e6 => (1e6, "M"),
            >= 1e3 => (1e3, "k"),
            >= 1 => (1.0, ""),
            >= 1e-3 => (1e-3, "m"),
            >= 1e-6 => (1e-6, "µ"),
            >= 1e-9 => (1e-9, "n"),
            _ => (1e-12, "p"),
        };

        // Six is the ceiling: past that the label is wider than the space between the ticks
        // it is trying to distinguish, and an unreadable axis is no better than a repetitive
        // one.
        int decimals = Math.Clamp((int)Math.Ceiling(-Math.Log10(step / scale)), 0, 6);
        string text = (value / scale).ToString("F" + decimals, CultureInfo.InvariantCulture);

        return unit.Length == 0 ? text : text + " " + unit;
    }
}
