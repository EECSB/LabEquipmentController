using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;

namespace LabEquipmentController
{
    /// <summary>
    /// One half of a segmented switch: a radio button drawn as a flat segment, sharing a
    /// hairline with the half beside it.
    ///
    /// A <see cref="RadioButton"/> underneath, and that is not an implementation detail — it
    /// is what keeps the halves mutually exclusive, walkable with the arrow keys, and named
    /// as one group to a screen reader. Two ordinary buttons would be two controls that
    /// happen to agree.
    ///
    /// Drawn here rather than by <c>Appearance.Button</c> with a <c>FlatAppearance</c> for
    /// one reason that could not be worked around: WinForms centres the *line box*, and a
    /// line box hangs the whole descent below the baseline. Neither of the captions this
    /// carries has a descender, so that space is dead and the words sat two pixels low —
    /// enough to read as bottom-aligned. <see cref="Padding"/> is ignored by the flat
    /// renderer, so there was nowhere to put the correction. Here there is: see
    /// <see cref="Lift"/>.
    ///
    /// Painting it also puts the fill, the border and the hover state in one place, instead
    /// of spread across four <c>FlatAppearance</c> properties and a handler that had to keep
    /// the foreground in step with them.
    /// </summary>
    internal sealed class SegmentButton : RadioButton
    {
        private bool _hot;

        public SegmentButton()
        {
            SetStyle(ControlStyles.UserPaint
                   | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw, true);

            Appearance = Appearance.Button;
            AutoSize = false;
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
        }

        /// <summary>
        /// The filled half: a grey mixed from the two the window is already built out of, so
        /// the selection reads as part of the chrome rather than as an alert.
        ///
        /// It was the system accent for a while, on the argument that a desktop app can read
        /// the colour Windows was told to use where a page cannot — true, and beside the
        /// point: nothing else in this window is accent coloured, so an accent-filled switch
        /// was the one thing on screen pulling the eye, and what it pulled it towards is a
        /// heading. Then it was <c>ControlDark</c> flat, which was the opposite problem: a
        /// heading in a dark box reads as a warning.
        ///
        /// Mixed rather than picked because there is no system colour between the border grey
        /// and the face grey, and this wants to sit between them: dark enough that the chosen
        /// half is obvious at a glance, light enough to be a heading.
        /// </summary>
        private static Color Fill => Mix(SystemColors.ControlDark, SystemColors.Control, 0.45f);

        /// <summary>
        /// <paramref name="weight"/> of <paramref name="a"/> and the rest of
        /// <paramref name="b"/>. Alpha is left at <paramref name="a"/>'s; nothing here is
        /// translucent.
        /// </summary>
        private static Color Mix(Color a, Color b, float weight)
        {
            float rest = 1f - weight;
            return Color.FromArgb(
                a.A,
                (int)Math.Round(a.R * weight + b.R * rest),
                (int)Math.Round(a.G * weight + b.G * rest),
                (int)Math.Round(a.B * weight + b.B * rest));
        }

        /// <summary>
        /// What Windows itself paints a button with the pointer over it — the blue wash and
        /// the blue edge every other button in this window gets, asked of the theme rather
        /// than guessed at, so the switch cannot drift away from them.
        ///
        /// Read by rendering one hot button into a bitmap and sampling it, because the theme
        /// exposes these as a drawing operation and not as two colours. Once per theme: the
        /// answer only changes when Windows changes it, and
        /// <see cref="OnSystemColorsChanged"/> throws it away when that happens.
        ///
        /// The fallbacks are for a session with visual styles off, where there is no blue to
        /// match and the flat grey is what the rest of the window looks like too.
        /// </summary>
        private static Color? _hotFill;
        private static Color? _hotBorder;

        private static void ReadHotColours()
        {
            if (_hotFill is not null) return;

            _hotFill = SystemColors.ControlLight;
            _hotBorder = SystemColors.ControlDark;

            if (!Application.RenderWithVisualStyles) return;

            try
            {
                using var probe = new Bitmap(64, 32);
                using (Graphics g = Graphics.FromImage(probe))
                {
                    g.Clear(SystemColors.Control);
                    ButtonRenderer.DrawButton(g, new Rectangle(0, 0, 64, 32), PushButtonState.Hot);
                }

                // Mid-face for the wash. Halfway along, so the rounded corners are nowhere
                // near it.
                _hotFill = probe.GetPixel(32, 16);

                // The edge is the darkest thing down that same column: the theme insets its
                // border by a pixel or two and only it knows by how much, so this looks for
                // the line rather than assuming where it starts.
                Color darkest = _hotFill.Value;
                for (int y = 0; y < 6; y++)
                {
                    Color at = probe.GetPixel(32, y);
                    if (at.GetBrightness() < darkest.GetBrightness()) darkest = at;
                }

                _hotBorder = darkest;
            }
            catch (Exception)
            {
                // A theme that will not render leaves the flat greys above in place.
            }
        }

