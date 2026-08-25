using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LabEquipmentController
{
    /// <summary>
    /// A live meter readout: polls one measurement on a timer and plots it against time,
    /// with the current value shown large enough to read from across the bench.
    ///
    /// A multimeter answers one reading per query, so the only way to see a trend — a
    /// drifting supply, a warming thermistor, a settling reference — is to ask repeatedly.
    /// The console can do that with a REPEAT script, but the answers scroll past as text;
    /// this plots them.
    ///
    /// Polling holds the instrument's link for as long as it runs, so it marks the session
    /// busy exactly as a running script does, and the console locks itself out meanwhile.
    /// </summary>
    public sealed class MultimeterReadoutForm : Form
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
        private readonly InstrumentSession _session;
        private readonly ReadingSeries _series = new();

        private readonly ComboBox _function = new();
        private readonly ComboBox _scale = new();
        private readonly NumericUpDown _interval = new();
        private readonly Button _btnStart = new();
        private readonly Button _btnClear = new();
        private readonly Button _btnSave = new();
        private readonly Label _value = new();
        private readonly Label _stats = new();
        private readonly TrendPanel _plot;

        // The toolbar IS the flow panel, auto-sized: a fixed-height Panel wrapping it hid
        // whichever buttons wrapped onto a second line.
        private readonly FlowLayoutPanel _toolbar = new();

        // Clear and Save CSV live along the bottom, away from the controls that decide what
        // the meter does. Up in the toolbar they sat one gap away from Start, which is the
        // button you reach for while watching the number — and Clear throws the run away.
        private readonly FlowLayoutPanel _actions = new();
        private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };

        private CancellationTokenSource? _pollCts;
        private ReadoutFunction _selected;

        /// <summary>
        /// One SI prefix the readout can be shown in, or the two special cases.
        ///
        /// A meter answers in whatever magnitude the quantity happens to be, so a
        /// capacitance range reads "-1.25025477E-12 F". That is the right number and an
        /// unreadable one: nobody works in units of 10⁻¹² farad, they work in picofarads.
        /// </summary>
        private readonly record struct UnitScale(string Label, string Prefix, int Exponent)
        {
            /// <summary>Pick the prefix for each reading, so the mantissa stays in 1..1000.</summary>
            public bool IsAuto => Exponent == int.MinValue;

            /// <summary>Print the instrument's own number, unscaled and unrounded.</summary>
            public bool IsRaw => Exponent == int.MaxValue;
        }

        private static readonly UnitScale[] Scales =
        {
            new("Auto",      "",  int.MinValue),
            new("pico (p)",  "p", -12),
            new("nano (n)",  "n", -9),
            new("micro (µ)", "µ", -6),
            new("milli (m)", "m", -3),
            new("unit",      "",  0),
            new("kilo (k)",  "k", 3),
            new("mega (M)",  "M", 6),
            new("giga (G)",  "G", 9),
            new("Raw",       "",  int.MaxValue),
        };

        private UnitScale _unit = Scales[0];

        /// <summary>Raised when polling starts (true) and stops (false), so the console can
        /// keep off the link while it runs.</summary>
        public event Action<bool>? PollingStateChanged;

        public MultimeterReadoutForm(InstrumentSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _selected = session.Profile.ReadoutFunctions[0];

            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9f);
            Text = "Readout — " + session.Title;
            // Wide enough that the toolbar stays on one row once its controls carry glyphs
            // and the app-wide button metrics. Below the minimum size it still wraps — that
            // part is deliberate.
            ClientSize = new Size(980, 520);
            MinimumSize = new Size(560, 380);
            StartPosition = FormStartPosition.CenterParent;

            _plot = new TrendPanel(_series) { Dock = DockStyle.Fill, BackColor = Color.FromArgb(16, 16, 16) };

            BuildToolbar();
            BuildReadout();

            // Fill first, then docked edges (this project's convention for docking order).
            // Among the two bottom-docked strips the later one lands outermost, so the
            // buttons sit below the statistics line rather than above it.
            Controls.Add(_plot);
            Controls.Add(_stats);
            Controls.Add(_actions);
            Controls.Add(_value);
            Controls.Add(_toolbar);

            SetTooltips();
            FormClosing += (_, e) => StopPolling();
        }

        private void BuildToolbar()
        {
            _toolbar.Dock = DockStyle.Top;
            _toolbar.Padding = new Padding(10, 8, 10, 8);
            _toolbar.AutoSize = true;
            _toolbar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _toolbar.WrapContents = true;
            FlowLayoutPanel row = _toolbar;

            var lblFn = new Label { Text = "Measure:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
            // Owner-drawn only so its closed height can follow ItemHeight — a DropDownList
            // combo is otherwise font-height-locked and cannot be grown to the row height.
            // Same trick as the script editor's Examples combo.
            _function.DropDownStyle = ComboBoxStyle.DropDownList;
            _function.DrawMode = DrawMode.OwnerDrawFixed;
            _function.DrawItem += Function_DrawItem;
            _function.Width = 170;
            _function.Margin = new Padding(0, 2, 14, 0);
            foreach (ReadoutFunction fn in _session.Profile.ReadoutFunctions) _function.Items.Add(fn.Label);
            _function.SelectedIndex = 0;
            _function.SelectedIndexChanged += OnFunctionChanged;

            var lblUnit = new Label { Text = "shown in", AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
            _scale.DropDownStyle = ComboBoxStyle.DropDownList;
            _scale.DrawMode = DrawMode.OwnerDrawFixed;
            _scale.DrawItem += (_, e) => ButtonStyle.DrawComboItem(_scale, e);
            _scale.Width = 110;
            _scale.Margin = new Padding(0, 2, 14, 0);
            foreach (UnitScale s in Scales) _scale.Items.Add(s.Label);
            _scale.SelectedIndex = 0;
            _scale.SelectedIndexChanged += OnScaleChanged;

            var lblEvery = new Label { Text = "every", AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
            _interval.Minimum = 100;
            _interval.Maximum = 60000;
            _interval.Increment = 100;
            _interval.Value = 1000;
            _interval.TextAlign = HorizontalAlignment.Right;
            _interval.Width = 80;
            _interval.Margin = new Padding(0, 2, 4, 0);
            // A wider gap than the ones between the pickers: everything to the left of here
            // says what to measure, and Start is what acts on it. Butted up against "ms" the
            // button read as part of the interval field.
            var lblMs = new Label { Text = "ms", AutoSize = true, Margin = new Padding(0, 6, 26, 0) };

            Button Tool(Button b, string text, EventHandler onClick)
            {
                ButtonStyle.Apply(b, text, onClick);
                return b;
            }

            row.Controls.Add(lblFn);
            row.Controls.Add(_function);
            row.Controls.Add(lblUnit);
            row.Controls.Add(_scale);
            row.Controls.Add(lblEvery);
            row.Controls.Add(_interval);
            row.Controls.Add(lblMs);
            row.Controls.Add(Tool(_btnStart, "Start", (_, _) => Toggle()));

            // Along the bottom, right-aligned, as everywhere else in the app that has a pair
            // of buttons acting on what the window is showing. RightToLeft flow, so Save CSV…
            // is added first to end up outermost.
            _actions.Dock = DockStyle.Bottom;
            _actions.FlowDirection = FlowDirection.RightToLeft;
            _actions.WrapContents = false;
            _actions.Padding = new Padding(10, 6, 10, 8);
            _actions.AutoSize = true;
            _actions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _actions.Controls.Add(Tool(_btnSave, "Save CSV…", (_, _) => SaveCsv()));
            _actions.Controls.Add(Tool(_btnClear, "Clear", (_, _) => { _series.Clear(); Refreshed(); }));
        }

        private void BuildReadout()
        {
            // The number, big. This window's whole point is being readable at a distance.
            // AutoSize off on both labels: a docked auto-sizing Label ignores the Height set
            // in OnLoad, which left this one collapsed and the statistics line clipped
            // mid-word.
            _value.Dock = DockStyle.Top;
            _value.AutoSize = false;
            _value.TextAlign = ContentAlignment.MiddleCenter;
            _value.Font = new Font("Consolas", 26f, FontStyle.Bold);
            _value.ForeColor = Color.FromArgb(0, 122, 90);
            _value.Text = "—";

            _stats.Dock = DockStyle.Bottom;
            _stats.AutoSize = false;
            _stats.TextAlign = ContentAlignment.MiddleLeft;
            _stats.Padding = new Padding(10, 0, 10, 0);
            _stats.Text = "No readings yet.";
        }

        private void SetTooltips()
        {
            // Every control gets a tooltip describing what it does.
            _tips.SetToolTip(_function, "Which measurement to poll. Each one sets the meter's "
                                      + "function and takes a reading, so changing it here changes "
                                      + "what the meter is measuring.");
            _tips.SetToolTip(_scale, "What magnitude to show the reading in. Auto picks the SI "
                                   + "prefix that keeps the number readable — a capacitance of "
                                   + "-1.25E-12 F reads as -1.25 pF. Pick a prefix to hold it "
                                   + "there regardless, or Raw to see exactly what the "
                                   + "instrument sent. The saved CSV is always raw.");
            _tips.SetToolTip(_interval, "How long to wait between readings, in milliseconds. The "
                                      + "meter's own conversion time sets the practical floor — "
                                      + "asking faster than it can answer just queues up.");
            _tips.SetToolTip(_btnStart, "Start or stop polling. While it runs, this instrument's "
                                      + "console is locked out, since two conversations on one "
                                      + "connection would collide.");
            _tips.SetToolTip(_btnClear, "Discard the readings collected so far and start the plot again.");
            _tips.SetToolTip(_btnSave, "Save the collected readings as a CSV file of time and value pairs.");
            _tips.SetToolTip(_value, "The most recent reading.");
            _tips.SetToolTip(_stats, "Reading count, and the smallest, largest and mean value on the plot.");
            _tips.SetToolTip(_plot, "Readings plotted against time since polling started. The "
                                  + "vertical scale follows the values seen so far.");
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            SetButtonIcons();          // after the handle exists, so the DPI is known
            NormalizeToolbarHeights(); // then pin the row to one height

            // Size the labels to the text they will hold, at this DPI, never a fixed number.
            // (The toolbar sizes itself — it is an auto-sizing FlowLayoutPanel.)
            _value.Height = TextRenderer.MeasureText("0.000000 V", _value.Font).Height
                          + LogicalToDeviceUnits(10);
            _stats.Height = TextRenderer.MeasureText("0", _stats.Font).Height
                          + LogicalToDeviceUnits(10);
        }

        private void SetButtonIcons()
        {
            UpdateStartIcon();
            ButtonStyle.SetIcon(this, _btnClear, "reset");
            ButtonStyle.SetDrawnIcon(this, _btnSave, "save");
        }

        /// <summary>
        /// The Start button is a toggle, so its glyph follows its label — the same play and
        /// stop pair the script window puts on its Run and Stop buttons. Called from both
        /// <see cref="SetButtonIcons"/> and <see cref="SetRunning"/>.
        /// </summary>
        private void UpdateStartIcon()
            => ButtonStyle.SetIcon(this, _btnStart, IsPolling ? "stopClock" : "startClock");

        /// <summary>
        /// Pin the toolbar to one height (SPEC §14). A glyph makes a button taller than the
        /// combo box and the interval spinner beside it, so without this the three buttons
        /// visibly stand above the rest of the row.
        /// </summary>
        private void NormalizeToolbarHeights()
        {
            int h = ButtonStyle.Normalize(this, _btnStart, _btnClear, _btnSave);

            // Start swaps its label and glyph as it toggles, so pin it to the wider of the
            // two states — otherwise the buttons beside it shuffle sideways on every press.
            // Both glyphs are the same size, so only the label moves the width.
            string was = _btnStart.Text;
            int startW = 0;
            foreach (string s in new[] { "Start", "Stop" })
            {
                _btnStart.Text = s;
                startW = Math.Max(startW, _btnStart.PreferredSize.Width);
            }
            _btnStart.Text = was;
            _btnStart.MinimumSize = new Size(Math.Max(startW, _btnStart.MinimumSize.Width), h);

            // The function picker grows to the button height, so the row reads as one strip.
            // The interval spinner cannot be grown at all, so it — and the three labels — are
            // centred on the row instead of hanging from the top of it.
            ButtonStyle.MatchHeight(_function, h);
            ButtonStyle.MatchHeight(_scale, h);
            foreach (Control c in _toolbar.Controls)
                if (c is not Button and not ComboBox) ButtonStyle.CentreInRow(c, h);
        }

        private void Function_DrawItem(object? sender, DrawItemEventArgs e)
            => ButtonStyle.DrawComboItem(_function, e);

        // ------------------------------------------------------------------ polling

        private bool IsPolling => _pollCts != null;

        private void Toggle()
        {
            if (IsPolling) StopPolling();
            else _ = StartPollingAsync();
        }

        /// <summary>Where a function sits in the profile's list, or -1 if it is not in it.</summary>
        private int IndexOf(ReadoutFunction fn)
        {
            IReadOnlyList<ReadoutFunction> all = _session.Profile.ReadoutFunctions;
            for (int i = 0; i < all.Count; i++)
                if (all[i] == fn) return i;
            return -1;
        }

        private void OnFunctionChanged(object? sender, EventArgs e)
        {
            // SelectedIndex is -1 whenever the combo has no selection, and ReadoutFunctions
            // is a plain array — so indexing it directly threw "Index was outside the bounds
            // of the array". A ComboBox drops its selection when its handle is recreated,
            // which WinForms does for reasons this window does not control: moving between
            // monitors of different DPI is the easy one to hit.
            //
            // Put the selection back rather than just returning, or the box shows blank and
            // the next change compares against a function the user can no longer see.
            int index = _function.SelectedIndex;
            if (index < 0 || index >= _session.Profile.ReadoutFunctions.Count)
            {
                int restore = IndexOf(_selected);
                if (restore >= 0 && restore != _function.SelectedIndex)
                    _function.SelectedIndex = restore;
                return;
            }

            ReadoutFunction fn = _session.Profile.ReadoutFunctions[index];
            if (fn == _selected) return;

            // A different quantity on the same axes would be meaningless — start again.
            _selected = fn;
            _series.Clear();
            Refreshed();
        }

        private async Task StartPollingAsync()
        {
            if (IsPolling) return;
            if (!_session.IsConnected)
            {
                MessageBox.Show(this, "This instrument isn't connected.", "Readout",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _pollCts = new CancellationTokenSource();
            SetRunning(true);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            CancellationToken ct = _pollCts.Token;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    string reply = await _session.Client.QueryAsync(_selected.Query, ct);

                    if (ReadingSeries.TryParseReading(reply, out double v))
                    {
                        _series.Add(clock.Elapsed.TotalSeconds, v);
                        Refreshed();
                    }
                    else
                    {
                        _stats.Text = "Could not read a number from: " + reply.Trim();
                    }

                    // Interval is read each time round, so changing it takes effect at once.
                    await Task.Delay((int)_interval.Value, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Stop button — nothing to report.
            }
            catch (Exception ex)
            {
                // Name the type as well as the message. "Index was outside the bounds of the
                // array" says nothing about where it came from, and this line is the only
                // report the user gets — the polling loop has nowhere else to put it.
                _stats.Text = $"Polling stopped — {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                _pollCts?.Dispose();
                _pollCts = null;
                SetRunning(false);
            }
        }

        private void StopPolling() => _pollCts?.Cancel();

        private void SetRunning(bool running)
        {
            _btnStart.Text = running ? "Stop" : "Start";
            UpdateStartIcon();
            _function.Enabled = !running;   // switching mid-run would mix two quantities
            PollingStateChanged?.Invoke(running);
        }

        private void OnScaleChanged(object? sender, EventArgs e)
        {
            int i = _scale.SelectedIndex;
            if (i < 0 || i >= Scales.Length) return;   // no selection — see OnFunctionChanged
            _unit = Scales[i];
            Refreshed();                               // restate what is already on screen
        }

        /// <summary>Repaint the number, the statistics line and the plot.</summary>
        private void Refreshed()
        {
            double? latest = _series.Latest;

            // One scale for the whole window, chosen from the latest reading rather than
            // per-number: min, max and mean in three different prefixes would be unreadable,
            // and the big number changing prefix as it drifts across a decade is worse.
            int exp = ExponentFor(latest ?? 0);

            // The plot's axis labels take the same scale, so the trace is read in the units
            // the number above it is written in.
            _plot.FormatValue = value => Format(value, exp) + " " + UnitLabel(exp);

            _value.Text = latest is double v ? Format(v, exp) + " " + UnitLabel(exp) : "—";

            if (_series.IsEmpty)
            {
                _stats.Text = "No readings yet.";
            }
            else
            {
                (double min, double max, double mean) = _series.Statistics();
                string capped = _series.TotalTaken > _series.Count
                    ? $"  (showing the last {_series.Count} of {_series.TotalTaken})"
                    : "";
                _stats.Text = $"{_series.Count} readings    min {Format(min, exp)}    "
                            + $"max {Format(max, exp)}    mean {Format(mean, exp)}    "
                            + $"{UnitLabel(exp)}{capped}";
            }

            _plot.Invalidate();
        }

        /// <summary>
        /// The power of ten the readout is divided by, given the current selection.
        ///
        /// Auto walks to the prefix that leaves the mantissa in 1..1000, which is what
        /// engineering notation means and what turns "-1.25025477E-12 F" into "-1.25 pF".
        /// Zero has no magnitude to read, so it keeps the plain unit.
        /// </summary>
        private int ExponentFor(double value)
        {
            if (_unit.IsRaw) return int.MaxValue;
            if (!_unit.IsAuto) return _unit.Exponent;

            double abs = Math.Abs(value);
            if (abs == 0 || double.IsNaN(abs) || double.IsInfinity(abs)) return 0;

            int exp = (int)Math.Floor(Math.Log10(abs) / 3) * 3;
            return Math.Clamp(exp, -12, 9);
        }

        private string Format(double v, int exponent) => exponent == int.MaxValue
            ? v.ToString("g6", CultureInfo.InvariantCulture)                  // raw, as sent
            : (v / Math.Pow(10, exponent)).ToString("g6", CultureInfo.InvariantCulture);

        /// <summary>The unit with its prefix — "pF", "mV", "kΩ" — or the bare unit.</summary>
        private string UnitLabel(int exponent)
        {
            if (exponent == int.MaxValue || exponent == 0) return _selected.Unit;
            foreach (UnitScale s in Scales)
                if (!s.IsAuto && !s.IsRaw && s.Exponent == exponent) return s.Prefix + _selected.Unit;
            return _selected.Unit;
        }

        private void SaveCsv()
        {
            if (_series.IsEmpty)
            {
                MessageBox.Show(this, "There are no readings to save yet.", "Save CSV",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dlg = new SaveFileDialog
            {
                Title = "Save readings",
                Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = "readings.csv",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.WriteAllText(dlg.FileName, _series.ToCsv($"{_selected.Label} ({_selected.Unit})"));
                _stats.Text = "Saved " + Path.GetFileName(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the readings:\n" + ex.Message, "Save CSV",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // --------------------------------------------------------------------- plot

        /// <summary>Draws the readings against time, autoscaling to what has been seen.</summary>
        private sealed class TrendPanel : Panel
        {
            private readonly ReadingSeries _data;

            /// <summary>
            /// How to write a value on the axis. Supplied by the form so the plot's scale
            /// labels agree with the big number above them — an axis marked "-1.3286e-12"
            /// under a reading of "-1.35765 pF" is two answers to the same question.
            /// </summary>
            // Set in code, never by a designer — WFO1000 asks every public control property
            // to say which it is.
            [System.ComponentModel.DesignerSerializationVisibility(
                System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public Func<double, string> FormatValue { get; set; } =
                v => v.ToString("g5", CultureInfo.InvariantCulture);

            public TrendPanel(ReadingSeries data)
            {
                _data = data;
                DoubleBuffered = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                Rectangle r = ClientRectangle;
                r.Inflate(-12, -12);
                if (r.Width < 20 || r.Height < 20) return;

                using var grid = new Pen(Color.FromArgb(48, 48, 48));
                for (int i = 0; i <= 4; i++)
                {
                    int y = r.Top + r.Height * i / 4;
                    g.DrawLine(grid, r.Left, y, r.Right, y);
                }

                if (_data.Count < 2)
                {
                    using var brush = new SolidBrush(Color.Gray);
                    using var font = new Font("Segoe UI", 9f);
                    g.DrawString("Press Start to begin plotting readings.", font, brush, r.Left + 6, r.Top + 6);
                    return;
                }

                (double min, double max, _) = _data.Statistics();
                double span = max - min;
                if (span <= 0) { span = Math.Abs(max) > 0 ? Math.Abs(max) * 0.1 : 1; min -= span / 2; }
                min -= span * 0.08;
                max += span * 0.08;
                span = max - min;

                double t0 = double.MaxValue, t1 = double.MinValue;
                foreach (Reading rd in _data.Items)
                {
                    if (rd.Seconds < t0) t0 = rd.Seconds;
                    if (rd.Seconds > t1) t1 = rd.Seconds;
                }
                double tSpan = Math.Max(t1 - t0, 1e-6);

                var points = new System.Collections.Generic.List<PointF>(_data.Count);
                foreach (Reading rd in _data.Items)
                {
                    float x = r.Left + (float)((rd.Seconds - t0) / tSpan * r.Width);
                    float y = r.Bottom - (float)((rd.Value - min) / span * r.Height);
                    points.Add(new PointF(x, y));
                }

                using var trace = new Pen(Color.FromArgb(0, 220, 160), 1.6f)
                {
                    LineJoin = LineJoin.Round,
                };
                g.DrawLines(trace, points.ToArray());

                // Label the vertical extremes so the trace has a scale.
                using var text = new SolidBrush(Color.Gainsboro);
                using var small = new Font("Consolas", 8.5f);
                g.DrawString(FormatValue(max), small, text, r.Left + 2, r.Top - 2);
                g.DrawString(FormatValue(min), small, text,
                             r.Left + 2, r.Bottom - small.Height);
                // Invariant, like the other two: on a machine whose locale uses a decimal
                // comma this read "5,89 s" while the axis labels beside it used a point.
                string span3 = tSpan.ToString("g3", CultureInfo.InvariantCulture) + " s";
                g.DrawString(span3, small, text,
                             r.Right - g.MeasureString(span3, small).Width - 2,
                             r.Bottom - small.Height);
            }
        }
    }
}
