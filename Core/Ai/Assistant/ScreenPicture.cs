using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace LabEquipmentController;

/// <summary>
/// An instrument's screen dump made into a PNG a model can be sent.
///
/// The families split between the two formats their guides document: a Rigol scope and the
/// newer R&amp;S analysers hand back PNG, a Siglent scope and the older analysers BMP. Every
/// provider takes PNG, JPEG, GIF and WebP and none of them takes BMP, so a BMP is decoded here
/// and written again — in managed code alone, because Core runs in the browser and on Linux
/// where System.Drawing does not.
/// </summary>
public static class ScreenPicture
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// The screen as a PNG with its size, or a sentence saying why it cannot be one. A PNG is
    /// passed through untouched; a BMP is converted; anything else is named rather than guessed.
    /// </summary>
    public static (byte[]? Png, int Width, int Height, string? Problem) ToPng(byte[] dump)
    {
        if (dump.Length >= 24 && dump.AsSpan(0, 8).SequenceEqual(PngSignature))
        {
            int w = BinaryPrimitives.ReadInt32BigEndian(dump.AsSpan(16, 4));
            int h = BinaryPrimitives.ReadInt32BigEndian(dump.AsSpan(20, 4));
            return (dump, w, h, null);
        }

        if (dump.Length >= 2 && dump[0] == (byte)'B' && dump[1] == (byte)'M')
        {
            try
            {
                (byte[] rgb, int w, int h) = DecodeBmp(dump);
                return (EncodePng(rgb, w, h), w, h, null);
            }
            catch (InvalidDataException ex)
            {
                return (null, 0, 0, ex.Message);
            }
        }

        if (dump.Length >= 3 && dump[0] == 0xFF && dump[1] == 0xD8 && dump[2] == 0xFF)
            return (null, 0, 0, "The screen came back as a JPEG, which this does not convert.");

        return (null, 0, 0, dump.Length == 0
            ? "The screen dump came back empty."
            : "The screen dump is neither a PNG nor a BMP, so it cannot be shown.");
    }

    /// <summary>
    /// A Windows bitmap as rows of RGB, top row first. Uncompressed 1, 4, 8, 16, 24 and 32 bits
    /// a pixel, with or without colour masks — what instruments write; the run-length forms are
    /// refused by name rather than drawn wrong.
    /// </summary>
    internal static (byte[] Rgb, int Width, int Height) DecodeBmp(byte[] b)
    {
        if (b.Length < 26) throw new InvalidDataException("The screen dump is too short to be a BMP.");

        int pixels = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(10, 4));
        int header = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(14, 4));
        int width, height, bits, compression = 0, colours = 0;
        bool topDown = false;

        if (header == 12)
        {
            width = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(18, 2));
            height = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(20, 2));
            bits = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(24, 2));
        }
        else
        {
            if (header < 40 || b.Length < 14 + header)
                throw new InvalidDataException("The screen dump's BMP header is not one this reads.");
            width = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(18, 4));
            height = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(22, 4));
            bits = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(28, 2));
            compression = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(30, 4));
            colours = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(46, 4));
        }

        if (height < 0) { topDown = true; height = -height; }
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
            throw new InvalidDataException($"The screen dump says it is {width} × {height} pixels, which is not a screen.");
        if (compression is 1 or 2)
            throw new InvalidDataException("The screen dump is a run-length-encoded BMP, which this does not read.");
        if (compression is not (0 or 3 or 6))
            throw new InvalidDataException($"The screen dump is a BMP compressed a way this does not read ({compression}).");

        // Colour masks: given after the header for BI_BITFIELDS, inside it from version 4 on.
        uint red = 0, green = 0, blue = 0;
        if (bits is 16 or 32)
        {
            if (compression is 3 or 6)
            {
                int at = header >= 52 ? 54 : 14 + header;
                if (b.Length < at + 12) throw new InvalidDataException("The screen dump's BMP colour masks are missing.");
                red = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
                green = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at + 4, 4));
                blue = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at + 8, 4));
            }
            else if (bits == 16) { red = 0x7C00; green = 0x03E0; blue = 0x001F; }
            else { red = 0x00FF0000; green = 0x0000FF00; blue = 0x000000FF; }
        }

        // The palette, for the indexed depths: four bytes an entry, blue first; three in the old header.
        byte[]? palette = null;
        if (bits <= 8)
        {
            int entries = colours > 0 ? colours : 1 << bits;
            int size = header == 12 ? 3 : 4;
            int start = 14 + header + (compression == 3 && header == 40 ? 12 : 0);
            if (b.Length < start + entries * size) throw new InvalidDataException("The screen dump's BMP palette is cut short.");
            palette = new byte[entries * 3];
            for (int i = 0; i < entries; i++)
            {
                palette[i * 3] = b[start + i * size + 2];
                palette[i * 3 + 1] = b[start + i * size + 1];
                palette[i * 3 + 2] = b[start + i * size];
            }
        }
        else if (bits is not (16 or 24 or 32))
        {
            throw new InvalidDataException($"The screen dump is a {bits}-bit BMP, which this does not read.");
        }

        int stride = ((width * bits + 31) / 32) * 4;
        if (pixels < 0 || (long)pixels + (long)stride * height > b.Length)
            throw new InvalidDataException("The screen dump is shorter than its BMP header says.");

        var rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            int row = pixels + (topDown ? y : height - 1 - y) * stride;
            int o = y * width * 3;
            for (int x = 0; x < width; x++, o += 3)
            {
                switch (bits)
                {
                    case 24:
                        rgb[o] = b[row + x * 3 + 2];
                        rgb[o + 1] = b[row + x * 3 + 1];
                        rgb[o + 2] = b[row + x * 3];
                        break;
                    case 16:
                    case 32:
                        uint px = bits == 16
                            ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(row + x * 2, 2))
                            : BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(row + x * 4, 4));
                        rgb[o] = Channel(px, red);
                        rgb[o + 1] = Channel(px, green);
                        rgb[o + 2] = Channel(px, blue);
                        break;
                    default:
                        int perByte = 8 / bits;
                        int shift = 8 - bits * (x % perByte + 1);
                        int index = (b[row + x / perByte] >> shift) & ((1 << bits) - 1);
                        if (index * 3 + 2 >= palette!.Length) index = 0;
                        rgb[o] = palette[index * 3];
                        rgb[o + 1] = palette[index * 3 + 1];
                        rgb[o + 2] = palette[index * 3 + 2];
                        break;
                }
            }
        }
        return (rgb, width, height);
    }

    // One colour out of a packed pixel, scaled to eight bits however wide its mask is.
    private static byte Channel(uint pixel, uint mask)
    {
        if (mask == 0) return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        return (byte)((((pixel & mask) >> shift) * 255 + max / 2) / max);
    }

    /// <summary>Rows of RGB written as an 8-bit truecolour PNG.</summary>
    internal static byte[] EncodePng(byte[] rgb, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write(PngSignature);

        var head = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(4, 4), height);
        head[8] = 8;    // bits a channel
        head[9] = 2;    // truecolour
        Chunk(output, "IHDR", head);

        // Each row behind the Sub filter, which a screen of flat colour compresses to almost
        // nothing: a dump of several hundred kilobytes goes to the model as a few dozen.
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = width * 3;
            var line = new byte[stride + 1];
            for (int y = 0; y < height; y++)
            {
                line[0] = 1;
                int row = y * stride;
                for (int i = 0; i < stride; i++)
                    line[i + 1] = (byte)(rgb[row + i] - (i >= 3 ? rgb[row + i - 3] : 0));
                z.Write(line);
            }
        }
        Chunk(output, "IDAT", packed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);

        byte[] name = [(byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]];
        output.Write(name);
        output.Write(data);

        uint crc = Crc(Crc(0xFFFFFFFFu, name), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (byte d in data) crc = CrcTable[(crc ^ d) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
