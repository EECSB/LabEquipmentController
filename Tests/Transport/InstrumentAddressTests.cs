using LabEquipmentController;

namespace LabEquipmentController.Tests;

/// <summary>
/// What a typed address means, pinned once for all three front ends.
///
/// This parser exists because there were two of it — the CLI's and the web server's, the
/// same code twice — and the desktop had neither, so its Address box refused
/// <c>vxi://192.168.1.19</c> though UI-SPEC §3.3 describes both builds' boxes as taking
/// one. The cases below are the spellings all three now accept, and the two flags the
/// desktop needs in order to decide when a discovered row may override what was typed.
/// </summary>
public class InstrumentAddressTests
{
    private static InstrumentAddress Parse(string text, int defaultRawPort = 5025)
    {
        Assert.True(InstrumentAddress.TryParse(text, out var a, out string error, defaultRawPort), error);
        return a;
    }

    [Theory]
    [InlineData("vxi://192.168.1.19")]
    [InlineData("VXI://192.168.1.19")]         // the scheme is not case-sensitive
    [InlineData("vxi://192.168.1.19/")]        // a trailing slash is not a device name
    public void A_vxi_address_is_VXI_11_on_the_portmapper(string text)
    {
        var a = Parse(text);

        Assert.Equal("192.168.1.19", a.Host);
        Assert.Equal(InstrumentTransport.Vxi11, a.Transport);
        Assert.Equal(111, a.Port);
        Assert.Equal("inst0", a.DeviceName);
        Assert.True(a.TransportNamed);
    }

    /// <summary>
    /// A path after the host is the logical device behind a gateway — the instrument at
    /// GPIB address 9 is not the gateway itself, and connecting to "inst0" would reach the
    /// wrong one.
    /// </summary>
    [Fact]
    public void A_vxi_address_can_name_the_device_behind_a_gateway()
    {
        var a = Parse("vxi://192.168.1.30/gpib0,9");

        Assert.Equal("192.168.1.30", a.Host);
        Assert.Equal("gpib0,9", a.DeviceName);
        Assert.Equal(InstrumentTransport.Vxi11, a.Transport);
    }

