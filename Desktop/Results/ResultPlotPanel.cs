using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// The recorded results, drawn.
///
/// A swept measurement produces a table, and a table of forty rows is not a frequency
/// response — the shape is the answer and the shape is only visible as a curve. The table is
/// still there beside this, because the numbers are what gets exported; this is for seeing
/// whether the measurement worked before spending an afternoon on the CSV.
///
/// Which column goes where is the user's choice, not a guess: a sequence can record anything
/// in any order, so the axes are pickers. The one guess made is the first sensible default —
/// first column across, everything else up — because a plot that needs configuring before it
/// shows anything is a plot nobody looks at.
/// </summary>
public sealed class ResultPlotPanel : UserControl
{
    private readonly ComboBox _xColumn = new();
    private readonly CheckedListBox _yColumns = new();

    /// <summary>Unit shown on the value axis. Guessed until the user types one.</summary>
    private readonly TextBox _unit = new();
    private bool _unitIsTyped;
    private readonly CheckBox _logX = new();
    private readonly CheckBox _logY = new();
    private readonly CheckBox _markers = new();
    private readonly Canvas _canvas = new();
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };

    private IReadOnlyList<SequenceRow> _rows = Array.Empty<SequenceRow>();
    private IReadOnlyList<string> _columns = Array.Empty<string>();
    private bool _loading;

    /// <summary>
    /// Colours for successive series. Chosen to stay apart on a white ground and to survive
    /// the common colour-blindness: no red/green pair carries meaning on its own.
    /// </summary>
    private static readonly Color[] SeriesColours =
    {
        Color.FromArgb(0, 90, 200), Color.FromArgb(200, 90, 0), Color.FromArgb(0, 140, 90),
        Color.FromArgb(150, 40, 160), Color.FromArgb(180, 30, 60), Color.FromArgb(90, 90, 90),
    };

    /// <summary>The strip under the curve: the axis pickers, the switches and Screenshot.</summary>
    private FlowLayoutPanel _options = null!;

    private readonly Button _btnShot = new();

    /// <summary>Exposed so the host can size it with the rest of its buttons (SPEC §14).</summary>
    public Button ScreenshotButton => _btnShot;

    /// <summary>The column of three switches, whose height the Y list is matched to.</summary>
    private FlowLayoutPanel _scales = null!;

    // The Y list is matched to the switch column in OnHandleCreated below, so the row reads
    // as one band rather than a short box next to a tall one.

    /// <summary>
    /// Save the curve as a PNG.
    ///
    /// The canvas only, not this control: the pickers and switches are how the picture was
    /// chosen, not part of it, and nobody wants a screenshot with a combo box in it.
    /// </summary>
    private void SaveImage()
    {
        if (_canvas.Width <= 0 || _canvas.Height <= 0) return;

        using var dlg = new SaveFileDialog
        {
            Title = "Save plot",
            Filter = "PNG image (*.png)|*.png|All files (*.*)|*.*",
            FileName = "plot.png",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            using var bmp = new Bitmap(_canvas.Width, _canvas.Height);
            _canvas.DrawToBitmap(bmp, new Rectangle(0, 0, _canvas.Width, _canvas.Height));
            bmp.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the plot:\n" + ex.Message, "Save plot",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>The glyph needs a handle for its DPI, so it is set once there is one.</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ButtonStyle.SetDrawnIcon(this, _btnShot, "camera");

        // Three rows before it scrolls. Sized from the list's own item height rather than
        // from the switch column beside it: they were matched to each other, so tightening
        // the switches used to shrink the list, and three columns is the common case — the
        // console records exactly three, and a sweep usually records two or three.
        int row = _yColumns.ItemHeight > 0 ? _yColumns.ItemHeight : Font.Height;
        _yColumns.Height = row * 3 + LogicalToDeviceUnits(6);
    }

    public ResultPlotPanel()
    {
        BuildUi();
    }

    private void BuildUi()
    {
        // Under the curve, not over it. The curve is what the tab is for; the pickers and
        // switches are what you reach for after looking at it, and above they pushed it down
        // the pane every time they wrapped to another line.
        var controls = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(6, 4, 6, 6),
        };

        // The X picker and the unit, stacked one above the other. A table rather than two
        // rows in a flow panel, so the box lines up under the combo despite the labels beside
        // them being different widths.
        //
        // The unit sat at the far end of the strip before, as far from the plot's axes as the
        // layout could put it. Here it also fills space the three-item Y list was leaving
        // empty, so the stack costs the curve no height at all.
        // The strip reads as three columns — X, Y, and the switches — so they are spaced
        // like columns. At the 12px the pickers used between themselves, the gap between one
        // group and the next was the same as the gap inside a group, and the whole row read
        // as one undifferentiated line of controls.
        const int betweenColumns = 30;

        var xStack = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, 0, betweenColumns, 0),
        };

        xStack.Controls.Add(new Label
        {
            Text = "X:", AutoSize = true, Margin = new Padding(0, 7, 4, 0),
        }, 0, 0);

        _xColumn.DropDownStyle = ComboBoxStyle.DropDownList;
        // Narrower than they were: the results buttons now share this line, and at the width
        // a results pane usually gets, the old sizes pushed them onto a row of their own.
        _xColumn.Width = 120;
        _xColumn.Margin = new Padding(0, 3, 0, 0);
        _xColumn.SelectedIndexChanged += (_, _) => { if (!_loading) Refresh(keepChoices: true); };
        xStack.Controls.Add(_xColumn, 1, 0);

        // The unit of what is being plotted. Guessed from the recorded commands or from the
        // column heading, and typeable, because the guess is only ever a guess: an instrument
        // asked for volts can be wired across a shunt and reading amps, and the person at the
        // bench knows which. Anything typed wins and stays; empty the box and the guess
        // returns.
        //
        // It labels the value axis — "V" is what makes those ticks read 8 mV — and is named
        // for where it sits rather than for that, which is how it was asked for.
        xStack.Controls.Add(new Label
        {
            Text = "X Unit:", AutoSize = true, Margin = new Padding(0, 9, 4, 0),
        }, 0, 1);

        // As wide as the combo above it, so the two read as one column rather than a box
        // that happens to sit under a wider one.
        _unit.Width = _xColumn.Width;
        _unit.Margin = new Padding(0, 5, 0, 0);
        _unit.Anchor = AnchorStyles.Left;
        _unit.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _unitIsTyped = _unit.Text.Trim().Length > 0;
            _canvas.SetUnit(_unit.Text.Trim());
        };
        xStack.Controls.Add(_unit, 1, 1);

        controls.Controls.Add(xStack);

        controls.Controls.Add(new Label
        {
            Text = "Y:", AutoSize = true, Margin = new Padding(0, 7, 4, 0),
        });

        // A checked list rather than a second combo: a sweep that records two readings wants
        // both curves on one pair of axes, which is the comparison the measurement was for.
        _yColumns.CheckOnClick = true;
        _yColumns.Width = 150;
        _yColumns.Height = 62;
        _yColumns.IntegralHeight = false;
        _yColumns.Margin = new Padding(0, 3, betweenColumns, 0);
        _yColumns.ItemCheck += (_, _) => BeginInvoke(() => { if (!_loading) Refresh(keepChoices: true); });
        controls.Controls.Add(_yColumns);

        var scales = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0, 0, 16, 0),
        };

        // Tight vertical margins: three checkboxes at their default spacing make a taller
        // column than the pickers beside them, and that column sets the height of the whole
        // strip, which comes off the curve.
        var tight = new Padding(0, 0, 0, 0);

        _logX.Text = "Log X";
        _logX.AutoSize = true;
        _logX.Margin = tight;
        _logX.CheckedChanged += (_, _) => { if (!_loading) Refresh(keepChoices: true); };
        scales.Controls.Add(_logX);

        _logY.Text = "Log Y";
        _logY.AutoSize = true;
        _logY.Margin = tight;
        _logY.CheckedChanged += (_, _) => { if (!_loading) Refresh(keepChoices: true); };
        scales.Controls.Add(_logY);

        _markers.Text = "Points";
        _markers.AutoSize = true;
        _markers.Margin = tight;
        _markers.Checked = true;
        _markers.CheckedChanged += (_, _) => _canvas.SetMarkers(_markers.Checked);
        scales.Controls.Add(_markers);

        controls.Controls.Add(scales);

        // The curve is worth keeping on its own — into a report, or beside the next run's.
        // Save CSV is the numbers; this is the picture. Camera glyph, the same one Capture
        // Screen carries, because it is the same gesture aimed at our plot instead of the
        // instrument's display.
        //
        // The glyph alone: the strip is short of width, the camera says it on its own, and the
        // tooltip carries the rest for anyone who wants it.
        ButtonStyle.Apply(_btnShot, "", (_, _) => SaveImage());
        // Room on every side: pressed against the edge of the strip it read as something the
        // layout had run out of space for.
        _btnShot.Margin = new Padding(14, 8, 10, 8);
        // Top as well as Right: the strip is as tall as the stacked switches beside it, and
        // centred in that the button floated halfway down with nothing to line up against.
        _btnShot.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        _options = controls;
        _scales = scales;

        // The pickers take whatever width they need and Screenshot sits against the far edge,
        // which a FlowLayoutPanel cannot express — it has no notion of alignment, only of
        // order. Two columns do: the flow fills the first, the button anchors right in the
        // second.
        var strip = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
        };
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        controls.Dock = DockStyle.Fill;
        strip.Controls.Add(controls, 0, 0);
        strip.Controls.Add(_btnShot, 1, 0);

        _canvas.Dock = DockStyle.Fill;

        Controls.Add(_canvas);
        Controls.Add(strip);

        SetTooltips();
    }

    private void SetTooltips()
    {
        _tips.SetToolTip(_xColumn, "Which recorded column runs across the bottom — usually "
                                 + "the value the sweep stepped.");
        _tips.SetToolTip(_yColumns, "Which columns to draw as curves. Tick more than one to "
                                  + "compare them on the same axes.");
        _tips.SetToolTip(_logX, "Log spacing across. A sweep written with POINTS … LOG wants "
                              + "this, or the low decades are squeezed into nothing.\r\n\r\n"
                              + "Disabled when any value is zero or negative — a log axis "
                              + "through zero has no meaning.");
        _tips.SetToolTip(_logY, "Log spacing up the side, for a reading that spans decades.");
        _tips.SetToolTip(_markers, "Mark each recorded point, so a sparse sweep is not mistaken "
                                 + "for a smooth curve.");
    }

    /// <summary>
    /// Show these results. Called as rows arrive, so it has to be cheap and must not disturb
    /// the choices the user has already made.
    /// </summary>
    public void Show(IReadOnlyList<string> columns, IReadOnlyList<SequenceRow> rows)
    {
        bool columnsChanged = !columns.SequenceEqual(_columns, StringComparer.Ordinal);
        _columns = columns;
        _rows = rows;

        if (columnsChanged) FillPickers();
        Refresh(keepChoices: !columnsChanged);
    }

    /// <summary>
    /// Rebuild the pickers, and choose the first sensible plot: the first column across,
    /// everything else up that has anything to draw. For a swept measurement that is exactly
    /// right, and it is the difference between a plot that appears and a plot that has to be
    /// assembled.
    ///
    /// <para>
    /// "That has anything to draw" earns its keep on a console's table, whose Command column
    /// holds <c>C1:BSWV?</c> on every row. Ticked by default it added a legend entry and a
    /// colour to a series with no points in it.
    /// </para>
    /// </summary>
    private void FillPickers()
    {
        _loading = true;
        try
        {
            _xColumn.Items.Clear();
            _yColumns.Items.Clear();

            foreach (string c in _columns)
            {
                _xColumn.Items.Add(c);
                _yColumns.Items.Add(c);
            }

            if (_xColumn.Items.Count > 0) _xColumn.SelectedIndex = 0;

            // Columns are known before the first row is, and a column with nothing in it yet
            // is not a text column — it is an empty one. Ticking everything is the better
            // guess there, and the first row arrives soon enough to be judged on.
            bool judge = _rows.Count > 0;
            for (int i = 1; i < _yColumns.Items.Count; i++)
                if (!judge || ColumnHasValues(i))
                    _yColumns.SetItemChecked(i, true);
        }
        finally { _loading = false; }
    }

    /// <summary>Whether any row has something in this column that reads as a number.</summary>
    private bool ColumnHasValues(int column)
        => ResultPlot.HasPlottableValues(
            _rows.Where(r => column < r.Values.Count).Select(r => (string?)r.Values[column]));

    /// <summary>
    /// The values of a column named "Command", if the table has one — what a console records
    /// alongside each reading, and the best clue to what the reading is of.
    /// </summary>
    private IEnumerable<string>? CommandColumnValues()
    {
        int column = -1;
        for (int i = 0; i < _columns.Count; i++)
            if (string.Equals(_columns[i], "Command", StringComparison.OrdinalIgnoreCase))
            {
                column = i;
                break;
            }

        if (column < 0) return null;

        var values = new List<string>();
        foreach (SequenceRow row in _rows)
            if (column < row.Values.Count) values.Add(row.Values[column]);
        return values;
    }

    /// <summary>
    /// Whether every value in a column that has one is a clock. All of them, not most: one
    /// plain number among the timestamps means the column is something else, and labelling
    /// it as a time would be a confident lie about what the axis shows.
    /// </summary>
    private bool ColumnIsClock(int column)
    {
        if (column < 0 || _rows.Count == 0) return false;

        bool any = false;
        foreach (SequenceRow row in _rows)
        {
            if (column >= row.Values.Count) continue;
            string v = row.Values[column];
            if (string.IsNullOrWhiteSpace(v)) continue;
            if (!ResultPlot.TryParseClock(v, out _)) return false;
            any = true;
        }
        return any;
    }

    private void Refresh(bool keepChoices)
    {
        _ = keepChoices;

        int x = _xColumn.SelectedIndex;
        var y = _yColumns.CheckedIndices.Cast<int>().ToList();

        IReadOnlyList<PlotSeries> series = ResultPlot.Build(_rows, _columns, x, y);

        // A log axis is offered only when it is meaningful. Left enabled, a single zero
        // reading would silently flatten the whole curve onto the left edge.
        bool xLoggable = series.Count > 0 && ResultPlot.CanBeLogarithmic(series.SelectMany(s => s.Points.Select(p => p.X)));
        bool yLoggable = series.Count > 0 && ResultPlot.CanBeLogarithmic(series.SelectMany(s => s.Points.Select(p => p.Y)));

        _loading = true;
        try
        {
            _logX.Enabled = xLoggable;
            _logY.Enabled = yLoggable;
            if (!xLoggable) _logX.Checked = false;
            if (!yLoggable) _logY.Checked = false;
        }
        finally { _loading = false; }

        // Label the X axis as a clock when that is what the column holds. Decided from the
        // raw strings rather than the parsed numbers, because by then a timestamp and a plain
        // count of seconds look identical.
        _canvas.SetXIsClock(ColumnIsClock(x));

        // Offer a unit for whatever is on the value axis, unless the user has typed one.
        if (!_unitIsTyped)
        {
            string? guess = MeasurementUnit.Guess(
                y.Count > 0 && y[0] < _columns.Count ? _columns[y[0]] : null,
                CommandColumnValues());

            _loading = true;
            try { _unit.Text = guess ?? ""; }
            finally { _loading = false; }

            _canvas.SetUnit(_unit.Text);
        }

        _canvas.Set(series,
                    ResultPlot.Axis(series.SelectMany(s => s.Points.Select(p => p.X)), _logX.Checked),
                    ResultPlot.Axis(series.SelectMany(s => s.Points.Select(p => p.Y)), _logY.Checked),
                    x >= 0 && x < _columns.Count ? _columns[x] : "");
    }

    /// <summary>The drawing itself. Separate so the pickers above it can stay simple.</summary>
    private sealed class Canvas : Panel
    {
        private IReadOnlyList<PlotSeries> _series = Array.Empty<PlotSeries>();
        private PlotAxis _x = new(0, 0, false, Array.Empty<double>());
        private PlotAxis _y = new(0, 0, false, Array.Empty<double>());
        private string _xName = "";

        private bool _markersOn = true;

        /// <summary>A method, not a property: the WinForms analyzer refuses a property on
        /// a control that it cannot write into a .Designer.cs, and this control is only
        /// ever built in code.</summary>
        public void SetMarkers(bool on) { _markersOn = on; Invalidate(); }

        public Canvas()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.White;
        }

        /// <summary>Whether the X values are seconds-of-day, so ticks read as clock times.</summary>
        private bool _xIsClock;

        /// <summary>Unit suffixed to the value-axis ticks: "950 µV" rather than "950 µ".</summary>
        private string _unit = "";

        public void SetXIsClock(bool clock) { _xIsClock = clock; }

        public void SetUnit(string unit)
        {
            unit = unit?.Trim() ?? "";
            if (_unit == unit) return;
            _unit = unit;
            Invalidate();
        }

        public void Set(IReadOnlyList<PlotSeries> series, PlotAxis x, PlotAxis y, string xName)
        {
            _series = series;
            _x = x;
            _y = y;
            _xName = xName;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using var font = new Font("Segoe UI", 8f);
            using var text = new SolidBrush(Color.FromArgb(70, 70, 70));

            int left = LogicalToDeviceUnits(66), right = LogicalToDeviceUnits(14);
            int top = LogicalToDeviceUnits(12), bottom = LogicalToDeviceUnits(34);
            int pw = ClientSize.Width - left - right;
            int ph = ClientSize.Height - top - bottom;

            if (pw < 40 || ph < 40) return;

            if (_series.Count == 0 || _x.IsEmpty || _y.IsEmpty)
            {
                string note = "Nothing recorded yet. A script builds this with RECORD.";
                SizeF size = g.MeasureString(note, font);
                g.DrawString(note, font, text,
                             (ClientSize.Width - size.Width) / 2,
                             (ClientSize.Height - size.Height) / 2);
                return;
            }

            var plot = new Rectangle(left, top, pw, ph);

            using var grid = new Pen(Color.FromArgb(228, 228, 228));
            using var frame = new Pen(Color.FromArgb(150, 150, 150));
            using var format = new StringFormat { Alignment = StringAlignment.Far };

            // How far apart the ticks are is what decides how many decimals their labels need.
            // A log axis steps by decades, so its own rounding is already distinguishing.
            double yStep = TickStep(_y), xStep = TickStep(_x);

            foreach (double t in _y.Ticks)
            {
                double f = ResultPlot.Fraction(_y, t);
                if (f is < 0 or > 1) continue;
                int py = top + (int)((1 - f) * ph);
                g.DrawLine(grid, left, py, left + pw, py);
                g.DrawString(ResultPlot.Format(t, yStep) + _unit, font, text,
                             new RectangleF(0, py - font.Height / 2f, left - 6, font.Height), format);
            }

            foreach (double t in _x.Ticks)
            {
                double f = ResultPlot.Fraction(_x, t);
                if (f is < 0 or > 1) continue;
                int px = left + (int)(f * pw);
                g.DrawLine(grid, px, top, px, top + ph);

                string label = _xIsClock ? ResultPlot.FormatClock(t, xStep)
                                         : ResultPlot.Format(t, xStep);
                SizeF size = g.MeasureString(label, font);
                g.DrawString(label, font, text, px - size.Width / 2, top + ph + 4);
            }

            g.DrawRectangle(frame, plot);

            if (_xName.Length > 0)
            {
                SizeF size = g.MeasureString(_xName, font);
                g.DrawString(_xName, font, text,
                             left + (pw - size.Width) / 2, top + ph + LogicalToDeviceUnits(17));
            }

            g.SetClip(plot);
            for (int s = 0; s < _series.Count; s++) DrawSeries(g, _series[s], s, plot);
            g.ResetClip();

            if (_series.Count > 1) DrawLegend(g, font, plot);
        }

        /// <summary>The gap between adjacent ticks, or 0 when that is not a useful idea —
        /// fewer than two ticks, or a log axis, whose gaps are decades apart.</summary>
        private static double TickStep(PlotAxis axis)
        {
            if (axis.Logarithmic || axis.Ticks.Count < 2) return 0;
            return Math.Abs(axis.Ticks[1] - axis.Ticks[0]);
        }

        private void DrawSeries(Graphics g, PlotSeries series, int index, Rectangle plot)
        {
            Color colour = SeriesColours[index % SeriesColours.Length];

            // Sorted by X so a sweep that recorded out of order still draws as a curve rather
            // than as a scribble back and forth across the plot.
            PlotPoint[] ordered = series.Points.OrderBy(p => p.X).ToArray();

            var pts = new PointF[ordered.Length];
            for (int i = 0; i < ordered.Length; i++)
                pts[i] = new PointF(
                    plot.Left + (float)(ResultPlot.Fraction(_x, ordered[i].X) * plot.Width),
                    plot.Top + (float)((1 - ResultPlot.Fraction(_y, ordered[i].Y)) * plot.Height));

            using var pen = new Pen(colour, 1.6f);
            if (pts.Length > 1) g.DrawLines(pen, pts);

            if (!_markersOn) return;

            // Markers only while they are still distinguishable; past that they merge into a
            // thick line and say nothing.
            if (pts.Length > 400) return;

            using var fill = new SolidBrush(colour);
            float r = pts.Length > 120 ? 1.5f : 2.5f;
            foreach (PointF p in pts) g.FillEllipse(fill, p.X - r, p.Y - r, r * 2, r * 2);
        }

        private void DrawLegend(Graphics g, Font font, Rectangle plot)
        {
            int pad = LogicalToDeviceUnits(6);
            int lineHeight = font.Height + 2;
            int width = _series.Max(s => (int)g.MeasureString(s.Name, font).Width)
                      + LogicalToDeviceUnits(24);

            var box = new Rectangle(plot.Right - width - pad, plot.Top + pad,
                                    width, _series.Count * lineHeight + pad);

            using var back = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
            using var edge = new Pen(Color.FromArgb(200, 200, 200));
            g.FillRectangle(back, box);
            g.DrawRectangle(edge, box);

            using var label = new SolidBrush(Color.FromArgb(50, 50, 50));
            for (int i = 0; i < _series.Count; i++)
            {
                int y = box.Top + pad / 2 + i * lineHeight;
                using var swatch = new SolidBrush(SeriesColours[i % SeriesColours.Length]);
                g.FillRectangle(swatch, box.Left + 5, y + font.Height / 2 - 1,
                                LogicalToDeviceUnits(12), 3);
                g.DrawString(_series[i].Name, font, label, box.Left + LogicalToDeviceUnits(20), y);
            }
        }
    }

}
