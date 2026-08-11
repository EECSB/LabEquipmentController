using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LabEquipmentController
{
    /// <summary>
    /// Reads an instrument datasheet with the user's AI connection and offers up the commands
    /// it found for review.
    ///
    /// Nothing is saved until the user has looked at the list and ticked what to keep. That
    /// review step is not politeness — it is the only thing standing between a model's guess
    /// and a catalog the rest of the app treats as fact (SPEC section 10).
    /// </summary>
    public sealed class DatasheetExtractForm : Form
    {
        private readonly AiConnection _connection;
        private readonly string _apiKey;
        private readonly string _instrumentKey;
        private readonly string _instrumentTitle;

        private readonly TextBox _path = new();
        private readonly Button _browse = new();
        private readonly Label _provider = new();
        private readonly CheckBox _extractLocally = new();
        private readonly Button _extract = new();
        private readonly Button _save = new();
        private readonly ProgressBar _progress = new();
        private readonly Label _status = new();
        private readonly ListView _list = new();
        private readonly ToolTip _tips = new();

        private CancellationTokenSource? _cts;
        private IReadOnlyList<CommandRef> _found = Array.Empty<CommandRef>();

        /// <summary>True when commands were saved, so the caller can reload its reference.</summary>
        public bool Saved { get; private set; }

        public DatasheetExtractForm(AiConnection connection, string apiKey,
                                    string instrumentKey, string instrumentTitle)
        {
            _connection = connection.Clone();
            _apiKey = apiKey;
            _instrumentKey = instrumentKey;
            _instrumentTitle = instrumentTitle;

            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9f);
            Text = "AI Datasheet Extraction — " + instrumentTitle;
            ClientSize = new Size(980, 620);
            MinimumSize = new Size(720, 460);
            StartPosition = FormStartPosition.CenterParent;

            BuildUi();
            ApplyRules();
        }

        /// <summary>
        /// Esc closes it. Nothing is committed on the way out — Save Ticked is the only
        /// thing that writes anything — so leaving costs nothing.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void BuildUi()
        {
            var top = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 3,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 12, 12, 6),
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var lblFile = new Label { Text = "Datasheet:", AutoSize = true, Margin = new Padding(0, 8, 10, 0) };
            _path.Dock = DockStyle.Fill;
            _path.PlaceholderText = "PDF, Word or text file";
            _path.TextChanged += (_, _) => ApplyRules();
            ButtonStyle.Apply(_browse, "Browse…", (_, _) => Browse());

            top.Controls.Add(lblFile, 0, 0);
            top.Controls.Add(_path, 1, 0);
            top.Controls.Add(_browse, 2, 0);

            _provider.AutoSize = true;
            _provider.ForeColor = SystemColors.GrayText;
            _provider.Margin = new Padding(0, 8, 0, 0);
            top.Controls.Add(new Label { Text = "Using:", AutoSize = true, Margin = new Padding(0, 8, 10, 0) }, 0, 1);
            top.Controls.Add(_provider, 1, 1);

            _extractLocally.Text = "Extract text locally before sending";
            _extractLocally.AutoSize = true;
            _extractLocally.Margin = new Padding(0, 6, 0, 6);
            // Ticking this sidesteps the upload caps entirely, so a size refusal has to clear
            // when it goes on. Guarded because ApplyRules sets Checked itself.
            _extractLocally.CheckedChanged += (_, _) => { if (!_updating) ApplyRules(); };
            top.Controls.Add(_extractLocally, 1, 2);

            ButtonStyle.Apply(_extract, "Extract", async (_, _) => await RunAsync());
            top.Controls.Add(_extract, 2, 2);

            // --- results ---
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.CheckBoxes = true;
            _list.FullRowSelect = true;
            _list.GridLines = true;
            _list.Columns.Add("Command", 300);
            _list.Columns.Add("Category", 150);
            _list.Columns.Add("Description", 460);

            var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
            listHost.Controls.Add(_list);

            // --- bottom ---
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 6, 12, 12),
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _progress.Dock = DockStyle.Fill;
            _progress.Height = 8;
            _progress.Visible = false;
            _status.AutoSize = false;
            _status.Dock = DockStyle.Fill;
            _status.UseMnemonic = false;   // refusals quote file names, which may contain '&'
            _status.Text = "Pick a datasheet, then Extract.";

            var left = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty,
            };
            left.Controls.Add(_status, 0, 0);
            left.Controls.Add(_progress, 0, 1);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,   // else the pair stacks vertically in its cell
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty,
            };
            ButtonStyle.Apply(_save, "Save Ticked", (_, _) => SaveTicked());
            _save.Enabled = false;
            buttons.Controls.Add(_save);

            bottom.Controls.Add(left, 0, 0);
            bottom.Controls.Add(buttons, 1, 0);

            Controls.Add(listHost);
            Controls.Add(bottom);
            Controls.Add(top);

            _tips.AutoPopDelay = 30000;
            _tips.SetToolTip(_path, "The instrument's programming guide or datasheet.");
            _tips.SetToolTip(_list, "Everything the model found. Untick anything that looks "
                                  + "wrong — these are extracted, not verified.");
            _tips.SetToolTip(_save, "Save the ticked commands for this instrument. They are "
                                  + "kept separately from the built-in catalogs and always "
                                  + "shown as AI-extracted.");
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ButtonStyle.SetDrawnIcon(this, _browse, "folder");
            ButtonStyle.SetIcon(this, _extract, "program");
            ButtonStyle.SetDrawnIcon(this, _save, "save");
            int h = ButtonStyle.Normalize(this, _browse, _extract, _save);
            ButtonStyle.CentreInRow(_path, h);

            // Measured from the font at this DPI, never a fixed number (SPEC §14). A refusal
            // runs to two lines, and a guessed height cut the descenders off the first one.
            _status.Height = TextRenderer.MeasureText("Ag", _status.Font).Height * 2
                           + LogicalToDeviceUnits(6);
        }

        // ------------------------------------------------------------------------- rules

        /// <summary>
        /// Keep the checkbox honest about the current provider and file. It only means
        /// anything for a PDF, and only where the provider could have taken the file itself.
        /// </summary>
        /// <summary>Set while ApplyRules drives the checkbox, so it does not re-enter itself.</summary>
        private bool _updating;

        private void ApplyRules()
        {
            if (_updating) return;
            _updating = true;
            try { ApplyRulesCore(); }
            finally { _updating = false; }
        }

        private void ApplyRulesCore()
        {
            _provider.Text = $"{_connection.Info.Label} · {_connection.EffectiveModel}";

            string path = _path.Text.Trim();
            bool chosen = path.Length > 0;
            bool isPdf = chosen && DocumentText.IsPdf(path);
            bool canChoose = isPdf && _connection.CanSendPdfDirectly;

            _extractLocally.Enabled = canChoose;

            // Before a file is picked the box shows what the connection would do, rather than
            // a tick that would imply this file is going to be flattened when it may not be.
            _extractLocally.Checked = !chosen
                ? _connection.EffectiveExtractTextLocally
                : !isPdf || _connection.EffectiveExtractTextLocally;

            string help = _connection.LocalExtractionHelp;
            if (chosen && !isPdf)
                help = "This file is not a PDF, so it is always read here and sent as text.\r\n\r\n" + help;
            _tips.SetToolTip(_extractLocally, help);

            _extract.Enabled = chosen && File.Exists(path) && _cts == null;
            WarnIfTooBigToUpload(path, isPdf);
        }

        /// <summary>
        /// Say up front when a PDF is too large to upload, rather than letting the user wait
        /// for the extractor to refuse it.
        ///
        /// Size only — the page count means opening the PDF, and this runs on every keystroke
        /// in the path box. The full check, pages included, still runs in the extractor.
        /// </summary>
        private void WarnIfTooBigToUpload(string path, bool isPdf)
        {
            if (_cts != null) return;   // mid-run: the status line belongs to progress

            bool uploading = isPdf && !_extractLocally.Checked;
            if (!uploading || !File.Exists(path))
            {
                if (_status.Text.StartsWith('“')) _status.Text = "Pick a datasheet, then Extract.";
                return;
            }

            string? refusal = AiUploadLimits.Check(
                _connection.Info, Path.GetFileName(path), new FileInfo(path).Length, pages: 0);

            if (refusal != null) _status.Text = refusal;
            else if (_status.Text.StartsWith('“')) _status.Text = "Ready to extract.";
        }

        private void Browse()
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Choose a datasheet",
                Filter = "Datasheets (*.pdf;*.docx;*.txt;*.md)|*.pdf;*.docx;*.txt;*.md"
                       + "|PDF (*.pdf)|*.pdf|Word (*.docx)|*.docx|Text (*.txt;*.md)|*.txt;*.md"
                       + "|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) _path.Text = dlg.FileName;
        }

        // --------------------------------------------------------------------- extraction

        private async Task RunAsync()
        {
            if (_cts != null) { _cts.Cancel(); return; }

            string path = _path.Text.Trim();
            if (!File.Exists(path)) return;

            // The per-extraction choice overrides what the connection stores, so a user can
            // send one file each way without going back to settings.
            AiConnection cn = _connection.Clone();
            if (_extractLocally.Enabled) cn.ExtractTextLocally = _extractLocally.Checked;

            _cts = new CancellationTokenSource();
            _extract.Text = "Stop";
            _save.Enabled = false;
            _list.Items.Clear();
            _progress.Visible = true;
            _progress.Style = ProgressBarStyle.Marquee;
            ApplyRules();

            var progress = new Progress<ExtractionProgress>(p =>
            {
                _status.Text = p.OfChunks > 1
                    ? $"{p.Stage}  ({p.FoundSoFar} found so far)"
                    : p.Stage;
                if (p.OfChunks > 1)
                {
                    _progress.Style = ProgressBarStyle.Continuous;
                    _progress.Maximum = p.OfChunks;
                    _progress.Value = Math.Min(p.Chunk, p.OfChunks);
                }
            });

            try
            {
                var extractor = new CommandExtractor(new AiClient());
                ExtractionResult result = await extractor.ExtractAsync(
                    cn, _apiKey, path, progress, _cts.Token);

                _found = result.Commands;
                Show(result);
            }
            catch (OperationCanceledException)
            {
                _status.Text = "Stopped.";
            }
            catch (Exception ex)
            {
                _status.Text = "Extraction failed.";
                MessageBox.Show(this, ex.Message, "AI Datasheet Extraction",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _extract.Text = "Extract";
                _progress.Visible = false;
                ApplyRules();
            }
        }

        private void Show(ExtractionResult result)
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (CommandRef c in result.Commands)
            {
                var item = new ListViewItem(c.Syntax) { Checked = true, Tag = c };
                item.SubItems.Add(c.Category);
                item.SubItems.Add(c.Description);
                _list.Items.Add(item);
            }
            _list.EndUpdate();

            _save.Enabled = result.Commands.Count > 0;

            string dropped = result.Rejected.Count > 0
                ? $"  {result.Rejected.Count} reply line(s) were not valid SCPI and were dropped."
                : "";
            _status.Text = result.Commands.Count == 0
                ? "No commands found in that document." + dropped
                : $"Found {result.Commands.Count} command(s). Untick anything wrong, then Save Ticked."
                  + dropped;
        }

        private void SaveTicked()
        {
            List<CommandRef> keep = _list.CheckedItems.Cast<ListViewItem>()
                .Select(i => (CommandRef)i.Tag!).ToList();

            if (keep.Count == 0)
            {
                MessageBox.Show(this, "Nothing is ticked.", "AI Datasheet Extraction",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                ExtractedCatalogStore.Save(_instrumentKey, new CommandReference
                {
                    Instrument = _instrumentTitle,
                    Source = $"Extracted from {Path.GetFileName(_path.Text.Trim())} by "
                           + $"{_connection.Info.Label} ({_connection.EffectiveModel}). "
                           + "Not verified against the instrument or a vendor guide.",
                    Commands = keep,
                });

                Saved = true;
                _status.Text = $"Saved {keep.Count} command(s) for {_instrumentTitle}.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save:\n" + ex.Message, "AI Datasheet Extraction",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cts?.Cancel();
            base.OnFormClosing(e);
        }
    }
}
