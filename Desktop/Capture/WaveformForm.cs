using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LabEquipmentController
{
    /// <summary>
    /// Plots captured oscilloscope traces, with CSV export, a zoomable view, and — where the
    /// caller can supply another capture — a Run button that keeps taking them.
    ///
    /// One capture, drawn on as many cards as are wanted. A card is a set of channels and a
    /// picture of them: alone on a card a channel has the vertical scale to itself, together on
    /// one card they share it, which is what makes two traces comparable and what stops a
    /// 100 mV ripple beside a 5 V square wave coming out the same height. Which of those a
    /// measurement wants is not something this window can know, so it is asked rather than
    /// answered — the tick boxes on a card say which channels it draws, the ✕ takes it away,
    /// and the dashed frame under the last one adds another.
    ///
    /// One capture serves them all. The union of what the cards ask for goes to the instrument
    /// once, so two cards showing two channels cost the same two reads as one card showing both,
    /// and unticking a channel redraws from what is already in hand rather than going back to
    /// the bench for an answer already given.
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

        /// <summary>The last capture, whole. Each card takes its own channels out of it.</summary>
        private IReadOnlyList<ChannelRead> _wave;

        private readonly List<Card> _cards = [];
        private readonly TableLayoutPanel _stack;
        private readonly AddCardStrip _add = new();
        private readonly Panel _bar;
        private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };

        // Only built when the caller can take another capture. A one-shot trace read from a
        // file or handed over by something that has since disconnected has nothing to run.
        //
        // It is handed the channels to read, because the pickers that choose them are on this
        // window: a second probe goes on the bench while the window is already open, and closing
        // it to say so would be the wrong way round.
        private readonly Func<IReadOnlyList<int>, CancellationToken, Task<IReadOnlyList<ChannelRead>>>? _recapture;

        // Capture Waveform is the single shot and Run is the loop. The window opens with a
        // capture already in it, so the single shot used not to be here at all — but a card
        // added on a channel the last capture did not include has nothing to draw until one is
        // taken, and "start the loop and stop it again" is a poor way to ask for one picture.
        private readonly Button _once = new();
        private readonly Button _run = new();
        private readonly NumericUpDown _interval = new();
        private readonly Label _every = new() { Text = "every", AutoSize = true };
        private readonly Label _ms = new() { Text = "ms", AutoSize = true };
        private CancellationTokenSource? _runCts;
        private bool _busy;

        /// <summary>
        /// The one button height for this window's font and display scale, once OnLoad has
        /// measured it. Kept, because a card added after that has to be settled to the same
        /// height — dressed only at load, the second card came up with an undressed Save
        /// button, no floppy on it and a foot half a line taller than the card above.
        /// </summary>
        private int _buttonHeight;

        /// <summary>Four cards is one per channel, which is as far apart as they go.</summary>
        private const int MaxCards = 4;

        private bool IsRunning => _runCts != null;

        /// <param name="recapture">
        /// Takes another capture from the same instrument, or null if this window is showing a
        /// trace nobody can refresh. Its presence is what decides whether Capture Waveform and
        /// Run appear at all.
        /// </param>
        public WaveformForm(IReadOnlyList<ChannelRead> wave, string source,
                            Func<IReadOnlyList<int>, CancellationToken, Task<IReadOnlyList<ChannelRead>>>? recapture = null)
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
            // beside the Save button, and Rebuild raises the height as cards are added.
            MinimumSize = new Size(480, 320);
            StartPosition = FormStartPosition.CenterParent;

            _stack = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(8, 8, 8, 0),
            };
            _stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            _add.Click += (_, _) => AddCard();

            // Above the cards, where the web build puts it: what you press to fill them comes
            // before what it fills, and a strip along the bottom of a stack of cards reads as
            // belonging to the last card rather than to the window.
            //
            // Height is set in OnLoad from the button's own measurement. Fixed values kept
            // failing here — 38px clipped the button, 54px clipped it again once the
            // display scale rose — because the button grows with the font and the bar did not.
            //
            // No padding underneath: the stack below brings its own eight, and two helpings
            // stood the first card twice as far from the strip as the cards stand from
            // each other. Eight at the sides, so the buttons line up with the cards' edges.
            _bar = new Panel { Dock = DockStyle.Top, Padding = new Padding(8, 10, 8, 0) };
            if (_recapture != null) _bar.Controls.Add(BuildRunControls());

            // Fill first, then docked edges (this project's convention for docking order).
            Controls.Add(_stack);
            Controls.Add(_bar);

            // One card to start, holding whatever this capture came with — which is what the
            // console asked the instrument for. Channel 1 if it somehow holds none, because a
            // card with nothing ticked has nothing to draw.
            List<int> opening = [.. wave.Where(c => c.Ok).Select(c => c.Channel).Distinct().OrderBy(c => c)];
            AddCard(opening.Count > 0 ? opening : [1]);

            FormClosing += (_, _) => StopRun();

            _tips.SetToolTip(_add, "Another card, drawing whichever channels you tick on it. "
                                 + "Channels on separate cards are read apart, each with the "
                                 + "vertical scale to itself; channels on one card share it and "
                                 + "are read against each other.");
            if (_recapture != null)
            {
                _tips.SetToolTip(_once, "Take one capture of every channel any card is showing, "
                                      + "and redraw them all.");
                _tips.SetToolTip(_run, "Keep taking captures and redraw each one. The zoom stays "
                                     + "where you put it, so a feature can be watched close up.");
                _tips.SetToolTip(_interval, "How long to wait between captures. A scope takes a "
                                          + "while to hand over a deep record — if captures take "
                                          + "longer than this, they simply follow one another.");
            }
        }

        // ------------------------------------------------------------------ the cards

        /// <summary>
        /// Lay the cards out again, and the frame that adds one under them.
        ///
        /// Percent rows rather than a height each: with one card open it fills the window, which
        /// is what this window did before there were cards, and with four it is four quarters.
        /// The form's own minimum grows with them, so a card cannot be dragged down to a strip of
        /// chrome with no picture between its halves.
        /// </summary>
        private void Rebuild()
        {
            _stack.SuspendLayout();
            _stack.Controls.Clear();
            _stack.RowStyles.Clear();
            _stack.RowCount = _cards.Count + 1;

            for (int i = 0; i < _cards.Count; i++)
            {
                _stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / _cards.Count));
                _stack.Controls.Add(_cards[i], 0, i);
            }
            _stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _stack.Controls.Add(_add, 0, _cards.Count);
            _add.Visible = _cards.Count < MaxCards;

            _stack.ResumeLayout(performLayout: true);
            SetEnabled();
            RaiseMinimum();
        }

        /// <summary>
        /// Another card, on the lowest channel no card is showing — so pressing it repeatedly
        /// walks 1, 2, 3, 4 and lands on one channel per card without anything being typed. Once
        /// all four are spoken for it starts again at 1, which is the overlapping case asked for
        /// the other way round.
        /// </summary>
        private void AddCard()
        {
            HashSet<int> taken = [.. _cards.SelectMany(c => c.Channels)];
            AddCard([Enumerable.Range(1, 4).FirstOrDefault(c => !taken.Contains(c), 1)]);
        }

        private void AddCard(IReadOnlyList<int> channels)
        {
            if (_cards.Count >= MaxCards) return;

            Card card = NewCard(channels);
            _cards.Add(card);
            Rebuild();
            if (_buttonHeight > 0) DressCard(card, _buttonHeight);

            // Whatever is already captured is already here, so a new card is drawn from it
            // rather than waiting to be asked: adding a card to look at channel 2 of a capture
            // that holds channel 2 should show it.
            Redraw(card);
        }

        /// <summary>
        /// Take a card away. The last one can go too: what is left is the frame that puts it
        /// back, which says what to do more plainly than a ✕ that refuses to work would.
        /// </summary>
        private void Drop(Card card)
        {
            if (!_cards.Remove(card)) return;
            if (_cards.Count == 0) StopRun();
            Rebuild();
            card.Dispose();
        }

        /// <summary>
        /// Turn a channel on or off for one card. The last one on cannot be turned off: a card
        /// with no channels is a card with nothing to draw, and the ✕ is the way to be rid of it.
        /// </summary>
        private void Picked(Card card, CheckBox box)
        {
            if (card.Boxes.All(b => !b.Checked)) { box.Checked = true; return; }

            // Redrawn from what is already in hand rather than by going back to the instrument:
            // the capture holds every channel any card asked for, so unticking one is a question
            // whose answer is already here. A channel newly ticked that the capture does not hold
            // stays blank until the next one, which is the honest state — nothing was measured
            // on it.
            Redraw(card);
            SetEnabled();
        }

        /// <summary>Cut the last capture down to one card's channels, and draw it.</summary>
        private void Redraw(Card card)
        {
            HashSet<int> mine = [.. card.Channels];
            card.Wave = [.. _wave.Where(c => mine.Contains(c.Channel))];
            card.Plot.ShowCapture(card.Wave);

            // A card whose channels the last capture does not hold has nothing to report —
            // so it says what to do about that rather than reporting nothing. Which is a
            // different sentence when there is no way to take another capture.
            card.Info.Text = card.Wave.Count > 0
                ? Summary(card.Wave)
                : _recapture != null
                    ? "Nothing on this card yet — press Capture Waveform."
                    : "Nothing on this card: this capture does not hold those channels.";

            SetZoomEnabled(card);
        }

        /// <summary>Every channel any card is showing, lowest first — what one capture must hold.</summary>
        private IReadOnlyList<int> Wanted()
        {
            List<int> picked = [.. _cards.SelectMany(c => c.Channels).Distinct().OrderBy(c => c)];
            return picked.Count > 0 ? picked : [1];
        }

        /// <summary>
        /// What can be pressed. Nothing to capture when there are no cards, and nothing to press
        /// twice while a capture is in flight.
        /// </summary>
        private void SetEnabled()
        {
            if (_recapture == null) return;
            _once.Enabled = _cards.Count > 0 && !_busy && !IsRunning;
            _run.Enabled = _cards.Count > 0;
        }

        /// <summary>
        /// Keep the window tall enough for the cards in it.
        ///
        /// A card is a head, a picture and a foot; squeezed, the picture is what goes, and a card
        /// with no picture in it is two rows of controls about nothing. Clamped to the screen —
        /// a minimum bigger than the display is a window that cannot be put anywhere.
        /// </summary>
        private void RaiseMinimum()
        {
            if (!IsHandleCreated) return;

            int need = LogicalToDeviceUnits(170) * Math.Max(1, _cards.Count)
                     + LogicalToDeviceUnits(120)          // the run bar, the add frame, the frame
                     + (Height - ClientSize.Height);
            int cap = Screen.FromControl(this).WorkingArea.Height;
            MinimumSize = new Size(MinimumSize.Width, Math.Min(Math.Max(need, 320), cap));
        }

        /// <summary>
        /// One card: the channels it draws along the top with the ✕ at the far end, the picture,
        /// and the figures with the two ways of keeping them along the bottom.
        /// </summary>
        private Card NewCard(IReadOnlyList<int> channels)
        {
            var card = new Card
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 8),
                BorderStyle = BorderStyle.FixedSingle,
            };

            // Which channels, then the ✕ at the far end — the same arrangement a console tab
            // uses, and for the same reason: the thing that removes something goes last.
            var head = new Panel { Dock = DockStyle.Top, Padding = new Padding(8, 4, 4, 2) };
            var picks = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = Padding.Empty,
            };
            picks.Controls.Add(new Label { Text = "Channels", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });

            for (int n = 1; n <= 4; n++)
            {
                int channel = n;
                var box = new CheckBox
                {
                    Text = channel.ToString(CultureInfo.InvariantCulture),
                    AutoSize = true,
                    Checked = channels.Contains(channel),
                    ForeColor = ChannelInk(channel),
                    Margin = new Padding(0, 5, 6, 0),
                };
                box.CheckedChanged += (_, _) => Picked(card, box);
                _tips.SetToolTip(box, $"Draw channel {channel} on this card. At least one channel is always on.");
                card.Boxes[channel - 1] = box;
                picks.Controls.Add(box);
            }

            card.Shut = new CrossButton { Dock = DockStyle.Right, Width = LogicalToDeviceUnits(22) };
            card.Shut.Click += (_, _) => Drop(card);
            _tips.SetToolTip(card.Shut, "Take this card away. The channels on it stop being captured.");

            // The gestures, as buttons. The wheel and the drag are quicker once you know they
            // are there, and nothing on a plot says that they are — so the three things they do
            // are on the card as well, where they can be found by looking.
            var zoomers = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = Padding.Empty,
            };
            card.ZoomIn = NewGlyphButton("plus", "Zoom in about the middle of what is drawn. "
                                               + "Ctrl and the wheel does the same about the pointer.",
                                         (_, _) => Nudge(card, 1.25));
            card.ZoomOut = NewGlyphButton("minus", "Zoom out about the middle of what is drawn.",
                                          (_, _) => Nudge(card, 1 / 1.25));
            card.ZoomFit = NewGlyphButton("fit", "Show the whole record again, centred. "
                                               + "Double-clicking the trace does the same.",
                                          (_, _) => ResetCard(card));
            // Air between the last of the three and the ✕. They are three ways of looking at
            // the trace and it is the way to be rid of the card, and at the ordinary gap they
            // read as four buttons that happen to be in a row.
            card.ZoomFit.Margin = new Padding(0, 2, LogicalToDeviceUnits(14), 0);

            zoomers.Controls.Add(card.ZoomIn);
            zoomers.Controls.Add(card.ZoomOut);
            zoomers.Controls.Add(card.ZoomFit);

            // Fill first, then the edges — and of the two docked right, the one added last ends
            // up furthest right, which is where the ✕ belongs.
            head.Controls.Add(picks);
            head.Controls.Add(zoomers);
            head.Controls.Add(card.Shut);

            // AutoEllipsis: the label fills whatever the buttons leave, and the figures do not
            // always fit. Without it the text is chopped mid-word — "span 1" reads as a value
            // rather than the start of one. With it the cut is marked, and the tooltip still
            // names every figure.
            var foot = new Panel { Dock = DockStyle.Bottom, Padding = new Padding(8, 4, 4, 4) };
            card.Info = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };
            _tips.SetToolTip(card.Info, "Sample count, peak-to-peak voltage per channel, total "
                                      + "time span, and the interval between samples.");

            card.Save = new Button { Dock = DockStyle.Right };
            ButtonStyle.Apply(card.Save, "Save CSV…", (_, _) => SaveCsv(card));
            _tips.SetToolTip(card.Save, "Save the channels on this card as a CSV file: one time "
                                      + "column, then a column of volts per channel. The whole "
                                      + "record, not the part on screen.");

            // The numbers, and then the picture. Same camera the plot panel carries, aimed at
            // the trace as it is drawn here — which is not the instrument's own screen.
            card.Png = new Button { Dock = DockStyle.Right };
            ButtonStyle.Apply(card.Png, "", (_, _) => SavePng(card));
            _tips.SetToolTip(card.Png, "Save this card's plot as a PNG image. Save CSV is the "
                                     + "numbers; this is the picture.");

            // Fill first, then the edges — and of the two docked right, the one added last is
            // the one that ends up furthest right.
            foot.Controls.Add(card.Info);
            foot.Controls.Add(card.Save);
            foot.Controls.Add(card.Png);

            card.Plot = new PlotPanel { Dock = DockStyle.Fill, BackColor = Color.Black };
            card.Plot.ViewChanged += (_, _) => SetZoomEnabled(card);
            _tips.SetToolTip(card.Plot, "Trace captured from the instrument, scaled to volts against "
                                      + "time using the instrument's own scaling data.\r\n\r\n"
                                      + "Ctrl+wheel zooms about the pointer, the wheel alone scrolls "
                                      + "sideways, dragging moves the trace, and double-clicking "
                                      + "shows the whole record again.\r\n\r\n"
                                      + "Each card zooms on its own: two cards on one capture are "
                                      + "two views of it.");

            card.Controls.Add(card.Plot);
            card.Controls.Add(head);
            card.Controls.Add(foot);
            card.Head = head;
            card.Foot = foot;
            return card;
        }

        /// <summary>
        /// A square button with a glyph and no label, for the three gestures.
        ///
        /// Sized in DressCard once the shared button height is known, like the camera in the
        /// foot: a glyph-only button as wide as a labelled one is a mark adrift in a box.
        /// </summary>
        private Button NewGlyphButton(string glyph, string tip, EventHandler onClick)
        {
            var b = new Button();
            ButtonStyle.Apply(b, "", onClick);
            b.Margin = new Padding(0, 2, 2, 0);
            b.Tag = glyph;
            _tips.SetToolTip(b, tip);
            return b;
        }

        /// <summary>
        /// One press of the zoom buttons: the wheel's own factor, about the middle.
        ///
        /// The middle rather than the pointer, because a button has no pointer to zoom about.
        /// The wheel keeps that, which is the reason to keep the wheel.
        /// </summary>
        private void Nudge(Card card, double factor)
        {
            card.Plot.ZoomAbout(0.5, factor);
            SetZoomEnabled(card);
        }

        private void ResetCard(Card card)
        {
            card.Plot.ResetView();
            SetZoomEnabled(card);
        }

        /// <summary>
        /// Zooming out and showing all of it are the same thing at the whole record, and neither
        /// is anything at all with no capture on the card. A control that cannot do its job is
        /// present and greyed, with the reason in its tooltip — see SPEC §7.
        /// </summary>
        private static void SetZoomEnabled(Card card)
        {
            bool drawn = card.Wave.Count > 0;
            card.ZoomIn.Enabled = drawn;
            card.ZoomOut.Enabled = drawn && !card.Plot.IsWholeRecord;
            card.ZoomFit.Enabled = drawn && !card.Plot.IsWholeRecord;
        }

        // ------------------------------------------------------------------ the strip

        /// <summary>
        /// Taking a capture, and keeping at it. Docked Left as one strip, which is all the
        /// bottom bar carries now — the figures and the two ways of keeping them belong to a
        /// card, and there can be four of those.
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

            ButtonStyle.Apply(_once, "Capture Waveform", (_, _) => _ = CaptureOnceAsync());
            _once.Margin = new Padding(0, 0, 14, 0);

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

            strip.Controls.Add(_once);
            strip.Controls.Add(_run);
            strip.Controls.Add(_every);
            strip.Controls.Add(_interval);
            strip.Controls.Add(_ms);
            return strip;
        }

        /// <summary>
        /// Set the buttons' glyphs and then give the bar exactly the height they need, at this
        /// DPI. In that order — a glyph is what makes a button taller.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            int h;
            if (_recapture != null)
            {
                ButtonStyle.SetDrawnIcon(this, _once, "wave");
                h = ButtonStyle.Normalize(this, _once, _run);
                UpdateRunIcon();
                ButtonStyle.CentreInRow(_every, h);
                ButtonStyle.CentreInRow(_ms, h);
                ButtonStyle.CentreInRow(_interval, h);
            }
            else
            {
                h = ButtonStyle.Height(this);
            }

            // Every card's buttons take the same height, and its two strips are sized to them.
            _buttonHeight = h;
            foreach (Card card in _cards) DressCard(card, h);

            _bar.Height = _recapture != null ? h + _bar.Padding.Vertical : 0;
            _bar.Visible = _recapture != null;

            // The figures and the two buttons share one strip, and how wide the figures are
            // depends on the capture — a minimum tuned to one trace elides the next one's.
            Card first = _cards[0];
            int need = TextRenderer.MeasureText(first.Info.Text, first.Info.Font).Width
                     + first.Save.Width + first.Png.Width + first.Foot.Padding.Horizontal
                     + (Width - ClientSize.Width)          // the frame
                     + LogicalToDeviceUnits(28);           // a little air before the buttons
            if (_recapture != null)
                need = Math.Max(need, _once.Width + _run.Width + _interval.Width
                                    + TextRenderer.MeasureText(_every.Text + _ms.Text, Font).Width
                                    + LogicalToDeviceUnits(80));

            if (need > MinimumSize.Width) MinimumSize = new Size(need, MinimumSize.Height);
            RaiseMinimum();

            // Never bigger than the screen it opens on, as elsewhere. After the minimum has
            // been raised above, so the clamp is against the width this window really wants.
            Rectangle work = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Math.Max(Width, MinimumSize.Width), work.Width),
                            Math.Min(Math.Max(Height, MinimumSize.Height), work.Height));

            _cards[0].Plot.Focus();   // so the wheel reaches a plot without a click first
        }

        /// <summary>
        /// Settle one card's metrics: the glyphs, the shared button height, and the two strips
        /// sized to it. Called for the cards that exist at load, and for each one added after.
        /// </summary>
        private void DressCard(Card card, int h)
        {
            if (card.Dressed) return;
            card.Dressed = true;

            ButtonStyle.SetDrawnIcon(this, card.Save, "save");
            ButtonStyle.SetDrawnIcon(this, card.Png, "camera");
            ButtonStyle.Normalize(this, card.Save, card.Png);

            // The head's three are smaller than the foot's: they sit on a row of tick boxes
            // rather than on a row of buttons, and a full-height button there would be the
            // tallest thing on the card by half again.
            int small = card.Boxes[0].Height + LogicalToDeviceUnits(4);
            foreach (Button b in new[] { card.ZoomIn, card.ZoomOut, card.ZoomFit })
            {
                ButtonStyle.SetDrawnIcon(this, b, (string)b.Tag!);
                b.MinimumSize = new Size(small + LogicalToDeviceUnits(6), small);
                b.MaximumSize = new Size(small + LogicalToDeviceUnits(6), small);
            }

            // A button with a glyph and no label is as wide as it is tall, plus its padding —
            // otherwise Normalize's shared minimum width leaves a camera adrift in a wide box.
            card.Png.MinimumSize = new Size(h, h);
            card.Png.MaximumSize = new Size(h + LogicalToDeviceUnits(6), h);

            card.Foot.Height = h + card.Foot.Padding.Vertical;
            card.Head.Height = card.Boxes[0].Height + card.Head.Padding.Vertical
                             + LogicalToDeviceUnits(6);
            card.Shut.Height = card.Head.Height;
        }

        // ------------------------------------------------------------------ capturing

        /// <summary>
        /// One capture, drawn on every card.
        ///
        /// The union of what the cards ask for goes to the instrument once. Two cards on channel
        /// 1 and channel 2 are two reads, the same two a single card showing both would take —
        /// and two cards both showing channel 1 are one read, drawn twice.
        /// </summary>
        private async Task CaptureOnceAsync()
        {
            if (_recapture == null || _busy || _cards.Count == 0) return;

            _busy = true;
            SetEnabled();
            try
            {
                await CaptureAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Report(ex);
            }
            finally
            {
                _busy = false;
                SetEnabled();
            }
        }

        private async Task CaptureAsync(CancellationToken ct)
        {
            _wave = await _recapture!(Wanted(), ct);
            if (ct.IsCancellationRequested || IsDisposed) return;
            foreach (Card card in _cards) Redraw(card);   // the views are left alone: see WaveformView
        }

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
            if (IsRunning || _recapture == null || _cards.Count == 0) return;

            _runCts = new CancellationTokenSource();
            SetRunning(true);
            CancellationToken ct = _runCts.Token;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Read the channels each time round, so ticking one takes effect on the next
                    // capture rather than on the next press.
                    await CaptureAsync(ct);
                    if (ct.IsCancellationRequested) break;

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
                Report(ex);
            }
            finally
            {
                _runCts?.Dispose();
                _runCts = null;
                if (!IsDisposed) { SetRunning(false); SetEnabled(); }
            }
        }

        /// <summary>
        /// Name the type as well as the message: a card's foot is the only place this window has
        /// to put a failure, and "Object reference not set" alone says nothing.
        /// </summary>
        private void Report(Exception ex)
        {
            if (_cards.Count > 0)
                _cards[0].Info.Text = $"Stopped — {ex.GetType().Name}: {ex.Message}";
        }

        /// <summary>
        /// The colour a scope draws each channel in — yellow, cyan, magenta, blue.
        ///
        /// Not a palette chosen here: it is the one printed beside the BNC connectors on the
        /// front panel of every scope this app talks to. A trace in a different colour from the
        /// one the instrument uses for it is a trace you have to look up.
        /// </summary>
        internal static Color ChannelInk(int channel) => channel switch
        {
            1 => Color.FromArgb(245, 217, 10),
            2 => Color.FromArgb(34, 211, 238),
            3 => Color.FromArgb(232, 121, 249),
            _ => Color.FromArgb(96, 165, 250),
        };

        private void SetRunning(bool running)
        {
            _run.Text = running ? "Stop" : "Run";
            if (IsHandleCreated) UpdateRunIcon();
            SetEnabled();
        }

        private void UpdateRunIcon()
            => ButtonStyle.SetIcon(this, _run, IsRunning ? "stopClock" : "startClock");

        // ------------------------------------------------------------------ the rest

        /// <summary>
        /// One card's figures: how many samples, what each channel measured, and over what.
        ///
        /// A channel that could not be read says so rather than quietly not being there — and it
        /// is filtered on <c>Ok</c> before anything reads its capture, because a channel the
        /// instrument does not have comes back with no capture at all.
        /// </summary>
        private static string Summary(IReadOnlyList<ChannelRead> captures)
        {
            List<ChannelRead> drawn = [.. captures.Where(c => c.Ok && c.Capture!.Samples.Count > 0)];
            List<string> failed = [.. captures.Where(c => !c.Ok).Select(c => $"CH{c.Channel}: {c.Error}")];

            if (drawn.Count == 0)
                return failed.Count > 0 ? string.Join("    ", failed) : "No samples.";

            WaveformCapture axis = drawn[0].Capture!;
            List<string> parts = [$"{axis.Samples.Count} points"];
            foreach (ChannelRead c in drawn)
            {
                double min = double.MaxValue, max = double.MinValue;
                foreach (WaveformSample s in c.Capture!.Samples)
                {
                    if (s.Voltage < min) min = s.Voltage;
                    if (s.Voltage > max) max = s.Voltage;
                }
                parts.Add($"CH{c.Channel} Vpp {(max - min):g3} V");
            }

            parts.Add($"span {axis.XIncrement * axis.Samples.Count:g3} s");
            parts.Add($"dt {axis.XIncrement:g3} s");
            parts.AddRange(failed);
            return string.Join("    ", parts);
        }

        /// <summary>
        /// What a saved card is called. The channels are in the name because with several cards
        /// open the alternative is four files called waveform, and which is which is the one
        /// thing you would want to know.
        /// </summary>
        private static string FileName(Card card, string extension)
            => "waveform-ch" + string.Join("-", card.Channels) + extension;

        private void SaveCsv(Card card)
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Save waveform",
                Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = FileName(card, ".csv"),
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllText(dlg.FileName, ToCsv(card.Wave)); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the waveform:\n" + ex.Message, "Save CSV",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// The card's plot as a picture. Save CSV is the numbers and this is what they look like
        /// — worth keeping on its own, into a report or beside the next run's, without reaching
        /// for a screenshot tool. The panel draws its own black ground, so the file carries it.
        /// </summary>
        private void SavePng(Card card)
        {
            if (card.Plot.Width < 1 || card.Plot.Height < 1) return;

            using var dlg = new SaveFileDialog
            {
                Title = "Save trace image",
                Filter = "PNG image (*.png)|*.png|All files (*.*)|*.*",
                FileName = FileName(card, ".png"),
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                using var bmp = new Bitmap(card.Plot.Width, card.Plot.Height);
                card.Plot.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the image:\n" + ex.Message, "Save image",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// The capture as CSV: one time column, then a column of volts per channel.
        ///
        /// The whole record rather than the visible slice — the zoom is a way of looking at the
        /// capture, not a way of choosing what was measured, and a file that quietly held a third
        /// of it because of where the plot happened to be is a file that lies.
        ///
        /// WaveformCapture.ToCsv writes one channel and is still what the CLI uses; this is the
        /// same numbers with a column per channel, which only this window has to produce.
        /// </summary>
        public static string ToCsv(IReadOnlyList<ChannelRead> captures)
        {
            List<ChannelRead> kept = [.. captures.Where(c => c.Ok && c.Capture!.Samples.Count > 0)];
            if (kept.Count == 0) return "Time (s)\r\n";

            var sb = new StringBuilder("Time (s)");
            foreach (ChannelRead c in kept) sb.Append(",CH").Append(c.Channel).Append(" (V)");
            sb.Append("\r\n");

            IReadOnlyList<WaveformSample> axis = kept[0].Capture!.Samples;
            for (int i = 0; i < axis.Count; i++)
            {
                sb.Append(axis[i].Time.ToString("G17", CultureInfo.InvariantCulture));
                foreach (ChannelRead c in kept)
                    sb.Append(',').Append(i < c.Capture!.Samples.Count
                        ? c.Capture!.Samples[i].Voltage.ToString("G17", CultureInfo.InvariantCulture)
                        : "");
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ the pieces

        /// <summary>
        /// One capture card: which channels it draws, the picture, and the figures under it.
        /// </summary>
        private sealed class Card : Panel
        {
            public readonly CheckBox[] Boxes = new CheckBox[4];
            public PlotPanel Plot = null!;
            public Label Info = null!;
            public Button Save = null!;
            public Button Png = null!;
            public CrossButton Shut = null!;
            public Button ZoomIn = null!;
            public Button ZoomOut = null!;
            public Button ZoomFit = null!;
            public Panel Head = null!;
            public Panel Foot = null!;

            /// <summary>Whether OnLoad has settled its metrics. A card added later is dressed then.</summary>
            public bool Dressed;

            /// <summary>The last capture, cut down to this card's channels.</summary>
            public IReadOnlyList<ChannelRead> Wave = [];

            /// <summary>Which channels are ticked, lowest first.</summary>
            public IReadOnlyList<int> Channels =>
                [.. Boxes.Where(b => b.Checked).Select(b => int.Parse(b.Text, CultureInfo.InvariantCulture))];
        }

        /// <summary>
        /// The ✕ that takes a card away.
        ///
        /// Drawn rather than a Button with a cross typed into it, because the app already has one
        /// of these — on every console tab — and two marks that do the same thing should be the
        /// same mark. Same two strokes, same grey, same white on red under the pointer.
        /// </summary>
        private sealed class CrossButton : Control
        {
            private bool _over;

            public CrossButton()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Hand;
                TabStop = false;
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                _over = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _over = false;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                using (var back = new SolidBrush(Parent?.BackColor ?? SystemColors.Control))
                    g.FillRectangle(back, ClientRectangle);

                // The square the mark is drawn in: centred, and as wide as the control is,
                // which is what keeps it square whatever height the head row settles at.
                int side = Math.Min(Width, Height);
                var box = new Rectangle((Width - side) / 2, (Height - side) / 2, side, side);

                if (_over)
                {
                    using var hot = new SolidBrush(Color.FromArgb(230, 90, 90));
                    g.FillRectangle(hot, box);
                }

                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(_over ? Color.White : SystemColors.GrayText, 1.4f);
                int inset = Math.Max(3, side / 3);
                g.DrawLine(pen, box.Left + inset, box.Top + inset, box.Right - inset, box.Bottom - inset);
                g.DrawLine(pen, box.Right - inset, box.Top + inset, box.Left + inset, box.Bottom - inset);
            }
        }

        /// <summary>
        /// The frame under the last card, and the way to another one.
        ///
        /// A dashed outline rather than a button, because it is a card-shaped hole where the next
        /// card goes — the same dashes a drop target wears, and for the same reason: dashes are
        /// what an empty place that takes something looks like. A solid button here would read as
        /// one more control in the row above rather than as the start of a new card.
        /// </summary>
        private sealed class AddCardStrip : Control
        {
            private bool _over;

            public AddCardStrip()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                Dock = DockStyle.Top;
                Cursor = Cursors.Hand;
                TabStop = false;
                Margin = new Padding(0, 0, 0, 8);
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                Height = LogicalToDeviceUnits(40);
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                _over = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _over = false;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                using (var back = new SolidBrush(Parent?.BackColor ?? SystemColors.Control))
                    g.FillRectangle(back, ClientRectangle);

                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color ink = _over ? SystemColors.Highlight : SystemColors.GrayText;

                var frame = new Rectangle(1, 1, Width - 3, Height - 3);
                using (var edge = new Pen(ink, 2f) { DashStyle = DashStyle.Dash })
                using (GraphicsPath path = Rounded(frame, LogicalToDeviceUnits(8)))
                    g.DrawPath(edge, path);

                const string words = "Add channel capture";
                Size text = TextRenderer.MeasureText(words, Font);
                int plus = LogicalToDeviceUnits(13);
                int gap = LogicalToDeviceUnits(7);
                int left = (Width - (plus + gap + text.Width)) / 2;
                int mid = Height / 2;

                using (var pen = new Pen(ink, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, left + plus / 2, mid - plus / 2, left + plus / 2, mid + plus / 2);
                    g.DrawLine(pen, left, mid, left + plus, mid);
                }

                TextRenderer.DrawText(g, words, Font,
                                      new Point(left + plus + gap, mid - text.Height / 2), ink);
            }

            /// <summary>A rectangle with its corners taken off, at the radius the web build uses.</summary>
            private static GraphicsPath Rounded(Rectangle r, int radius)
            {
                int d = Math.Max(2, radius * 2);
                var path = new GraphicsPath();
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }

        /// <summary>Double-buffered panel that draws one card's traces, grid, and voltage scale.</summary>
        private sealed class PlotPanel : Panel
        {
            private IReadOnlyList<ChannelRead> _w = [];
            private readonly WaveformView _view = new();

            // Where the last drag was, and the plot rectangle the wheel and the drag are
            // measured against. Both in this control's own coordinates.
            private Point? _dragFrom;
            private Rectangle _area;

            private const int MarginLeft = 64, MarginRight = 14, MarginTop = 12, MarginBottom = 30;

            public PlotPanel()
            {
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
            public void ShowCapture(IReadOnlyList<ChannelRead> w)
            {
                _w = w;
                Invalidate();
            }

            /// <summary>Whether the whole capture is on screen, for the buttons that say so.</summary>
            public bool IsWholeRecord => _view.IsWholeRecord;

            /// <summary>
            /// The view moved, by whatever means.
            ///
            /// The buttons on the card say whether there is anything to zoom out of, and the
            /// wheel and the drag can answer that question without going near them — so the
            /// panel says when it has moved rather than the card guessing.
            /// </summary>
            public event EventHandler? ViewChanged;

            private void Moved()
            {
                Invalidate();
                ViewChanged?.Invoke(this, EventArgs.Empty);
            }

            /// <summary>Zoom about a fraction across the plot — 0.5 being the middle.</summary>
            public void ZoomAbout(double at, double factor)
            {
                _view.ZoomAt(at, factor);
                Moved();
            }

            /// <summary>The whole record again, as a double-click does.</summary>
            public void ResetView()
            {
                _view.Reset();
                Moved();
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

                Moved();
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
                Moved();
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
                Moved();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                int w = ClientSize.Width, h = ClientSize.Height;
                int pw = w - MarginLeft - MarginRight, ph = h - MarginTop - MarginBottom;

                // Every channel of one capture is sampled on the same time base, so the first
                // one that has samples sets the axis and the count for all of them. Ok first:
                // a channel the instrument does not have comes back with no capture at all.
                var drawn = _w.Where(c => c.Ok && c.Capture!.Samples.Count > 0).ToList();
                if (pw < 20 || ph < 20 || drawn.Count == 0) { _area = Rectangle.Empty; return; }
                IReadOnlyList<WaveformSample> axis = drawn[0].Capture!.Samples;

                _area = new Rectangle(MarginLeft, MarginTop, pw, ph);

                (int first, int count) = _view.Range(axis.Count);
                if (count == 0) return;
                int last = first + count - 1;
                drawn = drawn.Where(c => c.Capture!.Samples.Count > last).ToList();
                if (drawn.Count == 0) return;

                // Scaled to what is on screen, not to the whole record: zooming into a ripple
                // riding on a 5V level is the reason to zoom at all, and a fixed vertical
                // scale would leave it a flat line at the top of the plot.
                //
                // One scale across every channel drawn, not one each: two traces drawn to two
                // different scales look like a comparison and are not one. Across the channels
                // on this card, not across the capture — putting two channels on separate cards
                // is how you ask to see each on its own terms.
                double vmin = double.MaxValue, vmax = double.MinValue;
                foreach (ChannelRead c in drawn)
                    for (int i = first; i <= last; i++)
                    {
                        double v = c.Capture!.Samples[i].Voltage;
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
                string t0 = $"{axis[first].Time:g3} s";
                string t1 = $"{axis[last].Time:g3} s";
                g.DrawString(t0, f, lbl, MarginLeft, MarginTop + ph + 6);
                SizeF t1Size = g.MeasureString(t1, f);
                g.DrawString(t1, f, lbl, MarginLeft + pw - t1Size.Width, MarginTop + ph + 6);

                // What you are looking at, and how to move it. Drawn whether or not it is
                // zoomed: a plot that only mentions the wheel once you have already found the
                // wheel is a plot that never mentions it. The reset is only worth naming when
                // there is something to reset.
                string note = _view.IsWholeRecord
                    ? "whole record — scroll to move, Ctrl+scroll to zoom"
                    : $"{_view.Span:P1} of the record — scroll to move, Ctrl+scroll to zoom, "
                    + "double-click to reset";
                SizeF size = g.MeasureString(note, f);
                using var dim = new SolidBrush(Color.FromArgb(140, 140, 140));
                g.DrawString(note, f, dim, MarginLeft + (pw - size.Width) / 2, MarginTop + ph + 6);

                var pts = new PointF[count];
                foreach (ChannelRead c in drawn)
                {
                    for (int i = 0; i < count; i++)
                    {
                        float x = MarginLeft + (count == 1 ? 0 : (float)i / (count - 1) * pw);
                        float y = MarginTop + (float)((vmax - c.Capture!.Samples[first + i].Voltage) / vspan * ph);
                        pts[i] = new PointF(x, y);
                    }
                    using var trace = new Pen(ChannelInk(c.Channel), 1.3f);
                    if (count > 1) g.DrawLines(trace, pts);
                }

                // Which colour is which channel, where the colours are. Only worth saying when
                // there is more than one of them.
                if (drawn.Count > 1)
                {
                    float lx = MarginLeft + 4;
                    foreach (ChannelRead c in drawn)
                    {
                        string name = "CH" + c.Channel;
                        using var ink = new SolidBrush(ChannelInk(c.Channel));
                        g.DrawString(name, f, ink, lx, MarginTop + 2);
                        lx += g.MeasureString(name, f).Width + 10;
                    }
                }
            }
        }
    }
}
