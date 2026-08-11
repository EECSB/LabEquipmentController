using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

public class VisaResourceTests
{
    [Fact]
    public void Parses_vxi11_instr_resource()
    {
        Assert.True(VisaResource.TryParse("TCPIP0::192.168.1.19::inst0::INSTR", out var r));
        Assert.Equal("192.168.1.19", r.Host);
        Assert.Equal(InstrumentTransport.Vxi11, r.Transport);
        Assert.Equal(Vxi11Client.PortmapperPort, r.Port);
        Assert.Equal("inst0", r.DeviceName);
    }

    [Fact]
    public void Parses_socket_resource_with_port()
    {
        Assert.True(VisaResource.TryParse("TCPIP0::192.168.1.19::5025::SOCKET", out var r));
        Assert.Equal(InstrumentTransport.RawSocket, r.Transport);
        Assert.Equal(5025, r.Port);
        Assert.Equal("192.168.1.19", r.Host);
    }

    [Fact]
    public void Board_index_and_device_are_optional_for_instr()
    {
        Assert.True(VisaResource.TryParse("TCPIP::192.168.1.19::INSTR", out var r));
        Assert.Equal(InstrumentTransport.Vxi11, r.Transport);
        Assert.Equal("inst0", r.DeviceName);
    }

    [Theory]
    [InlineData("tcpip0::192.168.1.19::5555::socket")]      // lowercase
    [InlineData("  TCPIP0::192.168.1.19::5555::SOCKET  ")]  // outer whitespace
    public void Parsing_is_case_insensitive_and_trimmed(string text)
    {
        Assert.True(VisaResource.TryParse(text.Trim(), out var r));
        Assert.Equal(InstrumentTransport.RawSocket, r.Transport);
        Assert.Equal(5555, r.Port);
    }

    [Theory]
    [InlineData("192.168.1.19")]            // plain IP — not a resource string
    [InlineData("192.168.1.19:5555")]       // IP:port
    [InlineData("GPIB0::7::INSTR")]          // non-TCPIP interface
    [InlineData("TCPIP0::192.168.1.19::SOCKET")]  // SOCKET without a port
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_non_tcpip_and_malformed(string? text)
    {
        Assert.False(VisaResource.TryParse(text, out _));
    }

    [Fact]
    public void Format_and_reparse_round_trips_both_transports()
    {
        string vxi = VisaResource.Format(InstrumentTransport.Vxi11, "192.168.1.19", 111);
        Assert.True(VisaResource.TryParse(vxi, out var a));
        Assert.Equal(InstrumentTransport.Vxi11, a.Transport);

        string sock = VisaResource.Format(InstrumentTransport.RawSocket, "192.168.1.19", 5555);
        Assert.True(VisaResource.TryParse(sock, out var b));
        Assert.Equal(InstrumentTransport.RawSocket, b.Transport);
        Assert.Equal(5555, b.Port);
    }
}
