using System;
using System.Collections.Generic;
using System.Linq;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The arithmetic behind the results plot.
///
/// Worth testing because a wrong curve is worse than no curve: it is read as a measurement.
/// The two places it can quietly lie are reading the numbers back out of whatever the
/// instrument said, and the log axis — which is the one anybody plotting a frequency
/// response will turn on.
/// </summary>
public class ResultPlotTests
{
    private static SequenceRow Row(params string[] values) => new(values);

    private static double Parse(string text)
    {
        Assert.True(ResultPlot.TryParseValue(text, out double v), $"\"{text}\" should parse");
        return v;
    }

    // ---------------------------------------------------------------------- parsing

    [Theory]
    [InlineData("1.549479e-01", 0.1549479)]
    [InlineData("+1.23E-3", 0.00123)]
    [InlineData("34700000", 34700000)]
    [InlineData("-2.5", -2.5)]
    [InlineData("  0.5  ", 0.5)]
    public void A_reply_that_is_just_a_number_reads_as_that_number(string text, double expected)
        => Assert.Equal(expected, Parse(text), 9);

    /// <summary>Instruments append units, and a unit is not a reason to lose the reading.</summary>
    [Theory]
    [InlineData("1.5V", 1.5)]
    [InlineData("2.5 Vrms", 2.5)]
    [InlineData("100Hz", 100)]
    public void A_unit_after_the_number_is_ignored(string text, double expected)
        => Assert.Equal(expected, Parse(text), 9);

