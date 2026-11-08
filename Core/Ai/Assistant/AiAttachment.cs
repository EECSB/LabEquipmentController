using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace LabEquipmentController;

/// <summary>What a file is to the assistant, which decides how it reaches the model.</summary>
public enum AttachmentKind
{
    /// <summary>Nothing the assistant can read.</summary>
    Unsupported,

    /// <summary>PNG, JPEG, GIF or WebP, sent as it is.</summary>
    Image,

    /// <summary>
    /// Sent as the file where the connection's endpoint reads one, which reads its text and sees
    /// each page; as the words read out of it here everywhere else — see
    /// <see cref="AiAttachment.PdfGoesAsFile"/>.
    /// </summary>
    Pdf,

    /// <summary>Anything that is text already — a script, a sequence, a CSV of readings — sent as its words.</summary>
    Text,

    /// <summary>A Word document (.docx), sent as its words, read out of it here with <see cref="DocumentText.ReadDocx(byte[], string)"/>.</summary>
    Document,
}

/// <summary>
/// One piece of what a question's files become in a request: words, or a picture or a PDF as
/// base64. Each provider writes these in its own shape — see <see cref="AiAttachment.Parts"/>.
/// </summary>
public sealed record AttachmentPart(AttachmentKind Kind, string Text, string MediaType = "", string Base64 = "", string Name = "");

/// <summary>
/// A file sent with a question: a screenshot of an instrument's display, a programming guide, a
/// CSV of the last run's readings, the script that failed.
///
/// GDS Viewer's, which is ConfigurationRuleEditor's AssistantAttachment — its kinds, its limits
/// and its reasons for turning a file away. Two things are different here. A spreadsheet is not
/// read: GDS Viewer reads workbooks with a reader of its own, and this library has none, so a
/// workbook is turned away with what to do instead — a sheet saved as a CSV is the same numbers,
/// and a model reads a CSV better than a grid anyway. And a PDF goes as the file or as its words
/// by what the connection it is sent to says (<see cref="PdfGoesAsFile"/>), decided at every
/// request, where GDS Viewer has the window mark each file with the model picked.
///
/// **Every question sends the whole conversation again**, its files included: neither provider
/// family keeps anything between requests. That is what the limits on a conversation are sized
/// against, and why a file can be left out (<see cref="Excluded"/>) — it stays on screen, and
/// stops being sent.
/// </summary>
public sealed class AiAttachment
{
    /// <summary>The most files one question can carry.</summary>
    public const int MostPerQuestion = 10;

    /// <summary>Anthropic's ceiling for one picture.</summary>
    public const int MostImageBytes = 5 * 1024 * 1024;

    /// <summary>For a PDF, a Word document or a text file.</summary>
    public const int MostFileBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Every file one conversation sends, together.
    ///
    /// They go again with every question, as base64 — a third bigger — so 12 MB is 16 on the
    /// wire: under Anthropic's 32 MB a request and OpenAI's 50 MB of files a request. GDS Viewer
    /// set it when Google took 20 MB inline; Google's own API takes 100 MB since January 2026, but
    /// the OpenAI-compatible endpoint the assistant reaches Gemini through documents no limit of
    /// its own (read 2026-10-29), so 12 stays until one is tried there. The web page's requests
    /// cross the bench's own server first, and Kestrel's default of 28.6 MB a request is under no
    /// strain from 16.
    /// </summary>
    public const int MostConversationBytes = 12 * 1024 * 1024;

    /// <summary>
    /// How much of one file's text goes into the prompt: about 57,000 tokens. Every later question
    /// sends it again, so a programming guide of a million characters would go on costing that for
    /// the rest of the conversation. Past it the model is told where the file was cut.
    /// </summary>
    public const int MostTextCharacters = 200_000;

    /// <summary>About what a picture costs: the most Anthropic charges for one it has scaled to fit, the figure ConfigurationRuleEditor's estimate uses.</summary>
    public const int ImageTokens = 1600;

    /// <summary>About what a page of a PDF costs sent as the file: its text, and a picture of the page.</summary>
    public const int PdfPageTokens = 2000;

    /// <summary>About what a page of a PDF costs as its words alone, until they are read — see <see cref="TokensFor"/>.</summary>
    public const int PdfPageTextTokens = 500;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    private static readonly string[] ImageTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    /// <summary>Text anybody sends, which a browser mostly has a type for. ".text" because <see cref="DocumentText"/> reads it.</summary>
    private static readonly string[] CommonText =
        [".txt", ".text", ".csv", ".tsv", ".json", ".xml", ".md", ".log", ".yaml", ".yml", ".ini", ".htm", ".html"];

