using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LabEquipmentController
{
    /// <summary>
    /// Plots a captured oscilloscope trace, with CSV export, a zoomable view, and — where the
    /// caller can supply another capture — a Run button that keeps taking them.
    /// </summary>
    public sealed class WaveformForm : Form
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

        private WaveformCapture _wave;
        private readonly PlotPanel _plot;
        private readonly Panel _bar;
        private readonly Label _info;
        private readonly Button _save;

        // Only built when the caller can take another capture. A one-shot trace read from a
        // file or handed over by something that has since disconnected has nothing to run.
        private readonly Func<CancellationToken, Task<WaveformCapture>>? _recapture;
        private readonly Button _run = new();
        private readonly NumericUpDown _interval = new();
        private readonly Label _every = new() { Text = "every", AutoSize = true };
        private readonly Label _ms = new() { Text = "ms", AutoSize = true };
        private CancellationTokenSource? _runCts;

        private bool IsRunning => _runCts != null;

        /// <param name="recapture">
        /// Takes another capture from the same instrument, or null if this window is showing a
        /// trace nobody can refresh. Its presence is what decides whether Run appears at all.
        /// </param>
        public WaveformForm(WaveformCapture wave, string source,
                            Func<CancellationToken, Task<WaveformCapture>>? recapture = null)
        {
            _wave = wave;
            _recapture = recapture;

            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9f);   // the app's font, so this window scales like the rest
            Text = "Waveform — " + source;
            // Twice the old 820x520 in each direction. A trace is read across, and the zoom
            // is only worth having if there are pixels to spread the samples over. OnLoad
            // clamps this to the screen it opens on.
            ClientSize = new Size(1640, 1040);
            // A floor only. OnLoad raises the width to whatever this capture's figures need
            // beside the Save button — 480 fitted two of the four and cut the third mid-word.
            MinimumSize = new Size(480, 320);
            StartPosition = FormStartPosition.CenterParent;

            _plot = new PlotPanel(wave) { Dock = DockStyle.Fill, BackColor = Color.Black };

            // Height is set in OnLoad from the button's own measurement. Fixed values kept
            // failing here — 38px clipped the button, 54px clipped it again once the
            // display scale rose — because the button grows with the font and the bar did not.
            _bar = new Panel { Dock = DockStyle.Bottom, Padding = new Padding(10, 10, 10, 10) };
            Panel bar = _bar;

            _save = new Button { Dock = DockStyle.Right };
            ButtonStyle.Apply(_save, "Save CSV…", (_, _) => SaveCsv());
            Button save = _save;

            // AutoEllipsis: the label fills whatever the button leaves, and the four figures
            // do not always fit. Without it the text is chopped mid-word — "span 1" reads as
            // a value rather than the start of one. With it the cut is marked, and the
            // tooltip below still names every figure.
            _info = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Text = Summary(wave),
            };
            Label info = _info;

            // Fill first, then docked edges (this project's convention for docking order).
            // The label went in after the button, so it was given the whole strip and Save CSV
            // was painted over its right-hand end: the figures were chopped at the button's
            // edge, with no ellipsis, because the label believed it had the room.
            bar.Controls.Add(info);
            bar.Controls.Add(save);
            if (_recapture != null) bar.Controls.Add(BuildRunControls());

            Controls.Add(_plot);
            Controls.Add(bar);

            FormClosing += (_, _) => StopRun();

            // Every control gets a tooltip describing what it does.
            var tips = new ToolTip { AutoPopDelay = 15000 };
            tips.SetToolTip(_plot, "Trace captured from the instrument, scaled to volts against "
                                 + "time using the instrument's own scaling data.\r\n\r\n"
                                 + "Ctrl+wheel zooms about the pointer, the wheel alone scrolls "
                                 + "sideways, dragging moves the trace, and double-clicking "
                                 + "shows the whole record again.");
            tips.SetToolTip(info, "Sample count, peak-to-peak voltage, total time span, and "
                                + "the interval between samples.");
            tips.SetToolTip(save, "Save the trace as a CSV file of time and voltage pairs.");
            if (_recapture != null)
            {
                tips.SetToolTip(_run, "Keep taking captures and redraw each one. The zoom stays "
                                    + "where you put it, so a feature can be watched close up.");
                tips.SetToolTip(_interval, "How long to wait between captures. A scope takes a "
                                         + "while to hand over a deep record — if captures take "
                                         + "longer than this, they simply follow one another.");
            }
        }

        /// <summary>
        /// Run, and how often. Docked Left as one strip so the figures keep the middle and
        /// Save CSV keeps the right, whatever width the window is dragged to.
        /// </summary>
        private FlowLayoutPanel BuildRunControls()
        {
            var strip = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = Padding.Empty,
            };

            ButtonStyle.Apply(_run, "Run", (_, _) => ToggleRun());
            _run.Margin = new Padding(0, 0, 14, 0);

            _every.Margin = new Padding(0, 6, 6, 0);

            _interval.Minimum = 50;
            _interval.Maximum = 60000;
            _interval.Increment = 50;
            _interval.Value = 500;
            _interval.TextAlign = HorizontalAlignment.Right;
            _interval.Width = 80;
            _interval.Margin = new Padding(0, 2, 4, 0);

            _ms.Margin = new Padding(0, 6, 18, 0);

            strip.Controls.Add(_run);
            strip.Controls.Add(_every);
            strip.Controls.Add(_interval);
            strip.Controls.Add(_ms);
            return strip;
        }

        /// <summary>
        /// Set the button's glyph and then give the bar exactly the height the button needs,
        /// at this DPI. In that order — the glyph is what makes the button taller.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            ButtonStyle.SetDrawnIcon(this, _save, "save");
            int h = _recapture == null
                ? ButtonStyle.Normalize(this, _save)
                : ButtonStyle.Normalize(this, _save, _run);

            if (_recapture != null)
            {
                UpdateRunIcon();
                ButtonStyle.CentreInRow(_every, h);
                ButtonStyle.CentreInRow(_ms, h);
                ButtonStyle.CentreInRow(_interval, h);
            }

            _bar.Height = h + _bar.Padding.Vertical;

            // The figures and the Save button share one strip, and how wide the figures are
            // depends on the capture — a minimum tuned to one trace elides the next one's.
            // Measure this trace's own text, after Normalize has settled the button's width.
            int need = TextRenderer.MeasureText(_info.Text, _info.Font).Width
                     + _save.Width + _bar.Padding.Horizontal
                     + (Width - ClientSize.Width)          // the frame
                     + LogicalToDeviceUnits(12);           // a little air before the button
            if (_recapture != null)
                need += _run.Width + _interval.Width
                      + TextRenderer.MeasureText(_every.Text + _ms.Text, _info.Font).Width
                      + LogicalToDeviceUnits(48);          // the strip's own margins

            if (need > MinimumSize.Width)
                MinimumSize = new Size(need, MinimumSize.Height);

            // Never bigger than the screen it opens on, as elsewhere. After the minimum has
            // been raised above, so the clamp is against the width this window really wants.
            Rectangle work = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Math.Max(Width, MinimumSize.Width), work.Width),
                            Math.Min(Math.Max(Height, MinimumSize.Height), work.Height));

            _plot.Focus();     // so the wheel reaches the plot without a click first
        }

        // ------------------------------------------------------------------ running

        private void ToggleRun()
        {
            if (IsRunning) StopRun();
            else _ = RunAsync();
        }

        private void StopRun() => _runCts?.Cancel();

        /// <summary>
        /// Take captures until stopped. Shaped like the readout window's polling loop, and
        /// deliberately sequential: the next capture is asked for only once the last has
        /// arrived, so a scope slower than the interval falls behind rather than accumulating
        /// a queue of reads it will never catch up with.
        /// </summary>
        private async Task RunAsync()
        {
            if (IsRunning || _recapture == null) return;

            _runCts = new CancellationTokenSource();
            SetRunning(true);
            CancellationToken ct = _runCts.Token;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    WaveformCapture next = await _recapture(ct);
                    if (ct.IsCancellationRequested) break;

                    _wave = next;
                    _info.Text = Summary(next);
                    _plot.ShowCapture(next);   // the view is left alone: see WaveformView

                    // Read each time round, so changing it takes effect at once.
                    await Task.Delay((int)_interval.Value, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Stop button — nothing to report.
            }
            catch (Exception ex)
            {
                // Name the type as well as the message: this line is the only report the loop
                // has anywhere to put, and "Object reference not set" alone says nothing.
                _info.Text = $"Stopped — {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                _runCts?.Dispose();
                _runCts = null;
                if (!IsDisposed) SetRunning(false);
            }
        }

        private void SetRunning(bool running)
        {
            _run.Text = running ? "Stop" : "Run";
            if (IsHandleCreated) UpdateRunIcon();
        }

        private void UpdateRunIcon()
            => ButtonStyle.SetIcon(this, _run, IsRunning ? "stopClock" : "startClock");

        // ------------------------------------------------------------------ the rest

        private static string Summary(WaveformCapture w)
        {
            if (w.Samples.Count == 0) return "No samples.";
            double min = double.MaxValue, max = double.MinValue;
            foreach (WaveformSample s in w.Samples)
            {
                if (s.Voltage < min) min = s.Voltage;
                if (s.Voltage > max) max = s.Voltage;
            }
            double span = w.XIncrement * w.Samples.Count;
            return $"{w.Samples.Count} points    Vpp {(max - min):g3} V    span {span:g3} s    dt {w.XIncrement:g3} s";
        }

        private void SaveCsv()
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Save waveform",
                Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = "waveform.csv",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllText(dlg.FileName, _wave.ToCsv()); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the waveform:\n" + ex.Message, "Save CSV",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>Double-buffered panel that draws the trace, grid, and voltage scale.</summary>
        private sealed class PlotPanel : Panel
        {
            private WaveformCapture _w;
            private readonly WaveformView _view = new();

            // Where the last drag was, and the plot rectangle the wheel and the drag are
            // measured against. Both in this control's own coordinates.
            private Point? _dragFrom;
            private Rectangle _area;

            private const int MarginLeft = 64, MarginRight = 14, MarginTop = 12, MarginBottom = 30;

            public PlotPanel(WaveformCapture w)
            {
                _w = w;
                DoubleBuffered = true;
                ResizeRedraw = true;

                // A Panel takes no focus by default, and the wheel goes to whatever has it —
                // without this the trace ignored the wheel while the Save button consumed it.
                SetStyle(ControlStyles.Selectable, true);
                TabStop = true;
            }

            /// <summary>
            /// Put a new trace on screen without touching the zoom. A method rather than a
            /// property: a settable public property on a Control makes the WinForms analyser
            /// ask how it should be serialised by a designer that never places this panel.
            /// </summary>
            public void ShowCapture(WaveformCapture w)
            {
                _w = w;
                Invalidate();
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                // Focus follows the pointer here so the wheel works without clicking first.
                // Safe in this window: nothing in it takes typed input.
                if (!Focused) Focus();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                if (_area.Width <= 0) return;

                double notches = e.Delta / 120.0;
                if (notches == 0) return;

                if (ModifierKeys.HasFlag(Keys.Control))
                {
                    // Where the pointer is across the plot, so the moment under it stays put.
                    double at = (e.X - _area.Left) / (double)_area.Width;
                    _view.ZoomAt(at, Math.Pow(1.25, notches));
                }
                else
                {
                    // Wheel alone scrolls sideways. Up goes earlier, matching the direction
                    // the trace moves under a drag in the same direction.
                    _view.PanBy(-notches * 0.15);
                }

                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (!Focused) Focus();
                if (e.Button == MouseButtons.Left) { _dragFrom = e.Location; Cursor = Cursors.SizeWE; }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_dragFrom is not Point from || _area.Width <= 0) return;

                int dx = e.X - from.X;
                if (dx == 0) return;

                // Drag right, trace moves right, so the view moves earlier.
                _view.PanBy(-dx / (double)_area.Width);
                _dragFrom = e.Location;
                Invalidate();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                _dragFrom = null;
                Cursor = Cursors.Default;
            }

            protected override void OnMouseDoubleClick(MouseEventArgs e)
            {
                base.OnMouseDoubleClick(e);
                _view.Reset();
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                int w = ClientSize.Width, h = ClientSize.Height;
                int pw = w - MarginLeft - MarginRight, ph = h - MarginTop - MarginBottom;
                if (pw < 20 || ph < 20 || _w.Samples.Count == 0) { _area = Rectangle.Empty; return; }

                _area = new Rectangle(MarginLeft, MarginTop, pw, ph);

                (int first, int count) = _view.Range(_w.Samples.Count);
                if (count == 0) return;
                int last = first + count - 1;

                // Scaled to what is on screen, not to the whole record: zooming into a ripple
                // riding on a 5V level is the reason to zoom at all, and a fixed vertical
                // scale would leave it a flat line at the top of the plot.
                double vmin = double.MaxValue, vmax = double.MinValue;
                for (int i = first; i <= last; i++)
                {
                    double v = _w.Samples[i].Voltage;
                    if (v < vmin) vmin = v;
                    if (v > vmax) vmax = v;
                }
                if (vmax - vmin < 1e-9) { vmax += 0.5; vmin -= 0.5; }
                double vpad = (vmax - vmin) * 0.08;
                vmin -= vpad; vmax += vpad;
                double vspan = vmax - vmin;

                using var grid = new Pen(Color.FromArgb(45, 45, 45));
                for (int i = 0; i <= 8; i++) { int x = MarginLeft + pw * i / 8; g.DrawLine(grid, x, MarginTop, x, MarginTop + ph); }
                for (int i = 0; i <= 6; i++) { int y = MarginTop + ph * i / 6; g.DrawLine(grid, MarginLeft, y, MarginLeft + pw, y); }

                if (vmin < 0 && vmax > 0)
                {
                    using var zero = new Pen(Color.FromArgb(95, 95, 95));
                    int y0 = MarginTop + (int)(vmax / vspan * ph);
                    g.DrawLine(zero, MarginLeft, y0, MarginLeft + pw, y0);
                }

                using var f = new Font("Segoe UI", 8f);
                using var lbl = new SolidBrush(Color.Gainsboro);
                g.DrawString($"{vmax:g3} V", f, lbl, 4, MarginTop - 2);
                g.DrawString($"{vmin:g3} V", f, lbl, 4, MarginTop + ph - 14);

                // The times at the ends of what is drawn. Without these, zooming changes the
                // picture without saying what part of the record you are looking at.
                string t0 = $"{_w.Samples[first].Time:g3} s";
                string t1 = $"{_w.Samples[last].Time:g3} s";
                g.DrawString(t0, f, lbl, MarginLeft, MarginTop + ph + 6);
                SizeF t1Size = g.MeasureString(t1, f);
                g.DrawString(t1, f, lbl, MarginLeft + pw - t1Size.Width, MarginTop + ph + 6);

                if (!_view.IsWholeRecord)
                {
                    string note = $"{_view.Span:P1} of the record — double-click to reset";
                    SizeF size = g.MeasureString(note, f);
                    using var dim = new SolidBrush(Color.FromArgb(140, 140, 140));
                    g.DrawString(note, f, dim, MarginLeft + (pw - size.Width) / 2, MarginTop + ph + 6);
                }

                var pts = new PointF[count];
                for (int i = 0; i < count; i++)
                {
                    float x = MarginLeft + (count == 1 ? 0 : (float)i / (count - 1) * pw);
                    float y = MarginTop + (float)((vmax - _w.Samples[first + i].Voltage) / vspan * ph);
                    pts[i] = new PointF(x, y);
                }
                using var trace = new Pen(Color.Lime, 1.3f);
                if (count > 1) g.DrawLines(trace, pts);
            }
        }
    }
}
