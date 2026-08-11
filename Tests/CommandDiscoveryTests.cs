using System;
using System.Text;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

public class CommandDiscoveryTests
{
    [Fact]
    public async Task Succeeds_and_parses_headers_when_supported()
    {
        var client = new FakeInstrumentClient
        {
            BinaryResponse = Encoding.ASCII.GetBytes(
                ":SYSTem:ERRor?\n:CHANnel1:SCALe\n*IDN\n:TIMebase:MAIN:SCALe\n:TRIGger:SWEep\n"),
        };

        var r = await CommandDiscovery.DiscoverAsync(client);

        Assert.True(r.Success);
        Assert.Equal(5, r.Count);
        Assert.Contains(":CHANnel1:SCALe", r.Headers);
        Assert.Contains("QUERYBIN:" + CommandDiscovery.Query, client.Log);
    }

    [Fact]
    public async Task Fails_when_the_query_throws()   // instrument doesn't implement it
    {
        var client = new FakeInstrumentClient { ThrowOn = CommandDiscovery.Query };
        var r = await CommandDiscovery.DiscoverAsync(client);
        Assert.False(r.Success);
        Assert.Empty(r.Headers);
    }

    [Fact]
    public async Task Fails_when_the_response_is_empty()
    {
        var client = new FakeInstrumentClient { BinaryResponse = Array.Empty<byte>() };
        var r = await CommandDiscovery.DiscoverAsync(client);
        Assert.False(r.Success);
    }

    [Theory]
    [InlineData("garbage nonsense text")]   // one line, not header-like
    [InlineData("0")]                         // a bare status value
    [InlineData("-113,\"Undefined header\"")] // an error-queue entry
    public async Task Fails_when_the_response_is_not_a_header_list(string body)
    {
        var client = new FakeInstrumentClient { BinaryResponse = Encoding.ASCII.GetBytes(body) };
        var r = await CommandDiscovery.DiscoverAsync(client);
        Assert.False(r.Success);
    }
}
