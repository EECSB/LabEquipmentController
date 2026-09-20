using System.Threading.Tasks;

namespace LabEquipmentController.Tests;

/// <summary>
/// Which commands are given longer, and that the link is left as it was found.
///
/// The case behind it: an SDM3065X takes longer than the app's three-second default to change
/// function and measure, so pressing the meter's own Resistance button reported a working
/// instrument as one that had stopped answering.
/// </summary>
public class QueryTimeTests
{
    [Theory]
    [InlineData("MEASure:VOLTage:DC?")]
    [InlineData("MEAS:RES?")]
    [InlineData("measure:capacitance?")]
    [InlineData(":MEASure:VPP? CHANnel1")]
    [InlineData("READ?")]
    [InlineData("*TST?")]
    public void A_command_that_takes_a_reading_is_recognised(string command)
    {
        Assert.True(QueryTime.TakesAReading(command));
    }

    [Theory]
    [InlineData("*IDN?")]
    [InlineData(":TIMebase:MAIN:SCALe?")]
    [InlineData("FETCh?")]                       // the reading already taken — no wait to allow for
    [InlineData("C1:BSWV?")]
    [InlineData(":RUN")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_is_left_on_the_user_s_own_figure(string? command)
    {
        Assert.False(QueryTime.TakesAReading(command));
        Assert.Equal(2000, QueryTime.Allow(command, 2000));
    }

    [Fact]
    public void A_reading_gets_the_floor_when_the_user_s_timeout_is_under_it()
    {
        Assert.Equal(QueryTime.ReadingFloorMs, QueryTime.Allow("MEASure:RESistance?", 3000));
    }

    [Fact]
    public void A_longer_timeout_than_the_floor_is_kept()
    {
        Assert.Equal(30000, QueryTime.Allow("MEASure:RESistance?", 30000));
    }

    [Fact]
    public async Task Asking_raises_the_link_for_the_call_and_puts_it_back()
    {
        int duringTheCall = 0;
        FakeInstrumentClient? client = null;
        client = new FakeInstrumentClient { TimeoutMs = 3000, Watching = _ => duringTheCall = client!.TimeoutMs };

        await client.AskAsync("MEASure:VOLTage:DC?");

        Assert.Equal(QueryTime.ReadingFloorMs, duringTheCall);
        Assert.Equal(3000, client.TimeoutMs);
    }

    [Fact]
    public async Task A_plain_question_is_asked_on_the_timeout_as_it_stands()
    {
        int duringTheCall = 0;
        FakeInstrumentClient? client = null;
        client = new FakeInstrumentClient { TimeoutMs = 3000, Watching = _ => duringTheCall = client!.TimeoutMs };

        await client.AskAsync("*IDN?");

        Assert.Equal(3000, duringTheCall);
        Assert.Equal(3000, client.TimeoutMs);
    }

    [Fact]
    public async Task A_reading_that_fails_still_puts_the_timeout_back()
    {
        var client = new FakeInstrumentClient { TimeoutMs = 3000, ThrowOn = "MEASure:RESistance?" };

        await Assert.ThrowsAnyAsync<System.Exception>(() => client.AskAsync("MEASure:RESistance?"));

        Assert.Equal(3000, client.TimeoutMs);
    }
}
