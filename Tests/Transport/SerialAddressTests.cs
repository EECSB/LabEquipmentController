using System;
using System.IO.Ports;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// A serial port, written as an address.
///
/// The transport had to be named for a reason that is easy to miss: <c>COM3</c> is a
/// perfectly legal hostname, so a bare port name cannot be told from a machine on the
/// network. Hence <c>serial://COM3</c>, the spelling SPEC §17 chose while this was still an
/// argument about whether to build it at all — and <c>ASRL3::INSTR</c>, which is how the
/// same thing is spelled by the VISA tools these strings get pasted out of.
/// </summary>
public class SerialAddressTests
{
    private static InstrumentAddress Parse(string text)
    {
        Assert.True(InstrumentAddress.TryParse(text, out var a, out string error), error);
        return a;
    }

    [Theory]
    [InlineData("serial://COM3", "COM3")]
    [InlineData("SERIAL://COM3", "COM3")]           // the scheme is not case-sensitive
    [InlineData("serial://COM3/", "COM3")]
    [InlineData("serial:///dev/ttyUSB0", "/dev/ttyUSB0")]
    [InlineData("serial:///dev/cu.usbserial-A50285BI", "/dev/cu.usbserial-A50285BI")]
    public void A_serial_address_is_the_port_name_and_the_defaults(string text, string port)
    {
        var a = Parse(text);

        Assert.Equal(port, a.Host);
        Assert.Equal(InstrumentTransport.Serial, a.Transport);
        Assert.True(a.TransportNamed);
        Assert.NotNull(a.Serial);
        Assert.Equal(9600, a.Serial!.BaudRate);
    }

    [Fact]
    public void The_line_settings_come_with_the_address()
    {
        var a = Parse("serial://COM3?baud=115200&parity=even&term=crlf");

        Assert.Equal("COM3", a.Host);
        Assert.Equal(115200, a.Serial!.BaudRate);
        Assert.Equal(Parity.Even, a.Serial.Parity);
        Assert.Equal(ScpiTerminator.CrLf, a.Serial.Terminator);
    }

    /// <summary>
    /// Three slashes is one more than most people count, and no serial port is named
    /// "dev/…", so putting the slash back cannot take a good address and make it wrong.
    /// </summary>
    [Fact]
    public void A_unix_device_path_missing_its_leading_slash_is_still_read()
        => Assert.Equal("/dev/ttyUSB0", Parse("serial://dev/ttyUSB0").Host);

    [Theory]
    [InlineData("serial://")]
    [InlineData("serial://COM3?baud=fast")]
    [InlineData("serial://COM3?colour=blue")]
    public void What_cannot_be_read_is_refused_with_a_reason(string text)
    {
        Assert.False(InstrumentAddress.TryParse(text, out _, out string error));
        Assert.NotEmpty(error);
    }

    /// <summary>
    /// VISA's own spelling. The numeric form is NI's Windows convention, which is where
    /// these strings are copied from; off Windows there is no agreed answer to "the third
    /// serial port", so the explicit form is the one that travels.
    /// </summary>
    [Theory]
    [InlineData("ASRL3::INSTR", "COM3")]
    [InlineData("ASRL1::INSTR", "COM1")]
    [InlineData("ASRL::COM4::INSTR", "COM4")]
    [InlineData("ASRL::/dev/ttyUSB0::INSTR", "/dev/ttyUSB0")]
    public void A_VISA_ASRL_resource_is_a_serial_port(string text, string port)
    {
        var a = Parse(text);

        Assert.Equal(port, a.Host);
        Assert.Equal(InstrumentTransport.Serial, a.Transport);
        Assert.NotNull(a.Serial);
    }

    /// <summary>
    /// A resource string carries no line settings — VISA keeps those as session attributes —
    /// so ASRL means the defaults, and serial:// is the spelling that can say otherwise.
    /// </summary>
    [Fact]
    public void An_ASRL_resource_says_nothing_about_baud_rate()
        => Assert.Equal(SerialSettings.Default, Parse("ASRL7::INSTR").Serial);

