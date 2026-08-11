using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// Reads a trace off a scope, in whichever dialect it speaks.
///
/// Lives in Core rather than in the console so that the command sequences can be exercised
/// against a fake client. That matters more here than almost anywhere else in the app: a
/// wrong command produces an error a user can see, but a wrong *scaling* produces a plot,
/// and a plot that is quietly at the wrong offset or the wrong timebase is exactly the kind
/// of confidently-wrong output this codebase keeps refusing to ship (SPEC §10).
///
/// Every command sent here appears in that family's catalog, transcribed from the vendor's
/// programming guide, and is covered by CatalogCoverageTests.
/// </summary>
public static class WaveformReader
{
    /// <summary>Read channel 1 in the given dialect.</summary>
    public static Task<WaveformCapture> ReadAsync(
        IInstrumentClient client, WaveformDialect dialect, int channel = 1,
        CancellationToken ct = default)
        => dialect switch
        {
            WaveformDialect.Rigol      => RigolAsync(client, channel, ct),
            WaveformDialect.Keysight   => KeysightAsync(client, channel, ct),
            WaveformDialect.Tektronix  => TektronixAsync(client, channel, ct),
            WaveformDialect.RohdeAscii => RohdeAsync(client, channel, ct),
            WaveformDialect.Siglent    => SiglentAsync(client, channel, ct),
            _ => throw new NotSupportedException(
                     "This instrument has no documented way to read samples back."),
        };

    // ------------------------------------------------------------------------------ rigol

    private static async Task<WaveformCapture> RigolAsync(
        IInstrumentClient c, int ch, CancellationToken ct)
    {
        await c.SendAsync($":WAVeform:SOURce CHANnel{ch}", ct);
        await c.SendAsync(":WAVeform:MODE NORMal", ct);
        await c.SendAsync(":WAVeform:FORMat BYTE", ct);
        string preamble = await c.QueryAsync(":WAVeform:PREamble?", ct);
        byte[] data = await c.QueryBinaryAsync(":WAVeform:DATA?", ct);
        return WaveformCapture.FromRigol(preamble, data);
    }

    // --------------------------------------------------------------------------- keysight

    private static async Task<WaveformCapture> KeysightAsync(
        IInstrumentClient c, int ch, CancellationToken ct)
    {
        await c.SendAsync($":WAVeform:SOURce CHANnel{ch}", ct);
        await c.SendAsync(":WAVeform:FORMat BYTE", ct);
        // NORMal caps the transfer at what is on screen. RAW needs the acquisition stopped
        // and is a different conversation; the screen record is what the button promises.
        await c.SendAsync(":WAVeform:POINts:MODE NORMal", ct);
        string preamble = await c.QueryAsync(":WAVeform:PREamble?", ct);
        byte[] data = await c.QueryBinaryAsync(":WAVeform:DATA?", ct);
        return WaveformCapture.FromKeysight(preamble, data);
    }

    // -------------------------------------------------------------------------- tektronix

    private static async Task<WaveformCapture> TektronixAsync(
        IInstrumentClient c, int ch, CancellationToken ct)
    {
        await c.SendAsync($"DATa:SOUrce CH{ch}", ct);
        await c.SendAsync("DATa:ENCdg RIBinary", ct);
        await c.SendAsync("DATa:WIDth 1", ct);

        // The scaling is read field by field. WFMOutpre? returns them all at once but
        // positionally, and the layout differs between the models this catalog covers.
        int width    = (int)await NumAsync(c, "WFMOutpre:BYT_Nr?", ct);
        string fmt   = (await c.QueryAsync("WFMOutpre:BN_Fmt?", ct)).Trim();
        string order = (await c.QueryAsync("WFMOutpre:BYT_Or?", ct)).Trim();

        double xincr = await NumAsync(c, "WFMOutpre:XINcr?", ct);
        double xzero = await NumAsync(c, "WFMOutpre:XZEro?", ct);
        double ptOff = await NumAsync(c, "WFMOutpre:PT_Off?", ct);
        double ymult = await NumAsync(c, "WFMOutpre:YMUlt?", ct);
        double yoff  = await NumAsync(c, "WFMOutpre:YOFf?", ct);
        double yzero = await NumAsync(c, "WFMOutpre:YZEro?", ct);

        byte[] data = await c.QueryBinaryAsync("CURVe?", ct);

        return WaveformCapture.FromTektronix(
            data, width,
            signed: fmt.StartsWith("RI", StringComparison.OrdinalIgnoreCase),
            msbFirst: order.StartsWith("MSB", StringComparison.OrdinalIgnoreCase),
            xincr, xzero, ptOff, ymult, yoff, yzero);
    }

    // ------------------------------------------------------------------------------ r&s

    private static async Task<WaveformCapture> RohdeAsync(
        IInstrumentClient c, int ch, CancellationToken ct)
    {
        await c.SendAsync("FORMat:DATA ASC", ct);
        string header = await c.QueryAsync($"CHANnel{ch}:DATA:HEADer?", ct);
        string values = await c.QueryAsync($"CHANnel{ch}:DATA?", ct);
        return WaveformCapture.FromRohdeAscii(header, values);
    }

    // -------------------------------------------------------------------------- siglent

    private static async Task<WaveformCapture> SiglentAsync(
        IInstrumentClient c, int ch, CancellationToken ct)
    {
        await c.SendAsync($":WAVeform:SOURce C{ch}", ct);
        await c.SendAsync(":WAVeform:STARt 0", ct);

        byte[] preamble = await c.QueryBinaryAsync(":WAVeform:PREamble?", ct);
        // The descriptor holds the timebase as an index into a per-model table rather than
        // as a value, so it is asked for directly instead.
        double secPerDiv = await NumAsync(c, ":TIMebase:SCALe?", ct);
        byte[] data = await c.QueryBinaryAsync(":WAVeform:DATA?", ct);

        return WaveformCapture.FromSiglent(preamble, data, secPerDiv);
    }

    // ---------------------------------------------------------------------------- shared

    /// <summary>
    /// Query a number, tolerating the header some instruments echo back. A Tektronix with
    /// HEADer ON answers WFMOutpre:YMUlt? with ":WFMOUTPRE:YMULT 4.0000E-3", and parsing
    /// that as a bare number throws.
    /// </summary>
    private static async Task<double> NumAsync(IInstrumentClient c, string query, CancellationToken ct)
    {
        string reply = (await c.QueryAsync(query, ct)).Trim();

        int space = reply.LastIndexOf(' ');
        if (space >= 0) reply = reply[(space + 1)..];

        if (!double.TryParse(reply, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            throw new FormatException($"{query} returned '{reply}', which is not a number.");
        return v;
    }
}