    /// <summary>
    /// Text a bench comes with, which no browser has a type for: this app's own scripts (.scpi)
    /// and multi-instrument scripts (.seq), and the Python a lab drives instruments with otherwise.
    /// </summary>
    private static readonly string[] BenchText = [".scpi", ".seq", ".py"];

    /// <summary>Excel, which this library has no reader for — every version of it, the binary one included.</summary>
    private static readonly string[] WorkbookFiles = [".xlsx", ".xlsm", ".xls"];

    private static readonly string[] WorkbookTypes =
    [
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.ms-excel.sheet.macroenabled.12",
    ];

    private const string DocumentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>Files turned away by their extension, each with a sentence of its own in <see cref="Refusal"/> saying what to send instead.</summary>
    private static readonly string[] Refused = [.. WorkbookFiles, ".doc", ".pptx", ".ppt", ".bmp"];

    /// <summary>What an Office file from before 2007 starts with — and one locked with a password, which Office wraps the same way.</summary>
    private static readonly byte[] CompoundFile = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static int _lastId;

    /// <summary>
    /// Held while the words are read, once. The window weighs a file on its own thread while a
    /// provider builds a request from it on another, and without this both would read it — and one
    /// could see it marked read before its words were.
    /// </summary>
    private readonly object _reading = new();

    private bool _read;
    private string _text = "";
    private string? _unreadable;
    private int _pages = -1;

    public AiAttachment(string name, string mediaType, byte[] data)
    {
        Name = name ?? "";
        MediaType = mediaType ?? "";
        Data = data ?? [];
        Id = Interlocked.Increment(ref _lastId);
    }

    /// <summary>A number of its own, so a chip and a tick stay with one file however many share a name.</summary>
    public int Id { get; }

    /// <summary>As the person's machine named it. Shown on the chip, and given to the model so a question can say which file it means.</summary>
    public string Name { get; }

    /// <summary>What the browser or the host said the file is. Often nothing — a desktop file has none, and a browser has none for a .seq — so the name decides first.</summary>
    public string MediaType { get; }

    public byte[] Data { get; }

    /// <summary>
    /// Left out of the conversation: on screen still, so it can be put back, and not sent. The
    /// model is told in one line that it was there — see <see cref="Parts"/> — rather than finding
    /// a question about a file it never saw.
    /// </summary>
    public bool Excluded { get; set; }

    public AttachmentKind Kind => KindOf(Name, MediaType);

    /// <summary>
    /// Whether a PDF goes to a connection as the file rather than as the words read out of it:
    /// only where the connection's chat endpoint takes one (<see cref="AiConnection.ChatTakesPdf"/>)
    /// and the person has not asked for PDFs as text (<see cref="AiConnection.ChatPdfAsText"/>).
    /// Decided at every request from the connection it goes to, so another connection picked
    /// sends the same file its own way.
    /// </summary>
    public static bool PdfGoesAsFile(AiConnection connection) => connection.ChatTakesPdf && !connection.ChatPdfAsText;

    /// <summary>What the file goes to a connection as: a PDF going as its words is text.</summary>
    public AttachmentKind SentKindFor(AiConnection connection)
        => Kind == AttachmentKind.Pdf && !PdfGoesAsFile(connection) ? AttachmentKind.Text : Kind;

    /// <summary>
    /// The words the file is sent as: a text file's decoded, a Word document's or a PDF's read out
    /// of it here. Read once, the first time anything asks — for a PDF that is when it first goes
    /// as text, since a PDF going as the file is never read here at all.
    /// </summary>
    public string Text
    {
        get
        {
            Read();
            return _text;
        }
    }

    /// <summary>
    /// Why the file's words could not be read, said so the person can do something about it — or
    /// null when they could, as they always can for a text file, which any bytes decode as.
    ///
    /// A PDF with no words — a scan, whose pages are pictures — says so in GDS Viewer's sentence,
    /// which names the way out: send it as the file, to a model that reads one.
    /// </summary>
    public string? Unreadable
    {
        get
        {
            Read();
            return _unreadable;
        }
    }

    /// <summary>Whether a PDF's words have been read out of it, whether or not there were any — until then the estimate guesses at them.</summary>
    public bool PdfRead
    {
        get
        {
            lock (_reading) return Kind == AttachmentKind.Pdf && _read;
        }
    }

