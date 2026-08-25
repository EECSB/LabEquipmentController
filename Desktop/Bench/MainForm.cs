using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LabEquipmentController
{
    public partial class MainForm : Form
    {
        private CancellationTokenSource? _scanCts;

        /// <summary>
        /// Non-null only while a connection attempt is in flight, which is also what makes
        /// the Connect button act as Cancel — the same one-button-two-jobs shape as Scan.
        /// </summary>
        private CancellationTokenSource? _connectCts;

        private readonly ToolTip _tips = new();

        // One session (and one console) per connected instrument. The scan panel and the
        // device list above them are shared — connecting opens another console rather than
        // replacing the current one.
        private readonly SessionRegistry _sessions = new();
        private readonly List<InstrumentWindow> _detached = new();

        // Empty-state text for the console area, captured before it is rewritten. Two of
        // them, because "Scan the network above" is wrong when the card above is listing
        // COM ports — and that sentence is the one thing on screen telling a new user what
        // to do next.
        private string _emptyConsoleText = "";
        private string _emptyConsoleNetworkText = "";

        private const string EmptyConsoleSerialText =
            "No instrument connected.\r\n\r\n"
            + "Pick a serial port above and press Connect (or double-click a row) to open a "
            + "console for it.\r\n"
            + "Each instrument gets its own tab, and any tab can be detached into its own window.";

        // The tab a context menu was opened over.
        private TabPage? _menuTarget;

        public MainForm()
        {
            InitializeComponent();
            LoadAppIcon();
        }

        /// <summary>Use the embedded app.ico for the title bar and taskbar.</summary>
        private void LoadAppIcon()
        {
            try
            {
                using Stream? s = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico");
                if (s != null) Icon = new Icon(s);
            }
            catch { /* cosmetic only — never block startup over an icon */ }
        }

        // ------------------------------------------------------------- lifecycle

        private void MainForm_Load(object? sender, EventArgs e)
        {
            // Load once; window size is applied before the layout math below runs off it.
            UserSettings settings = SettingsStore.Load();
            ApplyWindowSettings(settings);

            PopulateInterfaces();

            BuildMenu();            // before the geometry below is captured — it shifts it
            SetButtonIcons();       // glyphs first — a glyph is what sets a button's height
            NormalizeRowHeights();  // then square up every row around them

            // Before the card is laid out, because it decides the switch's size and the card
            // is measured from it. Below this call it was measured from the *designer's*
            // size — AutoSize, with a radio glyph's worth of padding still in it — so the
            // second half of the switch was placed a few pixels past the end of the first,
            // and the pair showed a gap until something resized the window and ran the
            // layout again.
            StyleScanKind();

            // Captured before anything moves: the scan group changes height when it is
            // laid out, and everything below it has to follow rather than overlap.
            _scanToDevicesGap = grpDevices.Top - grpScan.Bottom;
            LayoutScanGroup();

            // Capture the designed geometry before LayoutGroups starts changing it.
            _devicesNaturalHeight = grpDevices.Height;
            _devicesPadBelowList = grpDevices.Height - lstDevices.Bottom;
            _groupGap = grpConsole.Top - grpDevices.Bottom;
            // The gap under the console group, kept at every window size by LayoutGroups.
            //
            // It is a measurement of the designer file, so it is only ever as good as that
            // file: raise the form's ClientSize without growing grpConsole to match and this
            // captures the leftover space, after which the console stops short of the bottom
            // for ever, at every size, and resizing the window does not help. That is not a
            // hypothetical — it shipped. The bottom margin is meant to be the one the top and
            // sides use, so never take more than that.
            _pageBottomMargin = Math.Min(ClientSize.Height - grpConsole.Bottom, grpScan.Top);

            LayoutGroups();
            Resize += (_, _) =>
            {
                LayoutScanGroup();
                LayoutGroups();
                // The connect row was missing from here, so it kept the positions it was
                // given at startup while the window changed size around it.
                PositionTimeoutField();
                PositionSequenceButton();
            };

            LayoutDeviceColumns();
            lstDevices.SizeChanged += (_, _) => LayoutDeviceColumns();

            // The button is placed from the group's width, so it is repositioned when that
            // width changes rather than at a list of moments that are meant to cover it.
            // Load, Resize and the tab handler between them missed the layout pass that
            // follows auto-scaling, and the button sat 530px short of the right edge.
            grpConsole.SizeChanged += (_, _) => PositionSequenceButton();

            PositionTimeoutField();
            PositionSequenceButton();
            SetTooltips();

            _emptyConsoleNetworkText = lblNoConsole.Text;
            _emptyConsoleText = _emptyConsoleNetworkText;
            BuildTabContextMenu();
            UpdateConsoleHostVisibility();
            UpdateConnectionSummary();

            // Keep the Timeout field meaningful for consoles that are already open, not
            // just the next connection.
            numTimeout.ValueChanged += (_, _) => ApplyTimeoutToOpenSessions();

            WireScanKind();

            // Interface list / port / timeout are restored after their controls exist.
            ApplyInputSettings(settings);
        }

        /// <summary>
        /// Restore the saved window size and maximized state, clamped to the screen, and
        /// raise it to the designed default if that has grown since the file was written.
        ///
        /// The clamp applies to the designer's default size too, not only to a restored one.
        /// The default is chosen so a console is comfortable the moment it opens, which
        /// makes it taller than the working area of a 1366x768 laptop — and a window taller
        /// than the screen puts its bottom edge, and the resize grip with it, out of reach.
        /// </summary>
        private void ApplyWindowSettings(UserSettings s)
        {
            Size wa = Screen.GetWorkingArea(this).Size;

            // Width and Height are still the designer's default here, which is what a saved
            // size from an older generation gets raised to. Everything the user does after
            // that is remembered as usual — SaveSettings writes the current version back.
            (int wantWidth, int wantHeight) = UserSettings.StartingSize(
                s.WindowWidth, s.WindowHeight, s.LayoutVersion, Width, Height);

            // Not Math.Clamp: it throws when the minimum exceeds the maximum, which is
            // exactly what a screen smaller than MinimumSize produces — a 1024x600 netbook
            // against a 660 minimum height. Fitting the screen wins over the minimum there,
            // because a window that cannot be reached cannot be resized either.
            int Fit(int want, int min, int max) => Math.Min(Math.Max(want, min), max);

            Size = new Size(
                Fit(wantWidth, MinimumSize.Width, wa.Width),
                Fit(wantHeight, MinimumSize.Height, wa.Height));

            if (s.WindowMaximized) WindowState = FormWindowState.Maximized;
        }

        /// <summary>
        /// The selected interface's subnet written out — "192.168.1.1-192.168.1.254".
        ///
        /// Taken from the same enumeration the scan itself uses, so the field says exactly
        /// what Scan will sweep. Writing the idealised ".1-.255" would name the broadcast
        /// address, which §4 excludes, and a field that overstates its range by one host is
        /// the sort of thing someone only notices while debugging something else.
        /// </summary>
        private string SubnetRangeText()
        {
            if (cboInterface.SelectedItem is not LocalInterface iface) return "";

            var hosts = NetworkScanner.EnumerateHosts(iface.Address, iface.Mask, maxHosts: 4096, out _);
            return hosts.Count == 0 ? "" : $"{hosts[0]}-{hosts[^1]}";
        }

        /// <summary>
        /// Put the whole subnet in the range box, as the placeholder and as the value when
        /// the box is empty. Empty still means the whole subnet, so nothing behaves
        /// differently — it is now written down instead of implied, which also shows what
        /// to edit when narrowing it.
        /// </summary>
        private void ShowSubnetRange()
        {
            string all = SubnetRangeText();
            txtRange.PlaceholderText = all.Length > 0 ? all : "whole subnet";
            if (txtRange.Text.Trim().Length == 0) txtRange.Text = all;
        }

        /// <summary>Restore the last interface selection, port list, and timeout.</summary>
        private void ApplyInputSettings(UserSettings s)
        {
            if (!string.IsNullOrWhiteSpace(s.Ports)) cboPort.Text = s.Ports;
            txtRange.Text = s.ScanRange ?? "";

            if (s.TimeoutMs >= numTimeout.Minimum && s.TimeoutMs <= numTimeout.Maximum)
                numTimeout.Value = s.TimeoutMs;

            if (!string.IsNullOrWhiteSpace(s.InterfaceAddress))
            {
                for (int i = 0; i < cboInterface.Items.Count; i++)
                {
                    if (cboInterface.Items[i] is LocalInterface li &&
                        li.Address.ToString() == s.InterfaceAddress)
                    {
                        cboInterface.SelectedIndex = i;
                        break;
                    }
                }
            }

            // After the interface is settled, so the range describes the right subnet.
            ShowSubnetRange();
            cboInterface.SelectedIndexChanged += (_, _) => ShowSubnetRange();
        }

        /// <summary>
        /// Persist the current inputs and window geometry. Never throws.
        ///
        /// Loads the stored settings and changes only what this window owns. Building a fresh
        /// <see cref="UserSettings"/> here — which is what it used to do — wrote back the
        /// defaults for every field this form does not have a control for, so closing the app
        /// silently discarded the AI connection, its DPAPI-protected key, and the datasheet
        /// folder. They are set in other windows, and this one runs last.
        /// </summary>
        private void SaveSettings()
        {
            try
            {
                Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

                UserSettings settings = SettingsStore.Load();
                settings.InterfaceAddress = (cboInterface.SelectedItem as LocalInterface)?.Address.ToString();
                settings.Ports = cboPort.Text;
                settings.ScanRange = txtRange.Text.Trim();
                settings.TimeoutMs = (int)numTimeout.Value;
                settings.WindowWidth = bounds.Width;
                settings.WindowHeight = bounds.Height;
                settings.WindowMaximized = WindowState == FormWindowState.Maximized;
                settings.LayoutVersion = UserSettings.CurrentLayoutVersion;

                SettingsStore.Save(settings);
            }
            catch { /* a failed settings write must never block shutdown */ }
        }

        private void SetIcon(Button b, string name) => ButtonStyle.SetIcon(this, b, name);

        private readonly MenuStrip _menu = new();

        /// <summary>
        /// The menu bar at the top of the window, where Windows programs keep one.
        ///
        /// The three group boxes below are positioned absolutely (they anchor rather than
        /// dock), so a docked MenuStrip does not push them down — it draws straight over the
        /// first one. They are shifted by its height here, before MainForm_Load captures the
        /// geometry that <see cref="LayoutGroups"/> works from.
        /// </summary>
        private void BuildMenu()
        {
            // Tools does things to the bench; Help explains things. That is the line the two
            // menus are drawn along, and it is why the command library sits under Help — it
            // acts on nothing, it is reference material.
            var tools = new ToolStripMenuItem("&Tools");
            // A multi-instrument script drives several at once, so it belongs to the bench
            // rather than to any one console — which is why it lives here and a
            // single-instrument script lives on the console it runs against.
            tools.DropDownItems.Add(new ToolStripMenuItem(
                "&Multi-Instrument Scripts…", null, (_, _) => ShowSequence()));
            tools.DropDownItems.Add(new ToolStripSeparator());
            tools.DropDownItems.Add(new ToolStripMenuItem(
                "&AI Connection…", null, (_, _) => ShowAiSettings()));
            _menu.Items.Add(tools);

            var help = new ToolStripMenuItem("&Help");

            // Both reachable without an instrument, and both wanted before there is one: the
            // catalog is what an instrument accepts, and the language reference is how to say
            // it. Neither belonged behind a working connection.
            help.DropDownItems.Add(new ToolStripMenuItem(
                "&Command Library…", null, (_, _) => ShowCommandLibrary()));
            help.DropDownItems.Add(new ToolStripMenuItem(
                "&Script Language…", null, (_, _) => ShowScriptLanguage()));
            help.DropDownItems.Add(new ToolStripSeparator());
            help.DropDownItems.Add(new ToolStripMenuItem(
                "&About Lab Equipment Controller…", null, (_, _) => ShowAbout()));
            _menu.Items.Add(help);

            _menu.Dock = DockStyle.Top;
            Controls.Add(_menu);
            MainMenuStrip = _menu;

            int shift = _menu.PreferredSize.Height;
            foreach (Control c in new Control[] { grpScan, grpDevices, grpConsole })
                c.Top += shift;
        }

        private void ShowAbout()
        {
            using var dlg = new AboutForm();
            if (Icon != null) dlg.Icon = Icon;
            dlg.ShowDialog(this);
        }

        /// <summary>
        /// Edit the AI connection used for datasheet extraction. The key is encrypted on the
        /// way into settings.json and only decrypted back out here, so it is never on disk in
        /// the clear (see <see cref="SecretStore"/>).
        /// </summary>
        /// <summary>
        /// The multi-instrument sequence editor. Modeless and single-instance: a sweep runs
        /// for minutes, and the user needs the main window to connect the instruments it is
        /// waiting for.
        /// </summary>
        private SequenceForm? _sequence;

        /// <summary>
        /// The console group's designer top padding, kept because PositionSequenceButton
        /// overwrites it with a band sized for the button and must not compound its own work.
        /// </summary>
        private int? _consoleCaptionPad;

        private void btnSequence_Click(object? sender, EventArgs e) => ShowSequence();

        private void ShowSequence()
        {
            if (_sequence is { IsDisposed: false })
            {
                if (_sequence.WindowState == FormWindowState.Minimized)
                    _sequence.WindowState = FormWindowState.Normal;
                _sequence.BringToFront();
                _sequence.Focus();
                return;
            }

            _sequence = new SequenceForm(_sessions);
            if (Icon != null) _sequence.Icon = Icon;
            _sequence.FormClosed += (_, _) => _sequence = null;
            _sequence.Show(this);
        }

        private CommandLibraryForm? _library;
        private ScriptReferenceForm? _languageRef;

        /// <summary>
        /// Browse every built-in catalog, with no instrument attached.
        ///
        /// Modeless and single-instance. Both this and the language reference are things you
        /// read *while* doing something else — looking up a command to type into a console,
        /// checking what a loop looks like before writing one — and as modal dialogs they
        /// made you close them to use the app, which is the opposite of what they are for.
        /// Reopening focuses the one that is already up rather than stacking another.
        /// </summary>
        private void ShowCommandLibrary()
        {
            if (_library is { IsDisposed: false }) { _library.Focus(); return; }

            _library = new CommandLibraryForm();
            if (Icon != null) _library.Icon = Icon;
            _library.FormClosed += (_, _) => _library = null;
            _library.Show(this);
        }

        /// <summary>
        /// The script language, from the menu bar.
        ///
        /// The same page the Snippets button opens, and reachable here because the question
        /// "what can I even write?" arrives before an editor is open — sometimes before an
        /// instrument is connected. The multi-instrument dialect, being the larger one: it is
        /// the §9 language plus the forms for addressing more than one instrument, so a reader
        /// who only wants the smaller one loses nothing.
        /// </summary>
        private void ShowScriptLanguage()
        {
            if (_languageRef is { IsDisposed: false }) { _languageRef.Focus(); return; }

            _languageRef = new ScriptReferenceForm(ScriptLanguage.ForSequence);
            if (Icon != null) _languageRef.Icon = Icon;
            _languageRef.FormClosed += (_, _) => _languageRef = null;
            _languageRef.Show(this);
        }

        private void ShowAiSettings()
        {
            // The connections and their keys travel together — see AiBookStore. A blank key
            // box still means "leave the stored one alone", so a user editing a model does not
            // have to paste that connection's key again.
            (AiConnections book, var keys) = AiBookStore.Load();

            using var dlg = new AiSettingsForm(book, keys);
            if (Icon != null) dlg.Icon = Icon;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            AiBookStore.Save(dlg.Book, dlg.ApiKeys);
            AiConnectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Raised when the AI connection is edited, so open consoles can re-enable.</summary>
        public event EventHandler? AiConnectionChanged;

        /// <summary>
        /// Describe every control in a hover tooltip. Anything added to this window should
        /// get an entry here — the labels alone don't explain what the controls do. (The
        /// controls inside a console are tipped by <see cref="InstrumentConsole"/> itself.)
        /// </summary>
        private void SetTooltips()
        {
            _tips.AutoPopDelay = 15000;   // these are sentences; give them time to be read

            void Tip(Control c, string text) => _tips.SetToolTip(c, text);

            // --- network scan ---
            Tip(lblInterface, "Which of this PC's network adapters to search on.");
            Tip(cboInterface, "Network adapter and subnet to scan. Pick the one on the same "
                            + "network as your instruments — the host count shows how many "
                            + "addresses will be probed.");
            Tip(lblPort, "TCP ports probed on each address.");
            Tip(cboPort, "TCP ports to probe, comma-separated. 5025 is the usual raw SCPI "
                       + "socket, 5555 is Rigol's, and 111 is the RPC portmapper used to "
                       + "find VXI-11 instruments, and 3490 is where a Fluke 8845A/8846A "
                       + "answers.");
            Tip(lblRange, "Which addresses to probe. Empty scans the whole subnet.");
            Tip(txtRange, "Narrow the scan to part of the subnet. Leave it empty to sweep "
                        + "all of it.\r\n\r\n"
                        + "192.168.1.20-60      a range\r\n"
                        + "20-60                the same, in the selected adapter's subnet\r\n"
                        + "192.168.1.50         one address\r\n"
                        + "192.168.1.0/28       a block\r\n"
                        + "20-40, 55, 90-99     any mixture, comma-separated\r\n\r\n"
                        + "Worth using on a shared lab network: a full sweep knocks on every "
                        + "printer, camera and PLC as well as your instruments.");
            Tip(rdoNetwork, "Look for instruments over the network — a sweep of the subnet or "
                          + "the IP range given. This is the default.");
            Tip(rdoSerial, "Look at this machine's RS-232 serial ports instead, including any "
                         + "USB-to-serial adapter. They are listed rather than swept: a serial "
                         + "port cannot be discovered the way a subnet can, so Scan asks the "
                         + "ports you already have.");
            Tip(lblScope, "Which serial ports Scan opens.");
            Tip(cboScope, "All of this machine's serial ports, or a single one. Re-read each "
                        + "time the card switches to Serial, so an adapter plugged in just now "
                        + "is in the list.");
            Tip(lblBaud, "The baud rates Scan tries on each port.");
            Tip(cboBaud, "Tried in the order written, stopping at the first that answers. "
                       + "Most instruments ship at 9600; anything made this century is "
                       + "usually set to 115200. Everything else stays at 8-N-1 with no flow "
                       + "control — set those per port in the Address box if one needs them.");
            Tip(btnScan, ScanNetworkTip);
            Tip(progressScan, "How far the current scan has progressed.");
            Tip(lblStatus, "Current scan status: what was probed and what was found.");

            // --- discovered instruments ---
            Tip(lstDevices, "Instruments found by the last scan. Double-click a row to open "
                          + "a console for it.");
            Tip(btnExport, "Save the discovered-instrument list to a CSV file.");
            Tip(lblAddress, "Address of the instrument to connect to.");
            Tip(txtAddress, "Instrument address. Accepts a plain IP, an IP with port "
                          + "(192.168.1.50:5555), a vxi:// address, a serial port "
                          + "(serial://COM3, or serial://COM3?baud=115200 at other line "
                          + "settings), or a VISA resource string "
                          + "(TCPIP0::192.168.1.50::inst0::INSTR). Filled in automatically "
                          + "when you select a row above.");
            Tip(cboSerialPort, "The serial ports this machine reports, re-read each time the "
                             + "list is opened. Type instead of choosing to reach a port that "
                             + "is not listed, or to set the line settings: COM3?baud=115200, "
                             + "and likewise parity, databits, stopbits, flow and term. "
                             + "Without them it is 9600-8-N-1, no flow control.");
            Tip(btnConnect, "Open a console for the address shown. Each instrument gets its "
                          + "own tab below, so several can be connected at once. Disconnect "
                          + "from within a console.");
            Tip(btnSequence, "Write and run one script that drives several instruments at "
                          + "once, for measurements where they have to take turns. Also on "
                          + "the Tools menu.");
            Tip(lblConnection, "How many instruments are currently connected.");
            Tip(lblTimeout, "How long to wait for an instrument's reply.");
            Tip(numTimeout, "How long to wait for an instrument to reply, in milliseconds. "
                          + "Applies to connections already open as well as the next one. "
                          + "Raise it for slow operations; large transfers get extra time "
                          + "automatically.");

            // --- consoles ---
            Tip(tabConsoles, "One tab per connected instrument. Click its ✕ to disconnect " +
                             "and close it. Right-click a tab to detach "
                           + "it into its own window or to disconnect it.");
            Tip(lblNoConsole, "Consoles appear here once you connect to an instrument.");
        }

        /// <summary>Give the static toolbar buttons their icons (after DPI scaling settles).</summary>
        private void SetButtonIcons()
        {
            SetIcon(btnScan, "reset");       // ↻ scan/refresh (swaps to ■ while running)
            SetIcon(btnExport, "saveFile");
            SetIcon(btnConnect, "connect");
            SetIcon(btnSequence, "program");
        }

        /// <summary>
        /// Place the Timeout field in the Connect row, right-aligned. Done in code
        /// because a NumericUpDown does not reliably honour the designer's
        /// AutoScaleMode.Font layout — placed via the designer it lands in the wrong
        /// band. Positioning it here (after scaling has settled) and relative to the
        /// already-correct Connect button keeps it aligned.
        /// </summary>
        /// <summary>
        /// Lay out the connect row: address, timeout, Connect, what happened — then Export at
        /// the far right.
        ///
        /// Timeout sits beside the address because the two are one thought: where to dial and
        /// how long to wait for it. It used to be out at the right margin, as far from the
        /// address as the row allowed.
        ///
        /// Export Results is here rather than up beside Scan because it exports *this list*.
        /// A control belongs with the thing it acts on (SPEC §6), and the thing it acts on is
        /// the table directly above it.
        /// </summary>
        /// <summary>
        /// The gap before the button that answers the row: Scan on the scan row, Connect on
        /// the connect row. Both rows read the same way — two halves of a question, then the
        /// thing that answers it — and a comment in each said so while two separate literals
        /// held the number. Tuning one left the other behind, which is what a constant is for.
        /// </summary>
        private const int AnswerGapLogical = 52;

        /// <summary>
        /// What Scan does, in each mode. Two strings rather than one because the two acts
        /// are genuinely different, and the serial one has something to disclose: it opens
        /// ports, which the network scan's equivalent — a TCP connect — cannot be mistaken
        /// for. Anything on the other end of a serial port receives what is sent to it, and
        /// it may not be an instrument.
        /// </summary>
        private const string ScanNetworkTip =
            "Search for instruments — the whole subnet, or just the IP range if one is "
            + "given. Found devices appear in the list as they answer. Click again to stop "
            + "a running scan.";

        private const string ScanSerialTip =
            "Open each listed port and ask it for its identity, trying the baud rates above "
            + "in order. Every port stays listed either way; the ones that answer gain an "
            + "identity. Click again to stop.\n\n"
            + "This writes *IDN? to whatever is on the other end, and opening a port asserts "
            + "DTR and RTS — which resets some boards. Ports you have not chosen are not "
            + "opened.";

        private void PositionTimeoutField()
        {
            // Every control on this row is placed here, in code, so none of them may also be
            // anchored — an anchor moves a control when the form resizes and this method then
            // computes the next one's position from where the anchor left it. The designer had
            // the timeout label and box on Bottom|Right, so widening the window threw them at
            // the right edge and left Export Results stranded mid-row behind the status text.
            foreach (Control c in new Control[] { lblAddress, txtAddress, cboSerialPort,
                                                  lblTimeout, numTimeout, btnConnect,
                                                  lblConnection, btnExport })
                c.Anchor = AnchorStyles.Top | AnchorStyles.Left;

            int gap = LogicalToDeviceUnits(8);
            int label = LogicalToDeviceUnits(2);

            // Connect keeps a button's own height rather than the text box's. AlignRow
            // equalises everything on a row, which suits the boxes — they should share a
            // baseline — but it left the two action buttons noticeably taller than Export
            // Results beside them, which nothing aligns. Height here, centred below.
            btnConnect.Height = btnExport.Height;

            int rowH = Math.Max(btnConnect.Height, txtAddress.Height);
            int top = btnConnect.Top;

            void Place(Control c, int left)
            {
                c.Left = left;
                c.Top = top + (rowH - c.Height) / 2;
            }

            Place(lblAddress, lblAddress.Left);

            // Both address controls take the same place, and only one is ever visible: the row
            // is Address, singular, and a second box for the kind you are not using would be a
            // second thing to read past every time. Which of them is showing is the scan
            // card's switch, not a second switch here — see StyleScanKind.
            cboSerialPort.Width = txtAddress.Width;
            Place(txtAddress, lblAddress.Right + label);
            Place(cboSerialPort, txtAddress.Left);

            // The same two gaps the scan row uses, for the same reason: the address and the
            // timeout are halves of one question, and Connect is the answer to it.
            Place(lblTimeout, txtAddress.Right + gap);

            // Five digits and no more. The field's own Maximum is 30000, so nothing wider can
            // ever be typed into it, and a box sized for numbers it will not accept reads as
            // a box that is waiting for one.
            numTimeout.Width = LogicalToDeviceUnits(58);
            Place(numTimeout, lblTimeout.Right + label);

            Place(btnConnect, numTimeout.Right + LogicalToDeviceUnits(AnswerGapLogical));

            // A fixed box rather than an AutoSize one, so it can be clamped below. Set before
            // Place, which centres on the height it finds.
            lblConnection.AutoSize = false;
            lblConnection.AutoEllipsis = true;
            lblConnection.TextAlign = ContentAlignment.MiddleLeft;
            lblConnection.Height = lblConnection.PreferredHeight;

            Place(lblConnection, btnConnect.Right + LogicalToDeviceUnits(20));
            Place(btnExport, lstDevices.Right - btnExport.Width);

            // The status text is the only thing on this row with no width of its own, and
            // Export Results is pinned to the right corner — so once the text grew from
            // "Not connected." to "1 instrument connected." it ran on underneath the button
            // and lost its last few words. Give it exactly the gap between the two.
            lblConnection.Width = Math.Max(LogicalToDeviceUnits(40),
                btnExport.Left - LogicalToDeviceUnits(12) - lblConnection.Left);
        }

        /// <summary>
        /// Sit Multi-Instrument Scripts at the top-right of the console group, on the row the
        /// tabs start on.
        ///
        /// Placed from the group's own DisplayRectangle rather than a designer coordinate:
        /// the caption band's height comes from the font, so a fixed y drifts the moment the
        /// DPI changes. It sits with the console group because that group is the instruments
        /// a script addresses, and it stays put when there are none — which is exactly when
        /// someone might open it to see what a script would need.
        /// </summary>
        private void PositionSequenceButton()
        {
            // A header band of its own, above the tab strip.
            //
            // This used to take the top of DisplayRectangle, which put it on the first row
            // inside the group — and since the tab control is Dock.Fill, that is the tab
            // strip's own row. It floated over the strip's empty right end, which is fine
            // until the tabs reach it: they are laid out left to right from the full width of
            // the control, so with four instruments connected the fifth tab ran underneath.
            // Nothing about a Dock.Fill tab control can shorten its strip, so the strip has to
            // start lower instead.
            //
            // The band is measured rather than fixed. The caption's own height comes from the
            // font, so a constant would clip the button at one DPI and leave a gap at another.
            // Below the caption band, not inside it. The band is only as tall as the caption's
            // font and the button is half as tall again, so a button placed to sit level with
            // the caption crosses the frame line the GroupBox draws through it — which is what
            // "clipped by the caption" meant, and it looks like the button was punched through
            // the border.
            _consoleCaptionPad ??= grpConsole.Padding.Top;
            int gap = LogicalToDeviceUnits(4);
            int caption = grpConsole.DisplayRectangle.Top - grpConsole.Padding.Top;
            int pad = Math.Max(_consoleCaptionPad.Value, btnSequence.Height + gap * 2);

            if (grpConsole.Padding.Top != pad)
                grpConsole.Padding = new Padding(grpConsole.Padding.Left, pad,
                                                 grpConsole.Padding.Right, grpConsole.Padding.Bottom);

            btnSequence.Top = caption + gap;
            btnSequence.Left = grpConsole.ClientSize.Width - btnSequence.Width - gap;
            btnSequence.BringToFront();
        }

        /// <summary>
        /// Make each input row share one height and one centre line.
        ///
        /// ComboBox and TextBox derive their height from the font and silently ignore
        /// the Size.Height set in the designer; Button honours it. After the form's
        /// AutoScaleMode.Font pass (which stretches X and Y by different factors) the
        /// two disagree — measured 26px buttons against 22px combos, with centres 2-4px
        /// apart. So take the real, font-driven input height as the truth, size the
        /// buttons to it, and centre everything on a shared axis.
        /// </summary>
        private void NormalizeRowHeights()
        {
            // Every button gets the app-wide height (SPEC §14), whatever the designer left it
            // at — 23 and 23 px against a 20 px Export, one of them Flat, read as three
            // different kinds of button on one window.
            int h = ButtonStyle.Height(this);
            foreach (Button b in new[] { btnScan, btnConnect, btnExport, btnSequence })
            {
                b.FlatStyle = FlatStyle.Standard;
                b.Height = h;
                if (b.Width < b.PreferredSize.Width) b.Width = b.PreferredSize.Width;
            }

            // Buttons keep that height; the inputs beside them keep their own font-driven
            // one, and AlignRow centres the lot on a shared axis.
            AlignRow(cboInterface,
                     new Control[] { cboPort, numTimeout, txtRange },
                     new Control[] { lblInterface, cboInterface, lblPort, cboPort, lblRange,
                                     txtRange, lblTimeout, numTimeout, btnScan });

            // The progress bar leads the status line, so it centres on that row — not on
            // the input row above it, where it used to sit.
            AlignRow(lblStatus, Array.Empty<Control>(), new Control[] { lblStatus, progressScan });

            // The Address box is a TextBox, which renders a few px shorter than the
            // Interface ComboBox. Grow it to the ComboBox height first, so the Address
            // box and Connect button line up with the rest of the window.
            txtAddress.AutoSize = false;
            txtAddress.Height = cboInterface.Height;
            AlignRow(txtAddress,
                     Array.Empty<Control>(),
                     new Control[] { lblAddress, txtAddress, cboSerialPort,
                                     btnConnect, lblConnection });
        }

        /// <summary>
        /// Size <paramref name="matchHeight"/> to the reference control's height, then
        /// centre every control in <paramref name="row"/> on the reference's centre line.
        /// </summary>
        private static void AlignRow(Control reference, Control[] matchHeight, Control[] row)
        {
            int h = reference.Height;
            foreach (Control c in matchHeight) c.Height = h;

            int centre = reference.Top + reference.Height / 2;
            foreach (Control c in row) c.Top = centre - c.Height / 2;
        }

        /// <summary>
        /// Size the device list's columns. Done in code because ListView column widths
        /// are NOT touched by the form's AutoScaleMode.Font pass — left at their design
        /// values they stay 96-DPI-sized while the list itself scales, leaving a dead
        /// strip on the right. Identity takes whatever room is left over.
        /// </summary>
        private void LayoutDeviceColumns()
        {
            // The first two columns hold different things in the two modes and need
            // different room for them: "192.168.1.100" and a port number, or "/dev/ttyUSB0"
            // and "9600-8-N-1". The third and fourth are the same in both.
            bool serial = rdoSerial.Checked;
            colIp.Width = LogicalToDeviceUnits(serial ? 150 : 120);
            colPort.Width = LogicalToDeviceUnits(serial ? 110 : 55);
            colProto.Width = LogicalToDeviceUnits(90);

            int used = colIp.Width + colPort.Width + colProto.Width;

            // Reserve the vertical scrollbar's gutter even while it is hidden: it appears
            // as soon as the list fills up, and ClientSize shrinking for it does not raise
            // SizeChanged, so this is the only chance to account for it.
            //
            // SystemInformation.VerticalScrollBarWidth comes back in the *system* DPI's
            // pixels, which under-reserves on a scaled display, so take the larger of it and
            // the scaled nominal width. The extra few pixels on top are slack: filling the
            // client width to the last pixel still raises a horizontal scrollbar, and that
            // costs a whole row out of the five this list is sized to show — an expensive way
            // to lose an argument about rounding. Until Export moved off the list's header
            // band, its width was quietly providing this slack.
            int gutter = Math.Max(SystemInformation.VerticalScrollBarWidth, LogicalToDeviceUnits(17));
            int available = lstDevices.ClientSize.Width - gutter - LogicalToDeviceUnits(4);

            colIdentity.Width = Math.Max(LogicalToDeviceUnits(120), available - used);

            // Never let the columns overrun the list.
            int overflow = used + colIdentity.Width - available;
            if (overflow > 0)
                colIdentity.Width = Math.Max(LogicalToDeviceUnits(60), colIdentity.Width - overflow);

            // Make the list recompute its horizontal scroll range. Narrowing the window makes
            // it briefly overflow — the list is resized before this runs, so for one layout
            // pass the old (wide) columns sit in the new (narrow) list — and the scrollbar it
            // raises then does not go away when the columns shrink back inside. It costs a
            // whole row out of the five this list is sized to show.
            lstDevices.BeginUpdate();
            lstDevices.EndUpdate();
        }

        // ListView header handle, used to measure the header row's real height.
        private const int LVM_GETHEADER = 0x1000 + 31;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        /// <summary>How many instrument rows the device list must always have room for.</summary>
        private const int MinDeviceRows = 5;

        // The designed geometry, captured once before any of it is adjusted. Deriving these
        // from the live controls instead makes the layout depend on its own previous output,
        // which compounds on every resize — the first attempt at this grew the device group
        // without bound and pushed the consoles off the bottom of the window.
        private int _devicesNaturalHeight;   // the group's DPI-scaled design height
        private int _devicesPadBelowList;    // space under the list, holding the Address row
        private int _groupGap;               // designed gap between the two lower groups
        private int _pageBottomMargin;
        private int _scanToDevicesGap;       // designed margin under the console group

        /// <summary>
        /// Share the window's height between the device list and the consoles.
        ///
        /// Done here rather than with anchors because anchoring gave the console group its
        /// (DPI-scaled) height off the bottom edge first and left the device group whatever
        /// remained — which at 168 DPI was 60px, about one row, with the header hiding most
        /// of it. The device group now takes its natural height, or enough for
        /// <see cref="MinDeviceRows"/> rows if that is larger, and the consoles take the
        /// rest: they cope with being shorter far better than a list you cannot read.
        /// </summary>
        private void LayoutGroups()
        {
            int rowHeight = lstDevices.Font.Height + LogicalToDeviceUnits(5);
            int wantedList = ListHeaderHeight() + MinDeviceRows * rowHeight + LogicalToDeviceUnits(4);
            int wantedGroup = lstDevices.Top + wantedList + _devicesPadBelowList;

            int pageBottom = ClientSize.Height - _pageBottomMargin;
            int minConsole = LogicalToDeviceUnits(140);

            int devicesHeight = Math.Max(_devicesNaturalHeight, wantedGroup);

            // On a window too short for both, the consoles keep their minimum and the list
            // gives way — but never below the group's own furniture.
            int roomForDevices = pageBottom - grpDevices.Top - _groupGap - minConsole;
            devicesHeight = Math.Min(devicesHeight, Math.Max(roomForDevices, lstDevices.Top + _devicesPadBelowList));

            grpDevices.Height = devicesHeight;
            grpConsole.Top = grpDevices.Bottom + _groupGap;
            grpConsole.Height = Math.Max(minConsole, pageBottom - grpConsole.Top);
        }

        /// <summary>Height of the device list's column-header row, in device pixels.</summary>
        private int ListHeaderHeight()
        {
            if (lstDevices.IsHandleCreated)
            {
                IntPtr header = SendMessage(lstDevices.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                if (header != IntPtr.Zero && GetWindowRect(header, out NativeRect r) && r.Bottom > r.Top)
                    return r.Bottom - r.Top;
            }
            return LogicalToDeviceUnits(20);   // sensible fallback before the handle exists
        }

        /// <summary>
        /// Sit Export immediately after Scan. It exports what the scan found, so the two read
        /// as a pair, and both anchor to the left so the gap between them never changes.
        ///
        /// Export used to be squeezed inside the device list's column-header band at the
        /// header's height, which made it the one button in the app that was a different size
        /// from every other. The Connect row below was the obvious alternative and is the
        /// wrong answer: it already carries the address box, Connect, the connection status
        /// and Timeout, and Export landed on top of the status text as soon as the window
        /// approached its minimum width.
        /// </summary>
        /// <summary>
        /// Lay the Network Scan group out as two rows, left to right.
        ///
        /// Done in code rather than in the designer because every control here is a different
        /// height — combo, text box, button, progress bar — and the widths depend on the font.
        /// A designer coordinate that looks right at 100% puts "SCPI Port(s):" half under the
        /// interface combo on a machine whose adapter name runs long, which is exactly what it
        /// was doing.
        ///
        /// Row one says where to look. Row two says how much of it, then does it: range,
        /// ports, Scan, Export, and the progress and status that Scan produces — left to
        /// right in the order the work happens.
        /// </summary>
        private void LayoutScanGroup()
        {
            int margin = LogicalToDeviceUnits(12);
            int gap = LogicalToDeviceUnits(8);
            int group = LogicalToDeviceUnits(20);

            // A label and the control it names are one thing, so they sit together. The
            // gaps that carry meaning are the ones between groups, and they only read as
            // gaps if the ones inside a pair are smaller than they are.
            int label = LogicalToDeviceUnits(2);

            PositionScanHeading();

            int row1 = Math.Max(LogicalToDeviceUnits(24), rdoNetwork.Bottom + LogicalToDeviceUnits(6));
            int row1H = cboInterface.Height;
            int row2 = row1 + row1H + LogicalToDeviceUnits(10);
            btnScan.Height = btnExport.Height;   // as Connect, see PositionTimeoutField

            int row2H = Math.Max(Math.Max(txtRange.Height, cboPort.Height), btnScan.Height);

            void Place(Control c, int left, int rowTop, int height)
            {
                c.Left = left;
                c.Top = rowTop + (height - c.Height) / 2;
            }

            // The two modes lay out the same two rows: where to look, then how much of it,
            // then the button that does it. Only the mode that is showing is placed —
            // ApplyScanKind calls back in here, so the other one is positioned the moment it
            // becomes visible rather than kept in step while hidden.
            Control lastInput;

            if (!rdoSerial.Checked)
            {
                // --- row one: where to look ---
                Place(lblInterface, margin, row1, row1H);
                Place(cboInterface, lblInterface.Right + label, row1, row1H);
                cboInterface.Width = LogicalToDeviceUnits(330);

                // --- row two: how much of it, then do it ---
                Place(lblRange, margin, row2, row2H);
                Place(txtRange, lblRange.Right + label, row2, row2H);
                txtRange.Width = LogicalToDeviceUnits(200);

                // Tighter here and wider before Scan: the range and the ports are two halves
                // of the same question, and Scan is the answer to it.
                Place(lblPort, txtRange.Right + gap, row2, row2H);
                Place(cboPort, lblPort.Right + label, row2, row2H);
                // Sized to the default list and no wider — measured rather than guessed,
                // because the font scales with the display and a fixed width either clips the
                // ports or leaves a stretch of empty box after them.
                cboPort.Width = Math.Max(LogicalToDeviceUnits(110),
                                         TextRenderer.MeasureText("5025, 5555, 3490, 111", cboPort.Font).Width
                                         + LogicalToDeviceUnits(30));

                lastInput = cboPort;
            }
            else
            {
                // The same two questions, asked of a serial bench: which ports, and at what
                // speed. Both boxes start at one column rather than each following its own
                // label — "Ports:" and "Baud rate(s):" differ by half the width of the
                // shorter one, and two boxes staggered by that much read as a mistake. The
                // network pair gets away without this only because its two labels happen to
                // measure within a few pixels of each other.
                int column = margin + Math.Max(lblScope.Width, lblBaud.Width) + label;

                Place(lblScope, margin, row1, row1H);
                Place(cboScope, column, row1, row1H);
                cboScope.Width = LogicalToDeviceUnits(330);

                Place(lblBaud, margin, row2, row2H);
                Place(cboBaud, column, row2, row2H);
                cboBaud.Width = Math.Max(LogicalToDeviceUnits(110),
                                         TextRenderer.MeasureText("9600, 115200, 19200, 38400, 57600", cboBaud.Font).Width
                                         + LogicalToDeviceUnits(30));

                lastInput = cboBaud;
            }

            Place(btnScan, lastInput.Right + LogicalToDeviceUnits(AnswerGapLogical), row2, row2H);

            // Progress and status follow Scan because they are what Scan produces. The bar
            // leads the sentence beside it, so it is a fixed short length rather than a rule
            // stretching across the group away from the words it belongs to. The status label
            // takes the remainder, which is what keeps "Scan complete. Found 3 device(s)."
            // on screen — the earlier version reserved the label's width and let the bar have
            // everything else, and the bar grew across the window on a high-DPI display.
            Place(progressScan, btnScan.Right + gap, row2, row2H);
            progressScan.Width = LogicalToDeviceUnits(140);

            Place(lblStatus, progressScan.Right + gap, row2, row2H);
            lblStatus.Width = Math.Max(LogicalToDeviceUnits(80),
                                       grpScan.ClientSize.Width - margin - lblStatus.Left);

            grpScan.Height = row2 + row2H + margin;

            // Everything below moves with it.
            grpDevices.Top = grpScan.Bottom + _scanToDevicesGap;
        }

        /// <summary>
        /// "[Network Scan | Serial Scan]" where the heading goes, laid over the group's own
        /// top border.
        ///
        /// A <see cref="GroupBox"/> reserves a gap in that border for its <c>Text</c> and
        /// for nothing else, so a control in the heading has to cover the line itself. Both
        /// halves paint an opaque background, so both do. The 9px indent is where WinForms
        /// starts heading text, so the switch begins where the words used to.
        /// </summary>
        private void PositionScanHeading()
        {
            rdoNetwork.Left = LogicalToDeviceUnits(9);
            rdoNetwork.Top = 0;

            // One control, so the halves share an edge: the second starts one pixel inside
            // the first and the two 1px borders land on each other as a single hairline. A
            // gap here would make it two buttons that happen to agree.
            rdoSerial.Left = rdoNetwork.Right - 1;
            rdoSerial.Top = 0;
        }


        /// <summary>
        /// Tear every connection down on the way out. Detached consoles are released from
        /// their windows first, so those windows don't try to hand them back to a form that
        /// is already closing.
        /// </summary>
        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _scanCts?.Cancel();
            _connectCts?.Cancel();   // a half-made connection must not outlive the window

            // Best effort, and deliberately not allowed to throw: whatever happens to the
            // windows, CloseAllSessions below must still run and hand the instruments back.
            foreach (InstrumentWindow w in _detached.ToArray())
            {
                try
                {
                    w.ReleaseConsole();
                    w.Close();
                }
                catch { /* a window that won't tidy up must not strand an instrument */ }
            }
            _detached.Clear();

            CloseAllSessions();
        }

        /// <summary>
        /// Hand every instrument back to its front panel before the app exits. Each close
        /// runs on a worker thread with a short wait: an instrument that has been switched
        /// off must not hang the shutdown. Both client implementations tolerate the extra
        /// Dispose if the wait expires first.
        /// </summary>
        private void CloseAllSessions()
        {
            foreach (InstrumentSession s in _sessions.Sessions.ToArray())
            {
                try { Task.Run(() => s.CloseAsync()).Wait(TimeSpan.FromMilliseconds(750)); }
                catch { /* best effort — we are on the way out */ }
                s.Dispose();
                _sessions.Remove(s);
            }
        }

        private void MainForm_FormClosed(object? sender, FormClosedEventArgs e)
        {
            SaveSettings();
        }

        private void PopulateInterfaces()
        {
            cboInterface.Items.Clear();
            foreach (var iface in NetworkScanner.GetLocalInterfaces())
                cboInterface.Items.Add(iface);

            if (cboInterface.Items.Count > 0)
                cboInterface.SelectedIndex = 0;
            else
                lblStatus.Text = "No active IPv4 network interface found.";
        }

        /// <summary>Parse a comma/space/semicolon separated port list into distinct valid ports.</summary>
        private static List<int> ParsePorts(string text)
        {
            var ports = new List<int>();
            foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out int p) && p is > 0 and <= 65535 && !ports.Contains(p))
                    ports.Add(p);
            }
            return ports;
        }

        /// <summary>First port from the list, used when the Address box omits an explicit port.</summary>
        private int GetDefaultPort()
        {
            var ports = ParsePorts(cboPort.Text);
            return ports.Count > 0 ? ports[0] : 5025;
        }

        // ------------------------------------------------------------------ scan

        private async void btnScan_Click(object? sender, EventArgs e)
        {
            // One button, two jobs: while a scan is running it acts as Stop.
            if (_scanCts is not null)
            {
                btnScan.Enabled = false;   // debounce until the scan unwinds
                // Say so straight away. The scanner stops posting progress the moment it is
                // cancelled, so without this the window would sit on "Scanning 254 hosts…"
                // with a frozen bar for the fraction of a second the sweep takes to unwind,
                // which reads as an ignored click.
                lblStatus.Text = "Stopping…";
                _scanCts.Cancel();
                return;
            }

            if (rdoSerial.Checked)
            {
                await ScanSerialAsync();
                return;
            }

            if (cboInterface.SelectedItem is not LocalInterface iface)
            {
                MessageBox.Show(this, "Select a network interface first.", "Scan",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var ports = ParsePorts(cboPort.Text);
            if (ports.Count == 0)
            {
                MessageBox.Show(this, "Enter one or more port numbers, e.g. 5025, 5555.", "Scan",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // The scan uses its own short, fixed probe timeouts — deliberately NOT the
            // user's Timeout field, which governs instrument communication. A LAN
            // connect is instant or never, so a short connect timeout keeps discovery
            // fast; only the few hosts with port 111 open pay the longer identify window
            // (VXI-11 is framed and poison-safe, and a slow Rigol *IDN? can take ~2-3 s).
            const int scanConnectTimeout = 300;
            const int scanIdnTimeout = 3000;

            // An IP range narrows the sweep; empty means the whole subnet, which is what this
            // did before the field existed. A range that cannot be read stops the scan rather
            // than falling back to the subnet — silently probing 254 hosts because of a typo
            // is the opposite of what was asked for.
            List<IPAddress> hosts;
            bool capped;
            string scope;

            if (txtRange.Text.Trim().Length == 0)
            {
                hosts = NetworkScanner.EnumerateHosts(iface.Address, iface.Mask, maxHosts: 4096, out capped);
                scope = "subnet";
            }
            else
            {
                if (!HostRange.TryParse(txtRange.Text, iface.Address, out HostRange? range, out string why))
                {
                    MessageBox.Show(this, why, "IP Range", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    txtRange.Focus();
                    txtRange.SelectAll();
                    return;
                }

                hosts = range!.Enumerate(4096, out capped);
                scope = range.ToString();
            }

            if (hosts.Count == 0)
            {
                lblStatus.Text = "Nothing to scan — that range holds no addresses.";
                return;
            }

            lstDevices.Items.Clear();
            btnExport.Enabled = false;
            progressScan.Value = 0;
            progressScan.Maximum = hosts.Count;
            _scanCts = new CancellationTokenSource();

            btnScan.Text = "Stop";
            SetIcon(btnScan, "stopClock");
            cboInterface.Enabled = false;
            txtRange.Enabled = false;
            SetScanKindEnabled(false);

            string cappedNote = capped ? " (capped at 4096)" : "";
            string portList = string.Join(", ", ports);

            // The range is named in the status line, not just obeyed. Someone who narrowed a
            // scan and found nothing needs to be able to see that they narrowed it.
            string where = scope == "subnet" ? "" : $" of {scope}";
            lblStatus.Text = $"Scanning {hosts.Count} hosts{where} on port(s) {portList}{cappedNote}...";

            int scanned = 0;
            var progress = new Progress<int>(done =>
            {
                scanned = done;
                progressScan.Value = Math.Min(done, progressScan.Maximum);
                lblStatus.Text = $"Scanning {hosts.Count} hosts{where} on port(s) {portList}{cappedNote}… "
                               + $"{done}/{hosts.Count} probed, {lstDevices.Items.Count} found.";
            });

            // Instruments are listed the moment they answer rather than all at the end —
            // on a large subnet the sweep takes a while, and a list that stays empty
            // throughout reads as a frozen app even though the UI is responsive.
            var deviceFound = new Progress<ScpiDevice>(dev =>
            {
                AddDeviceRow(dev);
                btnExport.Enabled = lstDevices.Items.Count > 0;
            });

            try
            {
                // Task.Run keeps the scan's synchronous ramp-up (spinning up one task
                // per host) off the UI thread, so the window stays responsive and the
                // Stop button is handled promptly.
                CancellationToken token = _scanCts.Token;
                var devices = await Task.Run(
                    () => NetworkScanner.ScanAsync(hosts, ports, scanConnectTimeout, scanIdnTimeout,
                                                   progress, token, deviceFound),
                    token);

                btnExport.Enabled = lstDevices.Items.Count > 0;

                lblStatus.Text = _scanCts.IsCancellationRequested
                    ? $"Scan stopped. Found {devices.Count} device(s)."
                    : $"Scan complete. Found {devices.Count} device(s).";
            }
            catch (OperationCanceledException)
            {
                // Whatever was found before the stop stays listed — partial results are useful.
                lblStatus.Text = $"Scan cancelled after {scanned} host(s). "
                               + $"Found {lstDevices.Items.Count} device(s).";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Scan error: " + ex.Message;
            }
            finally
            {
                btnScan.Text = "Scan";
                SetIcon(btnScan, "reset");
                btnScan.Enabled = true;
                cboInterface.Enabled = true;
                txtRange.Enabled = true;
                SetScanKindEnabled(true);
                _scanCts?.Dispose();
                _scanCts = null;
            }
        }

        /// <summary>
        /// The serial half of Scan: open each chosen port and ask it who it is.
        ///
        /// Deliberately unlike the subnet sweep in two ways, both of them the physics rather
        /// than a preference. Every port stays in the list whatever comes of asking — it is
        /// still a port on this machine, still connectable at settings this did not try —
        /// where an IP that does not answer is simply not a row. And the sweep is over baud
        /// rates rather than addresses: the address is the port, and the thing that has to
        /// match before a character gets through is the speed.
        /// </summary>
        private async Task ScanSerialAsync()
        {
            // Index 0 is "All serial ports"; anything else is one port by name.
            var all = SerialPorts.Names();
            var ports = cboScope.SelectedIndex > 0
                ? new List<string> { cboScope.Text }
                : all.ToList();

            if (ports.Count == 0)
            {
                lblStatus.Text = "No serial ports on this machine — nothing to scan.";
                return;
            }

            var bauds = SerialScanner.ParseBaudRates(cboBaud.Text);
            if (bauds.Count == 0)
            {
                MessageBox.Show(this, "Enter one or more baud rates, e.g. 9600, 115200.", "Scan",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                cboBaud.Focus();
                cboBaud.SelectAll();
                return;
            }

            // Every port back to unasked before asking again, so a rate that answered last
            // time is not still showing beside a port that has since gone quiet.
            lstDevices.Items.Clear();
            foreach (SerialDevice d in SerialScanner.List(all)) AddSerialRow(d);
            btnExport.Enabled = lstDevices.Items.Count > 0;

            // Long enough for a slow instrument to compose an identity at 9600, short enough
            // that five rates against a dead port is a wait rather than a hang. Not the
            // user's Timeout field, for the same reason the network scan is not: that one
            // governs instrument communication, and at its 30 s maximum this would take
            // twelve and a half minutes to find nothing.
            const int idnTimeout = 1500;

            progressScan.Value = 0;
            progressScan.Maximum = ports.Count;
            _scanCts = new CancellationTokenSource();

            btnScan.Text = "Stop";
            SetIcon(btnScan, "stopClock");
            cboScope.Enabled = false;
            cboBaud.Enabled = false;
            SetScanKindEnabled(false);

            string rates = string.Join(", ", bauds);
            string scope = ports.Count == 1 ? ports[0] : $"{ports.Count} ports";
            lblStatus.Text = $"Asking {scope} at {rates}…";

            int found = 0;
            var progress = new Progress<int>(done =>
            {
                progressScan.Value = Math.Min(done, progressScan.Maximum);
                lblStatus.Text = $"Asking {scope} at {rates}… {done}/{ports.Count} asked, {found} answered.";
            });

            var deviceFound = new Progress<SerialDevice>(dev =>
            {
                if (dev.Answered) found++;
                AddSerialRow(dev);
            });

            try
            {
                CancellationToken token = _scanCts.Token;
                var results = await Task.Run(
                    () => SerialScanner.ScanAsync(ports, bauds, idnTimeout, progress, token, deviceFound),
                    token);

                int answered = results.Count(r => r.Answered);
                btnExport.Enabled = lstDevices.Items.Count > 0;

                lblStatus.Text = answered > 0
                    ? $"Scan complete. {answered} of {ports.Count} port(s) answered."
                    : $"Scan complete. None of the {ports.Count} port(s) answered at {rates}. "
                      + "The instrument may be at another rate, or need CR-LF — set either in "
                      + "the Address box.";
            }
            catch (OperationCanceledException)
            {
                lblStatus.Text = $"Scan stopped. {found} port(s) answered.";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Scan error: " + ex.Message;
            }
            finally
            {
                btnScan.Text = "Scan";
                SetIcon(btnScan, "reset");
                btnScan.Enabled = true;
                cboScope.Enabled = true;
                cboBaud.Enabled = true;
                SetScanKindEnabled(true);
                _scanCts?.Dispose();
                _scanCts = null;
            }
        }

        // --------------------------------------------------------------- connect

        private void lstDevices_SelectedIndexChanged(object? sender, EventArgs e)
        {
            // Fill the Address box with the full endpoint (IP:port) so the
            // discovered port — e.g. 5555 for Rigol — is used on Connect.
            if (lstDevices.SelectedItems.Count == 0) return;

            object? tag = lstDevices.SelectedItems[0].Tag;

            // The address as it would be typed, not as the scan found it: vxi://host for a
            // VXI-11 row rather than host:111. Both connect to the same instrument — §5 reads
            // 111 as VXI-11 — but 111 is the portmapper's port number and reads like a raw
            // socket on an odd one, which is the protocol going missing at the exact moment
            // the row was there to say it.
            if (tag is ScpiDevice dev)
            {
                txtAddress.Text = dev.TypedAddress;
                return;
            }

            // A serial row fills the port box, so it goes in without the scheme: that box is
            // a list of serial ports and should read as one. The line settings come with it
            // when they are not the default, because they are the ones that answered and
            // dropping them would connect at 9600 to an instrument the scan found at 115200.
            if (tag is SerialDevice port)
            {
                cboSerialPort.Text = port.TypedAddress.StartsWith("serial://", StringComparison.OrdinalIgnoreCase)
                    ? port.TypedAddress["serial://".Length..]
                    : port.TypedAddress;
            }
        }

        private async void lstDevices_DoubleClick(object? sender, EventArgs e)
        {
            await ConnectSelectedAsync();
        }

        private async void btnConnect_Click(object? sender, EventArgs e)
        {
            await ConnectSelectedAsync();
        }

        /// <summary>
        /// Open a console for the address in the Address box (or the selected row).
        /// Existing connections are left alone — each instrument gets its own session.
        /// </summary>
        private async Task ConnectSelectedAsync()
        {
            // One button, two jobs, exactly as Scan does: while a connection is being made
            // it acts as Cancel.
            //
            // Worth having because connecting is the one operation here that can hang for a
            // long time with nothing to show for it. A raw-socket connect to an address
            // that answers ARP but has nothing listening sits in SYN retransmits, and
            // VXI-11 makes two of them — portmapper, then the core channel. The Timeout
            // field bounds each attempt, but at its 30 s maximum that is a long time to
            // stare at a window that will not respond to anything else.
            if (_connectCts is not null)
            {
                btnConnect.Enabled = false;      // debounce until the attempt unwinds
                _connectCts.Cancel();
                return;
            }

            if (!TryResolveTarget(out InstrumentAddress target))
            {
                // Which box is showing decides what is worth saying. On Serial, "select a
                // device" names a list that cannot contain one — a sweep does not find COM3.
                MessageBox.Show(this,
                    rdoSerial.Checked
                        ? "Choose a serial port, or type one — COM3, /dev/ttyUSB0, or "
                          + "COM3?baud=115200 if the instrument is not at 9600.\n\n"
                          + "The list is what this machine reports. A USB-to-serial adapter "
                          + "appears in it once it is plugged in."
                        : "Select a device or type a valid address (IP, IP:port, vxi://host "
                          + "or a VISA resource string).",
                    "Connect", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string host = target.Host;

            // One session per instrument. The Rigol DS2202 wedges its firmware if a second
            // TCP session is opened against it — no ping, dead front panel, power-cycle to
            // recover — so an address that is already connected switches to its console
            // instead of dialling again.
            InstrumentSession? existing = _sessions.FindByHost(host);
            if (existing != null)
            {
                FocusConsole(existing);
                // Say so in the console too: if its tab was already the selected one,
                // bringing it to the front looks like the button did nothing.
                ConsoleFor(existing)?.AppendLog(
                    $"--- already connected to {host}; this is its console ---", Color.Gray);
                return;
            }

            _connectCts = new CancellationTokenSource();
            btnConnect.Text = "Cancel";
            SetIcon(btnConnect, "stopClock");
            lblConnection.Text = $"Connecting to {host} …";

            IInstrumentClient? client = null;
            try
            {
                CancellationToken ct = _connectCts.Token;
                int commsTimeout = (int)numTimeout.Value;   // the user-facing Timeout field
                // Wrapped, so everything that will later share this connection — the console,
                // the quick buttons, both script runners, the captures, discovery — takes its
                // turn instead of interleaving on the wire. See SerializedInstrumentClient.
                client = new SerializedInstrumentClient(target.CreateClient(commsTimeout));

                await client.ConnectAsync(ct);

                // Identify the instrument — this decides which quick commands make
                // sense (a scope's :MEASure? is meaningless to a Siglent generator).
                //
                // Cancelling here has to be told apart from the instrument simply not
                // answering, which is why this catch is not the usual bare one: a Rigol
                // that stays silent still gets a console, but a user who pressed Cancel
                // should not be handed one anyway.
                string idn = "";
                try { idn = await client.QueryAsync("*IDN?", ct); }
                catch (OperationCanceledException) { throw; }
                catch { /* not fatal */ }

                ct.ThrowIfCancellationRequested();

                // If it wouldn't answer now, reuse whatever the scan already learned.
                if (string.IsNullOrWhiteSpace(idn)) idn = FindDiscoveredIdentity(host);

                var session = new InstrumentSession(
                    client, idn, InstrumentProfile.ForIdentity(idn), commsTimeout);
                _sessions.Add(session);
                client = null;   // the session owns it from here

                InstrumentConsole console = CreateConsole(session);
                AttachConsole(console, select: true);

                console.AppendLog($"--- connected to {host} via {session.Client.Description} ---",
                    Color.MediumSpringGreen);
                if (!string.IsNullOrWhiteSpace(idn)) console.AppendLog("*IDN? -> " + idn, Color.SkyBlue);
                console.AppendLog($"--- quick commands loaded for: {session.Profile.Name} ---", Color.Gray);

                UpdateConnectionSummary();
                console.FocusCommandInput();
            }
            catch (OperationCanceledException)
            {
                // Reaching here now means the user pressed Cancel and nothing else: the
                // clients raise TimeoutException when their own deadline is what fired
                // (see Deadline), so "cancelled" is only ever said about a real cancel.
                //
                // The half-open socket has to go: a cancelled VXI-11 attempt can have the
                // portmapper channel up and the core channel not, and leaving that dangling
                // is how the next attempt to the same instrument finds it unresponsive.
                client?.Dispose();
                UpdateConnectionSummary();
                lblStatus.Text = $"Connection to {host} cancelled.";
            }
            catch (Exception ex)
            {
                client?.Dispose();
                UpdateConnectionSummary();
                // The status line keeps the outcome after the dialog is dismissed — otherwise
                // the window still reads "Ready." with nothing to show a connect was tried.
                lblStatus.Text = ex is TimeoutException
                    ? $"No answer from {host} — check it is switched on and on this network."
                    : $"Could not connect to {host}.";
                MessageBox.Show(this, $"Could not connect to {host}:\n\n{ex.Message}", "Connect",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _connectCts?.Dispose();
                _connectCts = null;
                btnConnect.Text = "Connect";
                SetIcon(btnConnect, "connect");
                btnConnect.Enabled = true;
            }
        }

        /// <summary>
        /// Network or Serial: which bench this window is looking at.
        ///
        /// It is the heading of the scan card, because that is the honest scope of it. The
        /// choice governs the inputs on that card, the columns of the list under it, and
        /// which box the Address row shows — one switch for all three, rather than a switch
        /// on the address row that left the card above it still saying "Network Scan" while
        /// the box below it held COM3.
        ///
        /// Network is the default and is restored on every launch rather than remembered.
        /// A saved Serial would open the app on a card listing the ports of a machine whose
        /// adapter may not be plugged in this morning; the cost of being wrong that way is
        /// much higher than one click.
        ///
        /// Drawn as a two-segment switch rather than a pair of radio buttons: one control
        /// with a filled half, sharing a hairline, the way the web's `.seg` group already
        /// does it for the script-language switch and the theme toggle. How it is painted —
        /// the grey fill, the border, the text raised off centre so it reads as centred, and
        /// why it is still a <see cref="RadioButton"/> underneath — is
        /// <see cref="SegmentButton"/>'s. All that is decided here is how much air each
        /// caption gets.
        /// </summary>
        private void StyleScanKind()
        {
            // Each half as wide as its own caption and no wider. Uneven halves are fine —
            // asked for directly — and they cost less than the alternative did: matched to
            // the wider one the switch was a block of empty space beside "Serial Scan".
            //
            // The height is the heading's, not a text box's. It sits on the group's top
            // border where a word would be, so it is sized to the word: any taller and it
            // stops reading as a heading and starts reading as a control that has fallen
            // into one. Measured from the font rather than from either caption, so the two
            // halves cannot differ by a pixel of line height.
            int padWidth = LogicalToDeviceUnits(8);
            int padHeight = LogicalToDeviceUnits(4);

            rdoNetwork.Size = rdoNetwork.Measure(padWidth, padHeight);
            rdoSerial.Size = new Size(rdoSerial.Measure(padWidth, padHeight).Width, rdoNetwork.Height);
        }

        /// <summary>
        /// The switch is dead while a scan runs, as the inputs beside it are.
        ///
        /// Not merely tidy: switching mid-sweep would empty the list the sweep is still
        /// filling, and rebuild the card the sweep's own <c>finally</c> is about to put back.
        /// The way out of a running scan is Stop, which is what that button now says.
        /// </summary>
        private void SetScanKindEnabled(bool enabled)
        {
            rdoNetwork.Enabled = enabled;
            rdoSerial.Enabled = enabled;
        }

        private void WireScanKind()
        {
            rdoSerial.CheckedChanged += (_, _) => ApplyScanKind(rdoSerial.Checked);

            // Both port lists are refreshed as they open, not once at startup. A USB-to-serial
            // adapter appears the moment it is plugged in, and the moment someone opens one of
            // these is exactly when they have just plugged one in.
            cboSerialPort.DropDown += (_, _) => RefreshSerialPorts();
            cboScope.DropDown += (_, _) => RefreshSerialScope();

            // A port list that grew while the card was open is a list of rows that is now
            // wrong, so the rows follow the box — but only when nothing has been asked yet.
            // After a scan those rows carry identities, and re-listing would throw them away.
            cboScope.DropDownClosed += (_, _) =>
            {
                bool asked = false;
                foreach (ListViewItem item in lstDevices.Items)
                    if (item.Tag is SerialDevice s && s.Probed) { asked = true; break; }

                if (!asked) ListSerialPorts(SerialPorts.Names());
            };
        }

        /// <summary>
        /// Put the whole card into one mode or the other: the inputs that ask the question,
        /// the columns that show the answer, and the box that connects to it.
        ///
        /// The list is emptied on the way through. Rows from the other mode are not merely
        /// stale, they are unreadable — an IP address under a column headed "Port", a
        /// baud rate under one headed "Port" — and a row that fills the Address box has to
        /// fill it with something the visible box can hold.
        /// </summary>
        private void ApplyScanKind(bool serial)
        {
            foreach (Control c in new Control[] { lblInterface, cboInterface, lblRange, txtRange, lblPort, cboPort })
                c.Visible = !serial;

            foreach (Control c in new Control[] { lblScope, cboScope, lblBaud, cboBaud })
                c.Visible = serial;

            txtAddress.Visible = !serial;
            cboSerialPort.Visible = serial;

            colIp.Text = serial ? "Port" : "IP Address";
            colPort.Text = serial ? "Settings" : "Port";

            lstDevices.Items.Clear();
            btnExport.Enabled = false;
            progressScan.Value = 0;

            // The two modes' inputs are different widths, so Scan and everything after it
            // move. Only the visible mode is ever placed; this is the moment the other one
            // becomes the visible one.
            LayoutScanGroup();
            LayoutDeviceColumns();

            _tips.SetToolTip(btnScan, serial ? ScanSerialTip : ScanNetworkTip);

            // "Scan the network above" is the one sentence on screen telling a new user what
            // to do next, so it has to be about the card that is actually above it.
            _emptyConsoleText = serial ? EmptyConsoleSerialText : _emptyConsoleNetworkText;
            UpdateConsoleHostVisibility();

            if (!serial)
            {
                lblStatus.Text = "Ready.";
                txtAddress.Focus();
                return;
            }

            RefreshSerialPorts();
            ListSerialPorts(RefreshSerialScope());

            // Setting a ComboBox's Text selects it, which leaves the baud list looking
            // picked-up when the focus is somewhere else entirely.
            cboBaud.SelectionLength = 0;
            cboSerialPort.Focus();
        }

        /// <summary>
        /// Re-read the machine's ports into the scope box, keeping the chosen one if it is
        /// still there. Touches nothing else: this also runs as the box is opened, and a
        /// dropdown that threw away the scan under it would be a trap.
        /// </summary>
        private IReadOnlyList<string> RefreshSerialScope()
        {
            var ports = SerialPorts.Names();
            string previous = cboScope.SelectedIndex > 0 ? cboScope.Text : "";

            cboScope.BeginUpdate();
            try
            {
                cboScope.Items.Clear();
                cboScope.Items.Add($"All serial ports  —  {ports.Count} found");
                foreach (string p in ports) cboScope.Items.Add(p);
            }
            finally
            {
                cboScope.EndUpdate();
            }

            int keep = previous.Length > 0 ? cboScope.Items.IndexOf(previous) : -1;
            cboScope.SelectedIndex = keep > 0 ? keep : 0;
            return ports;
        }

        /// <summary>
        /// The ports as rows, unasked. Opens nothing — this is the state the list is in until
        /// somebody presses Scan, and it is already enough to pick a port and connect
        /// (SPEC §17).
        /// </summary>
        private void ListSerialPorts(IReadOnlyList<string> ports)
        {
            lstDevices.Items.Clear();
            foreach (SerialDevice d in SerialScanner.List(ports)) AddSerialRow(d);
            btnExport.Enabled = lstDevices.Items.Count > 0;

            lblStatus.Text = ports.Count == 0
                ? "No serial ports on this machine — plug in an adapter and open the Ports list."
                : $"{ports.Count} port(s) listed. Scan asks each one for its identity.";
        }

        /// <summary>
        /// Re-read the machine's serial ports, keeping whatever is typed. Listed, never
        /// probed: opening a port to see what is on it is not the harmless connect a TCP
        /// probe is, and the settings would have to be right first anyway (SPEC §17).
        /// </summary>
        private void RefreshSerialPorts()
        {
            string typed = cboSerialPort.Text;

            cboSerialPort.BeginUpdate();
            try
            {
                cboSerialPort.Items.Clear();
                foreach (string name in SerialPorts.Names()) cboSerialPort.Items.Add(name);
            }
            finally
            {
                cboSerialPort.EndUpdate();
            }

            // One port and nothing typed: choose it. There is no ambiguity to leave open, and
            // an empty box in front of a list of one reads as a list of none.
            if (typed.Length == 0 && cboSerialPort.Items.Count == 1)
                cboSerialPort.SelectedIndex = 0;
            else
                cboSerialPort.Text = typed;
        }

        private bool TryResolveTarget(out InstrumentAddress target)
        {
            target = new InstrumentAddress("", InstrumentTransport.RawSocket, GetDefaultPort(), "inst0");

            // On Serial the port list is the address box, and what it holds is a port name
            // rather than a whole address — COM3, or COM3?baud=115200 where the instrument is
            // not at 9600. The scheme is added here rather than shown in the list, because a
            // dropdown of serial ports should read as a list of serial ports. Anyone who
            // pastes the full serial://COM3 gets it back unchanged.
            if (rdoSerial.Checked)
            {
                string chosen = cboSerialPort.Text.Trim();
                if (chosen.Length == 0) return false;

                if (!chosen.StartsWith("serial://", StringComparison.OrdinalIgnoreCase))
                    chosen = "serial://" + chosen;

                if (!InstrumentAddress.TryParse(chosen, out InstrumentAddress typedPort, out _))
                    return false;

                target = typedPort;
                return true;
            }

            string text = txtAddress.Text.Trim();

            // Nothing typed: fall back to the selected device row (IP + port + transport).
            if (string.IsNullOrEmpty(text))
            {
                if (lstDevices.SelectedItems.Count > 0 &&
                    lstDevices.SelectedItems[0].Tag is ScpiDevice sel)
                {
                    target = new InstrumentAddress(
                        sel.Address.ToString(), sel.Transport, sel.Port, "inst0");
                    return true;
                }
                return false;
            }

            // Every spelling the CLI and the web build accept, read by the one parser in
            // Core so all three agree: a VISA resource string, vxi://host, tcp://host,
            // serial://COM3, host:port, or a bare host on whichever port the Port(s) box
            // leads with.
            if (!InstrumentAddress.TryParse(text, out InstrumentAddress typed, out _, GetDefaultPort()))
                return false;

            target = typed;
            string host = typed.Host;
            int port = typed.Port;

            // A serial port is not a host and there is nothing on the device list to prefer
            // over it — the list is what a subnet sweep found, and a subnet sweep does not
            // find COM3 (SPEC §17). Its name is checked by trying to open it, which is the
            // only check there is.
            if (typed.Transport == InstrumentTransport.Serial)
                return host.Length > 0;

            // The user named the transport — a resource string, vxi:// or tcp://. Take it
            // as written rather than preferring a discovered row's, which is the whole
            // point of having said it.
            if (typed.TransportNamed)
                return IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) != UriHostNameType.Unknown;

            bool explicitPort = typed.PortNamed;

            // Prefer a discovered device's port/transport for this IP, so typing a
            // bare (already-found) address still reaches it the right way.
            ScpiDevice? match = null;
            foreach (ListViewItem item in lstDevices.Items)
            {
                if (item.Tag is ScpiDevice d && d.Address.ToString() == host &&
                    (!explicitPort || d.Port == port))
                {
                    match = d;
                    break;
                }
            }

            InstrumentTransport transport = typed.Transport;

            if (match != null)
            {
                if (!explicitPort) port = match.Port;
                transport = match.Transport;
            }
            else if (port == Vxi11Client.PortmapperPort)
            {
                transport = InstrumentTransport.Vxi11;   // an explicit :111 means VXI-11
            }

            target = typed with { Transport = transport, Port = port };

            return IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) != UriHostNameType.Unknown;
        }

        /// <summary>Append one discovered instrument to the device list.</summary>
        private void AddDeviceRow(ScpiDevice dev)
        {
            var item = new ListViewItem(dev.Address.ToString());
            item.SubItems.Add(dev.Port.ToString());
            item.SubItems.Add(dev.TransportName);
            item.SubItems.Add(string.IsNullOrEmpty(dev.Identity)
                ? "(responded, but no *IDN? reply)"
                : dev.Identity);
            item.Tag = dev;
            lstDevices.Items.Add(item);
        }

        /// <summary>
        /// The same four columns for a serial port. Unlike a network row, this one exists
        /// before anything has been asked: the port is on the machine whether or not an
        /// instrument answers on it, so the Identity cell is empty until Scan fills it.
        ///
        /// Replaces the row for the same port rather than appending, because Scan runs over
        /// ports that are already listed — appending would show COM3 twice, once as found
        /// and once as asked.
        /// </summary>
        private void AddSerialRow(SerialDevice dev)
        {
            var item = new ListViewItem(dev.PortName);
            item.SubItems.Add(dev.SettingsText);
            item.SubItems.Add(dev.TransportName);
            item.SubItems.Add(dev.IdentityText);
            item.Tag = dev;

            foreach (ListViewItem existing in lstDevices.Items)
            {
                if (existing.Tag is SerialDevice s &&
                    string.Equals(s.PortName, dev.PortName, StringComparison.OrdinalIgnoreCase))
                {
                    int at = existing.Index;
                    bool wasSelected = existing.Selected;
                    lstDevices.Items.RemoveAt(at);
                    lstDevices.Items.Insert(at, item);
                    item.Selected = wasSelected;
                    return;
                }
            }

            lstDevices.Items.Add(item);
        }

        /// <summary>Identity this address reported during the last scan, if any.</summary>
        private string FindDiscoveredIdentity(string host)
        {
            foreach (ListViewItem item in lstDevices.Items)
            {
                if (item.Tag is ScpiDevice d && d.Address.ToString() == host &&
                    !string.IsNullOrWhiteSpace(d.Identity))
                {
                    return d.Identity;
                }
            }
            return "";
        }

        // ---------------------------------------------------------- consoles

        /// <summary>
        /// Build a console for a session.
        ///
        /// It asks the form for nothing any more. Detaching is asked for by the tab, which is
        /// this form's own control, and a detached console comes back when its window closes.
        /// </summary>
        private InstrumentConsole CreateConsole(InstrumentSession session) => new(session);

        /// <summary>Put a console into a tab of the main window.</summary>
        private void AttachConsole(InstrumentConsole console, bool select)
        {
            var page = new TabPage(console.Session.Title)
            {
                Tag = console,
                ToolTipText = console.Session.Description,
                UseVisualStyleBackColor = true,
            };

            // Give the page a place in the tab strip — and a window handle — before the
            // console goes into it, so the control moves straight from its previous parent
            // into this one instead of passing through WinForms' parking window.
            tabConsoles.TabPages.Add(page);
            UpdateConsoleHostVisibility();          // the strip must be visible to realise the page
            if (select || tabConsoles.TabPages.Count == 1) tabConsoles.SelectedTab = page;

            console.Dock = DockStyle.Fill;
            page.Controls.Add(console);
            console.SetDetached(false);

            UpdateConsoleHostVisibility();
        }

        /// <summary>
        /// Move a console out of its tab and into its own window. The control itself is
        /// reparented, so the session, log and history carry over untouched.
        /// </summary>
        private void DetachConsole(InstrumentConsole console)
        {
            TabPage? page = PageFor(console);
            if (page == null) return;

            // Create and *show* the destination first, so AdoptConsole is a single
            // parent-to-parent move. Removing the console from its tab beforehand would
            // leave it unparented, which WinForms services with a hidden parking window and
            // an extra handle recreation — avoidable work on the control that carries this
            // instrument's whole log.
            var win = new InstrumentWindow(console.Session.Title, Icon)
            {
                Location = NextDetachedLocation(),
            };
            win.ReattachRequested += (s, e) => OnDetachedWindowClosed((InstrumentWindow)s!);
            _detached.Add(win);

            // Show it as owned by this window. A detached console is meant to be watched
            // while the main window is used, so it must never end up buried behind it —
            // ownership keeps it above this form (and only this form; other applications
            // still come over the top as usual).
            win.Show(this);
            win.AdoptConsole(console);
            win.BringToFront();
            win.Activate();

            // The page is empty now that the console has moved out of it.
            tabConsoles.TabPages.Remove(page);
            page.Dispose();

            UpdateConsoleHostVisibility();
        }

        /// <summary>
        /// A detached window is closing: take its console back into a tab. Called
        /// synchronously from the window's FormClosing, while it still owns the console.
        /// </summary>
        private void OnDetachedWindowClosed(InstrumentWindow win)
        {
            _detached.Remove(win);
            AttachConsole(win.Console, select: true);
        }

        /// <summary>Disconnect a console's instrument and close the console.</summary>
        private async Task CloseConsoleAsync(InstrumentConsole console)
        {
            InstrumentSession session = console.Session;

            // Drop the link FIRST, before touching any windows. Hand the front panel back
            // (VXI-11 needs an explicit device_local; raw sockets no-op and rely on the
            // close). Tidying the UI first would mean a failure while reparenting or
            // disposing a control left the instrument connected and stuck in remote mode
            // with nothing on screen to close — the one outcome worth designing against.
            _sessions.Remove(session);
            await session.CloseAsync();

            try
            {
                RemoveConsoleFromHost(console);
                console.Dispose();
            }
            catch (Exception ex)
            {
                // The instrument is already safely disconnected, so this is cosmetic —
                // report it rather than letting it surface as a failed disconnect.
                MessageBox.Show(this,
                    "The instrument was disconnected, but its console could not be closed "
                    + "cleanly:\n\n" + ex.Message,
                    "Disconnect", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                UpdateConsoleHostVisibility();
                UpdateConnectionSummary();
            }
        }

        /// <summary>Take a console out of whatever is hosting it — a tab, or its own window.</summary>
        private void RemoveConsoleFromHost(InstrumentConsole console)
        {
            InstrumentWindow? win = _detached.Find(w => w.Console == console);
            if (win != null)
            {
                win.ReleaseConsole();
                _detached.Remove(win);
                win.Close();
            }
            else if (PageFor(console) is TabPage page)
            {
                page.Controls.Remove(console);
                tabConsoles.TabPages.Remove(page);
                page.Dispose();
            }
        }

        /// <summary>The tab currently holding this console, or null if it is detached.</summary>
        private TabPage? PageFor(InstrumentConsole console)
        {
            foreach (TabPage p in tabConsoles.TabPages)
                if (ReferenceEquals(p.Tag, console)) return p;
            return null;
        }

        /// <summary>This session's console, wherever it is currently hosted.</summary>
        private InstrumentConsole? ConsoleFor(InstrumentSession session)
        {
            foreach (InstrumentWindow w in _detached)
                if (w.Console.Session == session) return w.Console;

            foreach (TabPage p in tabConsoles.TabPages)
                if (p.Tag is InstrumentConsole c && c.Session == session) return c;

            return null;
        }

        /// <summary>Show the session's console: select its tab, or raise its window.</summary>
        private void FocusConsole(InstrumentSession session)
        {
            foreach (InstrumentWindow w in _detached)
            {
                if (w.Console.Session == session)
                {
                    if (w.WindowState == FormWindowState.Minimized) w.WindowState = FormWindowState.Normal;
                    w.Activate();
                    return;
                }
            }

            foreach (TabPage p in tabConsoles.TabPages)
            {
                if (p.Tag is InstrumentConsole c && c.Session == session)
                {
                    tabConsoles.SelectedTab = p;
                    c.FocusCommandInput();
                    return;
                }
            }
        }

        /// <summary>Cascade detached windows down-right of the main window so they don't stack.</summary>
        private Point NextDetachedLocation()
        {
            int step = LogicalToDeviceUnits(28) * (_detached.Count + 1);
            Rectangle wa = Screen.GetWorkingArea(this);
            int x = Math.Min(Left + step, Math.Max(wa.Left, wa.Right - LogicalToDeviceUnits(400)));
            int y = Math.Min(Top + step, Math.Max(wa.Top, wa.Bottom - LogicalToDeviceUnits(300)));
            return new Point(x, y);
        }

        /// <summary>Swap between the tab strip and the "nothing connected" explanation.</summary>
        private void UpdateConsoleHostVisibility()
        {
            bool anyTabs = tabConsoles.TabPages.Count > 0;
            tabConsoles.Visible = anyTabs;
            lblNoConsole.Visible = !anyTabs;

            // The tab strip's height depends on whether there are tabs at all, and the
            // Scripts button lines up with it.
            PositionSequenceButton();

            // With every console detached the area is empty but the instruments are still
            // connected — say so, rather than repeating "nothing is connected".
            lblNoConsole.Text = _detached.Count > 0
                ? $"All {_detached.Count} instrument console(s) are open in their own windows.\r\n\r\n"
                  + "Close one of those windows to bring it back here."
                : _emptyConsoleText;
        }

        private void UpdateConnectionSummary()
        {
            lblConnection.Text = _sessions.Count switch
            {
                0 => "Not connected.",
                1 => "1 instrument connected.",
                int n => $"{n} instruments connected.",
            };
        }

        /// <summary>Apply the Timeout field to connections that are already open.</summary>
        private void ApplyTimeoutToOpenSessions()
        {
            int ms = (int)numTimeout.Value;
            foreach (InstrumentSession s in _sessions.Sessions)
            {
                s.UserTimeoutMs = ms;
                s.Client.TimeoutMs = ms;
            }
        }

        /// <summary>Right-click menu on the tab strip: the same two actions the console header offers.</summary>
        private void BuildTabContextMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Detach to Its Own Window", null, (_, _) =>
            {
                if (_menuTarget?.Tag is InstrumentConsole c) DetachConsole(c);
            }));
            menu.Items.Add(new ToolStripMenuItem("Disconnect and Close Tab", null, (_, _) =>
            {
                if (_menuTarget?.Tag is InstrumentConsole c) _ = CloseConsoleAsync(c);
            }));

            // Only offer the menu when the click actually landed on a tab, not on the
            // empty strip beside them or inside the console itself.
            menu.Opening += (_, e) => { if (_menuTarget == null) e.Cancel = true; };

            tabConsoles.ContextMenuStrip = menu;
            tabConsoles.MouseDown += (_, e) => _menuTarget = TabPageAt(e.Location);

            BuildTabGlyphs();
        }

        /// <summary>
        /// Give every tab the two things done to a tab: detach it, and close it.
        ///
        /// WinForms has neither, so the tabs are owner-drawn: the label, then two glyphs in
        /// squares at the right end, and a hit test on mouse-up. The context menu still carries
        /// both actions — these are the discoverable way to reach them, not the only one.
        ///
        /// Detach was a labelled button on the console's own header, which cost a row's width to
        /// say what a glyph says and could only be reached by first bringing that console to the
        /// front — a thing done to a tab, reachable only from inside the tab. The web build puts
        /// it on the tab beside the ✕ and this is the same arrangement and the same glyph.
        ///
        /// Mouse *up*, not down: a press that starts on a glyph and slides off should do nothing,
        /// and one of these disconnects an instrument.
        /// </summary>
        private void BuildTabGlyphs()
        {
            tabConsoles.DrawMode = TabDrawMode.OwnerDrawFixed;

            // Widen the tabs to make room for both glyphs. Scaled: TabControl.Padding.X is
            // per side and already in device pixels here, so adding a raw 16 left the label
            // clipped at 175% while looking right at 100%.
            tabConsoles.Padding = new Point(
                tabConsoles.Padding.X + LogicalToDeviceUnits(2 * CloseGlyphLogical),
                tabConsoles.Padding.Y);

            tabConsoles.DrawItem += (_, e) =>
            {
                if (e.Index < 0 || e.Index >= tabConsoles.TabPages.Count) return;

                Graphics g = e.Graphics;
                Rectangle bounds = tabConsoles.GetTabRect(e.Index);
                bool selected = tabConsoles.SelectedIndex == e.Index;

                using var back = new SolidBrush(selected ? SystemColors.Window : SystemColors.Control);
                g.FillRectangle(back, bounds);

                Rectangle close = CloseGlyphRect(bounds);
                Rectangle pop = DetachGlyphRect(bounds);
                bool over = _hoverClose == e.Index;

                if (over)
                {
                    using var hot = new SolidBrush(Color.FromArgb(230, 90, 90));
                    g.FillRectangle(hot, close);
                }

                // A quieter highlight than the ✕'s: this one opens a window, and red is the
                // colour of the glyph that disconnects an instrument.
                if (_hoverDetach == e.Index)
                {
                    using var warm = new SolidBrush(Color.FromArgb(210, 220, 235));
                    g.FillRectangle(warm, pop);
                }

                // The same glyph the web build draws on its own tabs, from the same 64x64
                // artwork the toolbar buttons use — see AppIcons.Drawn.
                //
                // Not disposed: Drawn caches by name and size and hands the same bitmap to every
                // caller, so disposing it here would leave the next paint — and every button
                // wearing that glyph — holding a dead image.
                //
                // Re-inked to the colour the ✕ is drawn in. The artwork is authored in the near
                // black a toolbar button wants, which beside a grey ✕ on the same tab reads as one
                // of the two being the live one. They are a pair and are drawn as a pair: grey
                // ordinarily, and darker under the pointer, which is that glyph's version of the
                // ✕ turning red.
                Image? mark = AppIcons.Drawn("detach", pop.Width - LogicalToDeviceUnits(4));
                if (mark != null)
                {
                    Color ink = _hoverDetach == e.Index
                        ? SystemColors.ControlText
                        : SystemColors.GrayText;
                    using var tint = new ImageAttributes();
                    tint.SetColorMatrix(new ColorMatrix(
                    [
                        [0f, 0f, 0f, 0f, 0f],
                        [0f, 0f, 0f, 0f, 0f],
                        [0f, 0f, 0f, 0f, 0f],
                        [0f, 0f, 0f, 1f, 0f],   // alpha through, so the shape is kept
                        [ink.R / 255f, ink.G / 255f, ink.B / 255f, 0f, 1f],
                    ]));

                    var where = new Rectangle(pop.X + (pop.Width - mark.Width) / 2,
                                              pop.Y + (pop.Height - mark.Height) / 2,
                                              mark.Width, mark.Height);
                    g.DrawImage(mark, where, 0, 0, mark.Width, mark.Height,
                                GraphicsUnit.Pixel, tint);
                }

                // The label stops where the first glyph starts, so a long instrument name is
                // trimmed rather than drawn underneath them.
                var textArea = new Rectangle(bounds.X + LogicalToDeviceUnits(6), bounds.Y,
                                             pop.Left - bounds.X - LogicalToDeviceUnits(8),
                                             bounds.Height);
                TextRenderer.DrawText(g, tabConsoles.TabPages[e.Index].Text, tabConsoles.Font,
                                      textArea, SystemColors.ControlText,
                                      TextFormatFlags.VerticalCenter | TextFormatFlags.Left
                                    | TextFormatFlags.EndEllipsis);

                using var pen = new Pen(over ? Color.White : SystemColors.GrayText, 1.4f);
                int inset = close.Width / 3;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawLine(pen, close.Left + inset, close.Top + inset,
                                close.Right - inset, close.Bottom - inset);
                g.DrawLine(pen, close.Right - inset, close.Top + inset,
                                close.Left + inset, close.Bottom - inset);
            };

            tabConsoles.MouseMove += (_, e) =>
            {
                (int wasClose, int wasPop) = (_hoverClose, _hoverDetach);
                _hoverClose = GlyphAt(e.Location, CloseGlyphRect);
                _hoverDetach = GlyphAt(e.Location, DetachGlyphRect);
                if (wasClose != _hoverClose || wasPop != _hoverDetach) tabConsoles.Invalidate();
            };

            tabConsoles.MouseLeave += (_, _) =>
            {
                if (_hoverClose < 0 && _hoverDetach < 0) return;
                _hoverClose = _hoverDetach = -1;
                tabConsoles.Invalidate();
            };

            tabConsoles.MouseUp += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;

                int shut = GlyphAt(e.Location, CloseGlyphRect);
                if (shut >= 0 && tabConsoles.TabPages[shut].Tag is InstrumentConsole closing)
                {
                    _ = CloseConsoleAsync(closing);
                    return;
                }

                int pop = GlyphAt(e.Location, DetachGlyphRect);
                if (pop >= 0 && tabConsoles.TabPages[pop].Tag is InstrumentConsole moving)
                    // Reparents the console, so let the click that asked for it finish first
                    // rather than pulling the control out from under its own handler.
                    BeginInvoke(new Action(() => DetachConsole(moving)));
            };
        }

        /// <summary>Logical width of each glyph square at the end of a tab.</summary>
        private const int CloseGlyphLogical = 16;

        /// <summary>Which tab's ✕ the pointer is over, or -1. Drives the hover highlight.</summary>
        private int _hoverClose = -1;

        /// <summary>The same, for the detach glyph beside it.</summary>
        private int _hoverDetach = -1;

        /// <summary>The ✕ square inside a tab's rectangle: the last thing on the tab.</summary>
        private Rectangle CloseGlyphRect(Rectangle tab)
        {
            int size = LogicalToDeviceUnits(CloseGlyphLogical);
            return new Rectangle(tab.Right - size - LogicalToDeviceUnits(4),
                                 tab.Top + (tab.Height - size) / 2, size, size);
        }

        /// <summary>The detach square, immediately to the left of the ✕.</summary>
        private Rectangle DetachGlyphRect(Rectangle tab)
        {
            Rectangle close = CloseGlyphRect(tab);
            return new Rectangle(close.Left - close.Width - LogicalToDeviceUnits(2),
                                 close.Top, close.Width, close.Height);
        }

        /// <summary>The tab whose glyph of that kind covers this point, or -1.</summary>
        private int GlyphAt(Point p, Func<Rectangle, Rectangle> square)
        {
            for (int i = 0; i < tabConsoles.TabPages.Count; i++)
                if (square(tabConsoles.GetTabRect(i)).Contains(p)) return i;
            return -1;
        }

        /// <summary>The tab whose header covers this point in the tab control, if any.</summary>
        private TabPage? TabPageAt(Point p)
        {
            for (int i = 0; i < tabConsoles.TabPages.Count; i++)
                if (tabConsoles.GetTabRect(i).Contains(p)) return tabConsoles.TabPages[i];
            return null;
        }

        // ------------------------------------------------------------- export

        private void btnExport_Click(object? sender, EventArgs e)
        {
            if (lstDevices.Items.Count == 0) return;

            using var dlg = new SaveFileDialog
            {
                Title = "Export discovered instruments",
                Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = "discovered-instruments.csv",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                // Whichever list is showing, written under its own header — the file says the
                // same four words the columns above it do.
                var devices = new List<ScpiDevice>();
                var ports = new List<SerialDevice>();
                foreach (ListViewItem item in lstDevices.Items)
                {
                    if (item.Tag is ScpiDevice d) devices.Add(d);
                    else if (item.Tag is SerialDevice s) ports.Add(s);
                }

                int count = devices.Count + ports.Count;
                File.WriteAllText(dlg.FileName, ports.Count > 0
                    ? ScanResultExport.ToCsv(ports)
                    : ScanResultExport.ToCsv(devices));
                lblStatus.Text = $"Exported {count} row(s) to {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not export results:\n" + ex.Message, "Export",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
