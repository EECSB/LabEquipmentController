using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace LabEquipmentController;

/// <summary>A run of consecutive addresses, inclusive at both ends.</summary>
public sealed record HostSpan(IPAddress First, IPAddress Last)
{
    /// <summary>How many addresses this covers. <c>long</c> because a mistyped prefix can
    /// cover the whole of IPv4, and that must be reportable rather than overflow to nonsense.</summary>
    public long Count => (long)HostRange.ToUInt(Last) - HostRange.ToUInt(First) + 1;

    public override string ToString()
        => First.Equals(Last) ? First.ToString() : $"{First}-{Last}";
}

/// <summary>
/// A slice of a subnet to scan, written the way someone would say it out loud.
///
/// Scanning a whole /24 costs a few seconds and scanning a /16 costs minutes, but the real
/// reason this exists is smaller than speed: a bench sits at a handful of known addresses,
/// and probing the other two hundred and fifty touches printers, cameras, PLCs and whatever
/// else shares the lab network. A range says "look here" and nowhere else.
///
/// Accepted, comma- or space-separated, in any mixture:
///
/// <list type="bullet">
/// <item><c>192.168.1.50</c> — one address</item>
/// <item><c>192.168.1.20-192.168.1.60</c> — a span, written out</item>
/// <item><c>192.168.1.20-60</c> — the same span, ending at a last octet</item>
/// <item><c>192.168.1.0/28</c> — a block, network and broadcast excluded as usual</item>
/// <item><c>20-60</c> or <c>.20-.60</c> — a span in the subnet of the selected interface</item>
/// </list>
///
/// The bare forms are why a context address is passed in: the field sits beside the
/// interface picker, and "20-60" is what someone types when the interface already says
/// which /24 they mean.
/// </summary>
public sealed class HostRange
{
    private static readonly char[] Separators = { ',', ';', ' ', '\t' };

    public IReadOnlyList<HostSpan> Spans { get; }

    private HostRange(IReadOnlyList<HostSpan> spans) => Spans = spans;

    /// <summary>Addresses covered, before any cap. Overlapping spans are counted twice.</summary>
    public long Count => Spans.Sum(s => s.Count);

    public override string ToString() => string.Join(", ", Spans);

    /// <summary>
    /// Read a range specification.
    /// </summary>
    /// <param name="text">What the user typed. Empty is not a range — the caller decides
    /// what "nothing" means, which here is the whole subnet.</param>
    /// <param name="context">
    /// An address whose first three octets fill in the bare forms — normally the selected
    /// interface's own address. Null makes those forms an error rather than a guess.
    /// </param>
    /// <param name="error">Why it could not be read, phrased for someone to act on.</param>
    public static bool TryParse(string? text, IPAddress? context,
                                out HostRange? range, out string error)
    {
        range = null;
        error = "";

        string[] parts = (text ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "Enter an address, a range such as 192.168.1.20-60, or leave it empty "
                  + "to scan the whole subnet.";
            return false;
        }

        var spans = new List<HostSpan>();
        foreach (string part in parts)
        {
            if (!TryParsePart(part, context, out HostSpan? span, out error)) return false;
            spans.Add(span!);
        }

        range = new HostRange(spans);
        return true;
    }

    /// <summary>
    /// The addresses themselves, in order, without duplicates.
    ///
    /// Overlapping spans are a normal thing to type — "192.168.1.20-60, 192.168.1.50" is
    /// someone adding an address they were not sure was already covered — and probing one
    /// host twice is not merely wasteful: the instruments this app talks to accept a single
    /// connection at a time (SPEC §13).
    /// </summary>
    public List<IPAddress> Enumerate(int maxHosts, out bool capped)
    {
        var seen = new HashSet<uint>();
        var hosts = new List<IPAddress>();

        foreach (HostSpan span in Spans)
        {
            uint first = ToUInt(span.First), last = ToUInt(span.Last);
            for (uint a = first; ; a++)
            {
                if (!seen.Contains(a))
                {
                    // The cap is tested against an address that would actually be added, so
                    // a range landing exactly on it is full rather than truncated, and a
                    // trailing duplicate does not report a truncation that never happened.
                    if (hosts.Count >= maxHosts) { capped = true; return hosts; }
                    seen.Add(a);
                    hosts.Add(FromUInt(a));
                }
                if (a == last) break;   // tested here, not in the for, so 255.255.255.255 terminates
            }
        }

        capped = false;
        return hosts;
    }

