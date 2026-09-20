using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// VXI-11 SCPI client (ONC RPC / Sun RPC over TCP).
///
/// Used by LAN instruments that expose no raw socket — e.g. the Siglent
/// SDG2042X, which only offers VXI-11 (its port 23 is a BusyBox shell, not SCPI).
///
/// Session flow:
///   1. Ask the RPC portmapper (TCP 111) for the VXI-11 Core program's port.
///      The core port is DYNAMIC — it must be looked up, never hard-coded.
///   2. Connect to that port, then create_link -> device_write / device_read -> destroy_link.
///
/// Wire format notes:
///   * ONC RPC over TCP uses record marking: each message is prefixed with a
///     4-byte header = (last-fragment bit 0x80000000) | fragment length.
///   * XDR is big-endian; strings/opaque are length-prefixed and padded to 4 bytes.
/// </summary>
public sealed class Vxi11Client : IInstrumentClient
{
    public const int PortmapperPort = 111;

    // --- Portmapper (RFC 1833) ---
    private const int PortmapProgram = 100000;
    private const int PortmapVersion = 2;
    private const int ProcGetPort = 3;
    private const int IpprotoTcp = 6;

    // --- VXI-11 Core channel ---
    private const int VxiCoreProgram = 0x0607AF; // 395183
    private const int VxiCoreVersion = 1;
    private const int ProcCreateLink = 10;
    private const int ProcDeviceWrite = 11;
    private const int ProcDeviceRead = 12;
    private const int ProcDeviceClear = 15;   // flush the instrument's I/O buffers

    /// <summary>Backstop on one response, so a stuck instrument can't grow the buffer
    /// without bound. Comfortably above a screen dump (~1.2 MB) or a deep trace.</summary>
    private const int MaxResponseBytes = 64 * 1024 * 1024;
    private const int ProcDeviceLocal = 17;   // return front panel to local control
    private const int ProcDestroyLink = 23;

    private const int FlagEnd = 8;      // device_write: END on final byte
    private const int ReasonEnd = 4;    // device_read: instrument signalled END
    private const int ErrorIoTimeout = 15;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _linkId;
    private bool _linked;
    private int _xid;
    private uint _maxRecvSize = 4096;

    /// <summary>
    /// Set when a reply was abandoned part way through, which leaves bytes on the connection
    /// that belong to no message anyone is still waiting for. Cleared only by connecting
    /// again — a fresh socket is the only thing that puts the stream back on a boundary.
    /// </summary>
    private bool _outOfStep;

    public string Host { get; }

    /// <summary>The dynamically-resolved VXI-11 core channel port (0 until connected).</summary>
    public int CorePort { get; private set; }

    /// <summary>VXI-11 logical device name — "inst0" for virtually all instruments.</summary>
    public string DeviceName { get; }

    public int TimeoutMs { get; set; } = 5000;

    public bool IsConnected => _linked && _tcp?.Connected == true;

    public string Description => $"VXI-11 (core port {CorePort})";

    public Vxi11Client(string host, string deviceName = "inst0")
    {
        Host = host;
        DeviceName = deviceName;
    }

    // ------------------------------------------------------------------ connect

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Close();

        CorePort = await GetCorePortAsync(ct).ConfigureAwait(false);
        if (CorePort == 0)
            throw new IOException("Host has an RPC portmapper but no VXI-11 instrument registered.");

