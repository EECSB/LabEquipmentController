using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LabEquipmentController
{
    /// <summary>
    /// A searchable view of an instrument's curated SCPI command reference. Filter by
    /// text, then double-click (or Insert) to drop a command into the caller's command
    /// box, or Copy it to the clipboard. Non-modal — handy to keep open beside the console.
    /// </summary>
    public sealed class CommandReferenceForm : Form
    {

        /// <summary>
        /// Esc closes it. Every window in the app that only shows you something behaves this
        /// way; the ones that ask you to decide something keep a button instead.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        private readonly CommandReference _reference;
        private readonly Action<string> _onInsert;

        private readonly TextBox _filter = new();
        private readonly Label _filterLabel = new();
        private readonly ListView _list = new();
        private readonly Button _insert = new();
        private readonly Button _copy = new();
        private readonly Label _source = new();
        private readonly ToolTip _tips = new();
        private Panel? _bottom;
        private FlowLayoutPanel? _buttons;

        /// <summary>Width the Command column needs for its longest listed entry; 0 until measured.</summary>
        private int _commandWidth;

        public CommandReferenceForm(CommandReference reference, Action<string> onInsert)
        {
            _reference = reference;
            _onInsert = onInsert;

            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9f);   // the app's font, so this window scales like the rest
            Text = "Command Reference — " + reference.Instrument;
            // Twice the old 860x560 in each direction. This window is a list to read down
            // and a Description column to read across, and both were the first thing anyone
            // had to resize. OnLoad clamps it to the screen it opens on.
            ClientSize = new Size(1720, 1120);
            MinimumSize = new Size(560, 380);
            StartPosition = FormStartPosition.CenterParent;

            BuildUi();
            Populate("");
        }

        private void BuildUi()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(10, 8, 10, 6) };
            _filterLabel.Text = "Filter:";
            _filterLabel.AutoSize = true;
            _filterLabel.Location = new Point(10, 12);
            // Both of these are provisional: the label is AutoSize, so how far it actually
            // reaches is not known until it has a font and a parent. OnLoad puts the box
            // after wherever the label ends.
            _filter.Location = new Point(62, 8);
            _filter.Width = ClientSize.Width - 62 - 12;
            _filter.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _filter.PlaceholderText = "type to filter by command, description, or subsystem…";
            _filter.TextChanged += (_, _) => Populate(_filter.Text);
            top.Controls.Add(_filterLabel);
            top.Controls.Add(_filter);

            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.ShowItemToolTips = true;
            _list.Columns.Add("✓", 34);          // provenance mark, see MarkFor
            _list.Columns.Add("Command", 270);
            _list.Columns.Add("Description", 480);
            _list.DoubleClick += (_, _) => InsertSelected();
            _list.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter) { InsertSelected(); e.Handled = true; }
            };
            _list.SizeChanged += (_, _) => FitColumns();

            // Both heights here are provisional — OnLoad replaces them with the height the
            // buttons actually measure once they have their glyphs and this display's scale.
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 70, Padding = new Padding(10, 6, 10, 8) };
            _bottom = bottom;

            _source.Dock = DockStyle.Top;
            _source.Height = 26;
            _source.Text = _reference.Source;
            _source.ForeColor = SystemColors.GrayText;
            _source.AutoEllipsis = true;

            // Padding on top, so the buttons are not pressed against the source line above
            // them. Their heights are worked out in OnLoad, which adds this on.
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 34,
                WrapContents = false,
                Padding = new Padding(0, 10, 0, 0),
            };
            _buttons = buttons;
            // Label reads "Insert", not "Insert →" — the glyph is the arrow now.
            ButtonStyle.Apply(_insert, "Insert", (_, _) => InsertSelected());
            ButtonStyle.Apply(_copy, "Copy", (_, _) => CopySelected());
            buttons.Controls.Add(_insert);
            buttons.Controls.Add(_copy);

            bottom.Controls.Add(buttons);
            bottom.Controls.Add(_source);

            // Fill first, then docked edges (this project's convention for docking order).
            // Added the other way round the list claimed the whole client area and the two
            // strips were drawn over it, so its last rows sat behind the button bar.
            Controls.Add(_list);
            Controls.Add(top);
            Controls.Add(bottom);

            // Every control gets a tooltip describing what it does.
            _tips.AutoPopDelay = 15000;
            _tips.SetToolTip(_filter, "Type to narrow the list — matches command text, "
                                    + "description, and subsystem name.");
            _tips.SetToolTip(_insert, "Put the selected command into the main window's "
                                    + "command box, ready to send.");
            _tips.SetToolTip(_copy, "Copy the selected command to the clipboard.");
            _tips.SetToolTip(_source, "Where these commands were taken from.");
            _tips.SetToolTip(_list, "✓ confirmed on this bench   •  also found in an independent "
                                  + "open-source driver   ⚠ the guide looks misprinted here — hover the "
                                  + "entry   (blank) from the programming guide only.");
        }

        /// <summary>
        /// Glyphs first, then size the bottom strip to fit them. Neither button has bundled
        /// artwork, so both are drawn — see <see cref="AppIcons.Drawn"/>.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Never open bigger than the screen it lands on. The size in the constructor is
            // what the list wants; a 1366x768 laptop cannot give it, and a window taller than
            // the desktop puts Insert and Copy out of reach. Before the filter box is sized,
            // so it is measured against the width this window actually ends up with.
            Rectangle work = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Math.Max(Width, MinimumSize.Width), work.Width),
                            Math.Min(Math.Max(Height, MinimumSize.Height), work.Height));

            // The designer-style position and width set in BuildUi are pre-scaling numbers,
            // and the Left|Right anchor then stretches the width again — the box ended up
            // running a few hundred pixels off the right edge of the window. Set both from
            // what the label really measures.
            //
            // The left edge matters as much as the width: "Filter:" is wider than the 52px
            // gap the designer-style number allowed, and the label is added first, so it
            // paints *over* the box and swallowed the first letter of the placeholder.
            if (_filter.Parent is Control host)
            {
                _filter.Left = _filterLabel.Right + LogicalToDeviceUnits(8);
                _filter.Width = host.ClientSize.Width - _filter.Left - LogicalToDeviceUnits(12);
            }

            ButtonStyle.SetDrawnIcon(this, _insert, "insert");
            ButtonStyle.SetDrawnIcon(this, _copy, "copy");
            int h = ButtonStyle.Normalize(this, _insert, _copy);

            if (_buttons != null)
            {
                // The gap above the buttons is DPI-scaled here rather than left at the flat
                // value the designer-style initialiser gave it.
                _buttons.Padding = new Padding(0, LogicalToDeviceUnits(10), 0, 0);
                _buttons.Height = h + _buttons.Padding.Vertical;
            }

            if (_bottom != null)
                _bottom.Height = (_buttons?.Height ?? h) + _source.Height + _bottom.Padding.Vertical;
        }

        /// <summary>
        /// Share the width between Command and Description.
        ///
        /// Command used to keep its designed 270 and Description took everything else, which
        /// was right when there was nothing else to take. At a wider window it left the
        /// syntax elided — "MEASure[:VOLTage]:DC? [{..." — beside a description column with a
        /// thousand spare pixels, in a window whose whole purpose is the syntax. Command now
        /// takes what its longest entry needs, up to a share of the width so one outlier
        /// cannot squeeze the descriptions out.
        ///
        /// The width it needs is measured from the strings with <see cref="TextRenderer"/>,
        /// once per Populate, rather than by asking the ListView to auto-size the column.
        /// Auto-sizing (Width = -1) is a message to a control whose handle does not exist yet
        /// when this first runs, from the constructor — which hung the app outright.
        /// </summary>
        private void FitColumns()
        {
            int total = _list.ClientSize.Width;
            int floor = LogicalToDeviceUnits(220);
            if (total < floor * 2) return;      // too narrow to share out; leave the design widths

            // Not Math.Clamp: it throws when the floor exceeds the cap, which is what a narrow
            // window produces. The floor wins there, and the guard above keeps that rare.
            int cap = total * 45 / 100;
            if (_commandWidth > 0)
                _list.Columns[1].Width = Math.Max(floor, Math.Min(_commandWidth, cap));

            int rest = total - _list.Columns[0].Width - _list.Columns[1].Width - 4;
            if (rest > 80) _list.Columns[2].Width = rest;
        }

        /// <summary>
        /// What the Command column would need to show every entry in full, measured from the
        /// text rather than from the control. Recomputed whenever the filter changes what is
        /// listed, so narrowing to one subsystem gives its commands the room they need.
        /// </summary>
        private void MeasureCommandColumn()
        {
            int widest = 0;
            foreach (ListViewItem item in _list.Items)
            {
                if (item.SubItems.Count < 2) continue;
                widest = Math.Max(widest,
                    TextRenderer.MeasureText(item.SubItems[1].Text, _list.Font).Width);
            }
            _commandWidth = widest == 0 ? 0 : widest + LogicalToDeviceUnits(18);
        }

        private void Populate(string filter)
        {
            filter = filter?.Trim() ?? "";
            _list.BeginUpdate();
            _list.Items.Clear();
            _list.Groups.Clear();

            var groups = new Dictionary<string, ListViewGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (CommandRef c in _reference.Commands)
            {
                if (filter.Length > 0 && !Matches(c, filter)) continue;

                if (!groups.TryGetValue(c.Category, out ListViewGroup? g))
                {
                    g = new ListViewGroup(c.Category);
                    groups[c.Category] = g;
                    _list.Groups.Add(g);
                }

                var item = new ListViewItem(MarkFor(c), g) { Tag = c };
                item.SubItems.Add(c.Syntax);
                item.SubItems.Add(c.Description);
                item.ToolTipText = string.IsNullOrEmpty(c.Example)
                    ? ProvenanceOf(c)
                    : ProvenanceOf(c) + "\r\nExample:  " + c.Example;
                _list.Items.Add(item);
            }

            _list.EndUpdate();
            MeasureCommandColumn();   // what is listed now decides how wide Command needs to be
            FitColumns();
        }

        /// <summary>
        /// How much the entry is trusted, at a glance. Every command comes from a vendor
        /// programming guide; these marks say what else backs it up.
        /// </summary>
        internal static string MarkFor(CommandRef c)
            => c.AiExtracted ? "◆"
             : c.GuideMisprint != null ? "⚠"
             : c.BenchVerified ? "✓" : c.CrossChecked ? "•" : "";

        internal static string ProvenanceOf(CommandRef c)
            // The diamond is checked first and deliberately reads as the odd one out: every
            // other mark means a human transcribed the command from a vendor guide, and this
            // one means nobody has. The warning comes next and outranks the ticks: an entry
            // can be faithfully transcribed and still be wrong, if the guide was.
            => c.AiExtracted  ? "Read out of a datasheet by AI. Not checked against a vendor "
                              + "guide or the instrument — treat it as a lead, not a fact."
             : c.GuideMisprint != null
                              ? "Transcribed exactly as the guide prints it, and the guide "
                              + "looks wrong here.\r\n" + c.GuideMisprint
             : c.BenchVerified ? "Confirmed on this bench, against the real instrument."
             : c.CrossChecked  ? "From the programming guide, and found in an independent open-source driver."
             : "From the programming guide.";

        private static bool Matches(CommandRef c, string filter)
            => c.Syntax.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || c.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || c.Category.Contains(filter, StringComparison.OrdinalIgnoreCase);

        private CommandRef? Selected()
            => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as CommandRef : null;

        private void InsertSelected()
        {
            if (Selected() is { } c) _onInsert(c.Syntax);
        }

        private void CopySelected()
        {
            if (Selected() is { } c)
            {
                try { Clipboard.SetText(c.Syntax); } catch { /* clipboard can be busy; ignore */ }
            }
        }
    }
}
