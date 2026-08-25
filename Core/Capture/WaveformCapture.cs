using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace LabEquipmentController;

/// <summary>One decoded waveform sample: time (seconds) and voltage (volts).</summary>
public readonly record struct WaveformSample(double Time, double Voltage);

/// <summary>
/// A captured oscilloscope trace decoded from a Rigol BYTE-format
/// <c>:WAVeform:DATA?</c> block plus its <c>:WAVeform:PREamble?</c> scaling.
/// </summary>
public sealed class WaveformCapture
{
    public IReadOnlyList<WaveformSample> Samples { get; init; } = Array.Empty<WaveformSample>();

    /// <summary>Time between samples, in seconds (the preamble's XINCrement).</summary>
    public double XIncrement { get; init; }

    /// <summary>
    /// Decode BYTE-format waveform data with the Rigol 10-field preamble
    /// (format,type,points,count,xinc,xorig,xref,yinc,yorig,yref). Each data byte is one
    /// sample: <c>volts = (raw - yref - yorig) * yinc</c>, <c>time = xorig + (i - xref) * xinc</c>.
    /// </summary>
    public static WaveformCapture FromRigol(string preamble, byte[] data)
    {
        double[] p = TenFieldPreamble(preamble);
        double xinc = p[4], xorig = p[5], xref = p[6];
        double yinc = p[7], yorig = p[8], yref = p[9];

        var samples = new WaveformSample[data.Length];
        for (int i = 0; i < data.Length; i++)
            samples[i] = new WaveformSample(
                xorig + (i - xref) * xinc,
                (data[i] - yref - yorig) * yinc);

        return new WaveformCapture { Samples = samples, XIncrement = xinc };
    }

    /// <summary>
    /// Decode BYTE-format data with the Keysight 10-field preamble. Same fields in the same
    /// order as <see cref="FromRigol"/>, and deliberately not the same arithmetic — the
    /// InfiniiVision guide states it twice, in prose and in its own worked example:
    ///
    ///     voltage = [(data value - yreference) * yincrement] + yorigin
    ///     time    = xorigin + (i * xincrement)
    ///
    /// Rigol subtracts yorigin alongside yreference and scales the pair; Keysight adds it
    /// afterwards, in volts. On a trace sitting away from centre screen the two disagree by
    /// the offset, and both produce a perfectly plausible-looking plot.
    /// </summary>
    public static WaveformCapture FromKeysight(string preamble, byte[] data)
    {
        double[] p = TenFieldPreamble(preamble);
        double xinc = p[4], xorig = p[5];
        double yinc = p[7], yorig = p[8], yref = p[9];

        var samples = new WaveformSample[data.Length];
        for (int i = 0; i < data.Length; i++)
            samples[i] = new WaveformSample(
                xorig + i * xinc,
                ((data[i] - yref) * yinc) + yorig);

        return new WaveformCapture { Samples = samples, XIncrement = xinc };
    }

    /// <summary>
    /// Decode a Tektronix CURVe? block against the WFMOutpre scaling fields. The programmer
    /// manual gives both formulae explicitly:
    ///
    ///     Xn = XZEro + XINcr * (n - PT_Off)
    ///     Yn = ((curve_in_dl - YOFf) * YMUlt) + YZEro
    ///
    /// A worked example elsewhere in the same manual drops YOFf, because it happens to be
    /// zero there. It is not always zero — it is the vertical position in digitizing levels —
    /// so the general form is used.
    /// </summary>
    /// <param name="width">Bytes per sample, from WFMOutpre:BYT_Nr? (1 or 2).</param>
    /// <param name="signed">RI (signed) rather than RP (unsigned), from WFMOutpre:BN_Fmt?.</param>
    /// <param name="msbFirst">MSB byte order, from WFMOutpre:BYT_Or?.</param>
    public static WaveformCapture FromTektronix(
        byte[] data, int width, bool signed, bool msbFirst,
        double xincr, double xzero, double ptOff, double ymult, double yoff, double yzero)
    {
        if (width is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(width));

        int count = data.Length / width;
        var samples = new WaveformSample[count];
        for (int i = 0; i < count; i++)
        {
            double raw = width == 1
                ? (signed ? (sbyte)data[i] : data[i])
                : Word(data, i * 2, signed, msbFirst);

            samples[i] = new WaveformSample(
                xzero + xincr * (i - ptOff),
                ((raw - yoff) * ymult) + yzero);
        }
        return new WaveformCapture { Samples = samples, XIncrement = xincr };
    }