    [Fact]
    public void A_vxi_address_with_no_host_is_refused_with_a_reason()
    {
        Assert.False(InstrumentAddress.TryParse("vxi://", out _, out string error));
        Assert.Contains("host", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_visa_resource_string_keeps_its_device_and_transport()
    {
        var a = Parse("TCPIP0::192.168.1.19::inst0::INSTR");

        Assert.Equal("192.168.1.19", a.Host);
        Assert.Equal(InstrumentTransport.Vxi11, a.Transport);
        Assert.Equal("inst0", a.DeviceName);
        Assert.True(a.TransportNamed);

        var socket = Parse("TCPIP0::192.168.1.20::5025::SOCKET");
        Assert.Equal(InstrumentTransport.RawSocket, socket.Transport);
        Assert.Equal(5025, socket.Port);
    }

    /// <summary>
    /// Port 111 is the portmapper, so writing it out asks for VXI-11 as surely as the
    /// scheme does. Read as a raw socket it connects and then waits for a SCPI reply the
    /// portmapper will never send.
    /// </summary>
    [Fact]
    public void An_explicit_port_111_means_VXI_11()
    {
        var a = Parse("192.168.1.19:111");

        Assert.Equal(InstrumentTransport.Vxi11, a.Transport);
        Assert.Equal(111, a.Port);
        Assert.True(a.PortNamed);
        Assert.False(a.TransportNamed);   // the port implied it; the text did not say it
    }

    [Fact]
    public void A_bare_host_takes_the_default_port_it_is_given()
    {
        Assert.Equal(5025, Parse("192.168.1.20").Port);

        // The desktop passes the first entry from its own SCPI Port(s) box, so a bare
        // address means there what pressing Scan means.
        var withOwnDefault = Parse("192.168.1.20", defaultRawPort: 5555);
        Assert.Equal(5555, withOwnDefault.Port);
        Assert.Equal(InstrumentTransport.RawSocket, withOwnDefault.Transport);
        Assert.False(withOwnDefault.PortNamed);
    }

    [Fact]
    public void A_tcp_address_is_a_raw_socket_said_out_loud()
    {
        var a = Parse("tcp://192.168.1.20:5555");

        Assert.Equal("192.168.1.20", a.Host);
        Assert.Equal(5555, a.Port);
        Assert.Equal(InstrumentTransport.RawSocket, a.Transport);
        Assert.True(a.TransportNamed);
    }

    /// <summary>
    /// An IPv6 literal is bracketed and its colons are not port separators. Splitting at
    /// the last colon of a bare ::1 would leave a host of ":" and a port of "1".
    /// </summary>
    [Theory]
    [InlineData("[::1]:5025", "::1", 5025)]
    [InlineData("[fe80::1]", "fe80::1", 5025)]
    public void An_IPv6_literal_keeps_its_colons(string text, string host, int port)
    {
        var a = Parse(text);

        Assert.Equal(host, a.Host);
        Assert.Equal(port, a.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("192.168.1.20:0")]
    [InlineData("192.168.1.20:99999")]
    [InlineData("[::1]:not-a-port")]
    public void What_cannot_be_read_is_refused_rather_than_guessed_at(string? text)
    {
        Assert.False(InstrumentAddress.TryParse(text, out _, out string error));
        Assert.NotEmpty(error);
    }

    /// <summary>
    /// What a discovered row puts in the Address box. `192.168.1.19:111` connects correctly —
    /// 111 is read as VXI-11 — and reads as a raw socket on an unusual port, which is the
    /// protocol going missing at the moment the row was there to say it. The desktop showed
    /// the port and the web showed the scheme, from the same scan, because each worked it out
    /// for itself; now neither does.
    /// </summary>
    [Theory]
    [InlineData(111, InstrumentTransport.Vxi11, "vxi://192.168.1.19")]
    [InlineData(5025, InstrumentTransport.RawSocket, "192.168.1.19:5025")]
    [InlineData(5555, InstrumentTransport.RawSocket, "192.168.1.19:5555")]
    public void A_discovered_row_is_written_the_way_it_would_be_typed(
        int port, InstrumentTransport transport, string expected)
    {
        var device = new ScpiDevice
        {
            Address = System.Net.IPAddress.Parse("192.168.1.19"),
            Port = port,
            Transport = transport,
        };

        Assert.Equal(expected, device.TypedAddress);

        // And it has to come back as the instrument it came from, or the box would be a
        // spelling nothing can read.
        var round = Parse(device.TypedAddress);
        Assert.Equal(transport, round.Transport);
        Assert.Equal("192.168.1.19", round.Host);
    }

    /// <summary>
    /// The client an address opens carries the device name with it. The desktop used to
    /// build its own client and pass only the host, so a typed <c>vxi://host/gpib0,9</c>
    /// parsed correctly and then connected to inst0 — the gateway itself rather than the
    /// instrument at GPIB address 9. One factory, so there is one place to get this right.
    /// </summary>
    [Fact]
    public void The_client_an_address_opens_keeps_the_device_behind_a_gateway()
    {
        using IInstrumentClient client = Parse("vxi://192.168.1.30/gpib0,9").CreateClient();

        var vxi = Assert.IsType<Vxi11Client>(client);
        Assert.Equal("192.168.1.30", vxi.Host);
        Assert.Equal("gpib0,9", vxi.DeviceName);
    }

    /// <summary>
    /// A resource string for an interface this app has no transport for is refused by name.
    /// It used to fall through to the host branch and be accepted as a hostname, so pasting
    /// the string NI-MAX shows for a GPIB instrument got you as far as a DNS failure —
    /// which answers a question nobody asked. SPEC §17 has why these two are not built.
    /// </summary>
    [Theory]
    [InlineData("GPIB0::9::INSTR", "GPIB")]
    [InlineData("USB0::0x0957::0x1799::MY12345678::0::INSTR", "USB")]
    public void A_resource_string_for_a_transport_we_do_not_have_says_so(string text, string named)
    {
        Assert.False(InstrumentAddress.TryParse(text, out _, out string error));
        Assert.Contains(named, error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And one that names an interface we do have, but is malformed, is a broken resource
    /// string rather than a machine called "TCPIP0::192.168.1.19::nonsense::WRONG".
    /// </summary>
    [Theory]
    [InlineData("TCPIP0::::INSTR")]
    [InlineData("ASRL::INSTR")]
    public void A_resource_string_that_cannot_be_read_is_not_treated_as_a_hostname(string text)
    {
        Assert.False(InstrumentAddress.TryParse(text, out _, out string error));
        Assert.Contains("resource string", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The rule above keys off the text in front of the first "::" being the name of a VISA
    /// interface — which an IPv6 literal's is not, and this is the case that would break if
    /// someone made that check any broader.
    /// </summary>
    [Fact]
    public void An_IPv6_literal_is_not_mistaken_for_a_resource_string()
        => Assert.Equal("fe80::1", Parse("[fe80::1]:5025").Host);

    /// <summary>
    /// The canonical spelling is what a session is keyed on, so it has to be a string the
    /// address box will take back — which is the round trip below.
    /// </summary>
    [Theory]
    [InlineData("vxi://192.168.1.19")]
    [InlineData("vxi://192.168.1.30/gpib0,9")]
    [InlineData("192.168.1.20:5025")]
    [InlineData("192.168.1.20:5555")]
    public void The_canonical_form_parses_back_to_itself(string text)
    {
        var once = Parse(text);
        var twice = Parse(once.Canonical);

        Assert.Equal(once.Canonical, twice.Canonical);
        Assert.Equal(once.Host, twice.Host);
        Assert.Equal(once.Transport, twice.Transport);
        Assert.Equal(once.Port, twice.Port);
        Assert.Equal(once.DeviceName, twice.DeviceName);
    }

    /// <summary>
    /// A raw socket is spelled out even on the usual port: the whole job of a canonical
    /// form is telling one instrument from another, and "the default" is not a name.
    /// </summary>
    [Fact]
    public void A_raw_socket_writes_its_port_out_even_when_it_is_the_usual_one()
        => Assert.Equal("192.168.1.20:5025", Parse("192.168.1.20").Canonical);
}
