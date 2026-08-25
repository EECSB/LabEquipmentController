using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// SCPI over an RS-232 serial port — <c>COM3</c> on Windows, <c>/dev/ttyUSB0</c> or
/// <c>/dev/cu.usbserial-*</c> elsewhere, including the USB-to-serial adapter most benches
/// actually use.
///
/// It is a short file on purpose. SCPI over serial is line-based in exactly the way SCPI
/// over a raw socket is, so everything about framing a message is
/// <see cref="ScpiFraming"/>'s, shared with <see cref="ScpiClient"/>, and everything above
/// the transport — sessions, both script runners, capture, the catalogs — cannot tell the
/// difference. What is genuinely different is here: line settings that must match before a
/// single character gets through, a terminator that is not always a bare line feed, and no
/// connection whose closing means anything to the instrument.
/// </summary>
public sealed class SerialInstrumentClient : IInstrumentClient
{
    private SerialPort? _port;
    private ScpiFraming? _wire;

    /// <summary>The port name — <c>COM3</c>, <c>/dev/ttyUSB0</c>. Named Host by the interface, which is LAN-shaped.</summary>
    public string Host { get; }

    /// <summary>Baud rate, framing, flow control and terminator.</summary>
    public SerialSettings Settings { get; }

    public string Description => $"serial ({Host}, {Settings})";

    /// <summary>
    /// How long to wait for a reply, in milliseconds. On a serial port this is enforced by
    /// the driver as well, as the longest gap between bytes — which is the more useful
    /// reading of it: a screenshot at 9600 baud takes minutes to arrive and is not late.
    /// </summary>
    public int TimeoutMs
    {
        get => _timeoutMs;
        set
        {
            _timeoutMs = value;
            if (_port is { IsOpen: true })
            {
                _port.ReadTimeout = value;
                _port.WriteTimeout = value;
            }
        }
    }
    private int _timeoutMs = 5000;

    public bool IsConnected => _port?.IsOpen == true;

    public SerialInstrumentClient(string portName, SerialSettings? settings = null)
    {
        Host = portName;
        Settings = settings ?? SerialSettings.Default;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Close();

        var port = new SerialPort(Host, Settings.BaudRate, Settings.Parity,
                                  Settings.DataBits, Settings.StopBits)
        {
            Handshake = Settings.Handshake,
            ReadTimeout = TimeoutMs,
            WriteTimeout = TimeoutMs,
        };

        // Most instruments and nearly every USB-to-serial bridge want to see these
        // asserted before they will talk; with hardware flow control the port drives RTS
        // itself and setting it here throws.
        port.DtrEnable = true;
        if (Settings.Handshake is Handshake.None or Handshake.XOnXOff) port.RtsEnable = true;

        // Open() is synchronous and can sit on a driver for a moment, so it goes to the
        // pool rather than the caller's thread — which on the desktop is the UI's.
        //
        // The three ways it fails are worth translating. Left alone they surface as "Could
        // not find file 'COM99'", which is true of a serial port only in the sense that
        // Windows opens one like a file, and is not what went wrong from where the user is
        // standing.
        try
        {
            await Deadline.RunAsync(t => Task.Run(port.Open, t), TimeoutMs,
                                    $"{Host} did not open", ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            port.Dispose();
            throw new IOException($"{Host} could not be opened: this machine has no serial port of that name.", ex);
        }
        catch (ArgumentException ex)
        {
            port.Dispose();
            throw new IOException($"'{Host}' is not the name of a serial port. Expected something like COM3 or /dev/ttyUSB0.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            port.Dispose();
            throw new IOException($"{Host} could not be opened: it is already in use by another program.", ex);
        }
        catch
        {
            port.Dispose();
            throw;
        }

        _port = port;
        _wire = new ScpiFraming(port.BaseStream, () => port.BytesToRead > 0, Host);

        // Whatever the last session left in the adapter's buffer is not an answer to
        // anything we are about to ask.
        try { port.DiscardInBuffer(); } catch { /* ignore */ }
        _wire.Drain();
    }

    /// <summary>Write a command (no response expected).</summary>
    public async Task SendAsync(string command, CancellationToken ct = default)
    {
        EnsureConnected();
        _wire!.Drain();   // discard any late reply from a previous slow query
        await _wire.WriteAsync(command, Settings.TerminatorText, ct).ConfigureAwait(false);
    }

    /// <summary>Write a query and read one line of response.</summary>
    public async Task<string> QueryAsync(string command, CancellationToken ct = default)
    {
        await SendAsync(command, ct).ConfigureAwait(false);
        EnsureConnected();
        return await _wire!.ReadLineAsync(TimeoutMs, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write a query and read an IEEE 488.2 binary block response (waveforms, screenshots),
    /// returning just the data payload.
    ///
    /// Unlike the raw-socket path this has no overall deadline, and that is deliberate
    /// rather than an omission. A total timeout is the only tool a socket has, because an
    /// async read on a <c>NetworkStream</c> ignores its own <c>ReadTimeout</c>; a serial
    /// port does not ignore it, so the bound here is the gap between bytes. Which is the
    /// question actually worth asking — 100 KB of screenshot at 9600 baud is a minute and
    /// three quarters of perfectly healthy transfer, and no fixed total that allows for it
    /// would still catch an instrument that has stopped talking.
    /// </summary>
    public async Task<byte[]> QueryBinaryAsync(string command, CancellationToken ct = default)
    {
        await SendAsync(command, ct).ConfigureAwait(false);
        EnsureConnected();
        return await _wire!.ReadBlockAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Hand the front panel back. There is no connection here whose closing says anything
    /// to the instrument — that is what a raw socket relies on and what RS-232 does not
    /// have — so the request has to be made in band. <c>SYSTem:LOCal</c> is SCPI-99's own
    /// answer for interfaces with no bus-level Go To Local, which is precisely this one,
    /// and 12 of the 36 catalogs here document it. On an instrument that does not, it costs
    /// one entry in an error queue that is about to be disconnected from.
    /// </summary>
    public async Task ReturnToLocalAsync(CancellationToken ct = default)
    {
        if (!IsConnected) return;
        try { await SendAsync("SYSTem:LOCal", ct).ConfigureAwait(false); }
        catch { /* best effort, exactly as the interface promises */ }
    }

    private void EnsureConnected()
    {
        if (_wire == null || _port is not { IsOpen: true })
            throw new InvalidOperationException("Not connected to an instrument.");
    }

    public void Close()
    {
        try { _port?.Close(); } catch { /* ignore */ }
        try { _port?.Dispose(); } catch { /* ignore */ }
        _wire = null;
        _port = null;
    }

    public void Dispose() => Close();
}
