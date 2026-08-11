using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace LabEquipmentController
{
    /// <summary>Shows a captured instrument screenshot, with Save-as-image and Copy actions.</summary>
    public sealed class ScreenCaptureForm : Form
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
        private readonly Image _image;
        private readonly FlowLayoutPanel _bar;
        private readonly Button _save;
        private readonly Button _copy;

        public ScreenCaptureForm(Image image, string instrument)
        {
            _image = image;

            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9f);   // the app's font, so this window scales like the rest
            Text = "Screen Capture — " + instrument;
            StartPosition = FormStartPosition.CenterParent;

            var pic = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = image,
                BackColor = Color.Black,
            };

            // Auto-sized, never a fixed height: the buttons grow with the display scale and
            // a hardcoded bar (it was 40px) clips them.
            _bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(6),
                WrapContents = false,
            };
            FlowLayoutPanel bar = _bar;
            _save = new Button();
            ButtonStyle.Apply(_save, "Save as PNG…", (_, _) => SaveImage());
            _copy = new Button();
            ButtonStyle.Apply(_copy, "Copy",
                (_, _) => { try { Clipboard.SetImage(_image); } catch { /* clipboard busy */ } });
            Button save = _save, copy = _copy;
            bar.Controls.Add(save);
            bar.Controls.Add(copy);

            // Fill first, then docked edges (this project's convention for docking order).
            // Added the other way round the picture took the whole client area and the button
            // strip was drawn over its bottom edge, hiding that band of the screenshot.
            Controls.Add(pic);
            Controls.Add(bar);

            // Every control gets a tooltip describing what it does.
            var tips = new ToolTip { AutoPopDelay = 15000 };
            tips.SetToolTip(pic, "Screen captured from the instrument.");
            tips.SetToolTip(save, "Save this screenshot as an image file.");
            tips.SetToolTip(copy, "Copy this screenshot to the clipboard.");
        }

        /// <summary>
        /// Fit the window to the image once the button bar has taken its real, font-driven
        /// height. Reserving a fixed number of pixels for the bar in the constructor left
        /// the buttons half off the bottom edge as soon as the display scale rose.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            SetButtonIcons();   // before the bar is measured — a glyph makes a button taller

            // Twice the image, and twice the bounds it is held between. Doubling only the
            // bounds would not have shown a larger picture: the PictureBox zooms to fit and
            // keeps the aspect ratio, so an 800x480 screenshot in a 960x480 window is still
            // drawn 800x480 with black either side — the height is what constrains it.
            int w = Math.Clamp(_image.Width * 2, LogicalToDeviceUnits(960), LogicalToDeviceUnits(2000));
            int h = Math.Clamp(_image.Height * 2, LogicalToDeviceUnits(480), LogicalToDeviceUnits(1280));

            MinimumSize = new Size(LogicalToDeviceUnits(360), LogicalToDeviceUnits(200) + _bar.Height);
            ClientSize = new Size(w, h + _bar.Height);

            // Never bigger than the screen it opens on, as elsewhere: a window taller than
            // the desktop puts Save and Copy out of reach.
            Rectangle work = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Math.Max(Width, MinimumSize.Width), work.Width),
                            Math.Min(Math.Max(Height, MinimumSize.Height), work.Height));
        }

        /// <summary>
        /// Called from OnLoad, once the handle exists and the display scale is known.
        /// Copy has no bundled artwork, so it is drawn — see <see cref="AppIcons.Drawn"/>.
        /// </summary>
        private void SetButtonIcons()
        {
            ButtonStyle.SetDrawnIcon(this, _save, "save");
            ButtonStyle.SetDrawnIcon(this, _copy, "copy");
            ButtonStyle.Normalize(this, _save, _copy);
        }

        private void SaveImage()
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Save screenshot",
                Filter = "PNG image (*.png)|*.png|BMP image (*.bmp)|*.bmp",
                FileName = "screen-capture.png",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                ImageFormat fmt = dlg.FileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
                    ? ImageFormat.Bmp : ImageFormat.Png;
                _image.Save(dlg.FileName, fmt);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the image:\n" + ex.Message, "Save",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _image?.Dispose();
            base.Dispose(disposing);
        }
    }
}
