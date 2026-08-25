using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;

namespace LabEquipmentController;

/// <summary>Which byte ends a message on the wire. TCP has one answer; RS-232 does not.</summary>
public enum ScpiTerminator
{
    /// <summary>A single line feed, as IEEE 488.2 and every LAN instrument here use.</summary>
    Lf,

    /// <summary>Carriage return then line feed — what a lot of RS-232 instruments expect.</summary>
    CrLf,

    /// <summary>A bare carriage return. Rare, and always a documented choice on the instrument.</summary>
    Cr,
}

/// <summary>
/// The line settings of a serial connection: the things that must already match before a
/// single character gets through, and the reason a serial port cannot be discovered the
/// way a subnet can (SPEC §17). A TCP instrument answers or does not; a serial one at the
/// wrong baud rate answers with rubbish, which is worse.
///
/// Defaults are 9600-8-N-1 with no flow control, which is what most instruments ship with
/// and what their manuals print beside the RS-232 connector. Everything is overridable
/// from the address itself — <c>serial://COM3?baud=115200&amp;parity=even</c> — so no
/// setting here needs a UI to reach.
/// </summary>
public sealed record SerialSettings
{
    /// <summary>Bits per second. 9600 unless the instrument was set otherwise.</summary>
    public int BaudRate { get; init; } = 9600;

    /// <summary>Data bits per character, 5 to 8.</summary>
    public int DataBits { get; init; } = 8;

    public Parity Parity { get; init; } = Parity.None;

    public StopBits StopBits { get; init; } = StopBits.One;

    /// <summary>Flow control. Instruments that need it say so; most do not.</summary>
    public Handshake Handshake { get; init; } = Handshake.None;

    /// <summary>
    /// What to put after a command. A LAN instrument is always <c>\n</c>; over RS-232 a
    /// good many want <c>\r\n</c>, and getting it wrong looks exactly like a dead port.
    /// Replies are read to the line feed either way, with carriage returns discarded, so
    /// this only decides what is written.
    /// </summary>
    public ScpiTerminator Terminator { get; init; } = ScpiTerminator.Lf;

    /// <summary>Everything at its default: 9600-8-N-1, no flow control, LF.</summary>
    public static readonly SerialSettings Default = new();