        await OpenCoreAsync(CorePort, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Open the core channel on a port already known, and start a link on it.
    ///
    /// Split from <see cref="ConnectAsync"/> because the portmapper lookup and the channel
    /// are two separate things: the lookup is how the port is found, not part of talking to
    /// the instrument once it is.
    /// </summary>
    internal async Task OpenCoreAsync(int corePort, CancellationToken ct)
    {
        CorePort = corePort;
        _outOfStep = false;
        _tcp = new TcpClient { NoDelay = true };
        await Deadline.RunAsync(t => _tcp.ConnectAsync(Host, CorePort, t).AsTask(), TimeoutMs,
                                $"{Host} did not answer on VXI-11 port {CorePort}", ct)
                      .ConfigureAwait(false);
        _stream = _tcp.GetStream();

        await CreateLinkAsync(ct).ConfigureAwait(false);

        // Flush any stale output the instrument had queued. The Rigol shares one SCPI
        // output queue across its raw socket and VXI-11, and a prior unread reply there
        // would desync every query; device_clear empties it so we start aligned.
        await DeviceClearAsync(ct).ConfigureAwait(false);
    }

    /// <summary>VXI-11 device_clear (proc 15): clear the instrument's I/O buffers.</summary>
    private async Task DeviceClearAsync(CancellationToken ct)
    {
        try
        {
            var args = new List<byte>();
            AddU32(args, (uint)_linkId);
            AddU32(args, 0);                 // flags
            AddU32(args, 0);                 // lock_timeout
            AddU32(args, (uint)TimeoutMs);   // io_timeout
            await CallAsync(_stream!, VxiCoreProgram, VxiCoreVersion, ProcDeviceClear, args, ct)
                .ConfigureAwait(false);
        }
        catch { /* best effort — a device that doesn't support clear just skips it */ }
    }

    /// <summary>Ask the portmapper which port the VXI-11 Core program is listening on.</summary>
    private async Task<int> GetCorePortAsync(CancellationToken ct)
    {
        using var pm = new TcpClient { NoDelay = true };
        await Deadline.RunAsync(t => pm.ConnectAsync(Host, PortmapperPort, t).AsTask(), TimeoutMs,
                                $"{Host} did not answer on the RPC portmapper (port {PortmapperPort})",
                                ct).ConfigureAwait(false);

        var args = new List<byte>();
        AddU32(args, VxiCoreProgram);
        AddU32(args, VxiCoreVersion);
        AddU32(args, IpprotoTcp);
        AddU32(args, 0);

        byte[] reply = await CallAsync(pm.GetStream(), PortmapProgram, PortmapVersion,
                                       ProcGetPort, args, ct).ConfigureAwait(false);
        int off = ResultsOffset(reply);
        return (int)Field(reply, off);
    }

    private async Task CreateLinkAsync(CancellationToken ct)
    {
        var args = new List<byte>();
        AddU32(args, 1);   // clientId
        AddU32(args, 0);   // lockDevice = false
        AddU32(args, 0);   // lock_timeout
        AddString(args, DeviceName);

        byte[] reply = await CallAsync(_stream!, VxiCoreProgram, VxiCoreVersion,
                                       ProcCreateLink, args, ct).ConfigureAwait(false);
        int off = ResultsOffset(reply);

        // Create_LinkResp { error, lid, abortPort, maxRecvSize }
        uint error = Field(reply, off);
        if (error != 0)
            throw new IOException($"VXI-11 create_link failed ({DescribeError(error)}).");

        _linkId = (int)Field(reply, off + 4);
        _maxRecvSize = Field(reply, off + 12);
        if (_maxRecvSize is 0 or > 1024 * 1024) _maxRecvSize = 8192; // sanity clamp
        _linked = true;
    }

    // --------------------------------------------------------------- read/write

    public async Task SendAsync(string command, CancellationToken ct = default)
    {
        EnsureConnected();

        var args = new List<byte>();
        AddU32(args, (uint)_linkId);
        AddU32(args, (uint)TimeoutMs);  // io_timeout
        AddU32(args, 0);                // lock_timeout
        AddU32(args, FlagEnd);          // flags: END on last byte
        AddOpaque(args, Encoding.ASCII.GetBytes(NormalizeCommand(command)));

        byte[] reply = await CallAsync(_stream!, VxiCoreProgram, VxiCoreVersion,
                                       ProcDeviceWrite, args, ct).ConfigureAwait(false);
        int off = ResultsOffset(reply);

        uint error = Field(reply, off);
        if (error != 0)
            throw new IOException($"VXI-11 device_write failed ({DescribeError(error)}).");
    }

    public async Task<string> QueryAsync(string command, CancellationToken ct = default)
    {
        await SendAsync(command, ct).ConfigureAwait(false);
        return await ReadResponseAsync(ct).ConfigureAwait(false);
    }

    public async Task<byte[]> QueryBinaryAsync(string command, CancellationToken ct = default)
    {
        await SendAsync(command, ct).ConfigureAwait(false);
        byte[] raw = await ReadRawAsync(ct).ConfigureAwait(false);
        return Ieee4882Block.Parse(raw);
    }

    /// <summary>
    /// Return the instrument to local control (VXI-11 device_local, proc 17) so its
    /// front panel unlocks. VXI-11 remote state lives on the instrument and survives
    /// the socket closing, so this must be sent explicitly — unlike a raw socket, where
    /// disconnecting is enough. Best-effort: never throws.
    /// </summary>
    public async Task ReturnToLocalAsync(CancellationToken ct = default)
    {
        if (!IsConnected || _stream == null) return;
        try
        {
            var args = new List<byte>();
            AddU32(args, (uint)_linkId);
            AddU32(args, 0);                 // flags — do not wait for a lock
            AddU32(args, 0);                 // lock_timeout
            AddU32(args, (uint)TimeoutMs);   // io_timeout
            await CallAsync(_stream, VxiCoreProgram, VxiCoreVersion, ProcDeviceLocal, args, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best effort — if it fails, the user can still press the panel's Local key.
        }
    }

    /// <summary>Full response as text (ASCII, trailing newline stripped).</summary>
    private async Task<string> ReadResponseAsync(CancellationToken ct)
    {
        byte[] raw = await ReadRawAsync(ct).ConfigureAwait(false);
        return Encoding.ASCII.GetString(raw).TrimEnd('\r', '\n');
    }

    /// <summary>Read raw response bytes until the instrument flags END (may span reads).</summary>
    private async Task<byte[]> ReadRawAsync(CancellationToken ct)
    {
        EnsureConnected();

        var data = new List<byte>();
        uint requestSize = Math.Min(_maxRecvSize, 8192);

        // Time since the last byte arrived. Big transfers are bounded by this rather than
        // by a read count — see the empty-read note below.
        var idle = Stopwatch.StartNew();

        while (data.Count <= MaxResponseBytes)
        {
            var args = new List<byte>();
            AddU32(args, (uint)_linkId);
            AddU32(args, requestSize);
            AddU32(args, (uint)TimeoutMs);  // io_timeout
            AddU32(args, 0);                // lock_timeout
            AddU32(args, 0);                // flags
            AddU32(args, 0);                // termChar (unused without termchrset)

            byte[] reply = await CallAsync(_stream!, VxiCoreProgram, VxiCoreVersion,
                                           ProcDeviceRead, args, ct).ConfigureAwait(false);
            int off = ResultsOffset(reply);

            // Device_ReadResp { error, reason, data<> }
            uint error = Field(reply, off);
            if (error == ErrorIoTimeout) break;          // no (more) data — treat as end
            if (error != 0)
                throw new IOException($"VXI-11 device_read failed ({DescribeError(error)}).");

            uint reason = Field(reply, off + 4);
            int len = (int)Field(reply, off + 8);
            if (len < 0 || off + 12 + len > reply.Length)
                throw new IOException(
                    $"Malformed VXI-11 device_read reply: it claims {len} bytes of data and "
                  + $"carries {Math.Max(0, reply.Length - off - 12)}.");
            if (len > 0) { data.AddRange(new ArraySegment<byte>(reply, off + 12, len)); idle.Restart(); }

            if ((reason & ReasonEnd) != 0) break;        // instrument signalled END

            // Do NOT stop on empty, non-END reads. The Rigol sends an empty packet
            // (reason=0, len=0) BEFORE its data, and also pauses part-way through a large
            // transfer while it produces more. Counting empties truncated a 1.15 MB screen
            // dump at exactly 64 KB ("Block claims 1152054 bytes but only 65525 remain"),
            // so wait on the clock instead: give up only once nothing at all has arrived
            // for a full timeout. The short sleep keeps an idle instrument from spinning us.
            if (len == 0)
            {
                if (idle.ElapsedMilliseconds > TimeoutMs) break;
                await Task.Delay(5, ct).ConfigureAwait(false);
            }
        }

        return data.ToArray();
    }

    private static string NormalizeCommand(string command) => command.TrimEnd('\r', '\n') + "\n";

    private void EnsureConnected()
    {
        if (!IsConnected || _stream == null)
            throw new InvalidOperationException("Not connected to an instrument.");
    }

    // ---------------------------------------------------------------- RPC core

    /// <summary>Build a complete, record-marked ONC RPC call frame.</summary>
    /// <param name="xid">
    /// The id this call is stamped with. The reply carries it back, which is the only way to
    /// tell this call's answer from the answer to one that was given up on — see
    /// <see cref="CallAsync"/>.
    /// </param>
    private byte[] BuildCallFrame(int program, int version, int proc, List<byte> args, out uint xid)
    {
        xid = (uint)Interlocked.Increment(ref _xid);

        var body = new List<byte>();
        AddU32(body, xid);
        AddU32(body, 0);              // msg_type = CALL
        AddU32(body, 2);              // rpcvers
        AddU32(body, (uint)program);
        AddU32(body, (uint)version);
        AddU32(body, (uint)proc);
        AddU32(body, 0); AddU32(body, 0);   // cred = AUTH_NULL
        AddU32(body, 0); AddU32(body, 0);   // verf = AUTH_NULL
        body.AddRange(args);

        var frame = new List<byte>();
        AddU32(frame, 0x80000000u | (uint)body.Count);   // last fragment | length
        frame.AddRange(body);
        return frame.ToArray();
    }

    /// <summary>
    /// Send one ONC RPC call and return its own reply payload (record marker stripped).
    ///
    /// **Its own**, which is the whole of the difficulty. Giving up on a slow reply does not
    /// take it off the wire: the instrument answers in its own time, and that answer arrives
    /// while the next call is waiting for a different one. Read blindly, every reply from then
    /// on belongs to the call before it — a Siglent SDM3065X asked for resistance a moment
    /// after a current reading takes longer than five seconds to change function, and after
    /// that one timeout the link reported "device_write failed (I/O timeout)" for a write it
    /// had not answered yet and then threw index errors out of the decoder for everything
    /// else, *IDN? included.
    ///
    /// The xid the call was stamped with is what sorts it out. A reply carrying another one is
    /// the answer to a call already given up on: drop it and read the next. The deadline still
    /// bounds the whole wait, so an instrument that has genuinely stopped talking times out as
    /// before rather than looping.
    /// </summary>
    private async Task<byte[]> CallAsync(NetworkStream stream, int program, int version,
                                         int proc, List<byte> args, CancellationToken ct)
    {
        if (_outOfStep)
            throw new IOException(
                "This VXI-11 link is out of step: a reply was cut short part way through and "
              + "what is left on the connection cannot be matched to a call. Reconnect to the "
              + "instrument.");

        byte[] frame = BuildCallFrame(program, version, proc, args, out uint xid);

        return await Deadline.RunAsync(async t =>
        {
            await stream.WriteAsync(frame, t).ConfigureAwait(false);

            while (true)
            {
                byte[] payload = await ReadReplyAsync(stream, t).ConfigureAwait(false);
                if (payload.Length >= 4 && ReadU32(payload, 0) == xid) return payload;
                // Someone else's answer, arriving late. Nothing here wants it.
            }
        }, TimeoutMs, $"{Host} did not answer {ProcName(proc)}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One whole RPC message — every fragment of it — with the record markers stripped.
    ///
    /// Abandoning this part way through is what the xid check above cannot repair: the bytes
    /// already taken are the front of a message whose tail would then be read as the next
    /// message's record marker, and nothing downstream could tell. That state is recorded
    /// rather than papered over, and the link says so until it is reconnected.
    /// </summary>
    private async Task<byte[]> ReadReplyAsync(NetworkStream stream, CancellationToken ct)
    {
        var payload = new List<byte>();
        bool started = false;
        try
        {
            while (true)
            {
                byte[] mk = await ReadExactAsync(stream, 4, ct).ConfigureAwait(false);
                started = true;
                uint marker = ReadU32(mk, 0);
                bool last = (marker & 0x80000000u) != 0;
                int len = (int)(marker & 0x7FFFFFFF);
                if (len > 0)
                    payload.AddRange(await ReadExactAsync(stream, len, ct).ConfigureAwait(false));
                if (last) return payload.ToArray();
            }
        }
        catch when (started)
        {
            _outOfStep = true;
            throw;
        }
    }

    /// <summary>
    /// The RPC procedure by name, so a timeout says which step went quiet. "device_read"
    /// stalling is an instrument still thinking about a query; "create_link" stalling is one
    /// that never opened the session at all, and the two want different things from the user.
    /// </summary>
    private static string ProcName(int proc) => proc switch
    {
        ProcGetPort      => "the portmapper (get_port)",
        ProcCreateLink   => "create_link",
        ProcDeviceWrite  => "device_write",
        ProcDeviceRead   => "device_read",
        ProcDeviceClear  => "device_clear",
        ProcDeviceLocal  => "device_local",
        ProcDestroyLink  => "destroy_link",
        _                => $"VXI-11 procedure {proc}",
    };

    private async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buf.AsMemory(read, count - read), ct).ConfigureAwait(false);
            }
            catch when (read > 0)
            {
                // Cut short with part of a record marker or a fragment already taken: the
                // bytes left behind cannot be lined up again. Say so rather than read on.
                _outOfStep = true;
                throw;
            }
            if (n <= 0) throw new IOException("Connection closed by instrument.");
            read += n;
        }
        return buf;
    }

