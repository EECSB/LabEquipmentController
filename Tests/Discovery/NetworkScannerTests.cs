using System.Linq;
using System.Net;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

public class NetworkScannerTests
{
    [Fact]
    public void Slash24_yields_254_hosts_excluding_network_and_broadcast()
    {
        var hosts = NetworkScanner.EnumerateHosts(
            IPAddress.Parse("192.168.1.100"), IPAddress.Parse("255.255.255.0"), 4096, out bool capped);

        Assert.False(capped);
        Assert.Equal(254, hosts.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), hosts.First());
        Assert.Equal(IPAddress.Parse("192.168.1.254"), hosts.Last());
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.0"), hosts);     // network
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.255"), hosts);   // broadcast
    }

    [Fact]
    public void Host_count_is_independent_of_which_address_in_the_subnet()
    {
        var a = NetworkScanner.EnumerateHosts(IPAddress.Parse("192.168.1.1"), IPAddress.Parse("255.255.255.0"), 4096, out _);
        var b = NetworkScanner.EnumerateHosts(IPAddress.Parse("192.168.1.254"), IPAddress.Parse("255.255.255.0"), 4096, out _);
        Assert.Equal(a, b);
    }

    [Fact]
    public void MaxHosts_caps_the_result_and_sets_the_flag()
    {
        var hosts = NetworkScanner.EnumerateHosts(
            IPAddress.Parse("192.168.1.100"), IPAddress.Parse("255.255.255.0"), 10, out bool capped);

        Assert.True(capped);
        Assert.Equal(10, hosts.Count);
    }

    [Fact]
    public void Slash30_yields_two_usable_hosts()
    {
        var hosts = NetworkScanner.EnumerateHosts(
            IPAddress.Parse("192.168.1.1"), IPAddress.Parse("255.255.255.252"), 4096, out bool capped);

        Assert.False(capped);
        Assert.Equal(new[] { IPAddress.Parse("192.168.1.1"), IPAddress.Parse("192.168.1.2") }, hosts);
    }

    [Fact]
    public void Common_scpi_ports_include_raw_sockets_and_vxi11()
    {
        Assert.Contains(5025, NetworkScanner.CommonScpiPorts);
        Assert.Contains(5555, NetworkScanner.CommonScpiPorts);
        Assert.Contains(Vxi11Client.PortmapperPort, NetworkScanner.CommonScpiPorts);
    }
}