    // ------------------------------------------------------------------------ parsing

    private static bool TryParsePart(string part, IPAddress? context,
                                     out HostSpan? span, out string error)
    {
        span = null;
        error = "";

        int slash = part.IndexOf('/');
        if (slash >= 0) return TryParseCidr(part, slash, context, out span, out error);

        int dash = part.IndexOf('-');
        if (dash < 0)
        {
            if (!TryParseAddress(part, context, out IPAddress? one, out error)) return false;
            span = new HostSpan(one!, one!);
            return true;
        }

        string left = part[..dash].Trim(), right = part[(dash + 1)..].Trim();
        if (!TryParseAddress(left, context, out IPAddress? from, out error)) return false;

        // The right side may be a whole address or just the octet the range ends on. Read it
        // relative to the left side, not to the interface: "10.0.5.20-60" ends at 10.0.5.60
        // whichever subnet this machine happens to be on.
        if (!TryParseAddress(right, from, out IPAddress? to, out error)) return false;

        if (ToUInt(to!) < ToUInt(from!))
        {
            error = $"\"{part}\" runs backwards — {to} comes before {from}.";
            return false;
        }

        span = new HostSpan(from!, to!);
        return true;
    }

    private static bool TryParseCidr(string part, int slash, IPAddress? context,
                                     out HostSpan? span, out string error)
    {
        span = null;

        if (!TryParseAddress(part[..slash].Trim(), context, out IPAddress? addr, out error))
            return false;

        if (!int.TryParse(part[(slash + 1)..].Trim(), NumberStyles.None,
                          CultureInfo.InvariantCulture, out int bits) || bits is < 0 or > 32)
        {
            error = $"\"{part}\" — a prefix length must be between 0 and 32.";
            return false;
        }

        uint mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
        uint network = ToUInt(addr!) & mask;
        uint broadcast = network | ~mask;

        // A /31 is a point-to-point link and a /32 is one host: both ends are usable, and
        // there is no network or broadcast address to leave out.
        span = bits >= 31
            ? new HostSpan(FromUInt(network), FromUInt(broadcast))
            : new HostSpan(FromUInt(network + 1), FromUInt(broadcast - 1));

        error = "";
        return true;
    }

    /// <summary>
    /// An address, or the tail of one filled in from <paramref name="context"/>.
    ///
    /// "60" and ".60" both mean "the same subnet, host 60"; "1.60" and ".1.60" replace the
    /// last two octets, and so on. Someone narrowing a scan writes the part that changes.
    /// </summary>
    private static bool TryParseAddress(string text, IPAddress? context,
                                        out IPAddress? address, out string error)
    {
        address = null;
        error = "";

        string s = text.TrimStart('.');
        if (s.Length == 0)
        {
            error = "An address is missing.";
            return false;
        }

        string[] given = s.Split('.');
        if (given.Length > 4)
        {
            error = $"\"{text}\" has more than four parts.";
            return false;
        }

        var octets = new int[4];
        for (int i = 0; i < given.Length; i++)
        {
            if (!int.TryParse(given[i], NumberStyles.None, CultureInfo.InvariantCulture, out int v)
                || v is < 0 or > 255)
            {
                error = $"\"{text}\" — \"{given[i]}\" is not a number between 0 and 255.";
                return false;
            }
            octets[4 - given.Length + i] = v;
        }

        if (given.Length < 4)
        {
            if (context == null || context.AddressFamily != AddressFamily.InterNetwork)
            {
                error = $"\"{text}\" is only part of an address. Write it in full, or select "
                      + "a network interface so the rest can be filled in.";
                return false;
            }

            byte[] baseBytes = context.GetAddressBytes();
            for (int i = 0; i < 4 - given.Length; i++) octets[i] = baseBytes[i];
        }

        address = new IPAddress(new[] { (byte)octets[0], (byte)octets[1],
                                        (byte)octets[2], (byte)octets[3] });
        return true;
    }

    // --------------------------------------------------------------------- conversions

    internal static uint ToUInt(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    internal static IPAddress FromUInt(uint value) => new(new[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value,
    });
}
