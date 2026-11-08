using System;
using System.Linq;
using System.Text;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The files a question carries: which are taken and why one is turned away, and what each becomes
/// in a request — a PDF as the file or as its words by what the connection it goes to says.
///
/// GDS Viewer's AiAttachmentTests and AiPdfTextTests, which are ConfigurationRuleEditor's, without
/// the workbooks: this library has no reader for them, so they are turned away with what to send
/// instead, and that is what is tested here.
/// </summary>
public class AiAttachmentTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>A real one-page PDF with these words on it.</summary>
    private static byte[] Pdf(string words) => DocumentTextTests.MinimalPdf(words);

    /// <summary>A real PDF with a page and no words on it, as a scan has none: its one line of text is empty.</summary>
    private static byte[] Scan => DocumentTextTests.MinimalPdf("");

    private static AiConnection Claude() => new() { Provider = AiProvider.Anthropic };

    private static AiConnection Gemini() => new() { Provider = AiProvider.Gemini };

    private static AiConnection OwnServer() => new() { Provider = AiProvider.OpenAiCompatible, BaseUrl = "http://localhost:11434" };

    // ----------------------------------------------------------------------- what a file is

    [Theory]
    [InlineData("screen.png", "", AttachmentKind.Image)]
    [InlineData("photo.JPG", "", AttachmentKind.Image)]
    [InlineData("guide.pdf", "", AttachmentKind.Pdf)]
    [InlineData("sweep.scpi", "", AttachmentKind.Text)]
    [InlineData("filter.seq", "", AttachmentKind.Text)]
    [InlineData("readings.csv", "application/vnd.ms-excel", AttachmentKind.Text)]
    [InlineData("pyvisa_loop.py", "", AttachmentKind.Text)]
    [InlineData("notes.docx", "", AttachmentKind.Document)]
    [InlineData("dragged", "image/webp", AttachmentKind.Image)]
    [InlineData("dragged", "application/pdf", AttachmentKind.Pdf)]
    [InlineData("dragged", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", AttachmentKind.Document)]
    [InlineData("notes", "text/plain", AttachmentKind.Text)]
    [InlineData("table.xlsx", "", AttachmentKind.Unsupported)]
    [InlineData("old.xls", "", AttachmentKind.Unsupported)]
    [InlineData("old.doc", "", AttachmentKind.Unsupported)]
    [InlineData("screen.bmp", "image/bmp", AttachmentKind.Unsupported)]
    [InlineData("setup.exe", "", AttachmentKind.Unsupported)]
    public void The_name_decides_before_the_type(string name, string type, AttachmentKind kind)
        => Assert.Equal(kind, AiAttachment.KindOf(name, type));

    /// <summary>
    /// A file the assistant cannot read but could be made into one it can is told how: a workbook
    /// to a CSV or a PDF, an old Word document to a .docx or a PDF, a deck to a PDF, a BMP to a PNG.
    /// </summary>
    [Fact]
    public void A_file_that_could_be_made_readable_is_turned_away_with_what_to_do_instead()
    {
        Assert.Contains("Excel workbook, which the assistant does not read. Save the sheet as a CSV, or print it to a PDF",
                        AiAttachment.Problem("readings.xlsx", "", [1]));
        Assert.Contains("Save the sheet as a CSV", AiAttachment.Problem("old.xls", "", [1]));
        Assert.Contains("Save the sheet as a CSV",
                        AiAttachment.Problem("dragged", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", [1]));
        Assert.Contains("Word document from before 2007, which the assistant does not read. Save it as a .docx or a PDF",
                        AiAttachment.Problem("manual.doc", "", [1]));
        Assert.Contains("PowerPoint deck", AiAttachment.Problem("slides.pptx", "", [1]));
        Assert.Contains("BMP picture, which no model takes. Save it as a PNG", AiAttachment.Problem("screen.bmp", "", [1]));
        Assert.Contains("not a file the assistant can read", AiAttachment.Problem("setup.exe", "", [1]));
    }

    [Fact]
    public void A_file_that_is_not_what_its_name_says_is_turned_away()
    {
        Assert.Contains("not a PNG, JPEG, GIF or WebP", AiAttachment.Problem("screen.png", "image/png", Text("hello")));
        Assert.Contains("not a PDF", AiAttachment.Problem("guide.pdf", "", Text("hello")));
        Assert.Contains("not a text file", AiAttachment.Problem("sweep.scpi", "", [0x6C, 0, 0x61, 0]));
        Assert.Contains("is empty", AiAttachment.Problem("sweep.scpi", "", []));

        Assert.Null(AiAttachment.Problem("screen.png", "", Png));
        Assert.Null(AiAttachment.Problem("guide.pdf", "", Pdf("*IDN?")));
        Assert.Null(AiAttachment.Problem("sweep.scpi", "", Text("VOLT 5")));
    }

    /// <summary>A Word document is turned away when it is not one — text called .docx, a zip with no body — and one locked with a password, which Office wraps as it wrapped its files before 2007.</summary>
    [Fact]
    public void A_word_document_that_is_not_one_is_turned_away()
    {
        byte[] locked = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0];

        Assert.Null(AiAttachment.Problem("notes.docx", "", DocumentTextTests.Docx("<w:p><w:r><w:t>*RST</w:t></w:r></w:p>")));
        Assert.Contains("is not a Word document, whatever its name says", AiAttachment.Problem("notes.docx", "", Text("a,b\n1,2\n")));
        Assert.Contains("is locked with a password, or is a Word document from before 2007", AiAttachment.Problem("notes.docx", "", locked));
    }

    [Fact]
    public void A_picture_can_be_half_what_a_document_can()
    {
        Assert.Null(AiAttachment.Refusal("screen.png", "", AiAttachment.MostImageBytes));
        Assert.Contains("5 MB at most", AiAttachment.Refusal("screen.png", "", AiAttachment.MostImageBytes + 1));

        Assert.Null(AiAttachment.Refusal("guide.pdf", "", AiAttachment.MostImageBytes + 1));
        Assert.Contains("10 MB at most", AiAttachment.Refusal("guide.pdf", "", AiAttachment.MostFileBytes + 1));
    }

    [Fact]
    public void The_limits_are_the_ones_the_guide_sets()
    {
        Assert.Equal(10, AiAttachment.MostPerQuestion);
        Assert.Equal(5 * 1024 * 1024, AiAttachment.MostBytes(AttachmentKind.Image));
        Assert.Equal(10 * 1024 * 1024, AiAttachment.MostBytes(AttachmentKind.Pdf));
        Assert.Equal(12 * 1024 * 1024, AiAttachment.MostConversationBytes);
        Assert.Equal(200_000, AiAttachment.MostTextCharacters);
    }

    [Fact]
    public void A_pictures_type_is_read_off_its_bytes()
    {
        Assert.Equal("image/png", AiAttachment.ImageType(Png));
        Assert.Equal("image/jpeg", AiAttachment.ImageType(Jpeg));
        Assert.Equal("image/gif", AiAttachment.ImageType(Encoding.ASCII.GetBytes("GIF89a....")));
        Assert.Equal("image/webp", AiAttachment.ImageType(Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ")));
        Assert.Null(AiAttachment.ImageType(Text("not a picture")));
    }

    /// <summary>A file with no name is taken by its type, as one with no extension is, rather than throwing.</summary>
    [Fact]
    public void A_file_with_no_name_is_taken_by_its_type()
    {
        Assert.Null(AiAttachment.Refusal(null, "image/png", 10));
        Assert.Equal("The file is not a file the assistant can read. It takes pictures (PNG, JPEG, GIF, WebP), PDFs, Word documents and text files — scripts, sequences, CSV.",
                     AiAttachment.Refusal(null, "application/octet-stream", 10));
        Assert.Null(AiAttachment.Problem(null, "image/png", Png));
    }

    [Fact]
    public void The_picker_opens_on_what_can_be_attached()
    {
        string[] accepted = AiAttachment.Accept.Split(',');

        Assert.Contains(".png", accepted);
        Assert.Contains(".pdf", accepted);
        Assert.Contains(".docx", accepted);
        Assert.Contains(".scpi", accepted);
        Assert.Contains(".seq", accepted);
        Assert.DoesNotContain(".xlsx", accepted);
        Assert.All(accepted, extension => Assert.NotEqual(AttachmentKind.Unsupported, AiAttachment.KindOf("x" + extension, "")));
    }

    // -------------------------------------------------------------------------- reading text

    [Fact]
    public void Text_is_utf8_unless_a_mark_says_otherwise_and_latin1_when_it_is_not()
    {
        Assert.Equal("1 µA", AiAttachment.Decode(Text("1 µA")));
        Assert.Equal("1 µA", AiAttachment.Decode([0xEF, 0xBB, 0xBF, .. Text("1 µA")]));
        Assert.Equal("1 µA", AiAttachment.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("1 µA")]));

        // A µ written in a Windows code page is one byte, which is not UTF-8 on its own.
        Assert.Equal("1 µA", AiAttachment.Decode([0x31, 0x20, 0xB5, 0x41]));
    }

    [Fact]
    public void A_text_file_goes_between_tags_and_is_cut_where_it_runs_long()
    {
        Assert.Equal("<attachment name=\"sweep.scpi\">" + Environment.NewLine + "VOLT 5" + Environment.NewLine + "</attachment>",
                     AiAttachment.Wrapped("sweep.scpi", "VOLT 5"));

        // A name with no x in it, since the x's are counted below.
        string cut = AiAttachment.Wrapped("readings.csv", new string('x', AiAttachment.MostTextCharacters + 1234));

        Assert.Contains("[Cut short here. The file goes on for another 1,234 characters, which were left out.]", cut);

        // Counted rather than searched for: xUnit searches as the culture does, and GDS Viewer found
        // looking for 200,001 x's among 200,000 that way took over a minute.
        Assert.Equal(AiAttachment.MostTextCharacters, cut.Count(character => character == 'x'));
    }

    [Fact]
    public void A_word_documents_words_are_what_it_is_sent_as()
    {
        var notes = new AiAttachment("notes.docx", "", DocumentTextTests.Docx(
            "<w:p><w:r><w:t>*RST</w:t></w:r><w:r><w:tab/></w:r><w:r><w:t>Reset the supply.</w:t></w:r></w:p>"));

        Assert.Null(notes.Unreadable);
        Assert.Contains("*RST\tReset the supply.", notes.Text);

        AttachmentPart part = Assert.Single(AiAttachment.Parts([notes], Claude()));
        Assert.StartsWith("<attachment name=\"notes.docx\">", part.Text);
    }

    /// <summary>A document whose words cannot be read says why, as the line it is sent as and as the reason Send waits.</summary>
    [Fact]
    public void A_word_document_that_cannot_be_read_says_why()
    {
        var broken = new AiAttachment("notes.docx", "", Text("not a zip"));

        Assert.Contains("Could not read 'notes.docx' as a Word document", broken.Unreadable);
        Assert.Equal(broken.Unreadable, AiAttachment.NotSentTo(Claude(), broken));
        Assert.StartsWith("[The person attached notes.docx. Could not read", Assert.Single(AiAttachment.Parts([broken], Claude())).Text);
    }

    // ------------------------------------------------------------------ what a request carries

    /// <summary>Files ahead of the question, each named: a picture after a line with its name, a text file inside tags carrying it.</summary>
    [Fact]
    public void Each_file_goes_named_and_in_the_order_it_was_attached()
    {
        AiAttachment[] files = [new("screen.png", "image/png", Png), new("sweep.scpi", "", Text("VOLT 5"))];

        var parts = AiAttachment.Parts(files, Claude());

        Assert.Equal(new[] { AttachmentKind.Text, AttachmentKind.Image, AttachmentKind.Text }, parts.Select(part => part.Kind));
        Assert.Equal("Attached file: screen.png", parts[0].Text);
        Assert.Equal(Convert.ToBase64String(Png), parts[1].Base64);
        Assert.StartsWith("<attachment name=\"sweep.scpi\">", parts[2].Text);
    }

    /// <summary>The type the bytes say, not the name: Anthropic refuses a JPEG sent as a PNG.</summary>
    [Fact]
    public void A_jpeg_called_png_is_sent_as_a_jpeg()
        => Assert.Equal("image/jpeg", AiAttachment.Parts([new AiAttachment("screen.png", "image/png", Jpeg)], Claude())[1].MediaType);

    /// <summary>A file left out is still said, in a line — left out without a word, it would be a question about a file the person never sent.</summary>
    [Fact]
    public void A_file_left_out_is_said_in_a_line()
    {
        var screen = new AiAttachment("screen.png", "", Png) { Excluded = true };

        AttachmentPart part = Assert.Single(AiAttachment.Parts([screen], Claude()));

        Assert.Equal(AttachmentKind.Text, part.Kind);
        Assert.Equal("[The person attached screen.png here, and has since left it out of the conversation.]", part.Text);
        Assert.Null(AiAttachment.NotSentTo(Claude(), screen));
    }

    /// <summary>A file the checks never saw — a host's own — that cannot be read is named rather than dropped.</summary>
    [Fact]
    public void A_file_that_cannot_be_sent_is_named_rather_than_dropped()
    {
        var odd = new AiAttachment("setup.exe", "", [0x4D, 0x5A]);

        Assert.Contains("setup.exe, which this model cannot be sent", Assert.Single(AiAttachment.Parts([odd], Claude())).Text);
        Assert.Contains("not a file the assistant can read", AiAttachment.NotSentTo(Claude(), odd));
    }

    /// <summary>
    /// A PDF goes as the file only where the endpoint reads one and the connection has not been
    /// told to read it locally: Anthropic, and OpenAI at its own address. Gemini, through Google's
    /// compatible endpoint, and a server of one's own are sent its words.
    /// </summary>
    [Fact]
    public void A_pdf_goes_as_the_file_only_where_the_endpoint_reads_one()
    {
        var local = Claude();
        local.ExtractTextLocally = true;

        Assert.True(AiAttachment.PdfGoesAsFile(Claude()));
        Assert.True(AiAttachment.PdfGoesAsFile(new AiConnection { Provider = AiProvider.OpenAiCompatible }));
        Assert.False(AiAttachment.PdfGoesAsFile(local));
        Assert.False(AiAttachment.PdfGoesAsFile(Gemini()));
        Assert.False(AiAttachment.PdfGoesAsFile(OwnServer()));

        var guide = new AiAttachment("guide.pdf", "application/pdf", Pdf("MEASure:VOLTage:DC?"));
        Assert.Equal(AttachmentKind.Pdf, guide.SentKindFor(Claude()));
        Assert.Equal(AttachmentKind.Text, guide.SentKindFor(Gemini()));
    }

    /// <summary>The same file to a connection that reads PDFs goes as the file, and is never read here.</summary>
    [Fact]
    public void A_pdf_sent_as_the_file_goes_whole_and_unread()
    {
        var guide = new AiAttachment("guide.pdf", "application/pdf", Pdf("MEASure:VOLTage:DC?"));

        var parts = AiAttachment.Parts([guide], Claude());

        Assert.Equal("Attached file: guide.pdf", parts[0].Text);
        Assert.Equal(AttachmentKind.Pdf, parts[1].Kind);
        Assert.Equal("application/pdf", parts[1].MediaType);
        Assert.Equal("guide.pdf", parts[1].Name);
        Assert.Null(AiAttachment.NotSentTo(Claude(), guide));
    }

    /// <summary>Sent as its words, a PDF goes to Gemini — which is not sent the file — as text inside its name's tags.</summary>
    [Fact]
    public void A_pdf_sent_as_its_words_goes_as_text_inside_its_names_tags()
    {
        var guide = new AiAttachment("guide.pdf", "application/pdf", Pdf("MEASure:VOLTage:DC?"));

        Assert.Null(AiAttachment.NotSentTo(Gemini(), guide));

        AttachmentPart part = Assert.Single(AiAttachment.Parts([guide], Gemini()));
        Assert.Equal(AttachmentKind.Text, part.Kind);
        Assert.StartsWith("<attachment name=\"guide.pdf\">", part.Text);
        Assert.Contains("MEASure:VOLTage:DC?", part.Text);
    }

    /// <summary>A PDF a browser named by its type alone is read as one too, so its words can go — the kind check and the reader agree.</summary>
    [Fact]
    public void A_pdf_known_only_by_its_type_is_read_as_a_pdf()
    {
        var dragged = new AiAttachment("dragged", "application/pdf", Pdf("OUTPut ON"));

        Assert.Equal(AttachmentKind.Pdf, dragged.Kind);
        Assert.Null(dragged.Unreadable);
        Assert.Contains("OUTPut ON", Assert.Single(AiAttachment.Parts([dragged], OwnServer())).Text);
    }

    /// <summary>
    /// A PDF with no words, as a scan has none, going as its words, is refused with GDS Viewer's
    /// sentence — so Send waits with the way out on it — and said in that line, never sent as nothing.
    /// </summary>
    [Fact]
    public void A_scanned_pdf_going_as_its_words_is_refused_with_what_to_do()
    {
        var scan = new AiAttachment("scan.pdf", "application/pdf", Scan);

        Assert.Equal("scan.pdf has no words to read. A scanned PDF has pictures of its pages and no words: send it as the file, to a model that reads one.",
                     AiAttachment.NotSentTo(Gemini(), scan));
        Assert.Contains("A scanned PDF has pictures of its pages", Assert.Single(AiAttachment.Parts([scan], Gemini())).Text);

        // To a connection that reads the file, it goes as the file, and nothing is refused.
        Assert.Null(AiAttachment.NotSentTo(Claude(), scan));
    }

    /// <summary>A PDF this library cannot open at all, going as its words, says so and names the same way out.</summary>
    [Fact]
    public void A_pdf_that_cannot_be_opened_going_as_its_words_says_so()
    {
        var broken = new AiAttachment("broken.pdf", "application/pdf", Encoding.Latin1.GetBytes("%PDF-1.7\nnot a document at all"));

        string? why = AiAttachment.NotSentTo(OwnServer(), broken);

        Assert.NotNull(why);
        Assert.Contains("broken.pdf", why);
        Assert.EndsWith("send it as the file, to a model that reads one.", why, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- what a file weighs

    /// <summary>Pages counted where they can be seen; then the page tree's count; then a guess from the size.</summary>
    [Fact]
    public void A_pdfs_pages_are_counted_for_the_estimate_without_opening_it()
    {
        static byte[] Raw(string body) => Encoding.Latin1.GetBytes("%PDF-1.7\n" + body + "\n%%EOF");

        Assert.Equal(3, AiAttachment.PageCount(Raw("<< /Type /Pages /Count 3 >> << /Type /Page >> << /Type/Page >> << /Type /Page\n>>")));
        Assert.Equal(7, AiAttachment.PageCount(Raw("<< /Type /Catalog >> << /Count 7 >> stream...")));
        Assert.Equal(4, AiAttachment.PageCount(new byte[200_000]));
        Assert.Equal(1, AiAttachment.PageCount(Raw("")));
        Assert.Equal(1, new AiAttachment("guide.pdf", "application/pdf", Pdf("*IDN?")).Pages);
    }

    /// <summary>
    /// What the estimate weighs a file at, as the connection is sent it: a picture's fixed share, a PDF
    /// going as the file by its pages, a PDF going as its words at a guess a page until they are read
    /// and by them after, a text file by its length — and a line, where it is left out.
    /// </summary>
    [Fact]
    public void A_file_is_weighed_at_what_it_adds_to_a_request_to_that_connection()
    {
        var screen = new AiAttachment("screen.png", "", Png);
        var script = new AiAttachment("sweep.scpi", "", Text(new string('w', 3500)));
        var guide = new AiAttachment("guide.pdf", "application/pdf", Pdf("MEASure:VOLTage:DC?"));

        Assert.Equal(AiAttachment.ImageTokens + 15, AiEstimate.Attachment(screen, Claude()));
        Assert.Equal(1000 + 15, AiEstimate.Attachment(script, Claude()));
        Assert.Equal(AiAttachment.PdfPageTokens + 15, AiEstimate.Attachment(guide, Claude()));

        // To Gemini the PDF goes as its words: guessed at, unread, and weighed by them once read.
        Assert.Equal(AiAttachment.PdfPageTextTokens + 15, AiEstimate.Attachment(guide, Gemini()));
        Assert.False(guide.PdfRead);
        Assert.Null(AiAttachment.NotSentTo(Gemini(), guide));
        Assert.True(guide.PdfRead);
        Assert.Equal(AiEstimate.TokensIn(guide.Text.Length) + 15, AiEstimate.Attachment(guide, Gemini()));

        screen.Excluded = true;
        Assert.Equal(25, AiEstimate.Attachment(screen, Claude()));
    }

    /// <summary>The same file again, left out, for a compaction's briefing: the bytes shared, the words read once between them.</summary>
    [Fact]
    public void A_file_left_out_for_a_briefing_is_another_of_it_with_the_same_bytes()
    {
        var guide = new AiAttachment("guide.pdf", "application/pdf", Pdf("OUTPut ON"));
        Assert.Contains("OUTPut ON", guide.Text);

        AiAttachment kept = guide.Unticked();

        Assert.NotSame(guide, kept);
        Assert.NotEqual(guide.Id, kept.Id);
        Assert.Same(guide.Data, kept.Data);
        Assert.True(kept.Excluded);
        Assert.False(guide.Excluded);
        Assert.True(kept.PdfRead);
        Assert.Equal(guide.Text, kept.Text);
    }
}
