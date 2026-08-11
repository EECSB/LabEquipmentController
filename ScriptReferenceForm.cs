using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// The whole script language on one page: what each form is for, and a worked example of
/// every one of them.
///
/// Built as a formatted document rather than a block of monospace, because it is teaching
/// something nobody has seen before and a wall of fixed-width text is not teaching. Headings
/// carry the structure, prose says why a form exists, and every example is coloured by the
/// same <see cref="ScriptLanguage"/> tokenizer the editor uses — so what you read here looks
/// exactly like what you will type, down to the colours.
///
/// The examples are real commands for real instruments, not placeholders. That is the point:
/// a reference that says "&lt;command&gt;" teaches the shape and nothing else, and the shape was
/// never the hard part.
/// </summary>
public sealed class ScriptReferenceForm : Form
{
    /// <summary>One section: a heading, a paragraph, and a script that demonstrates it.</summary>
    private sealed record Section(string Heading, string Prose, string Example);

    private readonly ScriptLanguage _language;
    private readonly Document _doc = new();

    private static readonly Color CodeBack = Color.FromArgb(246, 247, 249);
    private static readonly Color CodeEdge = Color.FromArgb(214, 219, 226);

    /// <summary>
    /// A RichTextBox that can draw a box round its code blocks.
    ///
    /// The background is the text engine's own (<see cref="RichTextBox.SelectionBackColor"/>),
    /// which is the part that actually makes a block read as a block. The outline is drawn
    /// after the control has painted itself, because a RichTextBox has no notion of a border
    /// round a run of text and there is nowhere else to put one. Blocks scrolled out of view
    /// are skipped rather than clipped, so scrolling costs nothing.
    /// </summary>
    private sealed class Document : RichTextBox
    {
        private const int WM_PAINT = 0x000F;

        private readonly List<(int Start, int Length, int Columns)> _blocks = new();

        /// <summary>Height of one code line, for the bottom edge of the last one.</summary>
        private int _lineHeight = 16, _left, _charWidth = 8;

        /// <summary>
        /// Where the outlines go, and how tall a code line is. A method rather than three
        /// properties: the WinForms analyzer refuses a property on a control it cannot write
        /// into a .Designer.cs, and this control is only ever built in code.
        /// </summary>
        public void SetBoxMetrics(int lineHeight, int left, int charWidth)
        {
            _lineHeight = lineHeight;
            _left = left;
            _charWidth = charWidth;
        }