    /// <summary>A PDF's pages, counted once from its bytes — see <see cref="PageCount"/>.</summary>
    public int Pages
    {
        get
        {
            lock (_reading)
            {
                if (_pages < 0) _pages = PageCount(Data);
                return _pages;
            }
        }
    }

    /// <summary>
    /// About what the file adds to a request to <paramref name="connection"/> — see
    /// <see cref="AiEstimate"/>. A picture at its fixed share; a PDF going as the file by its pages;
    /// a PDF going as its words at a guess a page until they are read, so typing in the box never
    /// waits on reading a guide, and by its words after; anything else by its words, as far as
    /// <see cref="MostTextCharacters"/> goes.
    /// </summary>
    public long TokensFor(AiConnection connection) => SentKindFor(connection) switch
    {
        AttachmentKind.Image => ImageTokens,
        AttachmentKind.Pdf => (long)Pages * PdfPageTokens,
        AttachmentKind.Text when Kind == AttachmentKind.Pdf && !PdfRead => (long)Pages * PdfPageTextTokens,
        _ => AiEstimate.TokensIn(Math.Min(Text.Length, MostTextCharacters)),
    };

    /// <summary>
    /// The same file again, left out: what a compaction keeps on its briefing (see
    /// <see cref="AiChat.CompactAsync"/>). Another of it rather than this one, so the question it went
    /// with goes on showing it as it was sent; the bytes are shared, not copied, and so are its words
    /// if they were read.
    /// </summary>
    public AiAttachment Unticked()
    {
        var copy = new AiAttachment(Name, MediaType, Data) { Excluded = true };
        lock (_reading)
        {
            copy._read = _read;
            copy._text = _text;
            copy._unreadable = _unreadable;
            copy._pages = _pages;
        }
        return copy;
    }

    private void Read()
    {
        lock (_reading)
        {
            if (_read) return;

            switch (Kind)
            {
                case AttachmentKind.Pdf:
                    try
                    {
                        _text = DocumentText.ReadPdf(Data, Name).Trim();
                        if (_text.Length == 0)
                            _unreadable = $"{Called(Name)} has no words to read. A scanned PDF has pictures of its pages "
                                        + "and no words: send it as the file, to a model that reads one.";
                    }
                    catch (InvalidDataException failed)
                    {
                        _unreadable = $"{failed.Message.TrimEnd('.')}. Send it as the file, to a model that reads one.";
                    }
                    break;

                case AttachmentKind.Document:
                    try
                    {
                        _text = DocumentText.ReadDocx(Data, Name);
                    }
                    catch (InvalidDataException failed)
                    {
                        _unreadable = failed.Message;
                    }
                    break;

                default:
                    _text = Decode(Data);
                    break;
            }

            _read = true;
        }
    }

    /// <summary>For a file picker, so it opens on what can be attached. Anything else can still be picked, and is turned away with a reason.</summary>
    public static string Accept => string.Join(",", ImageExtensions.Concat([".pdf", ".docx"]).Concat(CommonText).Concat(BenchText));

    /// <summary>
    /// What a file is. **The name decides before the type does**: a browser calls a CSV something
    /// different on every system and has no word at all for a .seq, and a file picked on the
    /// desktop comes with no type. The type is used only when the name says nothing — a picture
    /// dragged out of another page, or a PDF a browser names by its type alone, which is a PDF
    /// here as much as one called .pdf is, so its words can be read.
    /// </summary>
    public static AttachmentKind KindOf(string? name, string? mediaType)
    {
        string extension = Path.GetExtension(name ?? "").ToLowerInvariant();
        string type = (mediaType ?? "").Trim().ToLowerInvariant();

        if (ImageExtensions.Contains(extension)) return AttachmentKind.Image;
        if (extension == ".pdf") return AttachmentKind.Pdf;
        if (extension == ".docx") return AttachmentKind.Document;
        if (CommonText.Contains(extension) || BenchText.Contains(extension)) return AttachmentKind.Text;
        if (Refused.Contains(extension)) return AttachmentKind.Unsupported;

        // No extension it knows: the type is all there is.
        if (ImageTypes.Contains(type)) return AttachmentKind.Image;
        if (type == "application/pdf") return AttachmentKind.Pdf;
        if (type == DocumentType) return AttachmentKind.Document;
        if (type.StartsWith("text/", StringComparison.Ordinal) || type is "application/json" or "application/xml")
            return AttachmentKind.Text;

        return AttachmentKind.Unsupported;
    }