    /// <summary>Validate an RPC reply header and return the offset where results begin.</summary>
    private static int ResultsOffset(byte[] reply)
    {
        // xid(0) msg_type(4) reply_stat(8) verf_flavor(12) verf_len(16) [verf body] accept_stat results
        if (reply.Length < 24) throw new IOException("RPC reply too short.");
        if (ReadU32(reply, 4) != 1) throw new IOException("Malformed RPC reply (not a REPLY).");

        uint replyStat = ReadU32(reply, 8);
        if (replyStat != 0) throw new IOException($"RPC call rejected (reply_stat={replyStat}).");

        uint verfLen = ReadU32(reply, 16);
        int off = 20 + (int)((verfLen + 3u) & ~3u);   // verf body is padded to 4 bytes
        if (reply.Length < off + 4) throw new IOException("RPC reply truncated.");

        uint acceptStat = ReadU32(reply, off);
        if (acceptStat != 0) throw new IOException($"RPC call failed (accept_stat={acceptStat}).");

        return off + 4;
    }

    /// <summary>
    /// A 32-bit field out of a reply, or a sentence saying the reply was too short for it.
    ///
    /// Reading past the end used to raise "Index was outside the bounds of the array", which
    /// tells whoever meets it nothing at all about an instrument or a link.
    /// </summary>
    private static uint Field(byte[] reply, int offset)
    {
        if (offset + 4 > reply.Length)
            throw new IOException("Malformed VXI-11 reply: it ends part way through a field.");
        return ReadU32(reply, offset);
    }