    /// <summary>
    /// A bare engineering suffix multiplies. "100k" recorded from a FOR variable is 100000,
    /// and reading it as 100 would put the point four decades from where it belongs.
    /// </summary>
    [Theory]
    [InlineData("100k", 100_000)]
    [InlineData("2.5M", 2_500_000)]
    [InlineData("470p", 470e-12)]
    [InlineData("100m", 0.1)]
    public void An_engineering_suffix_scales_the_number(string text, double expected)
        => Assert.Equal(expected, Parse(text), 15);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("ERR")]
    [InlineData("no response")]
    public void Anything_without_a_number_is_not_a_point(string? text)
        => Assert.False(ResultPlot.TryParseValue(text, out _));

    /// <summary>
    /// The leading-run rule is what lets "1.5e-3V" give up only its unit, but applied to a
    /// clock it reads the hour and calls it the value. Eight readings taken in the same hour
    /// then share an X and the plot draws a vertical line instead of a trend.
    /// </summary>
    // ------------------------------------------------------------------- tick labels

    /// <summary>
    /// Readings that differ in the fourth decimal used to round to the same label, so the
    /// axis beside a plainly rising curve read as a column of identical numbers. The gap
    /// between ticks is what says how much precision the reader needs.
    /// </summary>
    [Fact]
    public void Adjacent_ticks_get_labels_that_differ()
    {
        const double step = 0.0001;
        Assert.Equal("1.2345", ResultPlot.Format(1.2345, step));
        Assert.Equal("1.2346", ResultPlot.Format(1.2346, step));
        Assert.NotEqual(ResultPlot.Format(1.2345, step), ResultPlot.Format(1.2346, step));
    }

    [Theory]
    [InlineData(20, 5, "20")]            // whole numbers stay whole
    [InlineData(20.5, 0.5, "20.5")]
    [InlineData(850e-6, 50e-6, "850 µ")] // the engineering unit still carries the magnitude
    [InlineData(1.5e3, 500, "1.5 k")]
    public void A_tick_label_carries_the_precision_its_step_needs(
        double value, double step, string expected)
        => Assert.Equal(expected, ResultPlot.Format(value, step));

    [Fact]
    public void Without_a_usable_step_the_plain_format_is_used()
    {
        Assert.Equal(ResultPlot.Format(0.155), ResultPlot.Format(0.155, 0));
        Assert.Equal(ResultPlot.Format(0.155), ResultPlot.Format(0.155, double.NaN));
    }

    // -------------------------------------------------------------------- clock values

    /// <summary>
    /// A timestamp reads as seconds since midnight, so one string can be the time shown in
    /// the table and a position along the axis. Before this it fell to the leading-run rule —
    /// the rule that lets "1.5e-3V" give up its unit — which took the hour and called it the
    /// value, stacking every reading from that hour on a single X.
    /// </summary>
    [Theory]
    [InlineData("20:14:03", 72843)]
    [InlineData("20:14:03.500", 72843.5)]
    [InlineData("00:00:00", 0)]
    [InlineData("1:2:3", 3723)]
    [InlineData("30:00:00", 108000)]     // a long run is still a time
    public void A_clock_reads_as_seconds_since_midnight(string text, double expected)
    {
        Assert.True(ResultPlot.TryParseClock(text, out double seconds));
        Assert.Equal(expected, seconds, 6);
        Assert.Equal(expected, Parse(text), 6);   // and through the general parser too
    }

    [Theory]
    [InlineData("09:00")]      // two fields: nine hours, or nine minutes? unanswerable
    [InlineData("1:2:3:4")]
    [InlineData("20:14:99")]   // 99 seconds is a typo, not a time
    [InlineData("20:99:03")]
    [InlineData("ab:cd:ef")]
    public void An_ambiguous_or_malformed_clock_is_not_read_at_all(string text)
    {
        Assert.False(ResultPlot.TryParseClock(text, out _));
        Assert.False(ResultPlot.TryParseValue(text, out _));
    }

    [Theory]
    [InlineData(72843, 1, "20:14:03")]
    [InlineData(72843.5, 0.5, "20:14:03.5")]
    [InlineData(72843.125, 0.01, "20:14:03.12")]
    [InlineData(72843, 60, "20:14:03")]        // a coarse axis drops the sub-second digits
    public void A_clock_tick_shows_sub_seconds_only_when_the_ticks_are_that_close(
        double seconds, double step, string expected)
        => Assert.Equal(expected, ResultPlot.FormatClock(seconds, step));

    [Theory]
    [InlineData("1.5e-3V", 1.5e-3)]
    [InlineData("100k", 100e3)]
    [InlineData("-2.75 V", -2.75)]
    public void A_value_with_a_unit_after_it_still_parses(string text, double expected)
        => Assert.Equal(expected, Parse(text), 12);

    // ------------------------------------------------------------------------ series

    [Fact]
    public void Each_ticked_column_becomes_its_own_curve()
    {
        var rows = new[] { Row("100", "1.0", "2.0"), Row("200", "1.5", "2.5") };
        var names = new[] { "Frequency", "A", "B" };

        IReadOnlyList<PlotSeries> series = ResultPlot.Build(rows, names, 0, new[] { 1, 2 });

        Assert.Equal(2, series.Count);
        Assert.Equal("A", series[0].Name);
        Assert.Equal(new[] { 100.0, 200.0 }, series[0].Points.Select(p => p.X));
        Assert.Equal(new[] { 2.0, 2.5 }, series[1].Points.Select(p => p.Y));
    }

    /// <summary>
    /// A reading that failed drops that point from that curve only. Plotting it as zero would
    /// invent a measurement; dropping the whole row would delete the other instrument's.
    /// </summary>
    [Fact]
    public void A_bad_reading_drops_its_own_point_and_nothing_else()
    {
        var rows = new[] { Row("100", "1.0", "2.0"), Row("200", "ERR", "2.5") };
        var names = new[] { "Frequency", "A", "B" };

        IReadOnlyList<PlotSeries> series = ResultPlot.Build(rows, names, 0, new[] { 1, 2 });

        Assert.Single(series[0].Points);              // A lost its second point
        Assert.Equal(2, series[1].Points.Count);      // B kept both
    }

    [Fact]
    public void The_x_column_is_never_also_drawn_as_a_curve()
        => Assert.Empty(ResultPlot.Build(new[] { Row("1", "2") },
                                         new[] { "X", "Y" }, 0, new[] { 0 }));

    [Fact]
    public void A_column_a_row_never_reached_does_not_throw()
    {
        var rows = new[] { Row("100"), Row("200", "1.5") };
        IReadOnlyList<PlotSeries> series = ResultPlot.Build(rows, new[] { "X", "Y" }, 0, new[] { 1 });

        Assert.Single(series);
        Assert.Single(series[0].Points);
    }

    // -------------------------------------------------------------------------- axes

    [Fact]
    public void A_linear_axis_covers_the_data_with_a_little_room()
    {
        PlotAxis axis = ResultPlot.Axis(new[] { 0.0, 10.0 }, log: false);

        Assert.True(axis.Min < 0 && axis.Max > 10);
        Assert.False(axis.Logarithmic);
        Assert.NotEmpty(axis.Ticks);
    }

    /// <summary>A constant reading is a legitimate result and still needs an axis to sit on.</summary>
    [Fact]
    public void A_flat_series_still_gets_an_axis_with_height()
    {
        PlotAxis axis = ResultPlot.Axis(new[] { 5.0, 5.0, 5.0 }, log: false);

        Assert.True(axis.Max > axis.Min);
        Assert.True(axis.Min < 5 && axis.Max > 5);
    }

    [Fact]
    public void A_log_axis_snaps_to_whole_decades_and_ticks_on_them()
    {
        PlotAxis axis = ResultPlot.Axis(new[] { 100.0, 100_000.0 }, log: true);

        Assert.True(axis.Logarithmic);
        Assert.Equal(100, axis.Min, 6);
        Assert.Equal(100_000, axis.Max, 6);
        Assert.Equal(new[] { 100.0, 1000.0, 10_000.0, 100_000.0 }, axis.Ticks.Select(t => Math.Round(t, 6)));
    }

    /// <summary>
    /// Asking for log on data that touches zero has no answer, so it gives a linear axis
    /// rather than a wrong one. The UI disables the box for the same reason.
    /// </summary>
    [Fact]
    public void Log_is_refused_when_the_data_touches_zero_or_goes_negative()
    {
        Assert.False(ResultPlot.Axis(new[] { 0.0, 10.0 }, log: true).Logarithmic);
        Assert.False(ResultPlot.Axis(new[] { -1.0, 10.0 }, log: true).Logarithmic);

        Assert.False(ResultPlot.CanBeLogarithmic(new[] { 0.0, 1.0 }));
        Assert.False(ResultPlot.CanBeLogarithmic(new[] { -1.0, 1.0 }));
        Assert.True(ResultPlot.CanBeLogarithmic(new[] { 0.5, 1000.0 }));
    }

    [Fact]
    public void An_axis_over_nothing_is_empty_rather_than_a_crash()
        => Assert.True(ResultPlot.Axis(Array.Empty<double>(), log: false).IsEmpty);

    /// <summary>Ticks land on 1, 2 or 5 times a power of ten — what a person would draw.</summary>
    [Theory]
    [InlineData(0, 10)]
    [InlineData(0, 1)]
    [InlineData(-5, 5)]
    [InlineData(0, 34_800_000)]
    public void Linear_ticks_are_round_numbers(double min, double max)
    {
        IReadOnlyList<double> ticks = ResultPlot.LinearTicks(min, max);

        Assert.NotEmpty(ticks);
        Assert.All(ticks, t => Assert.InRange(t, min, max));

        double step = ticks.Count > 1 ? ticks[1] - ticks[0] : 1;
        double mantissa = step / Math.Pow(10, Math.Floor(Math.Log10(step)));
        Assert.Contains(Math.Round(mantissa, 6), new[] { 1.0, 2.0, 5.0 });
    }

    // -------------------------------------------------------------------- positioning

    [Fact]
    public void A_value_maps_to_where_it_belongs_along_a_linear_axis()
    {
        var axis = new PlotAxis(0, 10, false, Array.Empty<double>());

        Assert.Equal(0.0, ResultPlot.Fraction(axis, 0), 9);
        Assert.Equal(0.5, ResultPlot.Fraction(axis, 5), 9);
        Assert.Equal(1.0, ResultPlot.Fraction(axis, 10), 9);
    }

    /// <summary>
    /// The whole point of a log axis: a decade takes the same width wherever it is, so 1 kHz
    /// sits halfway between 100 Hz and 100 kHz instead of at one percent.
    /// </summary>
    [Fact]
    public void A_log_axis_gives_every_decade_the_same_width()
    {
        var axis = new PlotAxis(100, 100_000, true, Array.Empty<double>());

        Assert.Equal(0.0, ResultPlot.Fraction(axis, 100), 9);
        Assert.Equal(1.0 / 3, ResultPlot.Fraction(axis, 1_000), 9);
        Assert.Equal(2.0 / 3, ResultPlot.Fraction(axis, 10_000), 9);
        Assert.Equal(1.0, ResultPlot.Fraction(axis, 100_000), 9);
    }

    [Fact]
    public void An_empty_axis_puts_everything_at_the_origin_rather_than_dividing_by_zero()
        => Assert.Equal(0.0, ResultPlot.Fraction(new PlotAxis(0, 0, false, Array.Empty<double>()), 5));

    // --------------------------------------------------------------------- formatting

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1000, "1 k")]
    [InlineData(34_800_000, "34.8 M")]
    [InlineData(0.155, "155 m")]
    [InlineData(1.5, "1.5")]
    [InlineData(470e-12, "470 p")]
    public void Tick_labels_are_short_enough_to_fit_under_an_axis(double value, string expected)
        => Assert.Equal(expected, ResultPlot.Format(value));

    // ------------------------------------------------------- which columns are worth offering

    /// <summary>
    /// The case this exists for: a console records the command beside each reading, so one
    /// column of the table is SCPI all the way down. Offered as a Y series it drew nothing
    /// and still took a legend entry and a colour.
    /// </summary>
    [Fact]
    public void A_column_of_commands_has_nothing_to_plot()
        => Assert.False(ResultPlot.HasPlottableValues(
            new[] { "C1:BSWV?", "C1:OUTP?", ":MEASure:VOLTage:DC?" }));

    [Fact]
    public void A_column_of_readings_does()
        => Assert.True(ResultPlot.HasPlottableValues(new[] { "1.549479e-01", "0.15", "1.5V" }));

    /// <summary>
    /// One good reading is enough. A sweep whose first point came back "ERR" is still a
    /// sweep, and dropping the column would hide the readings that did arrive.
    /// </summary>
    [Fact]
    public void One_reading_among_the_failures_is_enough_to_offer_the_column()
        => Assert.True(ResultPlot.HasPlottableValues(new[] { "ERR", "overload", "2.5" }));

    /// <summary>No rows yet — the caller offers every column until there is something to judge.</summary>
    [Fact]
    public void No_rows_means_nothing_to_plot()
        => Assert.False(ResultPlot.HasPlottableValues(Array.Empty<string?>()));

    /// <summary>A column that never filled in.</summary>
    [Fact]
    public void A_column_of_blanks_has_nothing_to_plot()
        => Assert.False(ResultPlot.HasPlottableValues(new string?[] { null, "", "   " }));

    /// <summary>Timestamps plot: they are the natural X for a logged run.</summary>
    [Fact]
    public void A_column_of_clocks_can_be_plotted()
        => Assert.True(ResultPlot.HasPlottableValues(new[] { "20:14:03", "20:14:04.125" }));
}