    /// <summary>The most a file of a kind can be.</summary>
    public static int MostBytes(AttachmentKind kind) => kind == AttachmentKind.Image ? MostImageBytes : MostFileBytes;

    /// <summary>
    /// Why a file cannot go with a question, from its name, its type and its size alone — so a file
    /// far too big is turned away before a byte of it is read. Null when nothing so far says no.
    ///
    /// A file this library cannot read but could be made into one it can is told how: a workbook
    /// saved as a CSV or printed to a PDF, an old Word document saved as a .docx or a PDF, a deck as
    /// a PDF, and a BMP — which this app's own screenshot window can save, and which no model takes
    /// — as a PNG.
    /// </summary>
    public static string? Refusal(string? name, string? mediaType, long size)
    {
        // A file with no name goes by its type, as one with no extension does, rather than
        // throwing — GDS Viewer's fix, for what another host's files come through as.
        name ??= "";
        string called = Called(name);
        string extension = Path.GetExtension(name).ToLowerInvariant();
        AttachmentKind kind = KindOf(name, mediaType);

        if (WorkbookFiles.Contains(extension)
         || (kind == AttachmentKind.Unsupported && WorkbookTypes.Contains((mediaType ?? "").Trim().ToLowerInvariant())))
            return $"{called} is an Excel workbook, which the assistant does not read. Save the sheet as a CSV, "
                 + "or print it to a PDF, and attach that.";

        if (extension == ".doc")
            return $"{called} is a Word document from before 2007, which the assistant does not read. Save it as a "
                 + ".docx or a PDF, and attach that.";

        if (extension is ".pptx" or ".ppt")
            return $"{called} is a PowerPoint deck, which the assistant does not read. Save it as a PDF, and attach that.";

        if (extension == ".bmp")
            return $"{called} is a BMP picture, which no model takes. Save it as a PNG, and attach that.";

        if (kind == AttachmentKind.Unsupported)
            return $"{called} is not a file the assistant can read. It takes pictures (PNG, JPEG, GIF, WebP), PDFs, "
                 + "Word documents and text files — scripts, sequences, CSV.";

        if (size == 0) return $"{called} is empty.";

        int most = MostBytes(kind);
        return size > most ? $"{called} is {Size(size)}, and a file of its kind can be {Size(most)} at most." : null;
    }

    /// <summary>
    /// Why a file cannot go with a question, said so the person can do something about it — or
    /// null when it can. <see cref="Refusal"/>, and then whether the bytes are what the name says:
    /// a name is only a claim, and a PDF called .png, or a binary file called .txt, is turned away
    /// with that sentence rather than sent as what it is not.
    ///
    /// This checks what the file is, not whether its words can be read. That is
    /// <see cref="Unreadable"/>, which reads them — so a host attaching a file asks both, once,
    /// when it is attached rather than when the question goes.
    /// </summary>
    public static string? Problem(string? name, string? mediaType, byte[] data)
    {
        name ??= "";
        if (Refusal(name, mediaType, data.Length) is string refused) return refused;

        string called = Called(name);

        return KindOf(name, mediaType) switch
        {
            AttachmentKind.Image when ImageType(data) is null => $"{called} is not a PNG, JPEG, GIF or WebP picture, whatever its name says.",
            AttachmentKind.Pdf when !IsPdf(data) => $"{called} is not a PDF, whatever its name says.",
            AttachmentKind.Text when !LooksLikeText(data) => $"{called} is not a text file, whatever its name says.",
            AttachmentKind.Document when Opens(data, 0, CompoundFile) =>
                $"{called} is locked with a password, or is a Word document from before 2007 with a new name. Save it "
              + "unlocked as a .docx or a PDF, and attach that.",
            AttachmentKind.Document when !IsWordPackage(data) => $"{called} is not a Word document, whatever its name says.",
            _ => null,
        };
    }

    /// <summary>
    /// Why a file waiting to be sent cannot go to a connection, for the line that says why Send
    /// waits — or null when it can. A PDF going as its words is read here, once, so a scan is
    /// found before the question goes rather than sent as nothing.
    /// </summary>
    public static string? NotSentTo(AiConnection connection, AiAttachment file)
    {
        if (file.Excluded) return null;

        return file.SentKindFor(connection) switch
        {
            AttachmentKind.Unsupported => Refusal(file.Name, file.MediaType, file.Data.Length)
                                       ?? $"{Called(file.Name)} is not a file the assistant can read.",
            AttachmentKind.Text or AttachmentKind.Document => file.Unreadable,
            _ => null,
        };
    }

