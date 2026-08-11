using System.Collections.Generic;
using System.Net;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

public class ScanResultExportTests
{
    private static ScpiDevice Dev(string ip, int port, InstrumentTransport t, string idn) =>
        new() { Address = IPAddress.Parse(ip), Port = port, Transport = t, Identity = idn };

    [Fact]
    public void Empty_list_still_emits_header()
    {
        string csv = ScanResultExport.ToCsv(new List<ScpiDevice>());
        Assert.Equal("IP Address,Port,Protocol,Identity\r\n", csv);
    }

    [Fact]
    public void Row_carries_ip_port_transport_and_identity()
    {
        string csv = ScanResultExport.ToCsv(new[]
        {
            Dev("192.168.1.25", 111, InstrumentTransport.Vxi11, "Siglent"),
        });
        Assert.Contains("192.168.1.25,111,VXI-11,Siglent\r\n", csv);
    }

    [Fact]
    public void Identity_with_commas_is_quoted()
    {
        // A real *IDN? reply is comma-separated and must not break the CSV columns.
        string csv = ScanResultExport.ToCsv(new[]
        {
            Dev("192.168.1.19", 5555, InstrumentTransport.Vxi11, "RIGOL,DS2202,DS2A,00.03"),
        });
        Assert.Contains("\"RIGOL,DS2202,DS2A,00.03\"", csv);
    }

    [Fact]
    public void Embedded_quotes_are_doubled()
    {
        string csv = ScanResultExport.ToCsv(new[]
        {
            Dev("10.0.0.1", 5025, InstrumentTransport.RawSocket, "has \"quotes\""),
        });
        Assert.Contains("\"has \"\"quotes\"\"\"", csv);
    }

    [Fact]
    public void Raw_socket_transport_is_named()
    {
        string csv = ScanResultExport.ToCsv(new[]
        {
            Dev("10.0.0.2", 5025, InstrumentTransport.RawSocket, ""),
        });
        Assert.Contains("10.0.0.2,5025,Raw socket,\r\n", csv);
    }
}
