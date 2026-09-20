using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace LabEquipmentController
{
    /// <summary>
    /// Where the user sets up their own AI connections: provider, endpoint, model and key.
    ///
    /// Several of them, because the model is a choice — the cheap fast one that suits reading
    /// commands out of a programming guide is not the one you want writing a sequence — and
    /// every AI window offers the same list. The picker at the top says which connection these
    /// fields are about, and choosing one here chooses it everywhere: there is one selection
    /// for the app, so a box that let you look at a connection without switching to it would be
    /// showing settings that are not the ones in force.
    ///
    /// The key box shows dots and is never written back into the settings file in the clear —
    /// <see cref="SecretStore"/> encrypts it for this Windows user first, one key per
    /// connection.
    /// </summary>
    public sealed class AiSettingsForm : Form
    {
        private readonly ComboBox _connections = new();
        private readonly Button _new = new();
        private readonly Button _delete = new();
        private readonly TextBox _name = new();
        private readonly ComboBox _provider = new();
        private readonly TextBox _baseUrl = new();
        private readonly TextBox _model = new();
        private readonly ComboBox _effort = new();
        private readonly TextBox _key = new();
        private readonly NumericUpDown _timeout = new();
        private readonly CheckBox _extractLocally = new();
        private readonly Label _providerNote = new();
        private readonly Button _ok = new();
        private readonly ToolTip _tips = new();

        /// <summary>The edited connections, valid once the dialog returns OK.</summary>
        public AiConnections Book { get; }

        /// <summary>
        /// The keys as they now stand, one per connection id.
        ///
        /// Not called Keys: inside a Form that is System.Windows.Forms.Keys, and ProcessCmdKey
        /// comparing against Keys.Escape would be comparing against this dictionary.
        /// </summary>
        public Dictionary<string, string> ApiKeys { get; }

        /// <summary>The connection the fields are about: whichever one is selected.</summary>
        private AiConnection Connection => Book.Selected ?? Book.Add();

        /// <summary>Set while the fields are being loaded, so writing them back is not a change.</summary>
        private bool _loading;

        public AiSettingsForm(AiConnections book, IReadOnlyDictionary<string, string> keys)
        {
            Book = book.Clone();
            ApiKeys = new Dictionary<string, string>(keys);
            if (Book.Items.Count == 0) Book.Add();

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

            BuildUi();
            RefreshList();
            LoadFrom(Connection);
        }

        private TableLayoutPanel _grid = null!;

        private void BuildUi()
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
            _baseUrl.TextChanged += (_, _) => Edited(c => c.BaseUrl = _baseUrl.Text.Trim());
            _model.Dock = DockStyle.Fill;
            _model.TextChanged += (_, _) => Edited(c => c.Model = _model.Text.Trim());

            // How hard the model works, in one list rather than three: every provider offers
            // this and every one spells it differently, so the names here are the app's and
            // Core translates them per provider. See AiEffort.
            foreach (AiEffort effort in Enum.GetValues<AiEffort>()) _effort.Items.Add(EffortLabel(effort));
            _effort.DropDownStyle = ComboBoxStyle.DropDownList;
            _effort.DrawMode = DrawMode.OwnerDrawFixed;
            _effort.DrawItem += (_, e) => ButtonStyle.DrawComboItem(_effort, e);
            _effort.Width = 190;
            _effort.SelectedIndexChanged += (_, _) =>
            {
                if (_effort.SelectedIndex >= 0)
                    Connection.Effort = Enum.GetValues<AiEffort>()[_effort.SelectedIndex];
            };

            _key.Dock = DockStyle.Fill;
            _key.UseSystemPasswordChar = true;
            _key.TextChanged += (_, _) =>
            {
                if (_loading) return;
                if (_key.Text.Trim() is { Length: > 0 } typed) ApiKeys[Connection.Id] = typed;
            };

            _timeout.Minimum = 15;
            _timeout.Maximum = 900;
            _timeout.Value = 300;
            _timeout.Width = 90;
            _timeout.ValueChanged += (_, _) => Edited(c => c.TimeoutSeconds = (int)_timeout.Value);

            // Which connection the fields below are about — and, because there is one
            // selection for the app, which one every AI window will spend.
            _connections.DropDownStyle = ComboBoxStyle.DropDownList;
            _connections.DrawMode = DrawMode.OwnerDrawFixed;
            _connections.DrawItem += (_, e) => ButtonStyle.DrawComboItem(_connections, e);
            _connections.Width = 260;
            _connections.SelectedIndexChanged += OnConnectionPicked;

            ButtonStyle.Apply(_new, "New", (_, _) => AddConnection());
            ButtonStyle.Apply(_delete, "Delete", (_, _) => DeleteConnection());

            var picker = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = Padding.Empty,
            };
            _connections.Margin = new Padding(0, 0, 8, 0);
            _new.Margin = new Padding(0, 0, 6, 0);
            _delete.Margin = new Padding(0, 0, 0, 0);
            picker.Controls.Add(_connections);
            picker.Controls.Add(_new);
            picker.Controls.Add(_delete);

            _name.Dock = DockStyle.Fill;
            _name.TextChanged += (_, _) => Edited(c => c.Name = _name.Text.Trim());

            AddRow("Connection:", picker);
            AddRow("Name:", _name);
            AddRow("Provider:", _provider);
            AddRow("Endpoint:", _baseUrl);
            AddRow("Model:", _model);
            AddRow("Effort:", _effort);
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
            _tips.SetToolTip(_connections, "Which connection to edit, and to use. The one "
                                         + "chosen here is the one every AI window spends.");
            _tips.SetToolTip(_new, "Another connection, on this provider's defaults. It "
                                 + "becomes the one in use.");
            _tips.SetToolTip(_delete, "Forget this connection and its key. The others are "
                                    + "left alone.");
            _tips.SetToolTip(_name, "What to call this connection in the pickers. Leave it "
                                  + "blank to be called after the model it reaches.");
            _tips.SetToolTip(_provider, "Which service to use. The endpoint and model below "
                                      + "are filled with that provider's defaults.");
            _tips.SetToolTip(_baseUrl, "Scheme and host only. Leave blank for the provider's "
                                     + "own endpoint; set it for a local server or a proxy.");
            _tips.SetToolTip(_model, "Model name as the provider spells it.");
            _tips.SetToolTip(_effort, "How hard to ask the model to think before it answers.\r\n\r\n"
                                    + "Higher is slower and costs more tokens. Working out what a "
                                    + "sequence of commands ought to be is worth it; copying commands "
                                    + "out of a programming guide is transcription and rarely is.\r\n\r\n"
                                    + "Provider default sends nothing at all, which is the setting to "
                                    + "leave it on for an endpoint that has never heard of the field — "
                                    + "some of them refuse a request carrying one they do not know.");
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
            int h = ButtonStyle.Normalize(this, _ok, _new, _delete);
            ButtonStyle.MatchHeight(_provider, h);
            ButtonStyle.MatchHeight(_effort, h);
            ButtonStyle.MatchHeight(_connections, h);

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

        /// <summary>
        /// What each step is called in the list. Only the first needs saying differently:
        /// "Default" alone reads as a level of effort, which is the one thing it is not. The
        /// script writer and the datasheet window read their pickers' words from here, so the
        /// three places the setting is made cannot call it three things.
        /// </summary>
        internal static string EffortLabel(AiEffort effort)
            => effort == AiEffort.Default ? "Provider default" : effort.ToString();

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

        // ------------------------------------------------------------------ the list

        /// <summary>
        /// Fill the picker from the book, without the fill counting as a choice.
        ///
        /// The labels come from Core, which is what keeps two connections on one model apart
        /// here and in every other picker in both builds without anyone naming them.
        /// </summary>
        private void RefreshList()
        {
            bool was = _loading;
            _loading = true;
            try
            {
                _connections.Items.Clear();
                var labels = Book.Labels();
                for (int i = 0; i < Book.Items.Count; i++)
                {
                    bool keyed = ApiKeys.TryGetValue(Book.Items[i].Id, out string? k) && k.Length > 0;
                    _connections.Items.Add(keyed ? labels[i] : labels[i] + "  (no key)");
                }

                int at = Book.Items.FindIndex(c => c.Id == Book.SelectedId);
                _connections.SelectedIndex = at >= 0 ? at : (Book.Items.Count > 0 ? 0 : -1);
                _delete.Enabled = Book.Items.Count > 0;
            }
            finally { _loading = was; }
        }

        private void OnConnectionPicked(object? sender, EventArgs e)
        {
            if (_loading || _connections.SelectedIndex < 0) return;
            if (_connections.SelectedIndex >= Book.Items.Count) return;

            Book.SelectedId = Book.Items[_connections.SelectedIndex].Id;
            LoadFrom(Connection);
        }

        /// <summary>Another connection, on the provider currently shown, and selected.</summary>
        private void AddConnection()
        {
            Book.Add(Connection.Provider);
            RefreshList();
            LoadFrom(Connection);
            _name.Focus();
        }

        /// <summary>
        /// Forget this connection and its key. The last one can go: a machine with no AI
        /// connection on it is the state this starts in, so it has to be reachable again.
        /// </summary>
        private void DeleteConnection()
        {
            if (Book.Selected is not { } going) return;

            if (MessageBox.Show(this,
                    $"Forget \"{going.EffectiveName}\" and the key stored against it?",
                    "AI Connection", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning)
                != DialogResult.OK) return;

            ApiKeys.Remove(going.Id);
            Book.Remove(going.Id);
            if (Book.Items.Count == 0) Book.Add();

            RefreshList();
            LoadFrom(Connection);
        }

        /// <summary>
        /// Write a typed field through to the connection it belongs to.
        ///
        /// As it is typed rather than at Apply, because the picker above can switch to another
        /// connection at any moment and what was typed must not go with it. Ignored while the
        /// fields are being loaded, or loading one connection would edit the last.
        /// </summary>
        private void Edited(Action<AiConnection> what)
        {
            if (_loading) return;
            what(Connection);
            RefreshList();
        }

        // ---------------------------------------------------------------------- load/save

        private void LoadFrom(AiConnection cn)
        {
            bool was = _loading;
            _loading = true;
            try { LoadFields(cn); }
            finally { _loading = was; }
        }

        private void LoadFields(AiConnection cn)
        {
            for (int i = 0; i < AiProviderInfo.Known.Count; i++)
            {
                if (AiProviderInfo.Known[i].Provider == cn.Provider) { _provider.SelectedIndex = i; break; }
            }
            if (_provider.SelectedIndex < 0) _provider.SelectedIndex = 0;

            _name.Text = cn.Name;
            _baseUrl.Text = string.IsNullOrWhiteSpace(cn.BaseUrl) ? cn.Info.DefaultBaseUrl : cn.BaseUrl;
            _model.Text = string.IsNullOrWhiteSpace(cn.Model) ? cn.Info.DefaultModel : cn.Model;
            _timeout.Value = Math.Clamp(cn.TimeoutSeconds, (int)_timeout.Minimum, (int)_timeout.Maximum);
            _effort.SelectedIndex = Array.IndexOf(Enum.GetValues<AiEffort>(), cn.Effort) is int at and >= 0 ? at : 0;

            // Each connection has its own key, so the box is emptied on the way in and the
            // placeholder says whether this one has anything stored. A key typed and left
            // behind on the last connection would otherwise be applied to this one.
            _key.Text = "";
            _key.PlaceholderText = ApiKeys.TryGetValue(cn.Id, out string? held) && held.Length > 0
                ? "A key is stored. Type to replace it."
                : "Paste your API key";

            ApplyProviderRules();
        }

        /// <summary>
        /// Check every connection before letting the box close.
        ///
        /// Every one, not only the one on screen: the fields write through as they are typed,
        /// so a bad endpoint can be three connections back and would otherwise be saved
        /// unremarked and fail at the next request instead.
        /// </summary>
        private void Accept()
        {
            foreach (AiConnection cn in Book.Items)
            {
                if (Uri.TryCreate(cn.EffectiveBaseUrl, UriKind.Absolute, out Uri? uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) continue;

                Book.SelectedId = cn.Id;
                RefreshList();
                LoadFrom(cn);
                MessageBox.Show(this,
                    $"The endpoint for \"{cn.EffectiveName}\" needs to be a full http or https "
                  + "address, for example https://api.openai.com.", "AI Connection",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}


