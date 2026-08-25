using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LabEquipmentController
{
    /// <summary>
    /// The About box, opened from Help ▸ About. Everything on it is read at runtime — the
    /// version from the assembly, the runtime from the framework, the catalog totals by
    /// counting the embedded catalogs — so nothing here can quietly go stale the way a
    /// hand-written figure does.
    /// </summary>
    public sealed class AboutForm : Form
    {
        public AboutForm()
        {
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9f);
            Text = "About Lab Equipment Controller";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 2,
                // Roomier than it was: the OK button that used to sit under this went away
                // with the redundant buttons, and the text was left running close to the
                // window edge on three sides with nothing between it and the frame.
                Padding = new Padding(24, 22, 24, 22),
            };
            _body = body;
            body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var icon = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(64, 64),
                Margin = new Padding(0, 0, 16, 0),
                Image = AppIconImage(),
            };
            body.Controls.Add(icon, 0, 0);

            // Auto-sized, and the window is sized to it in OnLoad. A fixed client size had to
            // be guessed, and guessed short — the runtime and catalog lines fell off the
            // bottom, which is exactly the sort of thing nobody notices in a dialog.
            var text = new Label
            {
                AutoSize = true,
                UseMnemonic = false,   // the text can contain '&' — show it, don't underline
                Text = Blurb(),
                Margin = new Padding(0),
            };
            body.Controls.Add(text, 1, 0);

            // Where the source is, stated as the web build's About box states it — the
            // address and the licence — and here it is a link rather than a line of text,
            // because this is the one of the two builds that can open a browser. An address
            // you have to read off a dialog and retype is an address nobody follows.
            //
            // Left out entirely rather than shown dead if the assembly carries no repository:
            // see AppInfo. Nothing on this box is allowed to be a guess.
            if (AppInfo.Repository.Length > 0)
            {
                var source = new LinkLabel
                {
                    AutoSize = true,
                    UseMnemonic = false,
                    Text = SourceLine(),
                    // The blank line that sets the blurb's own paragraphs apart, so this
                    // reads as the last of them rather than as something bolted underneath.
                    Margin = new Padding(0, 15, 0, 0),
                    LinkBehavior = LinkBehavior.HoverUnderline,
                };

                // Only the address is the link. "Source code:" and the licence beside it are
                // labels, and a licence that looked clickable would be a promise to open
                // something that is not there.
                source.Links.Add(source.Text.IndexOf(AppInfo.Repository, StringComparison.Ordinal),
                                 AppInfo.Repository.Length, AppInfo.Repository);
                source.LinkClicked += (_, e) => Open(e.Link?.LinkData as string);
                body.Controls.Add(source, 1, 1);
            }

            // No OK button. This box states facts and takes no decision, so there is
            // nothing for one to confirm — the title bar closes it, and so does Esc.
            Controls.Add(body);
        }

        private readonly TableLayoutPanel _body;

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
            ClientSize = _body.PreferredSize;   // then fit the window to what it holds
        }

        /// <summary>The app's own icon, or null — a missing icon must not break the dialog.</summary>
        private static Image? AppIconImage()
        {
            try
            {
                using var s = typeof(AboutForm).Assembly.GetManifestResourceStream("app.ico");
                if (s == null) return null;
                using var ico = new Icon(s, 64, 64);
                return ico.ToBitmap();
            }
            catch { return null; }
        }

        /// <summary>
        /// The repository, and the licence it is published under.
        ///
        /// Both off the assembly rather than written here — the same two figures the web
        /// build's About box and the NuGet listing state, from the same place, so the three
        /// cannot drift apart. See AppInfo.
        /// </summary>
        private static string SourceLine()
        {
            string licence = AppInfo.License;
            return "Source code:  " + AppInfo.Repository
                 + (licence.Length > 0 ? "    " + licence + " licence" : "");
        }

        /// <summary>
        /// Hand an address to whatever this machine opens links with.
        ///
        /// UseShellExecute, because without it a .NET process is asked to execute the URL as
        /// a program and refuses. A machine with no browser registered, or a shell that says
        /// no, is not worth an error dialog on top of an About box: the address is on screen
        /// either way, which is the thing this row is for.
        /// </summary>
        private static void Open(string? url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* no browser, or the shell refused */ }
        }

        private static string Blurb()
        {
            Assembly asm = typeof(AboutForm).Assembly;
            string version = asm.GetName().Version?.ToString(3) ?? "—";

            return "Lab Equipment Controller\r\n"
                 + "Version " + version + "\r\n\r\n"
                 + "Discover and control lab instruments over Ethernet using SCPI.\r\n"
                 + "Raw TCP sockets and native VXI-11, several instruments at once.\r\n\r\n"
                 + CatalogLine() + "\r\n\r\n"
                 + RuntimeInformation.FrameworkDescription + "\r\n"
                 + RuntimeInformation.OSDescription;
        }

        /// <summary>
        /// Count what is actually embedded rather than quoting a number from the README.
        /// Runs once, when the dialog opens; the catalogs are parsed and cached by
        /// <see cref="CommandReference"/> anyway.
        /// </summary>
        private static string CatalogLine()
        {
            int families = 0, commands = 0;
            foreach (InstrumentFamily f in Enum.GetValues<InstrumentFamily>())
            {
                CommandReference? r = CommandReference.ForFamily(f);
                if (r == null || r.Commands.Count == 0) continue;
                families++;
                commands += r.Commands.Count;
            }
            return $"{commands:N0} SCPI commands catalogued across {families} instrument families,\r\n"
                 + "transcribed from vendor programming guides.";
        }
    }
}
