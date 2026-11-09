using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LabEquipmentController
{
    ///
    ///An Excel workbook or a Word document read out as words, which is how a model is sent either: no provider takes one
    ///as a file.
    ///
    ///**Read where the app runs**, the page included: an .xlsx or a .docx is a zip of XML parts, and .NET opens both in the
    ///browser as they are, so nothing is added to the build. From GDS Viewer, rule for rule. What comes out is what that app sends:
    ///every sheet a row to a line, the row's number first and its cells between tabs; a document's paragraphs and
    ///tables, with its headers and footers.
    ///
    ///**Values as they mean, not as Excel dresses them.** A date reads as a date, a time as a time and a percentage as
    ///one; every other number reads as the number it is, without the decimals, separators or currency its format adds.
    ///A formula reads as what Excel last worked it out to, which the file keeps beside it - or as the formula itself when
    ///the file has none, as a workbook a script wrote often has not. The numbers Word puts in front of a list item or a
    ///heading by itself are drawn by Word and are not in the file's text, so they are not in this either.
    ///
    ///The older binary .xls and .doc are another format altogether, and are not read.
    ///
    public static class OfficeText
    {
        ///
        ///The most one part of a file is unpacked to. A zip says how big its parts are, and can be lying: a few
        ///megabytes can unpack to gigabytes, in a page that has to keep running.
        ///
        public const long MostPartBytes = 100L * 1024 * 1024;

        ///<summary>Excel's main namespace, as it writes it and as its Strict format does.</summary>
        private static readonly string[] sheetSpaces = { "http://schemas.openxmlformats.org/spreadsheetml/2006/main", "http://purl.oclc.org/ooxml/spreadsheetml/main" };

        private static readonly string[] wordSpaces = { "http://schemas.openxmlformats.org/wordprocessingml/2006/main", "http://purl.oclc.org/ooxml/wordprocessingml/main" };

        private static readonly string[] relationSpaces = { "http://schemas.openxmlformats.org/officeDocument/2006/relationships", "http://purl.oclc.org/ooxml/officeDocument/relationships" };

        ///<summary>Markup compatibility: a part written two ways, for an Office that knows the newer and one that does not.</summary>
        private const string compatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";

        private static readonly XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, CloseInput = true };

        ///<summary>What a number's format makes of it - all that a cell's style is read for.</summary>
        private enum Look
        {
            Plain,
            Date,
            Time,
            DateTime,

            ///<summary>Hours past a day, as [h]:mm does: a duration rather than a time of day.</summary>
            Elapsed,

            Percent
        }

        private sealed record Relation(string Id, string Type, string Target);

        #region A workbook

        ///
        ///Every sheet, a row to a line: its number, then its cells between tabs - as ConfigurationRuleEditor sends one. The
        ///number is there so an answer can say "row 214" and be found again; counting rows is not something a model does
        ///well. A row with nothing in it is left out, and so are the empty cells at the end of one.
        ///
        ///**Read no further than <paramref name="most"/> characters**, which is all of it that would be sent: a sheet of a
        ///million rows stops being read there, and Cut says so.
        ///
        public static (string Text, bool Cut) Workbook(byte[] data, int most)
        {
            using var package = new Package(data);

            string book = package.Main("xl/workbook.xml");
            var root = package.Load(book);
            var relations = package.Relations(book);
            bool date1904 = isTrue(childOf(root, "workbookPr")?.Attribute("date1904"));
            var strings = new List<string>();
            var looks = new List<Look>();

            if (relations.FirstOrDefault(each => each.Type.EndsWith("/sharedStrings", StringComparison.Ordinal)) is Relation shared && package.Has(shared.Target))
                strings = sharedStrings(package, shared.Target);

            if (relations.FirstOrDefault(each => each.Type.EndsWith("/styles", StringComparison.Ordinal)) is Relation styles && package.Has(styles.Target))
                looks = cellLooks(package.Load(styles.Target));

            var text = new StringBuilder();

            foreach (var sheet in childOf(root, "sheets")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                string name = (string?)sheet.Attribute("name") ?? "";
                string id = relationId(sheet);
                var part = relations.FirstOrDefault(each => each.Id == id);

                if (part is null || !package.Has(part.Target))
                    continue;

                string hidden = "";

                if ((string?)sheet.Attribute("state") is "hidden" or "veryHidden")
                    hidden = " (hidden)";

                if (!part.Type.EndsWith("/worksheet", StringComparison.Ordinal))
                {
                    text.Append($"Sheet \"{name}\"{hidden} is a chart, with no cells to read.\n\n");

                    continue;
                }

                text.Append($"Sheet \"{name}\"{hidden} - each line is a row: its number, then its cells, separated by tabs.\n");

                if (rows(package, part.Target, strings, looks, date1904, text, most))
                    return (text.ToString(0, most), true);

                text.Append('\n');
            }

            return (text.ToString(), false);
        }

        ///<summary>The workbook's strings, which its cells name by number - each one's runs together, less the phonetic guide Japanese text carries above itself.</summary>
        private static List<string> sharedStrings(Package package, string path)
        {
            var strings = new List<string>();

            using var reader = XmlReader.Create(package.Open(path), settings);

            reader.MoveToContent();

            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
                    strings.Add(runs((XElement)XNode.ReadFrom(reader)));
                else
                    reader.Read();
            }

            return strings;
        }

        ///<summary>The words of a string with runs in it, or of one without: every t under it, outside a phonetic run.</summary>
        private static string runs(XElement? holder)
        {
            var text = new StringBuilder();

            if (holder is null)
                return "";

            foreach (var element in holder.Descendants())
            {
                if (element.Name.LocalName == "t" && !element.Ancestors().Any(above => above.Name.LocalName == "rPh"))
                    text.Append(element.Value);
            }

            return text.ToString();
        }

        ///<summary>What each cell style does to a number, by the style's number - which is what a cell's s says.</summary>
        private static List<Look> cellLooks(XElement styles)
        {
            var codes = new Dictionary<int, string>();
            var looks = new List<Look>();

            foreach (var format in childOf(styles, "numFmts")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (int.TryParse((string?)format.Attribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                    codes[id] = (string?)format.Attribute("formatCode") ?? "";
            }

            foreach (var style in childOf(styles, "cellXfs")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                int.TryParse((string?)style.Attribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id);

                looks.Add(lookOf(id, codes.GetValueOrDefault(id)));
            }

            return looks;
        }

        ///
        ///What a number format shows a number as. Its own code when the file gives one; otherwise one of the formats
        ///Excel has built in, which a file names by number alone and which differ by country only in their order.
        ///
        private static Look lookOf(int id, string? code)
        {
            if (code is not null)
                return lookOfCode(code);

            if (id == 9 || id == 10)
                return Look.Percent;

            if (id == 22)
                return Look.DateTime;

            if (id == 46)
                return Look.Elapsed;

            if ((id >= 18 && id <= 21) || id == 45 || id == 47)
                return Look.Time;

            if ((id >= 14 && id <= 17) || (id >= 27 && id <= 36) || (id >= 50 && id <= 58))
                return Look.Date;

            return Look.Plain;
        }

        ///
        ///A format code read for what it shows, after what is only decoration is taken out: quoted words, an escaped
        ///character, the spacing and fill marks, and anything in brackets - a color, a currency, a condition - except a
        ///[h], [m] or [s], which counts time past a day. Only the first of its sections, the one for a number above zero.
        ///
        private static Look lookOfCode(string code)
        {
            var bare = new StringBuilder();
            bool elapsed = false;

            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];

                if (c == ';')
                    break;

                if (c == '"')
                {
                    int end = code.IndexOf('"', i + 1);

                    if (end < 0)
                        break;

                    i = end;
                }
                else if (c == '\\' || c == '_' || c == '*')
                {
                    i++;
                }
                else if (c == '[')
                {
                    int end = code.IndexOf(']', i + 1);

                    if (end < 0)
                        break;

                    string inside = code.Substring(i + 1, end - i - 1).ToLowerInvariant();

                    if (inside.Length > 0 && inside.All(letter => letter == 'h' || letter == 'm' || letter == 's'))
                        elapsed = true;

                    i = end;
                }
                else
                {
                    bare.Append(char.ToLowerInvariant(c));
                }
            }

            string shown = bare.ToString();

            if (shown.Contains('%'))
                return Look.Percent;

            if (elapsed)
                return Look.Elapsed;

            bool time = shown.Contains('h') || shown.Contains('s') || shown.Contains("am/pm") || shown.Contains("a/p");
            bool date = shown.Contains('y') || shown.Contains('d') || (shown.Contains('m') && !time);

            if (date && time)
                return Look.DateTime;

            if (date)
                return Look.Date;

            if (time)
                return Look.Time;

            return Look.Plain;
        }

        ///<summary>One sheet's rows into the text, until it passes <paramref name="most"/> - true when it did, and the rest was not read.</summary>
        private static bool rows(Package package, string path, List<string> strings, List<Look> looks, bool date1904, StringBuilder text, int most)
        {
            using var reader = XmlReader.Create(package.Open(path), settings);

            int number = 0;

            reader.MoveToContent();

            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
                {
                    reader.Read();

                    continue;
                }

                var row = (XElement)XNode.ReadFrom(reader);
                var cells = new List<string>();
                int column = -1;

                //Both are optional, and mean the next one when left out.
                number = whole(row.Attribute("r"), number + 1);

                foreach (var cell in row.Elements().Where(each => each.Name.LocalName == "c"))
                {
                    column = columnOf((string?)cell.Attribute("r"), column + 1);

                    while (cells.Count < column)
                        cells.Add("");

                    string value = cellText(cell, strings, looks, date1904);

                    if (column < cells.Count)
                        cells[column] = value;
                    else
                        cells.Add(value);
                }

                string line = string.Join('\t', cells).TrimEnd('\t');

                if (line.Length == 0)
                    continue;

                text.Append(number.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(line).Append('\n');

                if (text.Length > most)
                    return true;
            }

            return false;
        }

        ///<summary>What a cell reads as, on one line: a tab or a line break inside it would read as the end of it, or of the row.</summary>
        private static string cellText(XElement cell, List<string> strings, List<Look> looks, bool date1904)
        {
            string type = (string?)cell.Attribute("t") ?? "n";
            string? value = (string?)childOf(cell, "v");
            string text;

            if (type == "inlineStr")
            {
                text = runs(childOf(cell, "is"));
            }
            else if (value is null)
            {
                text = formula(cell);
            }
            else if (type == "s")
            {
                text = "";

                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index >= 0 && index < strings.Count)
                    text = strings[index];
            }
            else if (type == "b")
            {
                text = "FALSE";

                if (value == "1")
                    text = "TRUE";
            }
            else if (type == "e" || type == "str" || type == "d")
            {
                text = value;
            }
            else
            {
                int style = whole(cell.Attribute("s"), 0);
                var look = Look.Plain;

                if (style >= 0 && style < looks.Count)
                    look = looks[style];

                text = number(value, look, date1904);
            }

            return text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        }

        ///<summary>A formula the file keeps no result for, as Excel's box shows it - or nothing, for one that only shares another cell's.</summary>
        private static string formula(XElement cell)
        {
            string written = ((string?)childOf(cell, "f") ?? "").Trim();

            if (written.Length == 0)
                return "";

            return "=" + written;
        }

        ///
        ///A number as its format means it. A date or a time from Excel's count of days - since the last day of 1899, or
        ///since 1904 in a workbook that says so - to the nearest second; a percentage times a hundred; anything else as
        ///the number it is, to the fifteen digits Excel keeps, so 0.1 + 0.2 is 0.3 here as it is there.
        ///
        private static string number(string written, Look look, bool date1904)
        {
            if (!double.TryParse(written, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                return written;

            if (look == Look.Percent)
                return plain(value * 100) + "%";

            if (look == Look.Plain || value < 0 || value > 2_958_465)
                return plain(value);

            long seconds = (long)Math.Round(value * 86_400);

            if (look == Look.Elapsed)
                return FormattableString.Invariant($"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}");

            //Excel counts a 29 February 1900 that never was, so its days before it start a day later.
            var start = new DateTime(1899, 12, 30);

            if (date1904)
                start = new DateTime(1904, 1, 1);
            else if (value < 60)
                start = new DateTime(1899, 12, 31);

            var when = start.AddSeconds(seconds);
            string clock = "HH:mm:ss";

            if (when.Second == 0)
                clock = "HH:mm";

            if (look == Look.Date)
                return when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            if (look == Look.Time)
                return when.ToString(clock, CultureInfo.InvariantCulture);

            return when.ToString("yyyy-MM-dd " + clock, CultureInfo.InvariantCulture);
        }

        private static string plain(double value)
        {
            return value.ToString("G15", CultureInfo.InvariantCulture);
        }

        ///<summary>A cell reference's column, from 0 - B7 is 1 - or <paramref name="otherwise"/> when it has none.</summary>
        private static int columnOf(string? reference, int otherwise)
        {
            int column = 0;
            int letters = 0;

            foreach (char c in reference ?? "")
            {
                char upper = char.ToUpperInvariant(c);

                if (upper < 'A' || upper > 'Z')
                    break;

                column = column * 26 + (upper - 'A' + 1);
                letters++;
            }

            //Excel's last column is XFD.
            if (letters == 0 || letters > 3 || column > 16_384)
                return otherwise;

            return column - 1;
        }

        #endregion

        #region A document

        ///
        ///A Word document's words: its paragraphs a line each, and its tables a row to a line with the cells between tabs,
        ///as a workbook's are. What is in a text box follows the paragraph it is anchored to. The page header and footer
        ///come first and last, said once each however many sections repeat them.
        ///
        ///**Only what the document says now**: words deleted with changes tracked are left out and words inserted are in,
        ///and a field - a page number, a date, a cross-reference - reads as what Word last showed, not its code.
        ///
        public static string Document(byte[] data)
        {
            using var package = new Package(data);

            string main = package.Main("word/document.xml");
            var root = package.Load(main);
            var relations = package.Relations(main);
            var text = new StringBuilder();

            text.Append(margins(package, root, relations, "headerReference", "[Page header] "));
            text.Append('\n');

            if (wordChild(root, "body") is XElement body)
                blocks(body, text);

            text.Append('\n');
            text.Append(margins(package, root, relations, "footerReference", "[Page footer] "));

            return tidy(text.ToString());
        }

        ///<summary>The headers, or the footers, each a line after <paramref name="label"/>: one a section names, read once however many name it, and one that says what another already said left out.</summary>
        private static string margins(Package package, XElement root, List<Relation> relations, string reference, string label)
        {
            var text = new StringBuilder();
            var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var said = new HashSet<string>(StringComparer.Ordinal);

            foreach (var named in root.Descendants().Where(each => isWord(each, reference)))
            {
                string id = relationId(named);
                var part = relations.FirstOrDefault(each => each.Id == id);

                if (part is null || !read.Add(part.Target) || !package.Has(part.Target))
                    continue;

                var words = new StringBuilder();

                blocks(package.Load(part.Target), words);

                string line = oneLine(words.ToString());

                if (line.Length > 0 && said.Add(line))
                    text.Append(label).Append(line).Append('\n');
            }

            return text.ToString();
        }

        ///<summary>What a body, a cell, a text box or a header holds, in order: paragraphs and tables, and whatever wraps them.</summary>
        private static void blocks(XElement holder, StringBuilder text)
        {
            foreach (var child in holder.Elements())
            {
                if (isWord(child, "p"))
                    paragraph(child, text);
                else if (isWord(child, "tbl"))
                    table(child, text);
                else if (isCompatibility(child, "AlternateContent"))
                    blocks(chosen(child), text);
                else
                    blocks(child, text);
            }
        }

        ///<summary>A paragraph on a line of its own, and then what any text box anchored in it says.</summary>
        private static void paragraph(XElement holder, StringBuilder text)
        {
            var line = new StringBuilder();
            var boxes = new List<XElement>();

            inline(holder, line, boxes);

            text.Append(line).Append('\n');

            foreach (var box in boxes)
                blocks(box, text);
        }

        ///
        ///The words inside a paragraph, through whatever holds its runs - a link, a field, a tracked insertion, a content
        ///control, a drawing. Not a paragraph's or a run's own settings, where a tab means a tab stop; not what a tracked
        ///change deleted or moved away, tabs and breaks included. A field's code is an element of its own, never read.
        ///
        private static void inline(XElement holder, StringBuilder line, List<XElement> boxes)
        {
            foreach (var child in holder.Elements())
            {
                string name = child.Name.LocalName;

                if (isCompatibility(child, "AlternateContent"))
                    inline(chosen(child), line, boxes);
                else if (!wordSpaces.Contains(child.Name.NamespaceName))
                    inline(child, line, boxes);
                else if (name == "t")
                    line.Append(child.Value);
                else if (name == "tab" || name == "ptab")
                    line.Append('\t');
                else if (name == "br" || name == "cr")
                    line.Append('\n');
                else if (name == "noBreakHyphen")
                    line.Append('-');
                else if (name == "txbxContent")
                    boxes.Add(child);
                else if (name != "pPr" && name != "rPr" && name != "del" && name != "moveFrom")
                    inline(child, line, boxes);
            }
        }

        ///<summary>A table a row to a line, its cells between tabs, each cell's paragraphs run together on that line.</summary>
        private static void table(XElement holder, StringBuilder text)
        {
            foreach (var row in within(holder, "tr"))
            {
                var cells = new List<string>();

                foreach (var cell in within(row, "tc"))
                {
                    var words = new StringBuilder();

                    blocks(cell, words);
                    cells.Add(oneLine(words.ToString()));
                }

                string line = string.Join('\t', cells).TrimEnd('\t');

                if (line.Length > 0)
                    text.Append(line).Append('\n');
            }
        }

        ///<summary>The rows of a table, or the cells of a row, including those a content control or custom markup wraps.</summary>
        private static IEnumerable<XElement> within(XElement holder, string name)
        {
            foreach (var child in holder.Elements())
            {
                if (isWord(child, name))
                {
                    yield return child;
                }
                else if (isWord(child, "sdt") || isWord(child, "sdtContent") || isWord(child, "customXml"))
                {
                    foreach (var found in within(child, name))
                        yield return found;
                }
            }
        }

        ///<summary>The way of writing a part that an Office that knows it reads - the first choice - or the fallback when there is no choice.</summary>
        private static XElement chosen(XElement alternatives)
        {
            foreach (var child in alternatives.Elements())
            {
                if (isCompatibility(child, "Choice"))
                    return child;
            }

            return alternatives.Elements().FirstOrDefault(each => isCompatibility(each, "Fallback")) ?? new XElement("none");
        }

        ///<summary>Text on one line, its breaks and tabs spaces and its runs of spaces one.</summary>
        private static string oneLine(string text)
        {
            return string.Join(' ', text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        ///<summary>The text with no spaces at the ends of its lines, no more than one empty line in a row, and none at either end.</summary>
        private static string tidy(string text)
        {
            var kept = new StringBuilder();
            bool empty = false;

            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.TrimEnd();

                if (trimmed.Length == 0)
                {
                    empty = kept.Length > 0;

                    continue;
                }

                if (empty)
                    kept.Append('\n');

                kept.Append(trimmed).Append('\n');
                empty = false;
            }

            return kept.ToString().TrimEnd('\n');
        }

        private static bool isWord(XElement element, string name)
        {
            return element.Name.LocalName == name && wordSpaces.Contains(element.Name.NamespaceName);
        }

        private static XElement? wordChild(XElement element, string name)
        {
            return element.Elements().FirstOrDefault(child => isWord(child, name));
        }

        private static bool isCompatibility(XElement element, string name)
        {
            return element.Name.LocalName == name && element.Name.NamespaceName == compatibility;
        }

        #endregion

        #region What the bytes are

        ///
        ///The name of the root of a package's main part - "workbook" for a workbook, "document" for a Word document - or
        ///null when the bytes are not an Office package: what a file is turned away by when it is not what its name says.
        ///
        public static string? MainElement(byte[] data)
        {
            try
            {
                using var package = new Package(data);

                string main = package.Main("");

                if (!package.Has(main))
                    return null;

                using var reader = XmlReader.Create(package.Open(main), settings);

                reader.MoveToContent();

                return reader.LocalName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion

        #region Reading the package

        ///<summary>The child of a spreadsheet element with a name, whichever namespace it was written in.</summary>
        private static XElement? childOf(XElement? element, string name)
        {
            return element?.Elements().FirstOrDefault(child => child.Name.LocalName == name && sheetSpaces.Contains(child.Name.NamespaceName));
        }

        ///<summary>The r:id an element names another part by, or "" when it names none.</summary>
        private static string relationId(XElement element)
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.LocalName == "id" && relationSpaces.Contains(attribute.Name.NamespaceName))
                    return attribute.Value;
            }

            return "";
        }

        private static bool isTrue(XAttribute? attribute)
        {
            return (string?)attribute is "1" or "true";
        }

        private static int whole(XAttribute? attribute, int otherwise)
        {
            if (int.TryParse((string?)attribute, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return value;

            return otherwise;
        }

        ///
        ///An Office file as the zip it is: its parts by name - the case a writer used ignored, as the format says - and
        ///what each part's relationships name, which is how one part finds another.
        ///
        private sealed class Package : IDisposable
        {
            private readonly ZipArchive zip;
            private readonly Dictionary<string, ZipArchiveEntry> parts = new(StringComparer.OrdinalIgnoreCase);

            public Package(byte[] data)
            {
                zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);

                foreach (var entry in zip.Entries)
                    parts[entry.FullName.Replace('\\', '/').TrimStart('/')] = entry;
            }

            public bool Has(string path)
            {
                return parts.ContainsKey(path);
            }

            ///<summary>The part the package says is its main one, or <paramref name="otherwise"/> when it does not say.</summary>
            public string Main(string otherwise)
            {
                foreach (var relation in Relations(""))
                {
                    if (relation.Type.EndsWith("/officeDocument", StringComparison.Ordinal))
                        return relation.Target;
                }

                return otherwise;
            }

            ///<summary>A part, unpacked no further than <see cref="MostPartBytes"/>.</summary>
            public Stream Open(string path)
            {
                if (!parts.TryGetValue(path, out var entry))
                    throw new InvalidDataException($"it has no {path}");

                if (entry.Length > MostPartBytes)
                    throw new InvalidDataException($"its {path} unpacks to {AiAttachment.Size(entry.Length)}, and a part can be read at {AiAttachment.Size(MostPartBytes)} at most");

                return new Bounded(entry.Open(), path);
            }

            public XElement Load(string path)
            {
                using var reader = XmlReader.Create(Open(path), settings);

                var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);

                return document.Root ?? throw new InvalidDataException($"its {path} is empty");
            }

            ///<summary>What a part's relationships name - or the package's own, for "" - with each target made a path from the root.</summary>
            public List<Relation> Relations(string part)
            {
                string folder = "";
                string file = part;
                int slash = part.LastIndexOf('/');

                if (slash >= 0)
                {
                    folder = part.Substring(0, slash + 1);
                    file = part.Substring(slash + 1);
                }

                string path = folder + "_rels/" + file + ".rels";
                var relations = new List<Relation>();

                if (!Has(path))
                    return relations;

                foreach (var relation in Load(path).Elements().Where(each => each.Name.LocalName == "Relationship"))
                {
                    if ((string?)relation.Attribute("TargetMode") == "External")
                        continue;

                    string target = (string?)relation.Attribute("Target") ?? "";

                    relations.Add(new Relation((string?)relation.Attribute("Id") ?? "", (string?)relation.Attribute("Type") ?? "", resolved(folder, target)));
                }

                return relations;
            }

            public void Dispose()
            {
                zip.Dispose();
            }

            ///<summary>A target from the folder of the part that names it, or from the root when it starts with a slash.</summary>
            private static string resolved(string folder, string target)
            {
                var path = new List<string>();
                string whole = folder + target;

                if (target.StartsWith('/'))
                    whole = target;

                foreach (string step in Uri.UnescapeDataString(whole).Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (step == "..")
                    {
                        if (path.Count > 0)
                            path.RemoveAt(path.Count - 1);
                    }
                    else if (step != ".")
                    {
                        path.Add(step);
                    }
                }

                return string.Join('/', path);
            }
        }

        ///<summary>A part read no further than <see cref="MostPartBytes"/> unpacked, whatever its zip entry claimed.</summary>
        private sealed class Bounded : Stream
        {
            private readonly Stream inner;
            private readonly string path;
            private long read;

            public Bounded(Stream inner, string path)
            {
                this.inner = inner;
                this.path = path;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => read;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int got = inner.Read(buffer, offset, count);

                read += got;

                if (read > MostPartBytes)
                    throw new InvalidDataException($"its {path} unpacks to more than {AiAttachment.Size(MostPartBytes)}, the most a part can be read at");

                return got;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    inner.Dispose();

                base.Dispose(disposing);
            }
        }

        #endregion
    }
}
