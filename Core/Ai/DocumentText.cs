using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace LabEquipmentController;

/// <summary>What kind of file a datasheet is, and how it can be handed to a model.</summary>
public enum DocumentKind
{
    /// <summary>Plain text or Markdown — read as-is.</summary>
    Text,

    /// <summary>Word .docx — an OOXML zip. Always extracted locally; no provider takes .docx.</summary>
    Word,

    /// <summary>PDF — can be uploaded whole to providers that understand documents.</summary>
    Pdf,
}

/// <summary>
/// Pulls plain text out of a datasheet.
///
/// Note what this costs: flattening a PDF to text throws away the layout, and a two-column
/// programming guide interleaves the columns when it does. That is exactly how the offline
/// extraction pipeline used to produce commands whose descriptions belonged to their
/// neighbours. Prefer sending the PDF itself where the provider can read one — see
/// <see cref="AiProviderInfo.SupportsPdfUpload"/> — and treat this as the fallback.
/// </summary>
public static class DocumentText
{
    /// <summary>Extensions this can read, lowercase and dotted, for a file-dialog filter.</summary>
    public static readonly string[] SupportedExtensions =
        { ".txt", ".md", ".text", ".log", ".docx", ".pdf" };

    /// <summary>Classify by extension. Unknown extensions are read as text.</summary>
    public static DocumentKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => DocumentKind.Pdf,
        ".docx" => DocumentKind.Word,
        _ => DocumentKind.Text,
    };

    /// <summary>True if this file can be handed to a model as an uploaded document.</summary>
    public static bool IsPdf(string path) => KindOf(path) == DocumentKind.Pdf;

    /// <summary>
    /// Pages in a PDF, or 0 when that cannot be determined. Used to check a document against
    /// a provider's page cap before uploading it; a count that cannot be taken must not stop
    /// the upload, so an unreadable file answers 0 and the size check stands alone.
    /// </summary>
    public static int PageCount(string path)
    {
        if (KindOf(path) != DocumentKind.Pdf) return 0;
        try
        {
            using UglyToad.PdfPig.PdfDocument doc = UglyToad.PdfPig.PdfDocument.Open(path);
            return doc.NumberOfPages;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Read <paramref name="path"/> as text, whatever its kind. Throws
    /// <see cref="InvalidDataException"/> with a readable message when the file cannot be
    /// parsed — the caller shows that to the user rather than a stack trace.
    /// </summary>
    public static string Read(string path) => KindOf(path) switch
    {
        DocumentKind.Pdf => ReadPdf(path),
        DocumentKind.Word => ReadDocx(path),
        _ => File.ReadAllText(path),
    };

    // ------------------------------------------------------------------------------- pdf

    private static string ReadPdf(string path)
    {
        try
        {
            using UglyToad.PdfPig.PdfDocument doc = UglyToad.PdfPig.PdfDocument.Open(path);
            var sb = new StringBuilder();
            foreach (UglyToad.PdfPig.Content.Page page in doc.GetPages())
            {
                // ContentOrderTextExtractor follows the document's own content order, which
                // holds two-column layouts together far better than concatenating words by
                // position does. It is still a flattening — see the class remarks.
                sb.AppendLine(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor
                    .ContentOrderTextExtractor.GetText(page));
                sb.AppendLine();
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Could not read '{Path.GetFileName(path)}' as a PDF: {ex.Message}", ex);
        }
    }

    // ------------------------------------------------------------------------------ word

    private static readonly XNamespace W =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>
    /// Read a .docx without a dependency: it is a zip whose word/document.xml holds the body.
    /// Each w:t is a run of text, each w:p a paragraph, and w:tab / w:br are the whitespace
    /// that would otherwise be lost — a command and its description sit in one paragraph
    /// separated by a tab often enough that dropping tabs would run them together.
    /// </summary>
    private static string ReadDocx(string path)
    {
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(path);
            // Spelled out in full here rather than left to the wrapper below, which rethrows
            // InvalidDataException untouched to avoid nesting one message inside another.
            ZipArchiveEntry entry = zip.GetEntry("word/document.xml")
                ?? throw new InvalidDataException(
                    $"Could not read '{Path.GetFileName(path)}' as a Word document: "
                  + "it is a zip, but has no word/document.xml inside.");

            using Stream s = entry.Open();
            XDocument xml = XDocument.Load(s);

            var sb = new StringBuilder();
            foreach (XElement p in xml.Descendants(W + "p"))
            {
                foreach (XElement node in p.Descendants())
                {
                    if (node.Name == W + "t") sb.Append(node.Value);
                    else if (node.Name == W + "tab") sb.Append('\t');
                    else if (node.Name == W + "br") sb.AppendLine();
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Could not read '{Path.GetFileName(path)}' as a Word document: {ex.Message}", ex);
        }
    }

    // ---------------------------------------------------------------------------- chunking

    /// <summary>
    /// Split text into chunks of at most <paramref name="maxChars"/>, breaking on blank lines
    /// so a command and its description are not torn apart. A full programming guide runs to
    /// millions of characters and will not fit in one request.
    /// </summary>
    public static IReadOnlyList<string> Chunk(string text, int maxChars)
    {
        if (maxChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxChars));
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        if (text.Length <= maxChars) return new[] { text };

        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (string para in SplitParagraphs(text))
        {
            // A single paragraph longer than the budget is cut on the nearest line break.
            if (para.Length > maxChars)
            {
                if (current.Length > 0) { chunks.Add(current.ToString()); current.Clear(); }
                foreach (string slice in HardSplit(para, maxChars)) chunks.Add(slice);
                continue;
            }

            if (current.Length + para.Length + 2 > maxChars && current.Length > 0)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append("\n\n");
            current.Append(para);
        }

        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    private static IEnumerable<string> SplitParagraphs(string text)
        => text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
               .Select(p => p.Trim()).Where(p => p.Length > 0);

    private static IEnumerable<string> HardSplit(string text, int maxChars)
    {
        int i = 0;
        while (i < text.Length)
        {
            int take = Math.Min(maxChars, text.Length - i);
            if (i + take < text.Length)
            {
                int nl = text.LastIndexOf('\n', i + take - 1, take);
                if (nl > i) take = nl - i + 1;
            }
            yield return text.Substring(i, take);
            i += take;
        }
    }
}
