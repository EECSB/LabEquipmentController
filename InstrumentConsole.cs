using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// The command console for one connected instrument: quick-command buttons, the log, and
/// the command input — plus the tools that act on that instrument (script editor, command
/// discovery, screen and waveform capture).
///
/// It is a UserControl rather than part of the main form so that several can exist at once,
/// one per <see cref="InstrumentSession"/>, and so a console can be lifted out of its tab
/// and dropped into its own window without rebuilding anything (see
/// <see cref="InstrumentWindow"/>).
///
/// Built in code, with docked/auto-sized layout throughout: a control created at runtime
/// misses the form's AutoScaleMode.Font pass, so hardcoded pixel sizes would come out wrong
/// on any display that isn't exactly 96 DPI. Heights are normalised in
/// <see cref="NormalizeButtonHeights"/> instead, the same way ScriptForm's toolbar is.
/// </summary>
public sealed class InstrumentConsole : UserControl
{
    private readonly Label _lblHeader = new();
    private readonly Button _btnDetach = new();

    private readonly FlowLayoutPanel _flowQuick = new();

    /// <summary>Drag handle under the quick-command strip. See BuildQuickSplitter.</summary>
    private readonly Splitter _quickSplitter = new();

    /// <summary>
    /// The height the user dragged the strip to, or null while it sizes itself to its
    /// buttons. Deliberately per-console and not persisted: a console lasts as long as its
    /// connection, and the next instrument may have a different number of buttons entirely.
    /// </summary>
    private int? _quickHeight;
    private readonly FlowLayoutPanel _flowTools = new();
    private readonly Button _btnScript = new();
    private readonly Button _btnDiscover = new();
    private readonly Button _btnCapture = new();
    private readonly Button _btnWaveform = new();
    private readonly Button _btnReadout = new();
    private readonly Button _btnDatasheet = new();

    private readonly ResultsPanel _results = new();

    /// <summary>The queue strip above the log — see QueueStrip.</summary>
    private readonly QueueStrip _queueBar = new();

    /// <summary>
    /// The commands waiting on the connection, drawn as one run — an arrow, the command, an
    /// arrow, the next — inside a box that hugs the text rather than spanning the strip.
    ///
    /// Drawn rather than a Label because a Label's box is the whole control, and the whole
    /// control is as wide as the log. What is wanted is a mark around the queue itself, so it
    /// reads as one object that grows and shrinks, and so an empty queue leaves nothing behind.
    /// </summary>
    private sealed class QueueStrip : Panel
    {
        private string[] _items = Array.Empty<string>();

        public QueueStrip()
        {
            DoubleBuffered = true;      // it repaints on every queue change

            // Everything drawn here is positioned from the control's own width — the section
            // border spans it, and the chips are laid out until they run out of it. Without
            // this, widening the pane leaves the old border where it was and never paints the
            // ground it uncovered, which shows up as a border stopping short of the edge with
            // fragments beyond it.
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        public void ShowQueue(IReadOnlyList<string> items)
        {
            _items = items.ToArray();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int pad = LogicalToDeviceUnits(4);
            int gap = LogicalToDeviceUnits(6);

            // The section's own border, drawn whether or not anything is queued. An outline
            // that appears with the first command and vanishes with the last reads as a thing
            // arriving rather than a place where things arrive.
            var section = new Rectangle(
                Padding.Left,
                pad / 2,
                Math.Max(0, Width - Padding.Left - Padding.Right - 1),
                Math.Max(0, Height - pad - 1));

            using var edge = new Pen(Color.FromArgb(190, 190, 190));
            using var chipEdge = new Pen(Color.FromArgb(150, 150, 150));
            using var ink = new SolidBrush(ForeColor);

            g.DrawRectangle(edge, section);
            if (_items.Length == 0) return;

            // Inside it, one small box per command with an arrow between: each is a thing
            // waiting its turn, and the arrows are the order they will be taken in.
            g.SetClip(Rectangle.Inflate(section, -1, -1));

            SizeF arrow = g.MeasureString("▶", Font);
            float x = section.Left + gap;

            for (int i = 0; i < _items.Length; i++)
            {
                SizeF size = g.MeasureString(_items[i], Font);
                float lead = i > 0 ? arrow.Width + gap : 0;

                // Room for the whole thing, arrow included, checked before either is drawn.
                // Otherwise a queue that runs off the end leaves a dangling arrow pointing at
                // a command that was never painted.
                if (x + lead + size.Width + pad * 2 > section.Right) break;

                if (i > 0)
                {
                    g.DrawString("▶", Font, ink, x + gap / 2f,
                                 section.Top + (section.Height - arrow.Height) / 2f);
                    x += lead;
                }

                var chip = new RectangleF(
                    x,
                    section.Top + (section.Height - size.Height) / 2f - pad / 2f,
                    size.Width + pad * 2,
                    size.Height + pad);

                g.DrawRectangle(chipEdge, chip.X, chip.Y, chip.Width, chip.Height);
                g.DrawString(_items[i], Font, ink, chip.X + pad, chip.Y + pad / 2f);

                x = chip.Right;
            }

            g.ResetClip();
        }
    }

    private SplitContainer _logSplit = null!;
    private bool _splitPlaced;
    private TableLayoutPanel _header = null!;
    private TableLayoutPanel _commandRow = null!;
    private readonly Button _btnClearLog = new();
    private readonly Button _btnSaveLog = new();

    private readonly RichTextBox _log = new();
    private readonly Panel _txtHost = new();
    private readonly TextBox _txtCommand = new();
    private readonly Button _btnSend = new();

    private readonly ToolTip _tips = new();
    private ScriptForm? _scriptForm;
    private CommandReferenceForm? _referenceForm;
    private MultimeterReadoutForm? _readoutForm;
    private bool _detached;

    /// <summary>The connection this console drives.</summary>
    public InstrumentSession Session { get; }

    /// <summary>Raised when the user asks for this console to be moved into its own window.</summary>
    public event EventHandler? DetachRequested;

    /// <summary>Raised when a detached console asks to go back into the main window's tabs.</summary>
    public event EventHandler? ReattachRequested;

    public InstrumentConsole(InstrumentSession session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));