        /// <summary>
        /// How far to raise the text so that what you see is centred.
        ///
        /// Centring puts the middle of the *line box* in the middle of the button, and the
        /// line box is not the letters: it reserves room under the baseline for descenders
        /// these captions do not have. So the ink lands low — measured at two pixels below
        /// centre on a 175% display, which is enough to read as bottom-aligned.
        ///
        /// What wants centring is the block from the cap tops to the baseline. Its middle
        /// sits <c>ascent − lineHeight/2 − capHeight/2</c> below the line box's middle, so
        /// that is how far up to move it. Every term comes from the font, so this holds at
        /// any size and any display scale rather than being a constant that is right on one
        /// machine.
        /// </summary>
        private int Lift
        {
            get
            {
                FontFamily family = Font.FontFamily;
                float spacing = family.GetLineSpacing(Font.Style);
                if (spacing <= 0) return 0;

                float lineHeight = TextRenderer.MeasureText("Ay", Font).Height;
                float ascent = lineHeight * family.GetCellAscent(Font.Style) / spacing;
                float em = lineHeight * family.GetEmHeight(Font.Style) / spacing;

                // Cap height is the one term GDI+ does not expose. Seven tenths of the em is
                // what the faces this app runs under actually measure — Segoe UI, and Tahoma
                // behind it on an older Windows — and being a tenth out here costs a pixel.
                float cap = em * 0.70f;

                return (int)Math.Round(ascent - (lineHeight / 2f) - (cap / 2f));
            }
        }

        /// <summary>
        /// The size this segment wants: its own caption plus a little air either side.
        ///
        /// Each half takes its own width, so the halves are uneven and that is the intent —
        /// matched to the wider of the two, a switch is a block of empty space beside the
        /// shorter caption.
        /// </summary>
        public Size Measure(int padWidth, int padHeight)
        {
            Size text = TextRenderer.MeasureText(Text, Font);
            return new Size(text.Width + padWidth,
                            TextRenderer.MeasureText("Ay", Font).Height + padHeight);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hot = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hot = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        // Checking and focusing both change what is drawn, and neither raises Paint on its own.
        protected override void OnCheckedChanged(EventArgs e)
        {
            Invalidate();
            base.OnCheckedChanged(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Invalidate();
            base.OnLostFocus(e);
        }

        /// <summary>
        /// Windows changed theme, so the hot colours sampled from the old one are wrong.
        /// Thrown away rather than recomputed here: the next paint asks again.
        /// </summary>
        protected override void OnSystemColorsChanged(EventArgs e)
        {
            _hotFill = null;
            _hotBorder = null;
            Invalidate();
            base.OnSystemColorsChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ReadHotColours();

            // The selected half is filled; an unselected one is the colour of a box you can
            // type in, so the pair reads as one control with a chosen side.
            //
            // Hover only lifts the unselected half, and lifts it the way Windows lifts every
            // other button in this window: the theme's own wash and edge. Lightening the
            // selected one would make it look unselected at the exact moment somebody is
            // about to click it.
            bool hot = _hot && Enabled && !Checked;

            Color fill = Checked ? Fill : hot ? _hotFill!.Value : SystemColors.Window;
            Color edge = hot ? _hotBorder!.Value : SystemColors.ControlDark;

            using (var brush = new SolidBrush(fill))
                g.FillRectangle(brush, ClientRectangle);

            using (var pen = new Pen(edge))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

            Rectangle text = ClientRectangle;
            text.Offset(0, -Lift);

            TextRenderer.DrawText(
                g, Text, Font, text,
                Enabled ? SystemColors.ControlText : SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

            // Only when the keyboard put it there. A dotted rectangle under the pointer is
            // noise; the same rectangle after Tab is the only thing saying where you are.
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -3, -3));
        }
    }
}
