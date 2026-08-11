using System;
using System.Drawing;
using System.Windows.Forms;

namespace LabEquipmentController
{
    /// <summary>
    /// Where the user sets up their own AI connection: provider, endpoint, model and key.
    ///
    /// The key box shows dots and is never written back into the settings file in the clear —
    /// <see cref="SecretStore"/> encrypts it for this Windows user first.
    /// </summary>
    public sealed class AiSettingsForm : Form
    {
        private readonly ComboBox _provider = new();
        private readonly TextBox _baseUrl = new();
        private readonly TextBox _model = new();
        private readonly TextBox _key = new();
        private readonly NumericUpDown _timeout = new();
        private readonly CheckBox _extractLocally = new();
        private readonly Label _providerNote = new();
        private readonly Button _ok = new();
        private readonly ToolTip _tips = new();

        /// <summary>The edited connection, valid once the dialog returns OK.</summary>
        public AiConnection Connection { get; private set; }

        /// <summary>The key as typed. Empty means "leave whatever was stored alone".</summary>
        public string ApiKey => _key.Text.Trim();

        public AiSettingsForm(AiConnection? existing, string? existingKey)
        {
            Connection = existing?.Clone() ?? new AiConnection();

            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9f);
            Text = "AI Connection";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(660, 400);

            BuildUi(existingKey);
            LoadFrom(Connection);
        }

        private TableLayoutPanel _grid = null!;

        private void BuildUi(string? existingKey)
        {
            _grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(14),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            foreach (AiProviderInfo info in AiProviderInfo.Known) _provider.Items.Add(info.Label);
            _provider.DropDownStyle = ComboBoxStyle.DropDownList;
            _provider.DrawMode = DrawMode.OwnerDrawFixed;
            _provider.DrawItem += (_, e) => ButtonStyle.DrawComboItem(_provider, e);
            _provider.Dock = DockStyle.Fill;
            _provider.SelectedIndexChanged += OnProviderChanged;

            _baseUrl.Dock = DockStyle.Fill;
            _model.Dock = DockStyle.Fill;

            _key.Dock = DockStyle.Fill;
            _key.UseSystemPasswordChar = true;
            _key.PlaceholderText = existingKey is { Length: > 0 }
                ? "A key is stored. Type to replace it."
                : "Paste your API key";

            _timeout.Minimum = 15;
            _timeout.Maximum = 900;
            _timeout.Value = 300;
            _timeout.Width = 90;

            AddRow("Provider:", _provider);
            AddRow("Endpoint:", _baseUrl);
            AddRow("Model:", _model);
            AddRow("API key:", _key);
            AddRow("Timeout (s):", _timeout);

            // The checkbox the whole PDF question hangs on. Its hover text explains what it
            // does, why it exists, and what it costs — composed per provider in Core.
            _extractLocally.Text = "Extract text locally before sending";
            _extractLocally.AutoSize = true;
            _extractLocally.Margin = new Padding(0, 8, 0, 2);
            _extractLocally.CheckedChanged += (_, _) =>
            {
                // Once touched it is an explicit choice and stops following the provider.
                if (_extractLocally.Enabled) Connection.ExtractTextLocally = _extractLocally.Checked;
            };
            // Labelled like every other row. It was the one control in the column with a
            // blank label beside it, which left the checkbox floating clear of the grid.
            AddRow("PDF extraction:", _extractLocally);

            // Auto-sized and width-capped so the note wraps rather than being cut off; the
            // window is then fitted to it in OnLoad. A fixed height had to be guessed, and
            // guessed short — the cost explanation lost its last line.
            _providerNote.AutoSize = true;
            _providerNote.MaximumSize = new Size(470, 0);
            _providerNote.ForeColor = SystemColors.GrayText;
            _providerNote.Margin = new Padding(0, 2, 0, 6);
            AddRow("", _providerNote);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(14, 6, 14, 12),
            };
            // "Apply", not "OK": this window has no Cancel to be the other half of a pair, and
            // the button's job is to commit what has been typed, which is what it should say.
            ButtonStyle.Apply(_ok, "Apply", (_, _) => Accept());
            // No Cancel button. The title bar's X already does exactly that, and this was the
            // last window in the app still carrying a second way to do it. Apply stays: it is
            // not another way of dismissing the window, it is the one that commits the
            // settings — which is why it says Apply rather than OK.
            buttons.Controls.Add(_ok);

            Controls.Add(_grid);
            Controls.Add(buttons);

            AcceptButton = _ok;
            // Esc still closes without saving — that is what CancelButton was buying, and it
            // does not need a button on screen to keep doing it.
            CancelButton = null;

