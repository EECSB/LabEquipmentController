using System;

namespace LabEquipmentController;

/// <summary>
/// Parsing for IEEE 488.2 arbitrary block response data — the format instruments
/// use for bulk binary payloads (waveforms, screenshots).
///
///   Definite length:   #  &lt;n&gt;  &lt;n length digits&gt;  &lt;data bytes&gt;
///                       e.g. "#3256" + 256 bytes  (n=3, length=256)
///   Indefinite length:  #0  &lt;data...&gt;  &lt;newline with EOI&gt;
///
/// The classic mistakes are treating the payload as text, mis-reading the header,
/// and including the trailing newline in the data — so this is isolated and tested.
/// </summary>
public static class Ieee4882Block
{
    /// <summary>
    /// Extract the data payload from a complete response buffer. If the buffer is not
    /// a block (doesn't start with '#'), the whole buffer is returned with a single
    /// trailing CR/LF stripped (an ordinary text response).
    /// </summary>
    public static byte[] Parse(ReadOnlySpan<byte> response)
    {
        int i = 0;
        while (i < response.Length && (response[i] == (byte)' ' || response[i] == (byte)'\t')) i++;

        if (i >= response.Length || response[i] != (byte)'#')
            return TrimTrailingNewline(response).ToArray();   // not a block

        i++; // consume '#'
        if (i >= response.Length)
            throw new FormatException("Truncated block: nothing after '#'.");

        int n = response[i] - '0';
        if (n < 0 || n > 9)
            throw new FormatException($"Invalid block length-digit count '{(char)response[i]}'.");
        i++;

        if (n == 0)
        {
            // Indefinite length: data runs to the end, minus the terminating newline.
            return TrimTrailingNewline(response[i..]).ToArray();
        }

        if (i + n > response.Length)
            throw new FormatException("Truncated block: length digits missing.");

        long length = 0;
        for (int d = 0; d < n; d++)
        {
            byte c = response[i + d];
            if (c < (byte)'0' || c > (byte)'9')
                throw new FormatException("Non-digit in block length field.");
            length = length * 10 + (c - '0');
        }
        i += n;

        if (i + length > response.Length)
            throw new FormatException(
                $"Block claims {length} bytes but only {response.Length - i} remain.");

        return response.Slice(i, (int)length).ToArray();
    }

    /// <summary>
    /// Header size for a definite-length block: how many bytes precede the data, given
    /// the first two header bytes ('#' and the digit count). Used by streaming readers
    /// that must read the header before they know the payload length.
    /// </summary>
    public static int DefiniteHeaderLength(int lengthDigitCount) => 2 + lengthDigitCount;

    private static ReadOnlySpan<byte> TrimTrailingNewline(ReadOnlySpan<byte> data)
    {
        int end = data.Length;
        if (end > 0 && data[end - 1] == (byte)'\n') end--;
        if (end > 0 && data[end - 1] == (byte)'\r') end--;
        return data[..end];
    }
}
