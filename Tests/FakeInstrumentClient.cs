using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LabEquipmentController;

namespace LabEquipmentController.Tests;

/// <summary>
/// In-memory <see cref="IInstrumentClient"/> for testing the script/command layers
/// without a real instrument. Records every command and returns canned replies.
/// </summary>
public sealed class FakeInstrumentClient : IInstrumentClient
{
    /// <summary>Every command, prefixed "SEND:" or "QUERY:", in the order received.</summary>
    public List<string> Log { get; } = new();

    /// <summary>If a command's trimmed text equals this, SendAsync/QueryAsync throws.</summary>
    public string? ThrowOn { get; init; }

    /// <summary>Optional canned responses keyed by query text; defaults to "resp:&lt;cmd&gt;".</summary>
    public Dictionary<string, string> Responses { get; } = new();

    public bool ReturnedToLocal { get; private set; }

    /// <summary>Simulate an instrument that has gone away by the time we hand it back.</summary>
    public bool FailReturnToLocal { get; init; }

    public string Host { get; init; } = "fake";
    public string Description { get; init; } = "fake";
    public bool IsConnected { get; private set; } = true;
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Complete every operation asynchronously, the way a real socket does.
    ///
    /// Off by default — and that default is what hid a real bug for a whole feature. With
    /// tasks that are already complete, no <c>await</c> ever yields, so a caller's
    /// continuations stay on the thread that started them and code which is only correct on
    /// one thread looks correct. A loopback stand-in behaves the same way, which is why
    /// running against one proved nothing. A real instrument on real Ethernet yields on
    /// every round-trip, and the run died on line two.
    /// </summary>
    public bool Yields { get; init; }

    /// <summary>A round-trip that actually suspends, so continuations resume on a pool thread.</summary>
    private async Task HopAsync(CancellationToken ct)
    {
        if (Yields) await Task.Delay(1, ct).ConfigureAwait(false);
    }

    public Task ConnectAsync(CancellationToken ct = default) { IsConnected = true; return Task.CompletedTask; }

    public async Task SendAsync(string command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (command.Trim() == ThrowOn) throw new System.IO.IOException("simulated failure");
        await HopAsync(ct).ConfigureAwait(false);
        Log.Add("SEND:" + command);
    }

    public async Task<string> QueryAsync(string command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (command.Trim() == ThrowOn) throw new System.IO.IOException("simulated failure");
        await HopAsync(ct).ConfigureAwait(false);
        Log.Add("QUERY:" + command);
        return Responses.TryGetValue(command, out var r) ? r : "resp:" + command;
    }

    /// <summary>Bytes returned by <see cref="QueryBinaryAsync"/>.</summary>
    public byte[] BinaryResponse { get; init; } = System.Array.Empty<byte>();

    public Task<byte[]> QueryBinaryAsync(string command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (command.Trim() == ThrowOn) throw new System.IO.IOException("simulated failure");
        Log.Add("QUERYBIN:" + command);
        return Task.FromResult(BinaryResponse);
    }

    public Task ReturnToLocalAsync(CancellationToken ct = default)
    {
        if (FailReturnToLocal) throw new System.IO.IOException("simulated failure");
        ReturnedToLocal = true;
        return Task.CompletedTask;
    }

    public void Close() => IsConnected = false;
    public void Dispose() => Close();
}
