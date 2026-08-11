using System;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// Transport-agnostic SCPI connection. Implemented by:
///   * <see cref="ScpiClient"/>  — raw TCP socket (Rigol on 5555, Keysight/Siglent scopes on 5025)
///   * <see cref="Vxi11Client"/> — VXI-11 / ONC RPC (instruments with no raw socket, e.g. Siglent SDG2042X)
/// The UI talks to this and doesn't care which wire protocol is underneath.
/// </summary>
public interface IInstrumentClient : IDisposable
{
    string Host { get; }

    /// <summary>Human-readable transport description, shown in the UI.</summary>
    string Description { get; }

    bool IsConnected { get; }

    /// <summary>Connect / read / write timeout in milliseconds.</summary>
    int TimeoutMs { get; set; }

    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Write a command (no response expected).</summary>
    Task SendAsync(string command, CancellationToken ct = default);

    /// <summary>Write a query and read the response.</summary>
    Task<string> QueryAsync(string command, CancellationToken ct = default);

    /// <summary>
    /// Write a query and read an IEEE 488.2 binary block response (waveforms,
    /// screenshots), returning just the data payload with the block header and any
    /// trailing newline stripped.
    /// </summary>
    Task<byte[]> QueryBinaryAsync(string command, CancellationToken ct = default);

    /// <summary>
    /// Hand control back to the instrument's front panel before disconnecting. VXI-11
    /// sends an explicit device_local; raw sockets rely on the connection closing, so
    /// their implementation is a no-op. Best-effort — never throws.
    /// </summary>
    Task ReturnToLocalAsync(CancellationToken ct = default);

    void Close();
}