    /// <summary>
    /// What a question's files become in a request to <paramref name="connection"/>, ahead of the
    /// question's own words.
    ///
    /// **Ahead of the question**, so the question reads them before it is asked, and so that
    /// everything up to the last file is what was sent the time before, byte for byte — a
    /// question's own text can change when it moves back into the history, and its files do not —
    /// which is what a provider's cache holds on to. **Each one named**: a picture or a PDF just
    /// after a line saying which it is, and a text file inside tags carrying its name, so "the
    /// second screenshot" or "line 40 of the script" can be matched to the right one.
    ///
    /// A file left out, or one that cannot be sent, is still said in a line — left out without a
    /// word, it would read as a question about a file the person never sent.
    /// </summary>
    public static List<AttachmentPart> Parts(IReadOnlyList<AiAttachment> files, AiConnection connection)
    {
        var parts = new List<AttachmentPart>();

        foreach (AiAttachment file in files)
        {
            if (file.Excluded)
            {
                parts.Add(new(AttachmentKind.Text, $"[The person attached {file.Name} here, and has since left it out of the conversation.]"));
                continue;
            }

            switch (file.SentKindFor(connection))
            {
                case AttachmentKind.Image:
                    // The type the bytes say rather than the one the name does — Anthropic
                    // refuses a JPEG sent as a PNG.
                    parts.Add(new(AttachmentKind.Text, $"Attached file: {file.Name}"));
                    parts.Add(new(AttachmentKind.Image, "", ImageType(file.Data) ?? "image/png", Convert.ToBase64String(file.Data), file.Name));
                    break;

                case AttachmentKind.Pdf:
                    parts.Add(new(AttachmentKind.Text, $"Attached file: {file.Name}"));
                    parts.Add(new(AttachmentKind.Pdf, "", "application/pdf", Convert.ToBase64String(file.Data), file.Name));
                    break;

                case AttachmentKind.Unsupported:
                    // Turned away when it was attached, so only a file attached some other way gets here.
                    parts.Add(new(AttachmentKind.Text, $"[The person attached {file.Name}, which this model cannot be sent. If the question needs it, say so.]"));
                    break;

                default:
                    parts.Add(new(AttachmentKind.Text, file.Unreadable is string why
                        ? $"[The person attached {file.Name}. {why}]"
                        : Wrapped(file.Name, file.Text)));
                    break;
            }
        }

        return parts;
    }