    [Theory]
    [InlineData("ASRL::INSTR")]        // neither a number nor a name
    [InlineData("ASRL::COM3::SOCKET")] // SOCKET is a LAN resource class
    public void An_ASRL_resource_that_names_no_port_is_refused(string text)
        => Assert.False(InstrumentAddress.TryParse(text, out _, out _));

    /// <summary>
    /// The canonical spelling is what a session is keyed on, so it has to be a string the
    /// address box will take back — and it has to carry the baud rate, or reconnecting to
    /// the canonical form would reach the same port at the wrong speed.
    /// </summary>
    [Theory]
    [InlineData("serial://COM3", "serial://COM3")]
    [InlineData("serial://COM3?baud=9600", "serial://COM3")]           // the default is not worth saying
    [InlineData("serial://COM3?baud=115200", "serial://COM3?baud=115200")]
    [InlineData("serial:///dev/ttyUSB0", "serial:///dev/ttyUSB0")]
    [InlineData("ASRL3::INSTR", "serial://COM3")]
    public void The_canonical_form_is_what_could_be_typed_back_in(string text, string expected)
    {
        Assert.Equal(expected, Parse(text).Canonical);
        Assert.Equal(expected, Parse(Parse(text).Canonical).Canonical);
    }

    /// <summary>
    /// Two ports are two instruments, and the same port at two speeds is one instrument
    /// that cannot be open twice — which is what keying a session on this string has to say.
    /// </summary>
    [Fact]
    public void Two_serial_ports_are_told_apart_and_a_bare_COM3_is_not_a_host()
    {
        Assert.NotEqual(Parse("serial://COM3").Canonical, Parse("serial://COM4").Canonical);
        Assert.NotEqual(Parse("serial://COM3").Canonical, Parse("COM3").Canonical);
        Assert.Equal(InstrumentTransport.RawSocket, Parse("COM3").Transport);
    }

    [Fact]
    public void A_serial_address_opens_a_serial_client()
    {
        using IInstrumentClient client = Parse("serial://COM3?baud=115200").CreateClient(1234);

        var serial = Assert.IsType<SerialInstrumentClient>(client);
        Assert.Equal("COM3", serial.Host);
        Assert.Equal(115200, serial.Settings.BaudRate);
        Assert.Equal(1234, serial.TimeoutMs);
        Assert.False(serial.IsConnected);
    }

    [Theory]
    [InlineData("192.168.1.20:5025", typeof(ScpiClient))]
    [InlineData("vxi://192.168.1.20", typeof(Vxi11Client))]
    [InlineData("serial://COM3", typeof(SerialInstrumentClient))]
    public void Each_spelling_opens_the_transport_it_names(string text, Type expected)
    {
        using IInstrumentClient client = Parse(text).CreateClient();
        Assert.IsType(expected, client);
    }

    /// <summary>The console header and the device list both show this.</summary>
    [Fact]
    public void The_description_names_the_port_and_the_settings()
    {
        using var client = new SerialInstrumentClient("COM3", new SerialSettings { BaudRate = 115200 });
        Assert.Equal("serial (COM3, 115200-8-N-1)", client.Description);
    }

    [Fact]
    public async Task Asking_before_connecting_throws_rather_than_answering()
    {
        using var client = new SerialInstrumentClient("COM3");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAsync("*IDN?"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync("*RST"));
    }

    /// <summary>
    /// A port that is not there has to fail, and fail without leaving a half-open handle
    /// behind — the next attempt on the real port would find it locked by this one.
    /// </summary>
    [Fact]
    public async Task Opening_a_port_that_does_not_exist_fails_cleanly()
    {
        using var client = new SerialInstrumentClient("LEC-NOT-A-PORT");

        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync());
        Assert.False(client.IsConnected);
    }

    /// <summary>Best-effort means best-effort: nothing to hand back is not a failure.</summary>
    [Fact]
    public async Task Returning_an_unconnected_port_to_local_is_quiet()
    {
        using var client = new SerialInstrumentClient("COM3");
        await client.ReturnToLocalAsync();
    }
}