    /// <summary>
    /// Read the query part of a <c>serial://</c> address — <c>baud=115200&amp;parity=even</c>,
    /// in any order, with a bare number accepted as the baud rate so
    /// <c>serial://COM3?115200</c> means what it looks like. An empty query is
    /// <see cref="Default"/>, not an error.
    /// </summary>
    public static bool TryParse(string? query, out SerialSettings settings, out string error)
    {
        settings = Default;
        error = "";

        query = query?.Trim().TrimStart('?');
        if (string.IsNullOrEmpty(query)) return true;

        var result = Default;

        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string item = pair.Trim();
            if (item.Length == 0) continue;

            int eq = item.IndexOf('=');
            string key = (eq < 0 ? item : item[..eq]).Trim();
            string value = (eq < 0 ? "" : item[(eq + 1)..]).Trim();

            // A bare number is the baud rate: the one setting people actually change, and
            // the only one that could be written alone without ambiguity.
            if (eq < 0)
            {
                if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bare))
                {
                    error = $"'{key}' is not a serial setting. Expected baud, databits, parity, stopbits, flow or term.";
                    return false;
                }
                key = "baud";
                value = bare.ToString(CultureInfo.InvariantCulture);
            }

            switch (key.ToLowerInvariant())
            {
                case "baud" or "baudrate":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int baud) || baud <= 0)
                    {
                        error = $"'{value}' is not a baud rate.";
                        return false;
                    }
                    result = result with { BaudRate = baud };
                    break;

                case "databits" or "data":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bits) || bits is < 5 or > 8)
                    {
                        error = $"'{value}' is not a data-bit count (5 to 8).";
                        return false;
                    }
                    result = result with { DataBits = bits };
                    break;

                case "parity":
                    if (!TryParity(value, out Parity parity))
                    {
                        error = $"'{value}' is not a parity. Expected none, odd, even, mark or space.";
                        return false;
                    }
                    result = result with { Parity = parity };
                    break;

                case "stopbits" or "stop":
                    if (!TryStopBits(value, out StopBits stop))
                    {
                        error = $"'{value}' is not a stop-bit count. Expected 1, 1.5 or 2.";
                        return false;
                    }
                    result = result with { StopBits = stop };
                    break;

                case "flow" or "handshake":
                    if (!TryHandshake(value, out Handshake flow))
                    {
                        error = $"'{value}' is not a flow control. Expected none, rtscts, xonxoff or both.";
                        return false;
                    }
                    result = result with { Handshake = flow };
                    break;

                case "term" or "terminator" or "eol":
                    if (!TryTerminator(value, out ScpiTerminator term))
                    {
                        error = $"'{value}' is not a terminator. Expected lf, crlf or cr.";
                        return false;
                    }
                    result = result with { Terminator = term };
                    break;

                default:
                    error = $"'{key}' is not a serial setting. Expected baud, databits, parity, stopbits, flow or term.";
                    return false;
            }
        }

        settings = result;
        return true;
    }

    private static bool TryParity(string text, out Parity parity)
    {
        switch (text.ToLowerInvariant())
        {
            case "none" or "n": parity = Parity.None; return true;
            case "odd" or "o": parity = Parity.Odd; return true;
            case "even" or "e": parity = Parity.Even; return true;
            case "mark" or "m": parity = Parity.Mark; return true;
            case "space" or "s": parity = Parity.Space; return true;
            default: parity = Parity.None; return false;
        }
    }

    private static bool TryStopBits(string text, out StopBits stop)
    {
        switch (text.ToLowerInvariant())
        {
            case "1" or "one": stop = StopBits.One; return true;
            case "1.5" or "onepointfive": stop = StopBits.OnePointFive; return true;
            case "2" or "two": stop = StopBits.Two; return true;
            default: stop = StopBits.One; return false;
        }
    }

    private static bool TryHandshake(string text, out Handshake handshake)
    {
        switch (text.ToLowerInvariant())
        {
            case "none" or "off": handshake = Handshake.None; return true;
            case "rtscts" or "rts" or "hardware" or "hw": handshake = Handshake.RequestToSend; return true;
            case "xonxoff" or "xon" or "software" or "sw": handshake = Handshake.XOnXOff; return true;
            case "both" or "rtsxon": handshake = Handshake.RequestToSendXOnXOff; return true;
            default: handshake = Handshake.None; return false;
        }
    }

    private static bool TryTerminator(string text, out ScpiTerminator term)
    {
        switch (text.ToLowerInvariant())
        {
            case "lf" or "n" or "\\n": term = ScpiTerminator.Lf; return true;
            case "crlf" or "rn" or "\\r\\n": term = ScpiTerminator.CrLf; return true;
            case "cr" or "r" or "\\r": term = ScpiTerminator.Cr; return true;
            default: term = ScpiTerminator.Lf; return false;
        }
    }

    /// <summary>The characters written after every command.</summary>
    public string TerminatorText => Terminator switch
    {
        ScpiTerminator.CrLf => "\r\n",
        ScpiTerminator.Cr => "\r",
        _ => "\n",
    };

    /// <summary>
    /// Only the settings that are not at their default, in the spelling
    /// <see cref="TryParse"/> reads — so a canonical address stays as short as what was
    /// typed, and an all-default connection carries no query at all.
    /// </summary>
    public string Query
    {
        get
        {
            var parts = new List<string>();
            if (BaudRate != Default.BaudRate) parts.Add($"baud={BaudRate}");
            if (DataBits != Default.DataBits) parts.Add($"databits={DataBits}");
            if (Parity != Default.Parity) parts.Add($"parity={Parity.ToString().ToLowerInvariant()}");
            if (StopBits != Default.StopBits) parts.Add($"stopbits={StopBitsText}");
            if (Handshake != Default.Handshake) parts.Add($"flow={HandshakeText}");
            if (Terminator != Default.Terminator) parts.Add($"term={Terminator.ToString().ToLowerInvariant()}");
            return string.Join("&", parts);
        }
    }

    private string StopBitsText => StopBits switch
    {
        StopBits.OnePointFive => "1.5",
        StopBits.Two => "2",
        _ => "1",
    };

    private string HandshakeText => Handshake switch
    {
        Handshake.RequestToSend => "rtscts",
        Handshake.XOnXOff => "xonxoff",
        Handshake.RequestToSendXOnXOff => "both",
        _ => "none",
    };

    /// <summary>
    /// The engineer's shorthand — <c>9600-8-N-1</c> — with flow control and a non-default
    /// terminator appended only when there is something to say.
    /// </summary>
    public override string ToString()
    {
        string parityLetter = Parity switch
        {
            Parity.Odd => "O",
            Parity.Even => "E",
            Parity.Mark => "M",
            Parity.Space => "S",
            _ => "N",
        };

        string text = $"{BaudRate}-{DataBits}-{parityLetter}-{StopBitsText}";
        if (Handshake != Handshake.None) text += $" {HandshakeText}";
        if (Terminator != ScpiTerminator.Lf) text += $" {Terminator.ToString().ToLowerInvariant()}";
        return text;
    }
}
