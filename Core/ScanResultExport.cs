using System.Collections.Generic;
using System.Text;

namespace LabEquipmentController;

/// <summary>Serializes discovered instruments to CSV (RFC 4180).</summary>
public static class ScanResultExport
{
    /// <summary>Build a CSV document (with header row) from the given devices.</summary>
    public static string ToCsv(IEnumerable<ScpiDevice> devices)
    {
        var sb = new StringBuilder();
        sb.Append("IP Address,Port,Protocol,Identity\r\n");
        foreach (ScpiDevice d in devices)
        {
            sb.Append(Csv(d.Address.ToString())).Append(',')
              .Append(d.Port).Append(',')
              .Append(Csv(d.TransportName)).Append(',')
              .Append(Csv(d.Identity)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Quote a field when it contains a comma, quote, or newline; double embedded quotes.</summary>
    private static string Csv(string field)
    {
        if (field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
