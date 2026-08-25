using System.IO.Ports;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The line settings of a serial connection, and the fact that they have to be typed.
///
/// This is the part of serial that a subnet sweep has no equivalent of: an instrument at
/// the wrong baud rate does not fail to answer, it answers with rubbish, so every one of
/// these has to be right before the first character gets through. They travel in the
/// address — <c>serial://COM3?baud=115200</c> — which is why the parsing below is worth as
/// much attention as the address parser itself.
/// </summary>
public class SerialSettingsTests
{
    private static SerialSettings Parse(string query)
    {
        Assert.True(SerialSettings.TryParse(query, out var s, out string error), error);
        return s;
    }

    /// <summary>What almost every instrument's RS-232 page prints beside the connector.</summary>
    [Fact]
    public void Nothing_said_is_9600_8_N_1()
    {
        var s = Parse("");

        Assert.Equal(9600, s.BaudRate);
        Assert.Equal(8, s.DataBits);
        Assert.Equal(Parity.None, s.Parity);
        Assert.Equal(StopBits.One, s.StopBits);
        Assert.Equal(Handshake.None, s.Handshake);
        Assert.Equal(ScpiTerminator.Lf, s.Terminator);
        Assert.Equal("9600-8-N-1", s.ToString());
    }

    [Fact]
    public void Every_setting_can_be_named()
    {
        var s = Parse("baud=115200&databits=7&parity=even&stopbits=2&flow=rtscts&term=crlf");

        Assert.Equal(115200, s.BaudRate);
        Assert.Equal(7, s.DataBits);
        Assert.Equal(Parity.Even, s.Parity);
        Assert.Equal(StopBits.Two, s.StopBits);
        Assert.Equal(Handshake.RequestToSend, s.Handshake);
        Assert.Equal(ScpiTerminator.CrLf, s.Terminator);
    }

    /// <summary>Baud is the one people actually change, so it can be written on its own.</summary>
    [Theory]
    [InlineData("115200")]
    [InlineData("baud=115200")]
    [InlineData("baudrate=115200")]
    public void A_bare_number_is_the_baud_rate(string query)
        => Assert.Equal(115200, Parse(query).BaudRate);

    [Theory]
    [InlineData("parity=n", Parity.None)]
    [InlineData("parity=O", Parity.Odd)]
    [InlineData("parity=EVEN", Parity.Even)]
    [InlineData("parity=mark", Parity.Mark)]
    public void Parity_is_taken_long_or_short_and_in_any_case(string query, Parity expected)
        => Assert.Equal(expected, Parse(query).Parity);

    [Theory]
    [InlineData("stopbits=1", StopBits.One)]
    [InlineData("stopbits=1.5", StopBits.OnePointFive)]
    [InlineData("stopbits=2", StopBits.Two)]
    public void Stop_bits_include_the_one_and_a_half(string query, StopBits expected)
        => Assert.Equal(expected, Parse(query).StopBits);

    [Theory]
    [InlineData("flow=none", Handshake.None)]
    [InlineData("flow=xonxoff", Handshake.XOnXOff)]
    [InlineData("flow=hardware", Handshake.RequestToSend)]
    [InlineData("flow=both", Handshake.RequestToSendXOnXOff)]
    public void Flow_control_is_named_by_what_it_does(string query, Handshake expected)
        => Assert.Equal(expected, Parse(query).Handshake);

    /// <summary>
    /// The terminator is the one setting with no LAN counterpart, and getting it wrong looks
    /// exactly like a dead port: the instrument is listening and simply never sees a command.
    /// </summary>
    [Theory]
    [InlineData("term=lf", "\n")]
    [InlineData("term=crlf", "\r\n")]
    [InlineData("term=cr", "\r")]
    public void The_terminator_decides_what_ends_a_command(string query, string expected)
        => Assert.Equal(expected, Parse(query).TerminatorText);

    /// <summary>
    /// A wrong setting is refused rather than quietly defaulted. Defaulting would connect at
    /// 9600 to an instrument set to 115200, which is the failure that wastes an afternoon.
    /// </summary>
    [Theory]
    [InlineData("baud=fast")]
    [InlineData("baud=0")]
    [InlineData("databits=9")]
    [InlineData("databits=4")]
    [InlineData("parity=maybe")]
    [InlineData("stopbits=3")]
    [InlineData("flow=sometimes")]
    [InlineData("term=nul")]
    [InlineData("colour=blue")]
    public void A_setting_that_cannot_be_read_is_refused_with_a_reason(string query)
    {
        Assert.False(SerialSettings.TryParse(query, out _, out string error));
        Assert.NotEmpty(error);
    }

    /// <summary>
    /// Only what is not the default is written back, so a canonical address stays as short
    /// as what was typed and an ordinary connection carries no query at all.
    /// </summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("baud=9600", "")]
    [InlineData("baud=115200", "baud=115200")]
    [InlineData("baud=115200&parity=even", "baud=115200&parity=even")]
    [InlineData("term=crlf", "term=crlf")]
    public void The_query_written_back_says_only_what_is_unusual(string query, string expected)
        => Assert.Equal(expected, Parse(query).Query);

    [Fact]
    public void The_query_written_back_can_be_read_again()
    {
        var once = Parse("baud=57600&databits=7&parity=odd&stopbits=1.5&flow=xonxoff&term=cr");
        var twice = Parse(once.Query);

        Assert.Equal(once, twice);
    }

    /// <summary>The engineer's shorthand, as a manual prints it.</summary>
    [Theory]
    [InlineData("", "9600-8-N-1")]
    [InlineData("baud=115200", "115200-8-N-1")]
    [InlineData("baud=19200&databits=7&parity=even&stopbits=2", "19200-7-E-2")]
    [InlineData("flow=rtscts", "9600-8-N-1 rtscts")]
    [InlineData("term=crlf", "9600-8-N-1 crlf")]
    public void The_settings_read_out_as_a_manual_prints_them(string query, string expected)
        => Assert.Equal(expected, Parse(query).ToString());

    /// <summary>
    /// COM2 before COM10, and each kind of port with its own kind — which plain text sorting
    /// gets wrong, and gets wrong on exactly the bench that has enough adapters to care.
    /// </summary>
    [Fact]
    public void Port_names_sort_by_number_within_their_kind()
    {
        var sorted = SerialPorts.Sort(new[] { "COM10", "/dev/ttyUSB1", "COM2", "/dev/ttyUSB10", "COM1" });

        Assert.Equal(new[] { "/dev/ttyUSB1", "/dev/ttyUSB10", "COM1", "COM2", "COM10" }, sorted);
    }

    [Fact]
    public void Listing_the_ports_works_wherever_the_tests_run()
    {
        // No assertion about what is on this machine — a build server has no serial ports
        // and a bench has several. What is being pinned is that asking never throws.
        var names = SerialPorts.Names();
        Assert.NotNull(names);
    }
}