    /// <summary>
    /// Decode an R&amp;S CHANnel&lt;m&gt;:DATA? response read in ASCII format, where the
    /// instrument has already done the vertical arithmetic and returns volts.
    ///
    /// The timebase comes from CHANnel&lt;m&gt;:DATA:HEADer?, whose four fields are XStart in
    /// seconds, XStop in seconds, the record length in samples, and the number of values per
    /// sample. Samples are spaced evenly across the interval, so the increment is derived
    /// rather than read: the header carries no XINCrement field.
    /// </summary>
    public static WaveformCapture FromRohdeAscii(string header, string values)
    {
        string[] h = (header ?? "").Split(',');
        if (h.Length < 3)
            throw new FormatException($"Expected at least 3 header fields, got {h.Length}: '{header}'");

        double xstart = D(h[0]), xstop = D(h[1]);

        double[] volts = (values ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => D(s))
            .ToArray();

        double xinc = volts.Length > 1 ? (xstop - xstart) / (volts.Length - 1) : 0;

        var samples = new WaveformSample[volts.Length];
        for (int i = 0; i < volts.Length; i++)
            samples[i] = new WaveformSample(xstart + i * xinc, volts[i]);

        return new WaveformCapture { Samples = samples, XIncrement = xinc };
    }

    /// <summary>
    /// Decode a Siglent :WAVeform:DATA? block against the binary descriptor returned by
    /// :WAVeform:PREamble?. The guide documents both the byte offsets and the arithmetic:
    ///
    ///     voltage = code * (vdiv / code_per_div) - voffset
    ///     time    = -delay - (timebase * grid / 2) + index * interval
    ///
    /// with vdiv at 156, voffset at 160, code_per_div at 164, interval at 176 and delay at
    /// 180. Codes are signed: a byte above the centre code is negative.
    /// </summary>
    /// <param name="secondsPerDiv">
    /// The horizontal scale. Held in the descriptor as a table index rather than a value, so
    /// it is read with :TIMebase:SCALe? instead of decoded — an index is only as good as the
    /// table behind it, and the table differs between models.
    /// </param>
    /// <param name="grid">Horizontal divisions: 10 on the bench models, 12 on the handhelds.</param>
    public static WaveformCapture FromSiglent(byte[] preamble, byte[] data,
                                              double secondsPerDiv, int grid = 10)
    {
        const int VDiv = 156, VOffset = 160, CodePerDiv = 164, Interval = 176, Delay = 180;
        if (preamble.Length < Delay + 8)
            throw new FormatException(
                $"Siglent preamble is {preamble.Length} bytes; the scaling fields need at least {Delay + 8}.");

        float vdiv = BitConverter.ToSingle(preamble, VDiv);
        float voffset = BitConverter.ToSingle(preamble, VOffset);
        float codePerDiv = BitConverter.ToSingle(preamble, CodePerDiv);
        float interval = BitConverter.ToSingle(preamble, Interval);
        double delay = BitConverter.ToDouble(preamble, Delay);

        if (codePerDiv == 0)
            throw new FormatException("Siglent preamble reports zero codes per division.");

        double t0 = -delay - (secondsPerDiv * grid / 2.0);

        var samples = new WaveformSample[data.Length];
        for (int i = 0; i < data.Length; i++)
            samples[i] = new WaveformSample(
                t0 + i * interval,
                (sbyte)data[i] * (vdiv / codePerDiv) - voffset);

        return new WaveformCapture { Samples = samples, XIncrement = interval };
    }

    /// <summary>The ten comma-separated fields Rigol and Keysight both use.</summary>
    private static double[] TenFieldPreamble(string preamble)
    {
        string[] p = (preamble ?? "").Split(',');
        if (p.Length < 10)
            throw new FormatException($"Expected 10 preamble fields, got {p.Length}: '{preamble}'");
        return p.Take(10).Select(D).ToArray();
    }

    private static double Word(byte[] data, int at, bool signed, bool msbFirst)
    {
        int hi = msbFirst ? data[at] : data[at + 1];
        int lo = msbFirst ? data[at + 1] : data[at];
        int raw = (hi << 8) | lo;
        return signed ? (short)raw : raw;
    }

    /// <summary>Two-column CSV (Time (s), Voltage (V)) with a header row.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.Append("Time (s),Voltage (V)\r\n");
        foreach (WaveformSample s in Samples)
            sb.Append(s.Time.ToString("g9", CultureInfo.InvariantCulture)).Append(',')
              .Append(s.Voltage.ToString("g9", CultureInfo.InvariantCulture)).Append("\r\n");
        return sb.ToString();
    }

    private static double D(string s) =>
        double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
}