        public void ClearBlocks() => _blocks.Clear();
        public void AddBlock(int start, int length, int columns)
            => _blocks.Add((start, length, columns));

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_PAINT && _blocks.Count > 0) DrawBoxes();
        }

        private void DrawBoxes()
        {
            using Graphics g = Graphics.FromHwnd(Handle);
            using var pen = new Pen(CodeEdge);

            int pad = 3;
            foreach ((int start, int length, int columns) in _blocks)
            {
                if (start + length > TextLength) continue;

                Point top = GetPositionFromCharIndex(start);
                Point bottom = GetPositionFromCharIndex(start + length - 1);

                // Width from the block's own column count, so the outline hugs the shaded
                // rectangle rather than running out to the window edge past the end of it.
                var r = new Rectangle(_left, top.Y - pad,
                                      columns * _charWidth + pad * 2,
                                      bottom.Y - top.Y + _lineHeight + pad * 2);

                // Off the top or the bottom — GetPositionFromCharIndex clamps rather than
                // reporting that, so a block above the viewport would otherwise draw a line
                // across the first row of text.
                if (r.Bottom < 0 || r.Top > ClientSize.Height) continue;

                g.DrawRectangle(pen, r);
            }
        }
    }

    public ScriptReferenceForm(ScriptLanguage language)
    {
        _language = language;

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9f);
        Text = "Script Language Reference";
        // Wide enough that a sample line does not wrap, tall enough to hold a whole section
        // at once. At 860x760 this was a column of prose you scrolled a paragraph at a time,
        // which is a poor way to consult a reference while writing the thing it describes —
        // and it is read side by side with a script editor that is itself 1960 wide.
        ClientSize = new Size(1280, 1040);
        MinimumSize = new Size(560, 420);
        StartPosition = FormStartPosition.CenterParent;

        // The document is borderless and docked Fill, so without this the text sits flat
        // against the window edge. Less on the right, where the scrollbar already stands in
        // for a margin, and a little more at the bottom so the last line is not on the frame.
        Padding = new Padding(16, 12, 4, 14);

        _doc.Dock = DockStyle.Fill;
        _doc.ReadOnly = true;
        _doc.BorderStyle = BorderStyle.None;
        _doc.BackColor = Color.White;
        _doc.DetectUrls = false;
        _doc.ScrollBars = RichTextBoxScrollBars.Vertical;
        _doc.Margin = new Padding(0);

        // No Close button: the title bar already closes the window, and this one has nothing
        // to commit or cancel — a button that only does what the ✕ does is a button that
        // takes up the space where something meaningful would go.
        Controls.Add(_doc);

        Load += (_, _) =>
        {
            FitToScreen();

            // A fixed-width font, so one character's width sizes the whole box. Measured over
            // twenty characters because a single one rounds badly.
            using (Graphics g = _doc.CreateGraphics())
            {
                int charWidth = (int)Math.Round(g.MeasureString(new string('M', 20), _code).Width / 20);
                _doc.SetBoxMetrics(_code.Height, LogicalToDeviceUnits(22), charWidth);
            }

            Build();
            _doc.Select(0, 0);
        };
    }

    /// <summary>
    /// Never open bigger than the screen it lands on, and never off the edge of it.
    ///
    /// The size above is what the reference wants to be read at, and a 1366x768 laptop cannot
    /// give it — a window taller than the desktop puts its own bottom edge, and the scrollbar
    /// with it, out of reach. The same guard <see cref="InstrumentWindow"/> carries.
    ///
    /// This one also has to move the window, which that one does not. StartPosition is
    /// CenterParent, but the menu opens this form with Show rather than ShowDialog, and
    /// CenterParent only applies to a modal dialog — a modeless form falls back to the
    /// Windows cascade and lands near the top-left corner. So the size cannot be assumed to
    /// be centred on anything: shrinking a window whose top edge is already at y=88 leaves
    /// its bottom 88px past the taskbar. At 760 tall that was out of reach of any real
    /// screen; at 1040 it is not.
    /// </summary>
    private void FitToScreen()
    {
        Rectangle work = Screen.FromControl(this).WorkingArea;

        Size = new Size(Math.Min(Math.Max(Width, MinimumSize.Width), work.Width),
                        Math.Min(Math.Max(Height, MinimumSize.Height), work.Height));

        // Pull back only by as much as overhangs, so a window that already fits stays put.
        Location = new Point(Math.Max(work.Left, Math.Min(Left, work.Right - Width)),
                             Math.Max(work.Top, Math.Min(Top, work.Bottom - Height)));
    }

    /// <summary>
    /// Esc closes it.
    ///
    /// A form gets that for free from CancelButton, and CancelButton needs a button. With the
    /// button gone the keystroke went with it, which is not a trade anyone asked for — so it
    /// is wired directly.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ------------------------------------------------------------------------ writing

    // Fields, not properties returning a new Font each time: this document is built a few
    // hundred runs at a time, and a font per run is a GDI handle per run.
    private readonly Font _body = new("Segoe UI", 10f);
    private readonly Font _bodyBold = new("Segoe UI", 10f, FontStyle.Bold);
    private readonly Font _heading = new("Segoe UI Semibold", 13f, FontStyle.Bold);
    private readonly Font _lead = new("Segoe UI", 10f, FontStyle.Italic);
    private readonly Font _code = new("Consolas", 10.5f);

    private static readonly Color Ink = Color.FromArgb(28, 28, 28);
    private static readonly Color Quiet = Color.FromArgb(96, 96, 96);

    /// <summary>
    /// The instrument names the examples use, handed to the tokenizer as a set rather than
    /// read out of each example: a fragment showing one addressed line has no DEVICE above it,
    /// and "gen:" has to colour as an instrument here exactly as it will in the editor.
    /// </summary>
    private static readonly string[] ExampleAliases = { "gen", "scope", "dmm" };

    private void Append(string text, Font font, Color colour)
    {
        _doc.SelectionStart = _doc.TextLength;
        _doc.SelectionLength = 0;
        _doc.SelectionFont = font;
        _doc.SelectionColor = colour;
        _doc.SelectionBackColor = _doc.BackColor;
        _doc.SelectionIndent = 0;
        _doc.AppendText(text);
    }

    /// <summary>
    /// A line of prose with the key names in it picked out in bold.
    ///
    /// Split on the marker rather than parsed: these are three fixed strings, and a keystroke
    /// is the one thing in a paragraph of instructions the eye should be able to find without
    /// reading the sentence.
    /// </summary>
    private void AppendWithKeys(string text)
    {
        foreach (string part in text.Split('|'))
        {
            if (part.StartsWith('*')) Append(part[1..], _bodyBold, Ink);
            else Append(part, _body, Ink);
        }
    }

    /// <summary>
    /// A script example, coloured exactly as the editor colours it, in a shaded box.
    /// </summary>
    private void AppendCode(string script)
    {
        int indent = LogicalToDeviceUnits(28);

        IReadOnlyCollection<string> aliases = _language.IsSequence
            ? ExampleAliases
            : Array.Empty<string>();

        // Trailing blank lines would extend the shaded box past the code, so the block is
        // trimmed to what it actually contains.
        string[] lines = script.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        int blockStart = _doc.TextLength;

        // Every line padded to the same width. SelectionBackColor shades characters, not
        // lines, so without this the block's right edge follows the text and comes out
        // ragged — a stair rather than a box.
        int columns = lines.Max(l => l.Length) + 2;

        foreach (string line in lines)
        {
            int start = _doc.TextLength;

            _doc.SelectionStart = start;
            _doc.SelectionLength = 0;
            _doc.SelectionFont = _code;
            _doc.SelectionColor = Ink;
            _doc.SelectionBackColor = CodeBack;
            _doc.SelectionIndent = indent;

            _doc.AppendText(line.PadRight(columns) + "\n");

            foreach (ScriptToken t in _language.Tokenize(line, aliases))
            {
                _doc.Select(start + t.Start, t.Length);
                _doc.SelectionColor = ScriptEditor.ColourOf(t.Kind);
            }
        }

        _doc.AddBlock(blockStart, _doc.TextLength - blockStart, columns);

        _doc.SelectionStart = _doc.TextLength;
        _doc.SelectionLength = 0;
        _doc.SelectionIndent = 0;
        _doc.SelectionBackColor = _doc.BackColor;
        _doc.SelectionFont = _body;
        _doc.AppendText("\n\n");
    }

    private void Build()
    {
        _doc.Clear();
        _doc.ClearBlocks();

        Append(_language.IsSequence
                ? "Multi-instrument scripts\n"
                : "Instrument scripts\n",
            _heading, Ink);

        Append(_language.IsSequence
                ? "This language is this application's own. The commands inside it are SCPI, "
                + "which is a real standard; everything around them was invented here. Every "
                + "example below is a real command for a real instrument.\n\n"
                : "This language is this application's own. The lines that are not one of the "
                + "words below are sent to the instrument as they stand, and a line containing "
                + "'?' is a query whose reply is shown.\n\n",
            _lead, Quiet);

        foreach (Section s in Sections())
        {
            Append(s.Heading + "\n", _heading, Ink);
            Append(s.Prose + "\n\n", _body, Ink);
            AppendCode(s.Example);
        }

        Append("In the editor\n", _heading, Ink);
        AppendWithKeys("|*Tab| expands the word before the caret into a snippet, or jumps to "
                     + "the next «placeholder» in one you just inserted.\n"
                     + "|*Ctrl+Space| offers whatever can go where the caret is.\n"
                     + "|*Snippets| is this same list, and writes any of it into the editor "
                     + "for you.\n"
                     + "|*F5| runs.\n");
    }

    private IReadOnlyList<Section> Sections()
    {
        var common = new List<Section>
        {
            new("Comments",
                "A '#' or a '//' begins one. Worth writing: a script that turns on an output "
              + "is read by someone deciding whether it is safe to run.",
                "# Frequency response of the input filter.\n"
              + "// Both forms work.\n"),

            new("Waiting",
                "DELAY pauses for a number of milliseconds. Instruments settle, and a reading "
              + "taken before they have is a reading of the previous state. WAIT is the same "
              + "word.",
                "DELAY 300\n"),

            new("Messages",
                "PRINT writes a line into the output pane, which is how a long run says where "
              + "it has got to. ECHO and LOG are the same word.",
                "PRINT Sweep complete. Save CSV to plot the response.\n"),

            new("Repeating",
                "REPEAT runs the block up to its END a fixed number of times, and may be "
              + "nested.",
                "REPEAT 3\n    *IDN?\n    DELAY 500\nEND\n"),
        };

        if (!_language.IsSequence)
        {
            common.Insert(0, new Section(
                "Commands",
                "Any line that is not one of the words below is sent to the instrument exactly "
              + "as written. A line containing '?' is a query, and its reply appears in the "
              + "output pane.",
                "*IDN?\nC1:BSWV WVTP,SINE\nC1:OUTP ON\n"));

            common.Add(new Section(
                "Putting it together",
                "Set something, let it settle, then measure it — the shape almost every script "
              + "takes.",
                "PRINT Configuring channel 1...\n"
              + "C1:BSWV WVTP,SINE\n"
              + "C1:BSWV FRQ,1000\n"
              + "C1:BSWV AMP,2\n"
              + "# Enable the output only after the settings are in\n"
              + "C1:OUTP ON\n"
              + "DELAY 500\n"
              + "C1:BSWV?\n"));

            return common;
        }

        var sequence = new List<Section>
        {
            new("Naming the instruments",
                "DEVICE gives an instrument a short name and says which model it is. The model "
              + "is matched against whatever is connected, so a saved script still finds its "
              + "instruments after DHCP has moved them.",
                "DEVICE gen : SDG2042X\nDEVICE scope : DS2202\n"),

            new("Addressing a line",
                "A command has to say which instrument it is for — by prefix, or by sitting "
              + "inside a WITH block. A line that does not is an error, not a default: guessing "
              + "which instrument to drive is not something this app does.",
                "gen: C1:OUTP ON\n"
              + "\n"
              + "WITH gen\n"
              + "    C1:BSWV WVTP,SINE\n"
              + "    C1:BSWV AMP,2\n"
              + "END\n"),

            new("Sweeping",
                "FOR steps a value and runs its block at each one. STEP gives an even spacing; "
              + "POINTS … LOG spaces them per decade, which is how a filter or a frequency "
              + "response is actually measured — a linear sweep from 100 Hz to 100 kHz spends "
              + "almost every point above 10 kHz and skims over the corner. Numbers may carry "
              + "an engineering suffix: 1k, 2.5M, 100m.",
                "FOR f = 20M TO 35M STEP 100k\n    gen: C1:BSWV FRQ,$f\nEND\n"
              + "\n"
              + "FOR f = 100 TO 100k POINTS 40 LOG\n    gen: C1:BSWV FRQ,$f\nEND\n"),

            new("Keeping a reading",
                "'-> name' captures a query's reply, and $name uses it further down. RECORD "
              + "appends a row to the results table; COLUMNS names those columns and belongs "
              + "near the top.",
                "COLUMNS Frequency (Hz), Vout (Vrms)\n"
              + "\n"
              + "scope: :MEASure:VRMS? CHANnel1 -> vout\n"
              + "RECORD $f, $vout\n"),
        };

        sequence.AddRange(common);

        sequence.Add(new Section(
            "A whole measurement",
            "Declare the instruments, name the columns, sweep, record — then save the table as "
          + "CSV, or read the shape of it on the Plot tab.",
            "DEVICE gen : SDG2042X\n"
          + "DEVICE scope : DS2202\n"
          + "COLUMNS Frequency (Hz), Vout (Vrms)\n"
          + "\n"
          + "WITH gen\n"
          + "    C1:BSWV WVTP,SINE\n"
          + "    C1:BSWV AMP,2\n"
          + "    # Enable the output only after the settings are in\n"
          + "    C1:OUTP ON\n"
          + "END\n"
          + "\n"
          + "FOR f = 100 TO 100k POINTS 40 LOG\n"
          + "    gen: C1:BSWV FRQ,$f\n"
          + "    # Let the circuit and the scope settle\n"
          + "    DELAY 300\n"
          + "    scope: :MEASure:VRMS? CHANnel1 -> vout\n"
          + "    RECORD $f, $vout\n"
          + "END\n"
          + "\n"
          + "gen: C1:OUTP OFF\n"
          + "PRINT Sweep complete.\n"));

        return sequence;
    }
}
