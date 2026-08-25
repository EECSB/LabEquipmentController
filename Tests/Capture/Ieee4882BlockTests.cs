using System;
using System.Text;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

public class Ieee4882BlockTests
{
    private static byte[] Block(string header, byte[] payload, bool trailingNewline = false)
    {
        var h = Encoding.ASCII.GetBytes(header);
        var buf = new byte[h.Length + payload.Length + (trailingNewline ? 1 : 0)];
        h.CopyTo(buf, 0);
        payload.CopyTo(buf, h.Length);
        if (trailingNewline) buf[^1] = (byte)'\n';
        return buf;
    }

    [Fact]
    public void Parses_definite_length_block()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        Assert.Equal(payload, Ieee4882Block.Parse(Block("#14", payload)));
    }

    [Fact]
    public void Definite_length_reads_exactly_and_preserves_embedded_newline_and_null()
    {
        // 5-byte payload containing 0x0A (newline) and 0x00 — must NOT be truncated.
        var payload = new byte[] { 0x01, 0x0A, 0x00, 0xFF, 0x7E };
        Assert.Equal(payload, Ieee4882Block.Parse(Block("#15", payload, trailingNewline: true)));
    }

    [Fact]
    public void Multi_digit_length_is_parsed()
    {
        var payload = new byte[256];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;
        Assert.Equal(payload, Ieee4882Block.Parse(Block("#3256", payload)));
    }

    [Fact]
    public void Indefinite_length_returns_everything_minus_trailing_newline()
    {
        var buf = Encoding.ASCII.GetBytes("#0hello world\n");
        Assert.Equal(Encoding.ASCII.GetBytes("hello world"), Ieee4882Block.Parse(buf));
    }

    [Fact]
    public void Non_block_text_is_returned_with_newline_stripped()
    {
        Assert.Equal(Encoding.ASCII.GetBytes("1.234"),
            Ieee4882Block.Parse(Encoding.ASCII.GetBytes("1.234\r\n")));
    }

    [Theory]
    [InlineData("#")]      // nothing after '#'
    [InlineData("#3")]     // 3 length digits promised, none present
    [InlineData("#19")]    // length says 9 bytes, none follow
    public void Malformed_blocks_throw(string text)
    {
        Assert.Throws<FormatException>(() => Ieee4882Block.Parse(Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void Block_claiming_more_than_present_throws()
    {
        // #18 promises 8 bytes, only 3 follow
        Assert.Throws<FormatException>(() =>
            Ieee4882Block.Parse(Encoding.ASCII.GetBytes("#18abc")));
    }
}