    /// <summary>
    /// A file's words between tags carrying its name, so the model can tell where the file stops
    /// and the question starts — cut at <see cref="MostTextCharacters"/>, saying how much more there
    /// was.
    /// </summary>
    public static string Wrapped(string name, string text)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"<attachment name=\"{name.Replace('"', '\'')}\">");

        if (text.Length <= MostTextCharacters)
        {
            builder.AppendLine(text.TrimEnd());
        }
        else
        {
            int rest = text.Length - MostTextCharacters;
            builder.AppendLine(text[..MostTextCharacters].TrimEnd());
            builder.AppendLine($"[Cut short here. The file goes on for another {rest.ToString("N0", CultureInfo.InvariantCulture)} characters, which were left out.]");
        }

        builder.Append("</attachment>");
        return builder.ToString();
    }

    /// <summary>
    /// A picture's type read off its first bytes, or null for anything else. What the model is told
    /// has to be what the bytes are — Anthropic refuses a JPEG sent as a PNG — and a file's name is
    /// only a claim.
    /// </summary>
    public static string? ImageType(byte[] data)
    {
        if (Opens(data, 0, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (Opens(data, 0, [0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (Opens(data, 0, "GIF87a"u8) || Opens(data, 0, "GIF89a"u8)) return "image/gif";
        if (Opens(data, 0, "RIFF"u8) && Opens(data, 8, "WEBP"u8)) return "image/webp";
        return null;
    }

    /// <summary>"%PDF-" somewhere in the first kilobyte, which is as far in as the format lets it start.</summary>
    public static bool IsPdf(byte[] data) => data.AsSpan(0, Math.Min(data.Length, 1024)).IndexOf("%PDF-"u8) >= 0;

    /// <summary>
    /// Text has no NUL in it, unless it is UTF-16, which says so in its first two bytes. Looked for
    /// in the first few kilobytes, where a binary file gives itself away.
    /// </summary>
    public static bool LooksLikeText(byte[] data)
        => Opens(data, 0, [0xFF, 0xFE]) || Opens(data, 0, [0xFE, 0xFF])
        || data.AsSpan(0, Math.Min(data.Length, 8192)).IndexOf((byte)0) < 0;

    /// <summary>
    /// A text file's words: UTF-8 unless a byte order mark says otherwise, and Latin-1 when it is
    /// not UTF-8.
    ///
    /// A file that is not UTF-8 is most likely in a Windows code page from before it, as an older
    /// instrument's export or a script saved by an older editor is; Latin-1 reads every byte as some
    /// character, so the numbers that make up most of such a file come through whole — and a µ
    /// written as one byte comes through as a µ.
    /// </summary>
    public static string Decode(byte[] data)
    {
        if (Opens(data, 0, [0xFF, 0xFE])) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (Opens(data, 0, [0xFE, 0xFF])) return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);

        int start = Opens(data, 0, [0xEF, 0xBB, 0xBF]) ? 3 : 0;
        try
        {
            return new UTF8Encoding(false, true).GetString(data, start, data.Length - start);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(data);
        }
    }

    /// <summary>"512 bytes", "40 KB", "1.4 MB".</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024) return $"{bytes} bytes";
        if (bytes < 1024 * 1024) return $"{Math.Ceiling(bytes / 1024.0).ToString(CultureInfo.InvariantCulture)} KB";
        return $"{(bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture)} MB";
    }

    /// <summary>
    /// About how many pages a PDF has, for the estimate — not for anything that has to be right, and
    /// read off the bytes rather than by opening the document, so weighing a 400-page guide as it is
    /// attached costs nothing.
    ///
    /// **Its page objects, counted where they can be seen**: each page is an object that says
    /// /Type /Page, which a file written plainly shows. A newer one packs its objects into compressed
    /// streams, where they cannot be seen without inflating them — so then the page tree's /Count, the
    /// largest one there is, which is often still in the open; and with neither, one page for every
    /// 50 KB, which is what text and line drawings tend to come to. GDS Viewer's, as it is.
    /// </summary>
    public static int PageCount(byte[] data)
    {
        ReadOnlySpan<byte> span = data;
        int pages = 0;
        int at = 0;

        while (true)
        {
            int found = span[at..].IndexOf("/Type"u8);
            if (found < 0) break;

            int next = SkipSpaces(span, at + found + 5);
            if (StartsAt(span, next, "/Page"u8) && !IsLetter(span, next + 5)) pages++;

            at += found + 5;
        }

        if (pages > 0) return pages;

        int counted = MostCount(span);
        return counted > 0 ? counted : Math.Max(1, data.Length / 50_000);
    }

    /// <summary>The largest number after a /Count, or 0 when there is none to see.</summary>
    private static int MostCount(ReadOnlySpan<byte> span)
    {
        int most = 0;
        int at = 0;

        while (true)
        {
            int found = span[at..].IndexOf("/Count"u8);
            if (found < 0) break;

            int digit = SkipSpaces(span, at + found + 6);
            int value = 0;
            while (digit < span.Length && span[digit] >= (byte)'0' && span[digit] <= (byte)'9' && value < 100_000)
            {
                value = value * 10 + (span[digit] - (byte)'0');
                digit++;
            }

            most = Math.Max(most, value);
            at += found + 6;
        }

        return most;
    }

    private static int SkipSpaces(ReadOnlySpan<byte> span, int at)
    {
        while (at < span.Length && span[at] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') at++;
        return at;
    }

    private static bool StartsAt(ReadOnlySpan<byte> span, int at, ReadOnlySpan<byte> word)
        => at >= 0 && at + word.Length <= span.Length && span.Slice(at, word.Length).SequenceEqual(word);

    private static bool IsLetter(ReadOnlySpan<byte> span, int at)
        => at < span.Length && span[at] is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z');

    /// <summary>A file's name for a sentence, or "The file" for one that came with none.</summary>
    private static string Called(string name) => string.IsNullOrWhiteSpace(name) ? "The file" : name;

    /// <summary>A zip with a Word document's body in it: what a .docx is, read no further than its directory.</summary>
    private static bool IsWordPackage(byte[] data)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
            return zip.GetEntry("word/document.xml") != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool Opens(byte[] data, int offset, ReadOnlySpan<byte> signature)
        => data.Length >= offset + signature.Length && data.AsSpan(offset, signature.Length).SequenceEqual(signature);
}
