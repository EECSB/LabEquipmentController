using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace LabEquipmentController
{
    /// <summary>
    /// The whole shipped command library, browsable without an instrument attached: every
    /// manufacturer, every catalogued model, every command, and the vendor guide each was
    /// transcribed from.
    ///
    /// Built-in catalogs only. Commands a user extracted from their own datasheet live
    /// elsewhere (<see cref="ExtractedCatalogStore"/>) and stay out of here — this window is
    /// meant to answer "what does the app know", and mixing in one machine's unverified
    /// extractions would make that answer different on every machine.
    /// </summary>
    public sealed class CommandLibraryForm : Form
    {
        private readonly TreeView _tree = new();
        private readonly TextBox _filter = new();
        private readonly ListView _list = new();

        /// <summary>Narrows the listed commands, as <see cref="_filter"/> narrows the tree.</summary>
        private readonly TextBox _commandFilter = new();
        private readonly Panel _commandFilterHost = new();
        private readonly ContextMenuStrip _rowMenu = new();

        // One strip under each of the three columns, all the same height, so the tree, the
        // command list and the guide page end on one line. See LevelBottomStrips.
        private readonly FlowLayoutPanel _leftBottom = new();
        private readonly Panel _viewerBottom = new() { Dock = DockStyle.Bottom };
        private FlowLayoutPanel _guidePanel = null!;
        private readonly Label _guide = new();
        private readonly Label _count = new();
        private readonly Button _vendorPage = new();
        private readonly Button _setFolder = new();
        private readonly ToolTip _tips = new();

        private readonly List<Entry> _entries = new();
        private Entry? _selected;
        private string? _datasheetFolder;
        private SplitContainer _split = null!;

        // --- the embedded guide viewer ---
        private SplitContainer _pdfSplit = null!;
        private Microsoft.Web.WebView2.WinForms.WebView2? _viewer;
        private Label _viewerNote = null!;

        /// <summary>
        /// Whether the WebView2 runtime is usable. Null until the first attempt.
        ///
        /// It ships with Windows 10 and 11, but it is a separate component that can be
        /// absent or broken, and the failure only shows up when initialisation is awaited.
        /// One check, remembered, so a machine without it does not throw once per selection.
        /// </summary>
        private bool? _viewerAvailable;

        /// <summary>What the viewer currently has loaded, so re-selecting does not reload.</summary>
        private string? _viewerPath;

        /// <summary>One catalog, with everything the window needs about it.</summary>
        private sealed record Entry(
            InstrumentFamily Family, string CatalogName, string Manufacturer,
            string Instrument, CommandReference Reference);

        public CommandLibraryForm()
        {
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9f);
            Text = "Command Library";
            // Wide enough for three columns — tree, commands, guide — since the guide now
            // sits beside the commands rather than under them. Clamped in OnLoad, because
            // this is more than the working area of a 1366x768 laptop.
            // 1560 was set when the guide column arrived and was sized so all three panes
            // could be generous at once. They no longer need to be: the commands pane holds
            // a floor of its own now, so what comes off the total comes off the guide, which
            // is the pane that degrades gracefully — it falls back to page thumbnails.
            //
            // Now the size this window was actually being dragged to before anyone could use
            // it: a guide page is only readable when the third column is wide enough to show
            // it at something near its own width, and 1600 was not.
            ClientSize = new Size(2948, 1344);
            MinimumSize = new Size(820, 500);
            StartPosition = FormStartPosition.CenterParent;

            // Falls back to the repository's own datasheets/ when nothing is set, so a
            // development build finds the guides without anyone configuring it.
            _datasheetFolder = SettingsStore.Load().EffectiveDatasheetFolder();

            LoadCatalogs();
            BuildUi();
            BuildTree("");
        }

        // ------------------------------------------------------------------------- data

        private void LoadCatalogs()
        {
            foreach (InstrumentFamily family in Enum.GetValues<InstrumentFamily>())
            {
                CommandReference? r = CommandReference.ForFamily(family);
                if (r == null || r.Commands.Count == 0) continue;

                _entries.Add(new Entry(
                    family,
                    CommandReference.CatalogNameFor(family) ?? family.ToString(),
                    string.IsNullOrWhiteSpace(r.Manufacturer) ? "Other" : r.Manufacturer,
                    string.IsNullOrWhiteSpace(r.Instrument) ? family.ToString() : r.Instrument,
                    r));
            }
        }

        // --------------------------------------------------------------------------- ui

        private void BuildUi()
        {
            // Minimum sizes and the splitter position are set in OnLoad, not here: a
            // SplitContainer is 150px wide until the form is laid out, and asking for two
            // panels that need 560 between them throws before the window ever appears.
            _split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
            SplitContainer split = _split;

            // --- left: filter + manufacturer tree ---
            _filter.Dock = DockStyle.Top;
            _filter.PlaceholderText = "Filter by maker, model or command…";
            _filter.TextChanged += (_, _) => BuildTree(_filter.Text.Trim());

            _tree.Dock = DockStyle.Fill;
            _tree.HideSelection = false;
            _tree.ShowNodeToolTips = true;
            _tree.AfterSelect += (_, e) => Select(e.Node?.Tag as Entry);

            // Under the tree, stacked: the folder this window reads from, and then what it
            // found there. Both are about the collection as a whole rather than about the
            // catalog on the right, which is why they sit under the thing you pick from.
            _leftBottom.Dock = DockStyle.Bottom;
            _leftBottom.FlowDirection = FlowDirection.TopDown;
            _leftBottom.WrapContents = false;
            _leftBottom.AutoSize = true;
            _leftBottom.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _leftBottom.Padding = new Padding(0, 8, 0, 0);

            _count.AutoSize = true;
            _count.Margin = new Padding(2, 6, 0, 0);
            _leftBottom.Controls.Add(_setFolder);
            _leftBottom.Controls.Add(_count);

            // Fill first, then docked edges (this project's convention for docking order).
            var leftPad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 10, 5, 10) };
            leftPad.Controls.Add(_tree);
            leftPad.Controls.Add(_leftBottom);
            leftPad.Controls.Add(_filter);
            split.Panel1.Controls.Add(leftPad);

            // --- right: commands + the guide behind them ---
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.GridLines = true;
            _list.ShowItemToolTips = true;
            // Same provenance mark the per-console reference window shows (§10). This window
            // is the one reachable with nothing connected, so it is where a catalog is most
            // likely to be browsed — a flag that only appears in the other window is a flag
            // most readers never see.
            _list.Columns.Add("✓", 34);
            _list.Columns.Add("Command", 300);
            _list.Columns.Add("Category", 150);
            _list.Columns.Add("Description", 400);
            // Description takes whatever is left, so the list never needs a horizontal
            // scrollbar — one costs a row, and this list is the point of the window.
            _list.SizeChanged += (_, _) => FitColumns();

            // Double-click copies. The row is not editable and there is nothing else a
            // double-click could sensibly mean here, and it is the gesture people already
            // use in the per-console reference window to put a command somewhere useful.
            _list.DoubleClick += (_, _) => CopySelectedCommand();
            _list.MouseDown += SelectRowUnderRightClick;

            _rowMenu.Items.Add(new ToolStripMenuItem("Copy command", null, (_, _) => CopySelectedCommand()));
            // No menu over empty space: there is nothing to copy below the last row.
            _rowMenu.Opening += (_, e) => e.Cancel = _list.SelectedItems.Count == 0;
            _list.ContextMenuStrip = _rowMenu;

            // The tree's filter picks which instrument; this one picks which of its commands.
            // A catalog runs to 1200 entries, which is not a thing to scroll.
            _commandFilter.Dock = DockStyle.Fill;
            _commandFilter.PlaceholderText = "Filter these commands by name, category or description…";
            _commandFilter.TextChanged += (_, _) => FillCommands();

            // The box in a padded host rather than docked straight to the column, so it keeps
            // a gap off the list's column headers. Height is set in OnLoad from the box's own
            // preferred height, which is font-driven and not known yet.
            _commandFilterHost.Dock = DockStyle.Top;
            _commandFilterHost.Padding = new Padding(0, 0, 0, 6);
            _commandFilterHost.Controls.Add(_commandFilter);

            // The guide's one line, and the button that fetches it, on that line. Open Vendor
            // Page belongs beside the guide it opens rather than out at the window's corner:
            // it is the answer to the "no local copy" the line beside it may be reporting.
            // Set Datasheets Folder… stays on the bottom bar — that one is a setting for the
            // whole app, not something about this catalog.
            // A flow, not a table: the link follows the title immediately rather than sitting
            // at the far end of a column the title does not fill. WrapContents so a title
            // long enough to fill the row pushes the link to the next line instead of off
            // the edge.
            _guidePanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(0, 8, 0, 0),
            };
            FlowLayoutPanel guidePanel = _guidePanel;

            _guide.AutoSize = true;       // so the link knows where the text ends
            _guide.UseMnemonic = false;   // guide titles contain '&' — B&K, Rohde & Schwarz
            _guide.ForeColor = SystemColors.GrayText;
            _guide.Margin = new Padding(0, 5, 0, 0);   // onto the link's baseline

            ButtonStyle.Apply(_vendorPage, "Open Vendor Page", (_, _) => OpenVendorPage());
            ButtonStyle.Apply(_setFolder, "Set Datasheets Folder…", (_, _) => ChooseFolder());
            StyleAsLink(_vendorPage);

            guidePanel.Controls.Add(_guide);
            guidePanel.Controls.Add(_vendorPage);

            // A third column rather than a shelf under the commands: the guide is a page of
            // A4 in portrait, and a pane the width of the window and a third its height
            // shows a strip of one. Side by side, both the command list and the page get a
            // full column of height each.
            //
            // Collapsed until a guide is actually shown — an empty column would just be a
            // hole where the command list used to be.
            // Minimum sizes are set in OnLoad for the same reason the outer splitter's are:
            // this container is 150px wide until the form is laid out, and asking for two
            // panels that need more than that between them throws on the spot —
            // "SplitterDistance must be between Panel1MinSize and Width - Panel2MinSize".
            _pdfSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
            };

            // Commands and the guide strip below them travel together in the middle column,
            // so the viewer runs the full height of the window beside both.
            var commandsColumn = new Panel { Dock = DockStyle.Fill };
            // Fill first, then docked edges (this project's convention for docking order):
            // the list takes what the filter above and the guide below leave.
            commandsColumn.Controls.Add(_list);
            commandsColumn.Controls.Add(guidePanel);
            commandsColumn.Controls.Add(_commandFilterHost);
            // Opening the guide column narrows this one, so the width the title wraps at
            // changes with it.
            commandsColumn.SizeChanged += (_, _) => FitGuideWidth();
            _pdfSplit.Panel1.Controls.Add(commandsColumn);
            _pdfSplit.Panel2.Controls.Add(BuildViewerPanel());

            var rightPad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5, 10, 10, 10) };
            rightPad.Controls.Add(_pdfSplit);
            split.Panel2.Controls.Add(rightPad);

            // No bar across the foot of the window any more. A bar there spans all three
            // columns, so whatever sits on it belongs to none of them, and it held the two
            // columns beside it off the line the middle one ended on. Each column carries its
            // own strip instead, and LevelBottomStrips makes them one height.
            Controls.Add(split);


            _tips.AutoPopDelay = 20000;
            _tips.SetToolTip(_filter, "Narrows the tree. Matches the maker, the model, and the "
                                    + "commands themselves — type MEASure to find every "
                                    + "instrument that has one.");
            _tips.SetToolTip(_commandFilter, "Narrows the list beside it to the selected "
                                           + "instrument's commands that match — name, "
                                           + "category or description.");
            _tips.SetToolTip(_list, "Double-click a command to copy it, or right-click for "
                                  + "the same on a menu.");
            _tips.SetToolTip(_vendorPage, "Open the maker's documentation page in a browser. "
                                        + "Guides are not shipped with the app — they are the "
                                        + "vendors' copyright — so this is where to get them.");
            _tips.SetToolTip(_setFolder, "Choose the folder where you keep downloaded "
                                       + "programming guides. Only ever read from.");
        }

        /// <summary>
        /// Esc closes it.
        ///
        /// A form gets that for free from CancelButton, and CancelButton needs a button.
        /// With the button gone the keystroke went with it, which is not a trade anyone asked
        /// for — so it is wired directly.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Fit the screen before anything is measured against the form's width. Not
            // Math.Clamp: it throws when the minimum exceeds the maximum, which is what a
            // screen smaller than MinimumSize gives.
            Size wa = Screen.GetWorkingArea(this).Size;
            Size = new Size(Math.Min(Math.Max(Width, MinimumSize.Width), wa.Width),
                            Math.Min(Math.Max(Height, MinimumSize.Height), wa.Height));

            ButtonStyle.SetDrawnIcon(this, _vendorPage, "globe");
            ButtonStyle.SetDrawnIcon(this, _setFolder, "folder");
            // Only the real button is normalised. Normalize gives a control the app's button
            // height and a 76-unit minimum width, which is what makes a button look like one —
            // exactly the chrome the vendor link is meant not to have.
            ButtonStyle.Normalize(this, _setFolder);

            // Underlined, from the form's font rather than the button's own, which is still
            // the WinForms default this early.
            _vendorPage.Font = new Font(Font, FontStyle.Underline);

            // After the button has its real height — the left strip is the taller of the two
            // and it is the button that makes it so.
            FitGuideWidth();
            ButtonStyle.CentreInRow(_filter, _filter.Height);
            // The host carries the gap under the box: a docked control ignores its Margin,
            // so the only way to hold one off its neighbour is to pad the thing around it.
            _commandFilterHost.Height = _commandFilter.PreferredHeight
                                      + _commandFilterHost.Padding.Vertical;

            // Now that the form has a real width, the panels can be given their minimums and
            // the splitter a sensible starting position (see the note in BuildUi).
            _split.Panel1MinSize = LogicalToDeviceUnits(200);
            _split.Panel2MinSize = LogicalToDeviceUnits(320);
            FitColumns();
            // 300. Catalog names run to 79 characters and will not fit at any width worth
            // giving a tree, so chasing them was the wrong idea — 380 spent the width and
            // still clipped the long ones. The nodes carry tooltips with the full name, so
            // the tree only has to show enough to tell one catalog from the next.
            SetSplitter(_split, LogicalToDeviceUnits(300));

            ReserveCommandsWidth();
            // The guide can be dragged shut. Its column is half the window, which leaves the
            // Description column showing "Clears the standard…" for most entries — and a
            // reader comparing descriptions across a catalog does not want the guide open at
            // all. Zero rather than a minimum, so the splitter goes all the way; no button,
            // because a Hide Guides button was here once and was not wanted.
            _pdfSplit.Panel2MinSize = 0;

            // 55/45 to the commands: a guide page is portrait and the command list is three
            // columns of text, so the list is the one that suffers first from squeezing.
            SetSplitter(_pdfSplit, Math.Max(_pdfSplit.Panel1MinSize, (int)(_pdfSplit.Width * 0.55)));
            _pdfSplit.SizeChanged += (_, _) => ReserveCommandsWidth();

            // Dragging the guide shut leaves the panel a few pixels wide, and a WebView2 that
            // narrow still draws its own scrollbar — a sliver of grey furniture down the right
            // edge of a window the user just asked to be rid of. Hide the viewer once the
            // panel is too narrow to show a page, so closed looks closed.
            _pdfSplit.SplitterMoved += (_, _) => UpdateViewerVisibility();
            UpdateViewerVisibility();

            // Nothing is selected yet, so the guide column says so rather than sitting blank.
            _ = ShowGuideAsync();
        }

        /// <summary>
        /// Give Description the width the other two columns leave, minus the scrollbar
        /// gutter — reserved even while hidden, since it appears the moment the list fills
        /// and ClientSize shrinking for it raises no event to react to.
        /// </summary>
        /// <summary>
        /// Keep the commands pane wide enough for four columns with a readable Description,
        /// so opening the guide costs the guide's own width rather than the descriptions'.
        ///
        /// 620 is 34 for the mark, the Command and Category floors, ~300 for Description and
        /// the scrollbar gutter. Clamped to what the container can actually offer: a
        /// Panel1MinSize larger than the SplitContainer throws, and this window can be
        /// resized down to 820 wide.
        /// </summary>
        private void ReserveCommandsWidth()
        {
            // 520, down from 620. At 620 the guide column was squeezed to a strip of
            // thumbnails too small to read — the floor guarantees the commands their width,
            // so every pixel taken off the window came off the guide. The descriptions give
            // up a little back so the guide stays legible without being dragged open.
            int wanted = LogicalToDeviceUnits(520);
            int room = Math.Max(LogicalToDeviceUnits(200), _pdfSplit.Width - LogicalToDeviceUnits(40));
            _pdfSplit.Panel1MinSize = Math.Min(wanted, room);
        }

        /// <summary>
        /// Show the guide column's contents only when there is room for a page. Below that
        /// the panel is furniture: a scrollbar and nothing to scroll.
        /// </summary>
        private void UpdateViewerVisibility()
        {
            bool room = _pdfSplit.Panel2.Width >= LogicalToDeviceUnits(80);
            if (_viewer != null) _viewer.Visible = room;
            _viewerNote.Visible = room;
        }

        private void FitColumns()
        {
            if (_list.Columns.Count < 4) return;

            int gutter = Math.Max(SystemInformation.VerticalScrollBarWidth, LogicalToDeviceUnits(17));
            int available = _list.ClientSize.Width - _list.Columns[0].Width - gutter - LogicalToDeviceUnits(4);

            // Command and Category are given what they need and no more, so Description gets
            // a usable share of a narrow pane instead of the remainder after two generous
            // fixed columns. At the old fixed 300 + 150 the description had ~150px beside an
            // open guide, which is four words. Both shrink to a floor before it does.
            int command  = Math.Clamp(available / 3, LogicalToDeviceUnits(150), LogicalToDeviceUnits(300));
            int category = Math.Clamp(available / 6, LogicalToDeviceUnits(90),  LogicalToDeviceUnits(150));

            _list.Columns[1].Width = command;
            _list.Columns[2].Width = category;
            _list.Columns[3].Width = Math.Max(LogicalToDeviceUnits(120), available - command - category);

            // Same stale-scroll-range quirk the device list has: narrowing the window leaves
            // a horizontal scrollbar behind unless the list is made to recompute.
            _list.BeginUpdate();
            _list.EndUpdate();
        }

        // ------------------------------------------------------------------------ tree

        private void BuildTree(string filter)
        {
            _tree.BeginUpdate();
            _tree.Nodes.Clear();

            IEnumerable<Entry> shown = _entries.Where(en => Matches(en, filter));

            int instruments = 0;
            foreach (IGrouping<string, Entry> maker in shown
                         .GroupBy(en => en.Manufacturer)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var makerNode = new TreeNode(maker.Key);
                foreach (Entry en in maker.OrderBy(en => en.Instrument, StringComparer.OrdinalIgnoreCase))
                {
                    makerNode.Nodes.Add(new TreeNode($"{en.Instrument}  ({en.Reference.Commands.Count})")
                    {
                        Tag = en,
                        // The name is what identifies the catalog and the longest is 79
                        // characters, so some will always be clipped. Hovering gives the
                        // whole thing rather than leaving the reader to widen the pane.
                        ToolTipText = $"{en.Instrument}\r\n{en.Reference.Commands.Count} commands",
                    });
                    instruments++;
                }
                _tree.Nodes.Add(makerNode);
            }

            // Expanded while filtering — a filter that hides its own results is a filter that
            // looks broken.
            if (filter.Length > 0) _tree.ExpandAll();
            _tree.EndUpdate();

            int commands = shown.Sum(en => en.Reference.Commands.Count);
            _count.Text = $"{instruments:N0} instrument catalog(s), {commands:N0} command(s)"
                        + (filter.Length > 0 ? $"  matching “{filter}”" : " built in");

            KeepSelectionInStep();
        }

        /// <summary>
        /// Put the selection back on the rebuilt tree, or clear everything if what was
        /// selected is no longer in it.
        ///
        /// Rebuilding replaces every node, which drops the selection without raising
        /// AfterSelect — so nothing told the rest of the window. The commands, the guide line
        /// and the PDF all went on showing the previous instrument while the tree beside them
        /// listed a different maker entirely: filter to "Fluke" with the Rigol DP800 open and
        /// you got Fluke in the tree, DP800's 309 commands in the list, DP800's guide in the
        /// viewer, and a count line reading "125 command(s) matching Fluke" over the lot.
        ///
        /// Clearing unconditionally would be the easy fix and the wrong one: narrowing the
        /// filter around the instrument you are already reading should keep it, not throw it
        /// away. So it is re-found by identity where it survived the filter.
        /// </summary>
        private void KeepSelectionInStep()
        {
            if (_selected == null) return;

            TreeNode? again = _tree.Nodes.Cast<TreeNode>()
                .SelectMany(maker => maker.Nodes.Cast<TreeNode>())
                .FirstOrDefault(n => ReferenceEquals(n.Tag, _selected));

            if (again == null) { Select(null); return; }

            // Setting SelectedNode raises AfterSelect, which calls Select for us.
            _tree.SelectedNode = again;
        }

        private static bool Matches(Entry en, string filter)
        {
            if (filter.Length == 0) return true;
            if (en.Manufacturer.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
            if (en.Instrument.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;

            return en.Reference.Commands.Any(c =>
                c.Syntax.Contains(filter, StringComparison.OrdinalIgnoreCase)
             || c.Description.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        // --------------------------------------------------------------------- details

        private void Select(Entry? entry)
        {
            _selected = entry;
            FillCommands();
            UpdateGuidePanel();
        }

        /// <summary>
        /// Fill the list from the selected catalog, keeping only what the command filter
        /// matches.
        ///
        /// The filter is deliberately not cleared when the selection changes: comparing how
        /// two instruments spell the same measurement is one of the things this window is
        /// for, and having to retype it at each one would make that tedious.
        /// </summary>
        private void FillCommands()
        {
            string filter = _commandFilter.Text.Trim();

            _list.BeginUpdate();
            _list.Items.Clear();
            if (_selected != null)
            {
                foreach (CommandRef c in _selected.Reference.Commands)
                {
                    if (!MatchesCommand(c, filter)) continue;

                    // The command itself hangs off the row, so Copy takes the syntax as the
                    // catalog records it rather than whatever the column happened to show.
                    var item = new ListViewItem(CommandReferenceForm.MarkFor(c)) { Tag = c };
                    item.SubItems.Add(c.Syntax);
                    item.SubItems.Add(c.Category);
                    item.SubItems.Add(c.Description);
                    item.ToolTipText = CommandReferenceForm.ProvenanceOf(c);
                    _list.Items.Add(item);
                }
            }
            _list.EndUpdate();
        }

        private static bool MatchesCommand(CommandRef c, string filter)
            => filter.Length == 0
            || c.Syntax.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || c.Category.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || c.Description.Contains(filter, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Select the row under a right-click, before the menu opens on it.
        ///
        /// A ListView does not do this itself: right-clicking leaves the selection where it
        /// was, so Copy would have taken whichever row was last left-clicked — a different
        /// command from the one under the pointer, and silently, since copying looks the same
        /// either way.
        /// </summary>
        private void SelectRowUnderRightClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;

            ListViewItem? row = _list.HitTest(e.X, e.Y).Item;
            if (row == null) return;

            _list.SelectedItems.Clear();
            row.Selected = true;
        }

        /// <summary>
        /// Put the selected command on the clipboard, exactly as the catalog spells it.
        /// </summary>
        private void CopySelectedCommand()
        {
            if (_list.SelectedItems.Count == 0) return;
            if (_list.SelectedItems[0].Tag is not CommandRef c) return;

            try { Clipboard.SetText(c.Syntax); } catch { /* clipboard can be busy; ignore */ }
        }

        private void UpdateGuidePanel()
        {
            CommandGuide? guide = _selected?.Reference.Guide;

            if (guide == null)
            {
                _guide.Text = _selected == null
                    ? "Pick an instrument on the left."
                    : "No source guide is recorded for this catalog.";
                _vendorPage.Enabled = false;
                _ = ShowGuideAsync();
                return;
            }

            // The guide, and nothing about the file behind it. Whether there is a local copy
            // is already answered by the column beside this line, and answered better: where
            // there is one it renders the pages, and where there is not it says so and names
            // the folder it searched. Restating it here cost a second line — the file name
            // alone is about half the width of this column — to say what was already on
            // screen.
            _guide.Text = guide.Title
                        + (guide.Edition.Length > 0 ? $"  ({guide.Edition})" : "");

            _vendorPage.Enabled = guide.Url.Length > 0;
            FitGuideWidth();

            // The guide column follows the selection — that is the whole point of it being
            // a column rather than something behind a button.
            _ = ShowGuideAsync();
        }

        /// <summary>
        /// Make a button read as a hyperlink while staying a button.
        ///
        /// A LinkLabel would be the obvious choice and cannot carry the globe: its image
        /// support is a background, not a glyph beside the text. So the chrome is taken off a
        /// Button instead — no border, no fill, no hover plate — which keeps the drawn icon,
        /// the click handler and the disabled state that greys it when a catalog records no
        /// vendor URL.
        /// </summary>
        private void StyleAsLink(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.Transparent;
            b.FlatAppearance.MouseDownBackColor = Color.Transparent;
            b.BackColor = Color.Transparent;
            b.UseVisualStyleBackColor = false;
            b.ForeColor = SystemColors.HotTrack;
            b.Cursor = Cursors.Hand;
            b.Padding = Padding.Empty;
            b.Margin = new Padding(12, 0, 0, 0);
            b.TextImageRelation = TextImageRelation.ImageBeforeText;
        }

        /// <summary>
        /// Give the guide label a width to wrap at.
        ///
        /// It sizes its own height now — it is AutoSize, so the link beside it knows where
        /// the text ends — but an AutoSize label with no ceiling grows sideways for ever, and
        /// titles are not reliably short: "63200A Series High Power Electronic Load Operation
        /// &amp; Programming Manual  (October 2024)" is one of them. Without a ceiling that
        /// one runs off the end of the column, taking the link with it.
        ///
        /// The list is the same width as this panel and always current, so it is what the
        /// room is measured from.
        /// </summary>
        private void FitGuideWidth()
        {
            int room = _list.Width - _vendorPage.Width - LogicalToDeviceUnits(24);
            _guide.MaximumSize = new Size(Math.Max(LogicalToDeviceUnits(120), room), 0);
            LevelBottomStrips();
        }

        /// <summary>
        /// Give the three columns' bottom strips one height, so the tree, the command list and
        /// the guide page all end on the same line.
        ///
        /// They hold different things — a button over a count, a title beside a link, and
        /// nothing at all — so left to themselves they are three different heights and the
        /// three panes above them stop at three different places. The tallest wins, which is
        /// the left one: a button and a line of text against the guide's single line.
        ///
        /// AutoSize is turned off first. A panel that sizes itself cannot also be told a
        /// height — the next layout pass would put its own back.
        /// </summary>
        private void LevelBottomStrips()
        {
            if (_guidePanel == null) return;

            // The count wraps inside the tree's column now rather than running the width of
            // the window, and it is long enough to need to: at the old full-width bar it fit
            // on one line, and under the tree the filter it names was simply cut off.
            _count.MaximumSize = new Size(Math.Max(LogicalToDeviceUnits(120), _tree.Width), 0);

            int tallest = Math.Max(_leftBottom.PreferredSize.Height, _guidePanel.PreferredSize.Height);
            if (tallest <= 0) return;

            _leftBottom.AutoSize = false;
            _guidePanel.AutoSize = false;

            _leftBottom.Height = tallest;
            _guidePanel.Height = tallest;
            _viewerBottom.Height = tallest;
        }

        // ---------------------------------------------------------------- guide viewer

        /// <summary>
        /// The lower half of the right-hand side: the WebView2 that shows the guide, with a
        /// label over it for everything that is not a rendered PDF — no local copy yet, no
        /// runtime, or the file still opening.
        /// </summary>
        private Control BuildViewerPanel()
        {
            var host = new Panel { Dock = DockStyle.Fill };

            _viewerNote = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = SystemColors.GrayText,
                UseMnemonic = false,
            };

            _viewer = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill, Visible = false };

            // Both fill the panel, so exactly one of them is visible at a time. Relying on
            // z-order instead does not work: Controls.Add appends, and index 0 is the FRONT
            // of the z-order — so the label added first sat on top of the viewer and the
            // PDF rendered underneath "Opening …".
            host.Controls.Add(_viewer);
            host.Controls.Add(_viewerNote);

            // Nothing in it: it exists so the page ends level with the command list and the
            // tree, which both have something under them. Height comes from LevelBottomStrips.
            host.Controls.Add(_viewerBottom);
            return host;
        }

        /// <summary>
        /// Put the selected catalog's guide in the viewer.
        ///
        /// WebView2 needs an explicit initialisation before the first navigation, and that
        /// is where a missing runtime surfaces. It is done once and the outcome remembered:
        /// where it fails, the pane says so and Open Local Copy still hands the file to the
        /// shell, which is all this window could do before.
        /// </summary>
        private async Task ShowGuideAsync()
        {
            if (_viewer == null) return;

            // The column is always there, so it has to account for itself in every state,
            // not just the one where a PDF is available.
            if (_selected == null)
            {
                ShowNote("Pick an instrument on the left to read its guide here.");
                return;
            }

            string? path = LocalCopy();
            if (path == null)
            {
                _viewerPath = null;

                // Name the folder that was searched. Without it, a folder set one level too
                // deep — at a manufacturer's own folder rather than the root above it — reads
                // as a broken lookup rather than a wrong path, and every catalog but that one
                // manufacturer's says the same unhelpful thing.
                string where = string.IsNullOrWhiteSpace(_datasheetFolder)
                    ? "No datasheets folder is set."
                    : "Searched: " + _datasheetFolder;

                ShowNote(_selected.Reference.Guide == null
                    ? "No source guide is recorded for this catalog."
                    : "No local copy of this guide was found.\r\n\r\n" + where
                    + "\r\n\r\nUse Open Vendor Page to download it, then Set Datasheets Folder… "
                    + "to point the app at the folder your guides are kept in — the one holding "
                    + "the per-manufacturer folders, not one of them.");
                return;
            }

            if (_viewerAvailable == false) { ShowViewerUnavailable(); return; }

            if (_viewerAvailable == null)
            {
                ShowNote("Starting the viewer…");
                try
                {
                    await _viewer.EnsureCoreWebView2Async();
                    _viewerAvailable = true;
                }
                catch (Exception ex)
                {
                    _viewerAvailable = false;
                    _viewerUnavailableReason = ex.Message;
                    ShowViewerUnavailable();
                    return;
                }
            }

            if (_viewerPath == path) { ShowViewer(); return; }

            ShowNote("Opening " + Path.GetFileName(path) + "…");
            try
            {
                // A file:// URI, not the bare path: WebView2 navigates URLs, and a Windows
                // path with a space or a '#' in it is not one.
                _viewer.Source = new Uri(path);
                _viewerPath = path;
                ShowViewer();
            }
            catch (Exception ex)
            {
                _viewerPath = null;
                ShowNote("Could not open this guide:\r\n" + ex.Message);
            }
        }

        private string? _viewerUnavailableReason;

        /// <summary>
        /// Move a splitter to roughly where it is wanted, without throwing.
        ///
        /// SplitContainer refuses any SplitterDistance outside
        /// [Panel1MinSize, Width - Panel2MinSize], and on a container narrower than the two
        /// minimums together that range is empty — there is no legal value, so the only
        /// thing to do is leave it where it is.
        /// </summary>
        private static void SetSplitter(SplitContainer split, int want)
        {
            int span = split.Orientation == Orientation.Vertical ? split.Width : split.Height;
            int low = split.Panel1MinSize;
            int high = span - split.Panel2MinSize - split.SplitterWidth;
            if (high < low) return;

            split.SplitterDistance = Math.Min(Math.Max(want, low), high);
        }

        /// <summary>Put the message up and the viewer away.</summary>
        private void ShowNote(string text)
        {
            _viewerNote.Text = text;
            _viewerNote.Visible = true;
            if (_viewer != null) _viewer.Visible = false;
        }

        /// <summary>...and the other way round.</summary>
        private void ShowViewer()
        {
            _viewerNote.Visible = false;
            if (_viewer != null) _viewer.Visible = true;
        }

        private void ShowViewerUnavailable()
            => ShowNote(
                "The built-in viewer needs the Microsoft Edge WebView2 runtime, which this "
                + "machine does not have.\r\n\r\nUse Open Local Copy to read the guide in "
                + "your usual PDF reader instead."
                + (string.IsNullOrWhiteSpace(_viewerUnavailableReason)
                    ? "" : "\r\n\r\n(" + _viewerUnavailableReason + ")"));

        private string? LocalCopy()
            => _selected == null
                ? null
                : DatasheetLocator.Find(_datasheetFolder, _selected.Reference.Guide,
                                        _selected.CatalogName, _selected.Manufacturer);

        // --------------------------------------------------------------------- actions

        private void OpenVendorPage()
        {
            string? url = _selected?.Reference.Guide?.Url;
            if (string.IsNullOrWhiteSpace(url)) return;
            OpenExternally(url);
        }

        /// <summary>
        /// Hand a URL or a file to the shell. UseShellExecute is required — without it,
        /// Process.Start treats a URL as an executable name and throws.
        /// </summary>
        private void OpenExternally(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open:\n" + target + "\n\n" + ex.Message,
                    "Command Library", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ChooseFolder()
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Where do you keep your downloaded programming guides?",
                UseDescriptionForTitle = true,
                SelectedPath = _datasheetFolder ?? "",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            _datasheetFolder = dlg.SelectedPath;

            UserSettings settings = SettingsStore.Load();
            settings.DatasheetFolder = _datasheetFolder;
            SettingsStore.Save(settings);

            UpdateGuidePanel();
        }
    }
}
