using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController.Tests;

/// <summary>
/// A VXI-11 core channel on loopback: enough ONC RPC to create a link, take a command and
/// answer it, so the client's wire handling can be tested without a bench.
///
/// The portmapper step is left out — the client's <c>OpenCoreAsync</c> starts at the core
/// channel, and binding TCP 111 is not something a test may do (it needs root on Linux and
/// macOS, where this suite also runs).
///
/// What it exists for is the awkward instrument rather than the polite one: a query answered
/// <em>late</em>, after the client has given up waiting, which is what a Siglent meter does
/// when it is asked to change function. See <see cref="SlowCommand"/>.
/// </summary>
internal sealed class FakeVxi11Instrument : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;

    public const string Identity = "FAKE INSTRUMENTS,VXI-1,SN0001,1.0";

    /// <summary>A command this instrument is slow to answer, or null if it is never slow.</summary>
    public string? SlowCommand { get; init; }

    /// <summary>How long the slow command takes to come back.</summary>
    public int SlowMs { get; init; } = 1500;

    /// <summary>
    /// Send half a record and then stop, on the command named here. Nothing can line the
    /// connection up again afterwards, which is the case the client must refuse to guess at.
    /// </summary>
    public string? TruncateCommand { get; init; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public FakeVxi11Instrument()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        try
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
            using NetworkStream stream = client.GetStream();

            string pending = "";
            while (!_stop.IsCancellationRequested)
            {
                byte[]? call = await ReadMessageAsync(stream, _stop.Token);
                if (call == null) return;

                uint xid = ReadU32(call, 0);
                int proc = (int)ReadU32(call, 20);
                var results = new List<byte>();

                switch (proc)
                {
                    case 10:    // create_link -> error, lid, abortPort, maxRecvSize
                        AddU32(results, 0);
                        AddU32(results, 1);
                        AddU32(results, 0);
                        AddU32(results, 8192);
                        break;

                    case 11:    // device_write -> error, size
                        pending = CommandIn(call);
                        AddU32(results, 0);
                        AddU32(results, (uint)pending.Length);
                        break;

                    case 12:    // device_read -> error, reason (4 = END), data<>
                        if (TruncateCommand != null && pending == TruncateCommand)
                        {
                            await HalfARecordAsync(stream, _stop.Token);
                            continue;
                        }
                        if (SlowCommand != null && pending == SlowCommand)
                            await Task.Delay(SlowMs, _stop.Token);
                        AddU32(results, 0);
                        AddU32(results, 4);
                        AddOpaque(results, Encoding.ASCII.GetBytes(Answer(pending)));
                        break;

                    default:    // device_clear, device_local, destroy_link -> error
                        AddU32(results, 0);
                        break;
                }

                await stream.WriteAsync(Reply(xid, results), _stop.Token);
            }
        }
        catch (OperationCanceledException) { /* told to stop */ }
        catch (IOException) { /* the client went away */ }
        catch (SocketException) { /* ditto */ }
    }

    /// <summary>What this instrument answers. A number for anything it does not know.</summary>
    private static string Answer(string command)
        => command.StartsWith("*IDN?", StringComparison.OrdinalIgnoreCase) ? Identity : "1.234";

    /// <summary>The command out of a Device_WriteParms { lid, io_timeout, lock_timeout, flags, data<> }.</summary>
    private static string CommandIn(byte[] call)
    {
        int off = 40 + 16;                       // rpc header, then four fixed fields
        int len = (int)ReadU32(call, off);
        return Encoding.ASCII.GetString(call, off + 4, len).TrimEnd('\r', '\n');
    }

    /// <summary>A record marker promising more than is ever sent.</summary>
    private static async Task HalfARecordAsync(NetworkStream stream, CancellationToken ct)
    {
        var frame = new List<byte>();
        AddU32(frame, 0x80000000u | 64);
        frame.AddRange(new byte[8]);
        await stream.WriteAsync(frame.ToArray(), ct);
    }

    private static byte[] Reply(uint xid, List<byte> results)
    {
        var body = new List<byte>();
        AddU32(body, xid);
        AddU32(body, 1);    // msg_type = REPLY
        AddU32(body, 0);    // reply_stat = MSG_ACCEPTED
        AddU32(body, 0);    // verf flavor = AUTH_NULL
        AddU32(body, 0);    // verf length
        AddU32(body, 0);    // accept_stat = SUCCESS
        body.AddRange(results);

        var frame = new List<byte>();
        AddU32(frame, 0x80000000u | (uint)body.Count);
        frame.AddRange(body);
        return frame.ToArray();
    }

    /// <summary>One whole record-marked message, or null once the connection ends.</summary>
    private static async Task<byte[]?> ReadMessageAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[]? mk = await ReadExactAsync(stream, 4, ct);
        if (mk == null) return null;
        int len = (int)(ReadU32(mk, 0) & 0x7FFFFFFF);
        return await ReadExactAsync(stream, len, ct);
    }

    private static async Task<byte[]?> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(read, count - read), ct);
            if (n <= 0) return null;
            read += n;
        }
        return buf;
    }

    private static void AddU32(List<byte> b, uint v)
    {
        b.Add((byte)(v >> 24));
        b.Add((byte)(v >> 16));
        b.Add((byte)(v >> 8));
        b.Add((byte)v);
    }

    private static void AddOpaque(List<byte> b, byte[] data)
    {
        AddU32(b, (uint)data.Length);
        b.AddRange(data);
        for (int pad = (4 - data.Length % 4) % 4; pad > 0; pad--) b.Add(0);
    }

    private static uint ReadU32(byte[] b, int off)
        => (uint)((b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3]);

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try { _serving.Wait(TimeSpan.FromSeconds(2)); } catch { /* going away anyway */ }
        _stop.Dispose();
    }
}
