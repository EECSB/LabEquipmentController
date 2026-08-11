using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// Covers reading a datasheet into text. The PDF and Word fixtures are built here rather
/// than checked in as binaries: a hand-built file is inspectable, needs no network, and
/// still exercises the real PdfPig and OOXML paths.
/// </summary>
public class DocumentTextTests : IDisposable
{
    private readonly List<string> _temp = new();

    private string TempFile(string extension)
    {
        string p = Path.Combine(Path.GetTempPath(),
            "lec-doctest-" + Guid.NewGuid().ToString("N") + extension);
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (string p in _temp)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------------------ kinds

    [Theory]
    [InlineData("guide.pdf", DocumentKind.Pdf)]
    [InlineData("GUIDE.PDF", DocumentKind.Pdf)]
    [InlineData("guide.docx", DocumentKind.Word)]
    [InlineData("guide.txt", DocumentKind.Text)]
    [InlineData("guide.md", DocumentKind.Text)]
    [InlineData("guide.whatever", DocumentKind.Text)]
    public void KindOf_classifies_by_extension(string name, DocumentKind expected)
        => Assert.Equal(expected, DocumentText.KindOf(name));

    // -------------------------------------------------------------------------------- pdf

    [Fact]
    public void Reads_text_out_of_a_pdf()
    {
        string path = TempFile(".pdf");
        File.WriteAllBytes(path, MinimalPdf("MEASure:VOLTage:DC?"));

        string text = DocumentText.Read(path);

        Assert.Contains("MEASure:VOLTage:DC?", text);
    }

    [Fact]
    public void A_file_that_is_not_a_pdf_reports_which_file_and_why()
    {
        string path = TempFile(".pdf");
        File.WriteAllText(path, "this is not a PDF at all");

        var ex = Assert.Throws<InvalidDataException>(() => DocumentText.Read(path));

        Assert.Contains(Path.GetFileName(path), ex.Message);
        Assert.Contains("PDF", ex.Message);
    }

    // ------------------------------------------------------------------------------- word

    [Fact]
    public void Reads_paragraphs_and_tabs_out_of_a_docx()
    {
        string path = TempFile(".docx");
        WriteDocx(path,
            "<w:p><w:r><w:t>MEASure:VOLTage:DC?</w:t></w:r>"
          + "<w:r><w:tab/></w:r><w:r><w:t>Measure DC volts.</w:t></w:r></w:p>"
          + "<w:p><w:r><w:t>*IDN?</w:t></w:r></w:p>");

        string text = DocumentText.Read(path);

        // The tab has to survive: without it the command runs straight into its description.
        Assert.Contains("MEASure:VOLTage:DC?\tMeasure DC volts.", text);
        Assert.Contains("*IDN?", text);
    }

    [Fact]
    public void A_zip_that_is_not_a_docx_reports_which_file_and_why()
    {
        string path = TempFile(".docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            zip.CreateEntry("something-else.xml");

        var ex = Assert.Throws<InvalidDataException>(() => DocumentText.Read(path));

        Assert.Contains("Word", ex.Message);
    }

    // --------------------------------------------------------------------------- chunking

    [Fact]
    public void Short_text_is_one_chunk()
        => Assert.Single(DocumentText.Chunk("one two three", 100));

    [Fact]
    public void Chunks_break_on_blank_lines_so_entries_stay_whole()
    {
        string a = new('a', 40);
        string b = new('b', 40);
        string c = new('c', 40);

        IReadOnlyList<string> chunks = DocumentText.Chunk($"{a}\n\n{b}\n\n{c}", 100);

        Assert.All(chunks, ch => Assert.True(ch.Length <= 100, $"chunk was {ch.Length}"));
        // No paragraph may be split across two chunks.
        foreach (string para in new[] { a, b, c })
            Assert.Contains(chunks, ch => ch.Contains(para));
    }

    [Fact]
    public void A_paragraph_longer_than_the_budget_is_still_split()
    {
        IReadOnlyList<string> chunks = DocumentText.Chunk(new string('x', 250), 100);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, ch => Assert.True(ch.Length <= 100));
    }

    [Fact]
    public void Chunking_loses_no_content()
    {
        string text = string.Join("\n\n", new[] { new string('a', 60), new string('b', 60), new string('c', 60) });

        string rejoined = string.Concat(DocumentText.Chunk(text, 100)).Replace("\n", "");

        Assert.Equal(text.Replace("\n", ""), rejoined);
    }

    [Fact]
    public void Empty_text_yields_no_chunks()
        => Assert.Empty(DocumentText.Chunk("", 100));

    // ----------------------------------------------------------------------- pdf fixture

    /// <summary>
    /// A one-page PDF showing <paramref name="body"/> in Helvetica. Written by hand with a
    /// real cross-reference table — PdfPig can sometimes recover from a broken one, and a
    /// test that leans on error recovery is not testing the thing it claims to.
    /// </summary>
    private static byte[] MinimalPdf(string body)
    {
        string stream = $"BT /F1 12 Tf 20 150 Td ({Escape(body)}) Tj ET";
        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n")
          .Append("startxref\n").Append(xref).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Escape(string s)
        => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    // ---------------------------------------------------------------------- word fixture

    private static void WriteDocx(string path, string bodyXml)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        ZipArchiveEntry entry = zip.CreateEntry("word/document.xml");
        using Stream s = entry.Open();
        using var w = new StreamWriter(s, Encoding.UTF8);
        w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
              + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
              + "<w:body>" + bodyXml + "</w:body></w:document>");
    }
}
