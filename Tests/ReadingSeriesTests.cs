using System.Globalization;
using System.Linq;
using System.Threading;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

public class ReadingSeriesTests
{
    [Fact]
    public void Collects_readings_in_order()
    {
        var s = new ReadingSeries();
        s.Add(0.0, 1.5);
        s.Add(1.0, 2.5);

        Assert.Equal(2, s.Count);
        Assert.Equal(new[] { 1.5, 2.5 }, s.Items.Select(r => r.Value));
        Assert.Equal(2.5, s.Latest);
    }

    [Fact]
    public void Drops_the_oldest_once_full()
    {
        // Bounded on purpose: a meter polled all afternoon must not grow without limit.
        var s = new ReadingSeries(capacity: 3);
        for (int i = 0; i < 5; i++) s.Add(i, i);

        Assert.Equal(3, s.Count);
        Assert.Equal(new double[] { 2, 3, 4 }, s.Items.Select(r => r.Value));
        Assert.Equal(5, s.TotalTaken);   // the count of everything taken still tells the truth
    }

    [Fact]
    public void Statistics_cover_the_window()
    {
        var s = new ReadingSeries();
        foreach (double v in new[] { 2.0, 4.0, 6.0 }) s.Add(0, v);

        (double min, double max, double mean) = s.Statistics();
        Assert.Equal(2.0, min);
        Assert.Equal(6.0, max);
        Assert.Equal(4.0, mean);
    }

    [Fact]
    public void An_empty_series_reports_nothing_rather_than_throwing()
    {
        var s = new ReadingSeries();
        Assert.True(s.IsEmpty);
        Assert.Null(s.Latest);
        Assert.Equal((0.0, 0.0, 0.0), s.Statistics());
    }

    [Fact]
    public void Refuses_values_that_are_not_numbers()
    {
        var s = new ReadingSeries();
        s.Add(0, double.NaN);
        s.Add(1, double.PositiveInfinity);

        Assert.True(s.IsEmpty);
    }

    [Fact]
    public void Clear_resets_the_run()
    {
        var s = new ReadingSeries();
        s.Add(0, 1);
        s.Clear();

        Assert.True(s.IsEmpty);
        Assert.Equal(0, s.TotalTaken);
    }

    [Fact]
    public void Csv_has_a_header_and_invariant_numbers()
    {
        var s = new ReadingSeries();
        s.Add(0.5, 1.25);

        string csv = s.ToCsv("DC volts (V)");
        Assert.StartsWith("Time (s),DC volts (V)\r\n", csv);
        Assert.Contains("0.5,1.25", csv);
    }

    // ------------------------------------------------------------------- parsing

    [Theory]
    [InlineData("+1.234560E-01", 0.123456)]
    [InlineData("-4.5E+00", -4.5)]
    [InlineData("1.25\r\n", 1.25)]
    [InlineData("  2.5  ", 2.5)]
    [InlineData("3.5,VDC", 3.5)]          // some meters return several fields
    [InlineData("1.23 VDC", 1.23)]        // ... or tack the unit on
    public void Parses_the_shapes_meters_actually_return(string reply, double expected)
    {
        Assert.True(ReadingSeries.TryParseReading(reply, out double v));
        Assert.Equal(expected, v, 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("no numbers here")]
    public void Rejects_replies_with_no_number(string? reply)
    {
        Assert.False(ReadingSeries.TryParseReading(reply, out _));
    }

    [Fact]
    public void Parses_invariantly_regardless_of_the_machine_culture()
    {
        // On a machine using ',' as its decimal separator, culture-sensitive parsing would
        // silently read "1.25" as 125 — every logged value wrong by a factor of a hundred.
        CultureInfo original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("sl-SI");   // this bench's locale
            Assert.True(ReadingSeries.TryParseReading("1.25", out double v));
            Assert.Equal(1.25, v, 9);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}

public class ReadoutFunctionTests
{
    [Fact]
    public void A_multimeter_offers_functions_to_poll()
    {
        InstrumentProfile dmm = InstrumentProfile.ForIdentity("Siglent Technologies,SDM3065X,x,y");

        Assert.True(dmm.SupportsLiveReadout);
        Assert.Contains(dmm.ReadoutFunctions, f => f.Query == "MEASure:VOLTage:DC?" && f.Unit == "V");
    }

    [Theory]
    [InlineData("RIGOL TECHNOLOGIES,DS2202,x,y")]          // a scope plots against time itself
    [InlineData("Siglent Technologies,SDG2042X,x,y")]      // a generator has nothing to read
    [InlineData(null)]
    public void Everything_else_offers_none(string? identity)
    {
        Assert.False(InstrumentProfile.ForIdentity(identity).SupportsLiveReadout);
    }

    [Fact]
    public void Every_readout_query_is_a_query()
    {
        // A command with no '?' would hang the poll loop waiting for a reply.
        InstrumentProfile dmm = InstrumentProfile.ForIdentity("Siglent Technologies,SDM3065X,x,y");
        Assert.All(dmm.ReadoutFunctions, f => Assert.True(ScpiClient.IsQuery(f.Query)));
    }
}
