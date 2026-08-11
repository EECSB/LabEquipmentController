namespace LabEquipmentController;

/// <summary>
/// How a scope hands a trace back. Not a style choice — each vendor uses a different command
/// tree, a different way of describing the scaling, and a different arithmetic to turn a
/// stored sample into volts. Getting any of the three wrong produces a plot rather than an
/// error, which is why each one is transcribed from the vendor's guide and named here rather
/// than guessed at from the shape of the data.
/// </summary>
public enum WaveformDialect
{
    /// <summary>No documented way to read samples back over the wire.</summary>
    None = 0,

    /// <summary>
    /// Rigol :WAVeform tree. Ten-field comma-separated preamble; a stored sample converts
    /// with <c>volts = (raw - yreference - yorigin) * yincrement</c>.
    /// </summary>
    Rigol,

    /// <summary>
    /// Keysight :WAVeform tree. The same ten preamble fields in the same order as Rigol —
    /// Rigol followed Keysight here — but <b>not</b> the same arithmetic:
    /// <c>volts = ((raw - yreference) * yincrement) + yorigin</c>. Using Rigol's formula on a
    /// Keysight puts the trace at the wrong offset and nothing reports it.
    /// </summary>
    Keysight,

    /// <summary>
    /// Tektronix CURVe? with the scaling read out of WFMOutpre one field at a time. There is
    /// no combined preamble query worth using: WFMOutpre? returns them positionally and the
    /// layout varies by model, while the individual queries are unambiguous.
    /// </summary>
    Tektronix,

    /// <summary>
    /// Rohde &amp; Schwarz CHANnel&lt;m&gt;:DATA?, read in ASCII. The instrument returns
    /// volts directly, so there is no vertical scaling to get wrong; the timebase comes from
    /// CHANnel&lt;m&gt;:DATA:HEADer?. Slower than binary and chosen for exactly that reason.
    /// </summary>
    RohdeAscii,

    /// <summary>
    /// Siglent :WAVeform tree. The preamble is not text but a fixed-layout binary descriptor,
    /// and the scaling values sit at documented byte offsets inside it.
    /// </summary>
    Siglent,
}