            _tips.AutoPopDelay = 30000;   // the checkbox text is three paragraphs; give it time
            _tips.SetToolTip(_provider, "Which service to use. The endpoint and model below "
                                      + "are filled with that provider's defaults.");
            _tips.SetToolTip(_baseUrl, "Scheme and host only. Leave blank for the provider's "
                                     + "own endpoint; set it for a local server or a proxy.");
            _tips.SetToolTip(_model, "Model name as the provider spells it.");
            _tips.SetToolTip(_key, "Stored encrypted for your Windows account, not in plain "
                                 + "text. It never leaves this machine except to the provider.");
            _tips.SetToolTip(_timeout, "How long to wait for one request. A whole programming "
                                     + "guide takes longer than a page.");
        }

        /// <summary>
        /// Esc closes without saving. That is what <c>CancelButton</c> did while there was a
        /// Cancel button for it to point at; removing the button removed the behaviour with
        /// it, which is the same trap as the windows whose OK buttons went earlier.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void AddRow(string label, Control field)
        {
            var l = new Label
            {
                Text = label,
                AutoSize = true,
                Margin = new Padding(0, 8, 10, 0),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            int row = _grid.RowCount++;
            _grid.Controls.Add(l, 0, row);
            _grid.Controls.Add(field, 1, row);
            field.Margin = new Padding(0, 5, 0, 5);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            int h = ButtonStyle.Normalize(this, _ok);
            ButtonStyle.MatchHeight(_provider, h);

            // Fit the window to what it holds, so the provider note is never cut off.
            Size wanted = _grid.PreferredSize;
            ClientSize = new Size(Math.Max(wanted.Width, 620),
                                  wanted.Height + _ok.Height + LogicalToDeviceUnits(30));
        }

        // ------------------------------------------------------------------ provider

        private void OnProviderChanged(object? sender, EventArgs e)
        {
            if (_provider.SelectedIndex < 0) return;
            AiProviderInfo info = AiProviderInfo.Known[_provider.SelectedIndex];

            Connection.Provider = info.Provider;

            // A new provider means new defaults, so the checkbox goes back to following them
            // rather than carrying an answer that was right for a different service.
            Connection.ExtractTextLocally = null;

            if (IsPlaceholderUrl(_baseUrl.Text)) _baseUrl.Text = info.DefaultBaseUrl;
            if (IsPlaceholderModel(_model.Text)) _model.Text = info.DefaultModel;

            ApplyProviderRules();
        }

        private static bool IsPlaceholderUrl(string url)
            => string.IsNullOrWhiteSpace(url)
            || Array.Exists(AiProviderInfo.Known.ToArray(), p => p.DefaultBaseUrl == url.Trim());

        private static bool IsPlaceholderModel(string model)
            => string.IsNullOrWhiteSpace(model)
            || Array.Exists(AiProviderInfo.Known.ToArray(), p => p.DefaultModel == model.Trim());

        /// <summary>
        /// Reflect what this provider allows: where it cannot take a file the checkbox is
        /// ticked and disabled, because there is no other way to send a PDF and offering the
        /// choice would be a lie.
        /// </summary>
        private void ApplyProviderRules()
        {
            AiProviderInfo info = Connection.Info;

            _extractLocally.Enabled = info.SupportsPdfUpload;
            _extractLocally.Checked = Connection.EffectiveExtractTextLocally;

            _providerNote.Text = info.PdfCostNote;
            _tips.SetToolTip(_extractLocally, Connection.LocalExtractionHelp);
            _tips.SetToolTip(_providerNote, Connection.LocalExtractionHelp);
        }

        // ---------------------------------------------------------------------- load/save

        private void LoadFrom(AiConnection cn)
        {
            for (int i = 0; i < AiProviderInfo.Known.Count; i++)
            {
                if (AiProviderInfo.Known[i].Provider == cn.Provider) { _provider.SelectedIndex = i; break; }
            }
            if (_provider.SelectedIndex < 0) _provider.SelectedIndex = 0;

            _baseUrl.Text = string.IsNullOrWhiteSpace(cn.BaseUrl) ? cn.Info.DefaultBaseUrl : cn.BaseUrl;
            _model.Text = string.IsNullOrWhiteSpace(cn.Model) ? cn.Info.DefaultModel : cn.Model;
            _timeout.Value = Math.Clamp(cn.TimeoutSeconds, (int)_timeout.Minimum, (int)_timeout.Maximum);

            ApplyProviderRules();
        }

        private void Accept()
        {
            Connection.BaseUrl = _baseUrl.Text.Trim();
            Connection.Model = _model.Text.Trim();
            Connection.TimeoutSeconds = (int)_timeout.Value;

            if (!Uri.TryCreate(Connection.EffectiveBaseUrl, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show(this,
                    "The endpoint needs to be a full http or https address, for example "
                  + "https://api.openai.com.", "AI Connection",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}