    private static string DescribeError(uint code) => code switch
    {
        1 => "syntax error",
        3 => "device not accessible",
        4 => "invalid link identifier",
        5 => "parameter error",
        6 => "channel not established",
        8 => "operation not supported",
        9 => "out of resources",
        11 => "device locked by another link",
        12 => "no lock held",
        15 => "I/O timeout",
        17 => "I/O error",
        21 => "invalid address",
        23 => "abort",
        29 => "channel already established",
        _ => $"error {code}",
    };

    // ------------------------------------------------------------- XDR helpers

    private static void AddU32(List<byte> b, uint v)
    {
        b.Add((byte)(v >> 24));
        b.Add((byte)(v >> 16));
        b.Add((byte)(v >> 8));
        b.Add((byte)v);
    }

    private static uint ReadU32(byte[] b, int off)
        => ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];

    private static void AddOpaque(List<byte> b, byte[] data)
    {
        AddU32(b, (uint)data.Length);
        b.AddRange(data);
        for (int pad = (4 - (data.Length % 4)) % 4; pad > 0; pad--) b.Add(0);
    }

    private static void AddString(List<byte> b, string s) => AddOpaque(b, Encoding.ASCII.GetBytes(s));

    // ------------------------------------------------------------------ teardown

    public void Close()
    {
        if (_linked && _stream != null)
        {
            // Write synchronously with a short socket timeout and don't await replies:
            // Close() runs on the UI thread via Dispose(), and blocking on an async call
            // here would stall the window. The frames are delivered in order before the
            // socket's FIN, so the instrument processes them.
            try
            {
                _stream.WriteTimeout = 500;

                // Best-effort local restore for teardown paths that only Dispose() (e.g.
                // app exit). The interactive Disconnect awaits ReturnToLocalAsync first;
                // sending it again here is harmless (idempotent).
                var localArgs = new List<byte>();
                AddU32(localArgs, (uint)_linkId);
                AddU32(localArgs, 0);              // flags
                AddU32(localArgs, 0);              // lock_timeout
                AddU32(localArgs, (uint)TimeoutMs); // io_timeout
                byte[] localFrame = BuildCallFrame(VxiCoreProgram, VxiCoreVersion, ProcDeviceLocal, localArgs, out _);
                _stream.Write(localFrame, 0, localFrame.Length);

                // Release the link so the instrument doesn't hold it (some allow only one).
                var destroyArgs = new List<byte>();
                AddU32(destroyArgs, (uint)_linkId);
                byte[] destroyFrame = BuildCallFrame(VxiCoreProgram, VxiCoreVersion, ProcDestroyLink, destroyArgs, out _);
                _stream.Write(destroyFrame, 0, destroyFrame.Length);
            }
            catch { /* ignore — we're tearing the socket down regardless */ }
        }

        _linked = false;
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _tcp?.Close(); } catch { /* ignore */ }
        _stream = null;
        _tcp = null;
    }

    public void Dispose() => Close();
}