        // Let the hosting window own scaling and the font. Inherit is the documented
        // choice for a UserControl dropped into an already-scaled Form, and inheriting the
        // font keeps the console looking identical in a tab and in a detached window.
        // (Measured: declaring AutoScaleMode.Font here instead changes none of the host's
        // metrics either way — this is about not owning a decision twice, not a bug fix.)
        AutoScaleMode = AutoScaleMode.Inherit;

        BuildUi();
        BuildQuickCommands();
        SetTooltips();
        UpdateEnabledState();
    }

    // ------------------------------------------------------------------- layout

    private void BuildUi()
    {
        // --- header: what this console is talking to, and what can be done with it ---
        _header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            // Generous bottom padding: this row says what the console is attached to and how
            // to move its window, and the strip below it sends commands to an instrument.
            // Those are different jobs, and the gap is what says so.
            Padding = new Padding(6, 4, 6, 18),
        };
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // Identities are long, and this line must stay one line so the header's height
        // doesn't change with the window width. A Label always word-wraps within whatever
        // height it is given, so it is anchored (not docked) and pinned to a single line's
        // height in NormalizeButtonHeights — then AutoEllipsis clips the overflow with "…".
        // The full text stays available in the tooltip and on the tab.
        _lblHeader.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblHeader.AutoSize = false;
        _lblHeader.AutoEllipsis = true;
        _lblHeader.TextAlign = ContentAlignment.MiddleLeft;
        _lblHeader.Text = Session.Description;
        _header.Controls.Add(_lblHeader, 0, 0);

        // Detach alone up here — it is about this console's *window*, which is what the rest
        // of the header is about. Disconnect is about the instrument's session and lives at
        // the far end of the log strip below, well away from anything clicked routinely.
        ConfigureButton(_btnDetach, "Detach", (_, _) => OnDetachClicked());
        _btnDetach.Margin = Padding.Empty;
        _header.Controls.Add(_btnDetach, 1, 0);

        // --- quick commands + per-instrument tools ---
        // Height is managed by CapQuickStrip rather than by AutoSize, so that the user can
        // drag the strip taller than its automatic size. AutoSize would simply undo the
        // splitter's assignment on the next layout pass.
        BuildQuickSplitter();

        _flowQuick.Dock = DockStyle.Top;
        _flowQuick.AutoSize = false;
        _flowQuick.WrapContents = true;      // wrap rather than clip when the console is narrow
        _flowQuick.AutoScroll = true;        // ... and scroll rather than eat the log — see OnResize
        _flowQuick.Padding = new Padding(6, 4, 6, 6);

        // --- tools: a strip of their own, set off from the quick commands above ---
        //
        // The strip above sends SCPI the moment it is clicked; everything here opens a window
        // or starts a job. Mixing the two put "Capture Waveform" next to "CH1 On" as though
        // they were the same kind of thing, and a gap is the cheapest way to say they are not.
        _flowTools.Dock = DockStyle.Top;
        _flowTools.AutoSize = true;
        _flowTools.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _flowTools.WrapContents = true;
        // Close to the quick commands above, because §6 groups them together: both ask the
        // instrument for something, and only the vocabulary differs — one row is the
        // instrument's own measurements, the other is what the app can do with it. The wide
        // gap was here and it read as a divider between them.
        _flowTools.Padding = new Padding(6, 6, 6, 6);

        ConfigureButton(_btnScript, "Scripts…", (_, _) => OpenScriptEditor());
        ConfigureButton(_btnDiscover, "Discover Commands", async (_, _) => await DiscoverCommandsAsync());
        ConfigureButton(_btnCapture, "Capture Screen", async (_, _) => await CaptureScreenAsync());
        ConfigureButton(_btnWaveform, "Capture Waveform", async (_, _) => await CaptureWaveformAsync());
        ConfigureButton(_btnReadout, "Live Readout…", (_, _) => OpenReadout());
        ConfigureButton(_btnDatasheet, "AI Datasheet Extraction", (_, _) => OpenDatasheetExtract());
        _flowTools.Controls.Add(_btnScript);
        _flowTools.Controls.Add(_btnDiscover);
        _flowTools.Controls.Add(_btnCapture);
        _flowTools.Controls.Add(_btnWaveform);
        _flowTools.Controls.Add(_btnReadout);
        _flowTools.Controls.Add(_btnDatasheet);

        // The quick strip is capped against whatever this row leaves, and this row re-wraps
        // to a second line without the console itself changing size — so OnResize never fires
        // and the cap is never recomputed. Its own size change is the signal that matters.
        //
        // Posted rather than called: SizeChanged arrives in the middle of the parent's layout
        // pass, and resizing a sibling from there leaves the rest of that pass placing things
        // from the old numbers — the splitter ended up drawn inside the strip it divides.
        _flowTools.SizeChanged += (_, _) =>
        {
            if (IsHandleCreated) BeginInvoke(CapQuickStrip);
        };

        // --- command input, with the log's two buttons on the same row ---
        //
        // Clear Log and Save Log had a strip of their own above the log, along with a
        // Disconnect button. Disconnect has gone: the tab's ✕ ends the session, and so does
        // the tab's right-click menu, so the button was a third way to do the same thing.
        // Losing the whole strip hands the log back the eighty pixels it was costing, which
        // is the one thing this console has never had enough of.
        _commandRow = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 1,
            // Shared with the results pane's button strip, which docks to the bottom of the
            // other half of the same splitter and has to land on the same line.
            Padding = ResultsPanel.CommandRowInset,
        };
        _commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < 3; i++)
            _commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // A single-line TextBox is font-height-locked and cannot be made as tall as the Send
        // button beside it. So the border is drawn by a host panel, which can be any height,
        // and the box itself is borderless and centred inside it. What reads as one tall
        // input is a panel with a text box floating in the middle of it.
        _txtHost.Dock = DockStyle.Fill;
        _txtHost.BorderStyle = BorderStyle.FixedSingle;
        _txtHost.BackColor = SystemColors.Window;
        _txtHost.Margin = new Padding(0, 0, 6, 0);
        _txtHost.Padding = new Padding(4, 0, 4, 0);

        _txtCommand.BorderStyle = BorderStyle.None;
        // Left only: CentreCommandBox owns the width, and two owners disagreed.
        _txtCommand.Anchor = AnchorStyles.Left;
        _txtCommand.Font = new Font("Consolas", 10F);
        _txtCommand.PlaceholderText = "Type a SCPI command and press Enter   (e.g.  *IDN?)";
        _txtCommand.KeyDown += TxtCommand_KeyDown;
        _txtHost.Controls.Add(_txtCommand);
        _txtHost.SizeChanged += (_, _) => CentreCommandBox();
        _commandRow.Controls.Add(_txtHost, 0, 0);

        ConfigureButton(_btnSend, "Send", async (_, _) => await SendCurrentCommandAsync());
        _btnSend.Margin = Padding.Empty;
        _commandRow.Controls.Add(_btnSend, 1, 0);

        // Commands take their turn on the connection rather than interleaving, so a fast
        // hand — or Enter held down — builds a queue. Say how deep it is on the button that
        // made it, otherwise the console looks as though it has stopped listening.
        if (Session.Client is SerializedInstrumentClient queued)
            queued.PendingChanged += (_, _) =>
            {
                // Raised on whichever thread ran the exchange, and the handle may already be
                // gone if the tab is closing.
                if (IsHandleCreated) BeginInvoke(() => ShowQueue(queued.Queued));
            };

        // Send acts on the box to its left; these two act on the log above. The gap after
        // Send is what says they are different jobs sharing a row.
        ConfigureButton(_btnClearLog, "Clear Log", (_, _) => _log.Clear());
        ConfigureButton(_btnSaveLog, "Save Log", (_, _) => SaveLog());
        _btnClearLog.Margin = new Padding(LogicalToDeviceUnits(40), 0, 0, 0);
        _btnSaveLog.Margin = new Padding(LogicalToDeviceUnits(6), 0, 0, 0);
        _commandRow.Controls.Add(_btnClearLog, 2, 0);
        _commandRow.Controls.Add(_btnSaveLog, 3, 0);

        // --- log ---  (in a padded host so it doesn't run into the control's edges)
        _log.Dock = DockStyle.Fill;
        _log.ReadOnly = true;
        _log.DetectUrls = false;
        _log.BackColor = Color.FromArgb(24, 24, 24);
        _log.ForeColor = Color.Gainsboro;
        _log.Font = new Font("Consolas", 9.5F);
        var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 6, 0) };
        logHost.Controls.Add(_log);

        // Log on the left, recorded readings on the right. Side by side rather than stacked
        // because height is the thing this console never has enough of, and a vertical split
        // costs none of it.
        // Minimums applied once it has a size — see SplitLayout.SetMinimums; in an initialiser
        // they throw out of the setter while the control is still 150px wide.
        _logSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
        };
        // The log, the command box, Send and the two log buttons are one column: they all act
        // on the log, and they resize with it. The command row used to be docked across the
        // whole console, which ran it underneath the results pane and made the two panes
        // resize against a row that belonged to only one of them.
        //
        // The queue, along the top of the log column: every command joins the right-hand end
        // as it is sent and leaves the left as the instrument answers it. A count on the Send
        // button said how many were waiting but never which, so a burst of identical-looking
        // clicks told you nothing about what was actually still to come.
        //
        // Matched to the Results/Plot tab band across the splitter, so the two columns begin
        // their content on the same line.
        _queueBar.Dock = DockStyle.Top;
        _queueBar.Padding = new Padding(8, 0, 8, 0);
        _queueBar.Font = new Font("Consolas", 9f);
        _queueBar.ForeColor = Color.FromArgb(60, 60, 60);
        _queueBar.BackColor = Color.FromArgb(238, 238, 238);

        // Fill first, then the docked edges (this project's docking convention).
        _logSplit.Panel1.Controls.Add(logHost);
        _logSplit.Panel1.Controls.Add(_commandRow);
        _logSplit.Panel1.Controls.Add(_queueBar);

        _results.Dock = DockStyle.Fill;
        _results.FileStem = Session.Title.Replace(':', '-');
        // The wall clock the reading was taken at, to the millisecond. The plotter reads a
        // clock as seconds since midnight and labels the axis back as a time, so this one
        // string is the timestamp in the table, the position on the curve, and the stamp in
        // the exported CSV. Milliseconds because readings can arrive several to the second,
        // and an axis that cannot separate them is an axis that stacks them.
        _results.SetColumns(new[] { "Time", "Command", "Value" });
        _logSplit.Panel2.Controls.Add(_results);

        // Placed on the first layout that has a real width, not in OnLoad: the console is
        // built before it is put in a tab, so at Load the split is still a few hundred pixels
        // wide and 62% of that leaves the log a column. Posted rather than set inline for the
        // usual reason — this arrives mid-layout.
        _logSplit.SizeChanged += (_, _) =>
        {
            if (_splitPlaced || !IsHandleCreated) return;
            if (_logSplit.Width < 320) return;
            _splitPlaced = true;
            BeginInvoke(() =>
            {
                SplitLayout.SetMinimums(_logSplit, 160, 120);
                // Even halves. The log used to take the larger share, which left the plot too
                // narrow to read an axis off wherever the console was not already wide.
                SplitLayout.SetFraction(_logSplit, 0.5);
            });
        };

        // Fill first, then docked edges (this project's convention for docking order):
        // the last control added is docked outermost, so the header ends up at the top and
        // the log tools end up immediately above the log.
        Controls.Add(_logSplit);
        // Tools before quick commands: with Dock.Top the last one added sits outermost, so
        // this order puts the quick-command strip above the tools strip on screen.
        Controls.Add(_flowTools);
        // The splitter goes between them, so it resizes the strip above it. A Top-docked
        // splitter drags the control it was added after.
        Controls.Add(_quickSplitter);
        Controls.Add(_flowQuick);
        Controls.Add(_header);
    }

    /// <summary>
    /// Let the quick-command strip be dragged taller.
    ///
    /// Its automatic height is one thing on a generator with eight buttons and quite another
    /// on a meter with eighteen, or on an FSL, whose catalog is large enough that a future
    /// profile could carry a great many. Capping it protects the log (see
    /// <see cref="CapQuickStrip"/>) but leaves the rest behind a scrollbar, and a scrollbar
    /// is a poor place to keep buttons you press all day.
    /// </summary>
    private void BuildQuickSplitter()
    {
        _quickSplitter.Dock = DockStyle.Top;
        _quickSplitter.Height = LogicalToDeviceUnits(5);
        _quickSplitter.BackColor = SystemColors.Control;
        _quickSplitter.MinExtra = LogicalToDeviceUnits(120);   // always leave a usable log
        _quickSplitter.MinSize = LogicalToDeviceUnits(30);     // ...and at least one button row

        // A drag fixes the height until it is let go of again; the strip stops following
        // the button count and follows the user instead.
        _quickSplitter.SplitterMoved += (_, _) =>
        {
            _quickHeight = _flowQuick.Height;
            CapQuickStrip();
        };

        // Double-click hands it back to the automatic sizing, which is the standard way out
        // of a splitter you have dragged somewhere unhelpful.
        _quickSplitter.DoubleClick += (_, _) =>
        {
            _quickHeight = null;
            CapQuickStrip();
        };
    }

    /// <summary>Common setup for every button here: auto-sized so it scales with DPI without clipping.</summary>
    private static void ConfigureButton(Button b, string text, EventHandler onClick)
        => ButtonStyle.Apply(b, text, onClick);

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        SetButtonIcons();          // after the handle exists, so the DPI is known
        _results.ApplyIcons();     // ...including the results pane's two, before measuring
        NormalizeButtonHeights();  // then make every button in a row one height
        _results.PinToolHeight();  // ...which is what the results strip sizes itself to
        SizeCommandSeparator();    // ...and the buttons' widths decide the separator

        // The tab band's height is only known once the results pane has been laid out.
        int band = _results.TabStripHeight;
        _queueBar.Height = band > 0 ? band : _queueBar.Font.Height + LogicalToDeviceUnits(10);
        CapQuickStrip();
    }

    /// <summary>
    /// Let the gap before Clear Log close when the row is too narrow to afford it.
    ///
    /// That gap is deliberate — it is what says Send acts on the box while Clear Log and
    /// Save Log act on the log above — and at 40 logical pixels it costs nothing on the main
    /// window, where the row is well over a thousand wide. Detached is another matter: that
    /// window gives the log side 719 pixels, and Send, the two log buttons and a fixed gap
    /// took 511 of them, leaving the command box 190. The box is the working surface of the
    /// console and it was the thing being squeezed.
    ///
    /// So the separator is the part that yields. It keeps its full width while the box can
    /// still have a usable one and gives back whatever is needed after that, down to the
    /// ordinary spacing between two buttons. Nothing here resizes the box: the row's first
    /// column is Percent 100 and takes the leftover on its own, which it was always doing
    /// correctly — the leftover was simply being spent elsewhere.
    /// </summary>
    private void SizeCommandSeparator()
    {
        if (_commandRow == null) return;

        int full = LogicalToDeviceUnits(40);   // what it wants
        int least = LogicalToDeviceUnits(6);   // what two adjacent buttons get
        int wantBox = LogicalToDeviceUnits(140);

        int fixedPart = _btnSend.Width + _btnClearLog.Width + _btnSaveLog.Width
                      + _txtHost.Margin.Horizontal + _btnSaveLog.Margin.Horizontal;
        int room = _commandRow.DisplayRectangle.Width - fixedPart;
        if (room <= 0) return;                 // mid-layout; a later pass has real numbers

        int gap = Math.Max(least, Math.Min(full, room - wantBox));
        if (_btnClearLog.Margin.Left == gap) return;
        _btnClearLog.Margin = new Padding(gap, 0, 0, 0);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        CapQuickStrip();
        SizeCommandSeparator();
    }

    /// <summary>
    /// Stop the command strip from crowding out the log.
    ///
    /// The strip is docked to the top and grows as its buttons wrap, and the log below it
    /// merely fills what is left. On a short console — a meter has eighteen buttons, and
    /// they wrap to three or four rows when the window is narrow — "what is left" reached
    /// zero, and the log, the log tools and part of the strip itself simply vanished off the
    /// bottom. Capping the strip and letting it scroll means the console loses a row of
    /// buttons you can scroll to, rather than the output you came to read.
    /// </summary>
    private void CapQuickStrip()
    {
        if (Height <= 0 || _header == null || _commandRow == null) return;

        // Work back from what must remain visible rather than guessing a percentage: the
        // header, the tool strip, the log's own toolbar, the command box, and a few lines
        // of log.
        //
        // The tool strip has to be counted too. It is docked to the top and AutoSize, so it
        // wraps to a second row as soon as the console is narrower than its six buttons laid
        // end to end — and that row was being taken out of the log rather than out of the
        // quick strip this method caps. At the default window width it took all of it: the
        // log measured zero pixels high and the log toolbar sat on top of the command box.
        //
        // PreferredSize rather than Height: this runs from OnResize, before the strip has
        // re-wrapped to the new width, so Height is still the answer for the old one.
        int tools = _flowTools.ClientSize.Width > 0
            ? _flowTools.GetPreferredSize(new Size(_flowTools.ClientSize.Width, 0)).Height
            : _flowTools.Height;

        // The command row is no longer counted here: it lives inside the split below, so it
        // comes out of the log's own column rather than off the top of everything.
        int fixedRows = _header.Height + tools + _quickSplitter.Height;

        // Room for the strip, in order of preference: everything else plus a few lines of log;
        // failing that, everything else plus one line; failing that, a single row of buttons
        // with the rest scrolled.
        //
        // The middle step is the one that was missing. The floor used to be absolute, and a
        // floor that cannot yield does not prevent the squeeze — it just moves it somewhere
        // that has no floor of its own. Below about 424px the fixed rows plus that floor came
        // to more than the console had, so the log measured zero and the log toolbar was laid
        // down on top of the command box.
        int cap = Height - fixedRows - LogicalToDeviceUnits(64);
        if (cap < LogicalToDeviceUnits(52))
            cap = Height - fixedRows - LogicalToDeviceUnits(16);
        cap = Math.Max(LogicalToDeviceUnits(30), cap);

        // ...and never more than the console actually has left, whatever the floors say. This
        // is the same trap one step down: a minimum that cannot yield does not stop the
        // squeeze, it just pushes it into the rows below, which have no minimum to protect
        // them and simply overlap.
        cap = Math.Min(cap, Math.Max(0, Height - fixedRows));

        // What the buttons want, now that AutoSize is off and this has to work it out. The
        // proposed width is the strip's own, because the wrap point — and so the number of
        // rows — depends on it.
        //
        // Until the first real layout that width is zero, and a zero width wraps every
        // button onto its own row: the strip would ask for the whole console and get the
        // cap. Wait for a width rather than act on that.
        if (_flowQuick.ClientSize.Width <= 0) return;
        int natural = _flowQuick.GetPreferredSize(new Size(_flowQuick.ClientSize.Width, 0)).Height;

        // A dragged height wins over the automatic one, but not over the cap: the log has
        // to survive the console being made short, however the strip was sized.
        // The floors are already in the cap; applying one again here would undo the clamp
        // above and put the overlap straight back.
        int want = _quickHeight ?? natural;
        _flowQuick.Height = Math.Max(0, Math.Min(want, cap));
    }

    private void SetButtonIcons()
    {
        void Ico(Button b, string name) => ButtonStyle.SetIcon(this, b, name);

        Ico(_btnClearLog, "reset");
        SetDrawnIcon(_btnSaveLog, "save");
        Ico(_btnScript, "program");
        Ico(_btnSend, "stepClock");

        // No bundled artwork fits the rest, so they are drawn — see AppIcons.Drawn.
        SetDrawnIcon(_btnDiscover, "search");
        SetDrawnIcon(_btnCapture, "camera");
        SetDrawnIcon(_btnWaveform, "wave");
        SetDrawnIcon(_btnReadout, "bars");
        SetDrawnIcon(_btnDatasheet, "ai");
        UpdateDetachIcon();
    }

    private void SetDrawnIcon(Button b, string glyph) => ButtonStyle.SetDrawnIcon(this, b, glyph);

    /// <summary>The detach button's glyph points out of the window, or back into it.</summary>
    private void UpdateDetachIcon() => SetDrawnIcon(_btnDetach, _detached ? "attach" : "detach");

    /// <summary>
    /// One height for every button in this console (SPEC §14) — header, quick-command strip,
    /// log tools and Send alike. They used to be measured three different ways and came out
    /// three different heights: the strip took its own tallest control, the header took the
    /// taller of its two, and Send took the command box's font-driven height.
    /// </summary>
    private void NormalizeButtonHeights()
    {
        var buttons = new List<Button> { _btnDetach, _btnSend, _btnClearLog, _btnSaveLog };
        buttons.AddRange(_results.Buttons);
        foreach (Control c in _flowQuick.Controls) if (c is Button b) buttons.Add(b);
        foreach (Control c in _flowTools.Controls) if (c is Button b) buttons.Add(b);

        int h = ButtonStyle.Normalize(this, buttons.ToArray());

        // The command box's host takes the button height; the box floats in the middle of it.
        _txtHost.Height = h;
        CentreCommandBox();

        // One line of text, measured now that the real font is in effect.
        _lblHeader.Height = _lblHeader.PreferredHeight;
    }

    /// <summary>
    /// Float the (font-height-locked) command box in the middle of its host panel, and give
    /// it the host's full width.
    ///
    /// The width is set here rather than left to the Left|Right anchor because an anchor only
    /// preserves the margins the control had when its parent was first laid out. Detaching
    /// reparents this whole console into a new window, and the box came out of that zero
    /// pixels wide — a console you could read but not type into. Setting it on every
    /// SizeChanged has no such history to get wrong.
    /// </summary>
    private void CentreCommandBox()
    {
        Rectangle inner = _txtHost.DisplayRectangle;   // honours the host's side padding
        _txtCommand.Left = inner.Left;
        _txtCommand.Width = Math.Max(0, inner.Width);
        _txtCommand.Top = inner.Top + Math.Max(0, (inner.Height - _txtCommand.Height) / 2);
    }

    /// <summary>
    /// Build the quick-command strip for this instrument. The buttons carry their SCPI text
    /// in Tag, which is also how <see cref="UpdateEnabledState"/> tells them apart from the
    /// fixed tool buttons beside them.
    /// </summary>
    private void BuildQuickCommands()
    {
        _flowQuick.SuspendLayout();

        int index = 0;
        foreach (QuickCommand qc in Session.Profile.Commands)
        {
            var b = new Button { Tag = qc.Command };
            ConfigureButton(b, qc.Label, async (s, _) =>
            {
                if (s is Button btn && btn.Tag is string cmd) await SendCommandAsync(cmd);
            });
            _tips.SetToolTip(b, $"Sends  {qc.Command}");   // hover shows the actual SCPI

            // Give the transport-control commands a matching glyph.
            string? icon = qc.Label switch
            {
                "Run" => "startClock",
                "Stop" => "stopClock",
                "Single" => "stepClock",
                _ => null,
            };
            if (icon != null) ButtonStyle.SetIcon(this, b, icon);

            _flowQuick.Controls.Add(b);
            _flowQuick.Controls.SetChildIndex(b, index++);   // keep the tool buttons last
        }

        _flowQuick.ResumeLayout();
    }

    /// <summary>
    /// Describe every control in a hover tooltip. Anything added to this console should get
    /// an entry here — the labels alone don't explain what the controls do. (Quick-command
    /// buttons are tipped as they are built, with the SCPI they send.)
    /// </summary>
    private void SetTooltips()
    {
        _tips.AutoPopDelay = 15000;   // these are sentences; give them time to be read

        _tips.SetToolTip(_lblHeader, "The instrument this console is connected to: address, "
                                   + "transport, recognised type, and its *IDN? reply.");
        _tips.SetToolTip(_btnDetach, "Move this console into its own window, so several "
                                   + "instruments can be watched side by side. Closing that "
                                   + "window puts the console back in a tab.");
        _tips.SetToolTip(_btnClearLog, "Clear the console log above.");
        _tips.SetToolTip(_btnSaveLog, "Save the console log to a text file.");
        _tips.SetToolTip(_btnScript, "Open a script editor to run a sequence of SCPI commands "
                                   + "against this instrument.");
        _tips.SetToolTip(_btnDiscover, "Ask the instrument to list the commands it supports. "
                                     + "If it can't, opens the built-in command reference for it instead.");
        _tips.SetToolTip(_btnCapture, "Download the instrument's screen as an image you can save.");
        _tips.SetToolTip(_btnWaveform, "Download the channel 1 trace and plot it, with CSV export.");
        _tips.SetToolTip(_btnReadout, "Poll one measurement on a timer and plot it against "
                                    + "time — for watching a value drift or settle. Offered for "
                                    + "instruments that return a reading per query, such as a multimeter.");
        _tips.SetToolTip(_log, "Log of commands sent to this instrument and replies received.");
        _tips.SetToolTip(_txtCommand, "Type a SCPI command and press Enter to send it. Use the "
                                    + "Up and Down arrows to recall earlier commands.");
        _tips.SetToolTip(_btnSend, "Send the command in the box to this instrument.");
    }

    // --------------------------------------------------------------- host state

    /// <summary>True when this console lives in its own window rather than a tab.</summary>
    public bool IsDetached => _detached;

    /// <summary>Tell the console which way round it is being hosted, so its button reads right.</summary>
    public void SetDetached(bool detached)
    {
        _detached = detached;
        _btnDetach.Text = detached ? "Re-attach" : "Detach";
        if (IsHandleCreated) UpdateDetachIcon();
        _tips.SetToolTip(_btnDetach, detached
            ? "Put this console back into a tab in the main window."
            : "Move this console into its own window, so several instruments can be watched "
            + "side by side. Closing that window puts the console back in a tab.");
    }

    private void OnDetachClicked()
    {
        if (_detached) ReattachRequested?.Invoke(this, EventArgs.Empty);
        else DetachRequested?.Invoke(this, EventArgs.Empty);
    }

    public void FocusCommandInput()
    {
        if (_txtCommand.Enabled) _txtCommand.Focus();
    }

    /// <summary>Close the windows this console owns. Called when its session goes away.</summary>
    public void CloseChildWindows()
    {
        if (_scriptForm is { IsDisposed: false })
        {
            _scriptForm.AllowClose();
            _scriptForm.Close();
            _scriptForm = null;
        }
        if (_referenceForm is { IsDisposed: false })
        {
            _referenceForm.Close();
            _referenceForm = null;
        }
        if (_readoutForm is { IsDisposed: false })
        {
            _readoutForm.Close();   // its FormClosing stops the polling loop
            _readoutForm = null;
        }
    }

    /// <summary>
    /// Enable or disable everything that talks to the instrument. Disabled while a script is
    /// driving the link — two request/response streams on one connection would collide.
    /// </summary>
    private void UpdateEnabledState()
    {
        bool live = Session.IsConnected && !Session.IsBusy;

        _txtCommand.Enabled = live;
        // The host draws the border and the background, so it has to grey out with the box.
        _txtHost.BackColor = live ? SystemColors.Window : SystemColors.Control;
        _btnSend.Enabled = live;
        _btnDiscover.Enabled = live;
        _btnCapture.Enabled = live && Session.Profile.ScreenCaptureCommand != null;
        _btnWaveform.Enabled = live && Session.Profile.SupportsWaveformCapture;
        // Stays enabled while the readout itself is polling — that window is how you stop it.
        _btnReadout.Enabled = Session.IsConnected && Session.Profile.SupportsLiveReadout;

        // Quick-command buttons carry their SCPI in Tag; the tool buttons beside them
        // (Clear Log, Save Log, Scripts) have none and stay usable at all times.
        foreach (Control c in _flowQuick.Controls)
            if (c is Button b && b.Tag is string) b.Enabled = live;
    }

    // -------------------------------------------------------------- console i/o

    /// <summary>Append a line to this console's log.</summary>
    public void AppendLog(string text, Color color)
    {
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = color;
        _log.AppendText(text + Environment.NewLine);
        _log.SelectionColor = _log.ForeColor;
        _log.ScrollToCaret();
    }

    private async void TxtCommand_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            await SendCurrentCommandAsync();
        }
        else if (e.KeyCode == Keys.Up)
        {
            e.SuppressKeyPress = true;
            RecallHistory(-1);
        }
        else if (e.KeyCode == Keys.Down)
        {
            e.SuppressKeyPress = true;
            RecallHistory(+1);
        }
    }

    private void RecallHistory(int direction)
    {
        _txtCommand.Text = Session.History.Recall(direction);
        _txtCommand.SelectionStart = _txtCommand.Text.Length;
    }

    private async Task SendCurrentCommandAsync()
    {
        string command = _txtCommand.Text.Trim();
        if (command.Length == 0) return;

        Session.History.Add(command);
        _txtCommand.Clear();

        await SendCommandAsync(command);
    }

    /// <summary>Send one command to this instrument and echo it (and any reply) to the log.</summary>
    private async Task SendCommandAsync(string command)
    {
        if (!Session.IsConnected)
        {
            AppendLog("Not connected.", Color.Tomato);
            return;
        }

        if (string.IsNullOrWhiteSpace(command)) return;

        AppendLog("> " + command, Color.Silver);

        try
        {
            if (ScpiClient.IsQuery(command))
            {
                string response = await Session.Client.QueryAsync(command);
                AppendLog(response.Length == 0 ? "(no response)" : response, Color.MediumSpringGreen);
                RecordIfNumeric(command, response);
            }
            else
            {
                await Session.Client.SendAsync(command);
                AppendLog("(sent)", Color.Gray);
            }
        }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex.Message, Color.Tomato);
        }
    }

    /// <summary>
    /// Hand the queue to the strip: the command being answered first, then the ones waiting
    /// behind it in the order they were sent.
    /// </summary>
    private void ShowQueue(IReadOnlyList<string> queued) => _queueBar.ShowQueue(queued);

    /// <summary>
    /// Put a reading in the results table, if that is what the reply was.
    ///
    /// Only a reply that is entirely one number counts. A meter answering "1.234567E+00" is a
    /// reading; *IDN? and :SYST:ERR? are not, and a list like "1.0,2.0" is a block of data
    /// rather than a point on a curve. Guessing at those would fill the table with rows that
    /// mean nothing and drag the plot's scale with them.
    ///
    /// The point of this is repetition: send MEAS:VOLT? a few times, or hold Enter on it, and
    /// the drift is a line rather than a column of numbers to read down.
    /// </summary>
    private void RecordIfNumeric(string command, string response)
    {
        string value = response.Trim();
        if (value.Length == 0
            || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return;

        _results.AddRow(new SequenceRow(new[]
        {
            DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            command,
            value,
        }));
    }

    // ------------------------------------------------------------------- tools

    private void SaveLog()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Save console log",
            Filter = "Text file (*.txt)|*.txt|Log file (*.log)|*.log|All files (*.*)|*.*",
            FileName = SuggestedLogFileName(),
        };
        if (dlg.ShowDialog(Owner) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, _log.Text);
            AppendLog($"--- log saved to {dlg.FileName} ---", Color.Gray);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Owner, "Could not save the log:\n" + ex.Message, "Save Log",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>"console-log-DS2202A.txt" — with several consoles open, the model tells them apart.</summary>
    private string SuggestedLogFileName()
    {
        (_, string model) = InstrumentProfile.ParseIdentity(Session.Identity);
        foreach (char bad in Path.GetInvalidFileNameChars()) model = model.Replace(bad, '-');
        model = model.Trim();
        return model.Length == 0 ? "console-log.txt" : $"console-log-{model}.txt";
    }

    private void OpenScriptEditor()
    {
        if (_scriptForm == null || _scriptForm.IsDisposed)
        {
            // Each console gets its own editor, bound to its own session — running a script
            // against "whatever is connected" would be ambiguous with several instruments open.
            _scriptForm = new ScriptForm(
                () => Session.IsConnected ? Session.Client : null,
                Session.Title,
                InstrumentProfile.FamilyForIdentity(Session.Identity),
                Session.Identity);
            if (Owner?.Icon != null) _scriptForm.Icon = Owner.Icon;
            _scriptForm.RunStateChanged += OnScriptRunStateChanged;
        }
        _scriptForm.Show();
        if (_scriptForm.WindowState == FormWindowState.Minimized)
            _scriptForm.WindowState = FormWindowState.Normal;
        _scriptForm.BringToFront();
        _scriptForm.Activate();
    }

    /// <summary>While a script drives this instrument, keep this console off it.</summary>
    private void OnScriptRunStateChanged(bool running)
    {
        Session.IsBusy = running;
        UpdateEnabledState();
    }

    /// <summary>
    /// Open the live readout for this instrument. Polling holds the link, so the window
    /// marks the session busy while it runs and this console locks itself out — the same
    /// rule a running script follows.
    /// </summary>
    /// <summary>
    /// Read this instrument's datasheet with the user's AI connection. Needs a connection to
    /// have been set up first — there is no built-in key, by design: the user brings their
    /// own and it stays on their machine.
    /// </summary>
    private void OpenDatasheetExtract()
    {
        UserSettings settings = SettingsStore.Load();
        string? key = SecretStore.Unprotect(settings.AiApiKeyProtected);

        if (settings.Ai == null || string.IsNullOrWhiteSpace(key))
        {
            MessageBox.Show(Owner,
                "No AI connection is set up yet.\n\nAdd one under Tools ▸ AI Connection — "
              + "you supply your own provider and key, and the key is stored encrypted for "
              + "your Windows account.",
                "AI Datasheet Extraction", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Keyed on the model rather than the address: an instrument on DHCP moves, and its
        // extracted commands should follow the instrument, not the lease.
        (_, string model) = InstrumentProfile.ParseIdentity(Session.Identity);
        using var dlg = new DatasheetExtractForm(
            settings.Ai, key!, string.IsNullOrEmpty(model) ? Session.Host : model, Session.Title);
        if (Owner?.Icon != null) dlg.Icon = Owner.Icon;
        dlg.ShowDialog(Owner);

        // Saved commands change what the reference window should show, so drop the cached one.
        if (dlg.Saved && _referenceForm is { IsDisposed: false })
        {
            _referenceForm.Close();
            _referenceForm = null;
        }
    }

    private void OpenReadout()
    {
        if (!Session.Profile.SupportsLiveReadout)
        {
            MessageBox.Show(Owner, "A live readout is only offered for instruments that "
                + "return one measurement per query, such as a multimeter.",
                "Live Readout", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_readoutForm == null || _readoutForm.IsDisposed)
        {
            _readoutForm = new MultimeterReadoutForm(Session);
            if (Owner?.Icon != null) _readoutForm.Icon = Owner.Icon;
            _readoutForm.PollingStateChanged += running =>
            {
                Session.IsBusy = running;
                UpdateEnabledState();
            };
        }
        _readoutForm.Show();
        if (_readoutForm.WindowState == FormWindowState.Minimized)
            _readoutForm.WindowState = FormWindowState.Normal;
        _readoutForm.BringToFront();
        _readoutForm.Activate();
    }

    /// <summary>
    /// Try to enumerate the instrument's command set via SYSTem:HELP:HEADers?. Many
    /// instruments (Rigol/Siglent included) don't implement it, so on failure we say so
    /// and open the curated reference instead.
    /// </summary>
    private async Task DiscoverCommandsAsync()
    {
        if (!Session.IsConnected) return;

        _btnDiscover.Enabled = false;
        AppendLog("--- discovering commands (" + CommandDiscovery.Query + ") ---", Color.Gray);
        try
        {
            CommandDiscoveryResult result = await CommandDiscovery.DiscoverAsync(Session.Client);
            if (result.Success)
            {
                AppendLog($"--- {result.Count} command headers reported by the instrument ---",
                    Color.MediumSpringGreen);
                AppendLog(result.HeaderList, Color.Gainsboro);
            }
            else
            {
                ShowCuratedReferenceOrMessage();
            }
        }
        catch (Exception ex)
        {
            AppendLog("Discover failed: " + ex.Message, Color.Tomato);
        }
        finally
        {
            UpdateEnabledState();
        }
    }

    private void ShowCuratedReferenceOrMessage()
    {
        CommandReference? reference = CommandReference.ForIdentity(Session.Identity);
        if (reference is { Commands.Count: > 0 })
        {
            AppendLog($"--- instrument doesn't support live discovery; opening the built-in "
                    + $"reference for {reference.Instrument} ---", Color.MediumSpringGreen);
            OpenReferenceWindow(reference);
            return;
        }

        const string msg = "Couldn't retrieve command info from the instrument, and no " +
                           "built-in reference is bundled for it. Check its programming manual instead.";
        AppendLog(msg, Color.Tomato);
        MessageBox.Show(Owner, msg, "Discover Commands", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenReferenceWindow(CommandReference reference)
    {
        // Fold in anything extracted from a datasheet for this instrument. Merge keeps the
        // transcribed entries first and drops extracted duplicates of them — showing both
        // would imply two sources agreed when only one of them is a source.
        (_, string model) = InstrumentProfile.ParseIdentity(Session.Identity);
        CommandReference? extracted = ExtractedCatalogStore.Load(
            string.IsNullOrEmpty(model) ? Session.Host : model);
        reference = ExtractedCatalogStore.Merge(reference, extracted);

        // Recreate each time so the window always matches this instrument.
        _referenceForm?.Close();
        _referenceForm = new CommandReferenceForm(reference, InsertIntoCommandBox);
        if (Owner?.Icon != null) _referenceForm.Icon = Owner.Icon;
        _referenceForm.Show();
    }

    /// <summary>Drop a command from the reference into this console's input, ready to send.</summary>
    private void InsertIntoCommandBox(string command)
    {
        _txtCommand.Text = command;
        _txtCommand.SelectionStart = _txtCommand.Text.Length;
        _txtCommand.Focus();
    }

    private async Task CaptureScreenAsync()
    {
        if (!Session.IsConnected) return;
        string? cmd = Session.Profile.ScreenCaptureCommand;
        if (string.IsNullOrEmpty(cmd))
        {
            MessageBox.Show(Owner, "No screen-capture command is known for this instrument.",
                "Capture Screen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _btnCapture.Enabled = false;
        AppendLog($"--- capturing screen ({cmd}) ---", Color.Gray);

        try
        {
            // A full-screen BMP is ~1 MB; give the transfer headroom over the user's timeout.
            Session.Client.TimeoutMs = Math.Max(Session.UserTimeoutMs, 15000);

            foreach (string setup in Session.Profile.ScreenCaptureSetup)
                await Session.Client.SendAsync(setup);

            byte[] data = await Session.Client.QueryBinaryAsync(cmd);

            // GDI+ keeps a Bitmap tied to its source stream, so copy into a standalone bitmap.
            Bitmap shot;
            using (var ms = new MemoryStream(data))
            using (var loaded = new Bitmap(ms))
                shot = new Bitmap(loaded);

            AppendLog($"--- screen captured ({data.Length:N0} bytes) ---", Color.MediumSpringGreen);
            var viewer = new ScreenCaptureForm(shot, Session.Title);
            if (Owner?.Icon != null) viewer.Icon = Owner.Icon;
            viewer.Show();
        }
        catch (Exception ex)
        {
            AppendLog("Screen capture failed: " + ex.Message, Color.Tomato);
            MessageBox.Show(Owner,
                "Screen capture failed:\n" + ex.Message + "\n\nIf it timed out, raise the Timeout and retry.",
                "Capture Screen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            Session.Client.TimeoutMs = Session.UserTimeoutMs;
            UpdateEnabledState();
        }
    }

    private async Task CaptureWaveformAsync()
    {
        if (!Session.IsConnected) return;
        if (!Session.Profile.SupportsWaveformCapture)
        {
            MessageBox.Show(Owner, "Waveform capture isn't supported for this instrument.",
                "Capture Waveform", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _btnWaveform.Enabled = false;
        AppendLog("--- capturing waveform (CH1) ---", Color.Gray);

        try
        {
            Session.Client.TimeoutMs = Math.Max(Session.UserTimeoutMs, 10000);

            WaveformCapture wave = await WaveformReader.ReadAsync(
                Session.Client, Session.Profile.WaveformDialect);

            AppendLog($"--- waveform captured ({wave.Samples.Count} points) ---", Color.MediumSpringGreen);

            // The window can ask for more of the same. It goes through the session's client,
            // so a running capture takes its turn on the link alongside anything typed into
            // the console rather than interleaving with it — see SerializedInstrumentClient.
            var form = new WaveformForm(wave, Session.Title + " — CH1", RecaptureWaveformAsync);
            if (Owner?.Icon != null) form.Icon = Owner.Icon;
            form.Show();
        }
        catch (Exception ex)
        {
            AppendLog("Waveform capture failed: " + ex.Message, Color.Tomato);
            MessageBox.Show(Owner, "Waveform capture failed:\n" + ex.Message, "Capture Waveform",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            Session.Client.TimeoutMs = Session.UserTimeoutMs;
            UpdateEnabledState();
        }
    }

    /// <summary>
    /// Take another capture for a waveform window that is running.
    ///
    /// It raises the timeout for the read and puts it back, exactly as the one-shot path
    /// does: a deep record takes longer to hand over than a command reply, and the window
    /// may still be running long after <see cref="CaptureWaveformAsync"/>'s own finally has
    /// restored the user's value.
    /// </summary>
    private async Task<WaveformCapture> RecaptureWaveformAsync(CancellationToken ct)
    {
        if (!Session.IsConnected)
            throw new InvalidOperationException("the instrument is no longer connected");

        Session.Client.TimeoutMs = Math.Max(Session.UserTimeoutMs, 10000);
        try
        {
            return await WaveformReader.ReadAsync(
                Session.Client, Session.Profile.WaveformDialect, channel: 1, ct);
        }
        finally { Session.Client.TimeoutMs = Session.UserTimeoutMs; }
    }

    /// <summary>
    /// The window currently hosting this console — a tab in the main form, or its own
    /// detached window. Used as the owner for dialogs and child windows so they follow the
    /// console around rather than always belonging to the main form.
    /// </summary>
    private Form? Owner => FindForm();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseChildWindows();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
