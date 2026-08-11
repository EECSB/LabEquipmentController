using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// Writes a script from a description, using the user's AI connection.
///
/// Both editors open this one window — the single-instrument one and the sequence one —
/// because the difference between them is two arguments: which language to write, and
/// which instruments may be addressed.
///
/// Nothing is applied until the user has read it. That is the same rule the datasheet
/// extractor follows and for the same reason (SPEC §10, §11b): a model writing SCPI is
/// guessing unless it has been handed the catalog, and even then the result is a draft.
/// The generated script lands in a preview beside the request, with anything not found in
/// the catalog listed underneath, and only reaches the editor when the user presses Use.
/// </summary>
public sealed class ScriptAiForm : Form
{
    private readonly AiConnection _connection;
    private readonly string _apiKey;
    private readonly IReadOnlyList<ScriptContextInstrument> _instruments;
    private readonly bool _isSequence;
    private readonly string _currentScript;
    private readonly string _recentOutput;

    private readonly TextBox _prompt = new();
    private readonly TextBox _result = new();
    private readonly CheckBox _revise = new();
    private readonly CheckBox _includeOutput = new();
    private readonly Button _generate = new();
    private readonly Button _use = new();
    private readonly Label _context = new();
    private readonly Label _promptHint = new();

    // Held so the prompt box can be kept visible when the window shrinks — see KeepPromptVisible.
    private SplitContainer? _split;
    private TableLayoutPanel? _options;
    private readonly Label _status = new();

    /// <summary>
    /// The model's note and the catalog findings, under the script they are about.
    ///
    /// A text box rather than a label, for two reasons: an auto-sizing label grew with the
    /// note and squeezed the script above it down to two visible lines, and an undocumented
    /// command listed here is something the reader wants to select and copy.
    /// </summary>
    private readonly TextBox _notes = new();
    private readonly ProgressBar _progress = new();
    private readonly ToolTip _tips = new();

    private CancellationTokenSource? _cts;

    /// <summary>The script to put in the editor, or null if the user closed without using one.</summary>
    public string? Script { get; private set; }

    public ScriptAiForm(AiConnection connection, string apiKey,
                        IReadOnlyList<ScriptContextInstrument> instruments,
                        bool isSequence, string currentScript, string recentOutput)
    {
        _connection = connection.Clone();
        _apiKey = apiKey;
        _instruments = instruments;
        _isSequence = isSequence;
        _currentScript = currentScript ?? "";
        _recentOutput = recentOutput ?? "";

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9f);
        Text = "Write a Script with AI";
        // Bigger than it was: at 1020×700 the draft showed six lines, which is not enough of
        // a script to read before agreeing to run it — and reading it is the whole point of
        // this window. Clamped to the screen in Load, so a large default is safe.
        ClientSize = new Size(1320, 920);
        // 700×520 was a guess, and nothing in the window fitted it: the two checkboxes and the
        // Write Script button cannot share a row that narrow, and the height left the prompt
        // box or the draft below it with no room. Measured instead — this is the smallest size
        // where every control is whole and both text boxes are worth typing in.
        MinimumSize = new Size(920, 700);
        StartPosition = FormStartPosition.CenterParent;

        BuildUi();
    }

    private void BuildUi()
    {
        // --- what the model will be told about, so the user can see it before asking ---
        _context.Dock = DockStyle.Top;
        _context.AutoSize = true;
        _context.Padding = new Padding(12, 12, 12, 6);
        _context.Text = ContextSummary();

        // --- request over result ---
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            Panel1MinSize = 110,
            Panel2MinSize = 140,
        };

        _prompt.Multiline = true;
        _prompt.Dock = DockStyle.Fill;
        _prompt.ScrollBars = ScrollBars.Vertical;
        _prompt.Font = new Font("Segoe UI", 10f);
        _prompt.PlaceholderText = PromptHint();
        _prompt.TextChanged += (_, _) => _generate.Enabled = _cts == null && HasPrompt;

        // Everything that belongs to the *request* sits with the request: the two switches
        // that decide what else gets sent, and the button that sends it. Write Script used to
        // be at the bottom of the window beside Use This Script, which put the verb that acts
        // on the box above next to the verb that acts on the box below.
        var options = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 4, 0, 0),
        };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var switches = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
        };

        _revise.Text = "Revise the current script";
        _revise.AutoSize = true;
        _revise.Checked = _currentScript.Trim().Length > 0;
        _revise.Enabled = _currentScript.Trim().Length > 0;
        _revise.Margin = new Padding(0, 6, 20, 0);
        switches.Controls.Add(_revise);

        _includeOutput.Text = "Include the last run's output";
        _includeOutput.AutoSize = true;
        _includeOutput.Checked = _recentOutput.Trim().Length > 0;
        _includeOutput.Enabled = _recentOutput.Trim().Length > 0;
        _includeOutput.Margin = new Padding(0, 6, 0, 0);
        switches.Controls.Add(_includeOutput);

        ButtonStyle.Apply(_generate, "Write Script", (_, _) => _ = GenerateAsync());
        _generate.Enabled = false;
        _generate.Anchor = AnchorStyles.Right;
        _generate.Margin = new Padding(12, 0, 0, 0);

        options.Controls.Add(switches, 0, 0);
        options.Controls.Add(_generate, 1, 0);
        _split = split;
        _options = options;

        split.Panel1.Controls.Add(_prompt);
        split.Panel1.Controls.Add(options);

        // The example lives in the label, not only in the placeholder: the prompt box takes
        // focus when the window opens, and WinForms hides a placeholder on focus — so the one
        // hint about how much detail helps would never be read.
        _promptHint.Dock = DockStyle.Top;
        _promptHint.AutoSize = true;
        _promptHint.Padding = new Padding(0, 0, 0, 4);
        _promptHint.Text = "Describe what the script should do — " + PromptHint();
        split.Panel1.Controls.Add(_promptHint);
        split.Panel1.Padding = new Padding(12, 0, 12, 0);

        _result.Multiline = true;
        _result.Dock = DockStyle.Fill;
        _result.ReadOnly = true;
        _result.WordWrap = false;
        _result.ScrollBars = ScrollBars.Both;
        _result.Font = new Font("Consolas", 10f);
        _result.BackColor = Color.FromArgb(250, 250, 250);

        // The findings sit directly under the script they are about — an undocumented
        // command is the one thing worth reading before pressing Use.
        _notes.Dock = DockStyle.Bottom;
        _notes.Multiline = true;
        _notes.ReadOnly = true;
        _notes.BorderStyle = BorderStyle.None;
        _notes.BackColor = SystemColors.Control;
        _notes.ScrollBars = ScrollBars.Vertical;
        _notes.TabStop = false;
        _notes.Visible = false;

        split.Panel2.Controls.Add(_result);
        split.Panel2.Controls.Add(_notes);
        split.Panel2.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 4),
            Text = "Proposed script — read it before using it:",
        });
        split.Panel2.Padding = new Padding(12, 0, 12, 0);

        // --- one button, at the right edge, acting on the draft above it ---
        //
        // There is no Close: the title bar already has one, and a second way to do the same
        // nothing was sitting where the eye looks for the thing that commits.
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(12, 6, 12, 12),
        };

        ButtonStyle.Apply(_use, "Use This Script", (_, _) => UseResult());
        _use.Enabled = false;
        bottom.Controls.Add(_use);

        _progress.Dock = DockStyle.Bottom;
        _progress.Height = 4;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.Visible = false;

        _status.Dock = DockStyle.Bottom;
        // Height comes from the font, once the form has passed its own down (see the
        // Load handler). A flat 22 was right at 100% and cut the descenders off at 175%:
        // these windows scale their fonts without scaling their own layout.
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(12, 0, 0, 0);
        _status.Text = "Ready.";

        // Fill first, then docked edges.
        Controls.Add(split);
        Controls.Add(_context);
        Controls.Add(bottom);
        Controls.Add(_progress);
        Controls.Add(_status);

        SetTooltips();

        // An AutoSize label ignores the width its Dock gives it — it grows to whatever its
        // text needs, and only MaximumSize makes it wrap. Both wrap widths used to be worked
        // out once from ClientSize while the form was still at its 1320-wide default, so
        // shrinking the window left the text running hundreds of pixels off the right edge.
        Resize += (_, _) => FitLabelWidths();
        FitLabelWidths();

        Load += (_, _) =>
        {
            // Fit the screen before anything is measured against the form's width. Not
            // Math.Clamp: it throws when the minimum exceeds the maximum, which is what a
            // screen smaller than MinimumSize gives.
            Size wa = Screen.GetWorkingArea(this).Size;
            Size = new Size(Math.Min(Math.Max(Width, MinimumSize.Width), wa.Width),
                            Math.Min(Math.Max(Height, MinimumSize.Height), wa.Height));

            _status.Height = _status.PreferredHeight + LogicalToDeviceUnits(6);
            ButtonStyle.Normalize(this, _generate, _use);
            ButtonStyle.SetDrawnIcon(this, _generate, "ai");
        };

        Shown += (_, _) =>
        {
            SplitLayout.SetFraction(split, 0.30);
            FitLabelWidths();     // now that the labels have a real width to wrap against
            _prompt.Focus();
        };

        // A request in flight has to be let go before the window that owns it disappears.
        FormClosing += (_, _) => _cts?.Cancel();
    }

    /// <summary>
    /// Esc closes it, which here means "do not use this draft" — the same thing the ✕ means,
    /// and the same key every other window in the app answers to.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool HasPrompt => _prompt.Text.Trim().Length > 0;

    /// <summary>What the model is being given, in one paragraph the user can check.</summary>
    private string ContextSummary()
    {
        if (_instruments.Count == 0)
            return "No instrument is connected, so there is no command catalog to write "
                 + "against. Connect one first — otherwise the script can only be guesswork.";

        var lines = new List<string>();
        foreach (ScriptContextInstrument i in _instruments)
        {
            int count = i.Reference?.Commands.Count ?? 0;
            string name = i.Alias.Length > 0 ? $"{i.Alias} — {i.Model}" : i.Model;
            lines.Add(count > 0
                ? $"• {name}: {count:N0} documented commands"
                : $"• {name}: no catalog — only *IDN?, *RST, *CLS and *OPC? can be checked");
        }

        return "The model is given the command catalogs below and told to use nothing else. "
             + "Check what it writes before you run it.\r\n" + string.Join("\r\n", lines);
    }

    /// <summary>
    /// Re-wrap the two explanatory labels to the current width. The insets match each label's
    /// own surroundings: <see cref="_context"/> is docked to the form and carries 12px of its
    /// own padding on each side; the hint sits inside the split panel, which adds 12 more.
    /// Floored so a label never goes to zero width and swallows its text.
    /// </summary>
    private void FitLabelWidths()
    {
        int floor = LogicalToDeviceUnits(200);
        _context.MaximumSize = new Size(
            Math.Max(floor, ClientSize.Width - LogicalToDeviceUnits(24)), 0);
        _promptHint.MaximumSize = new Size(
            Math.Max(floor, ClientSize.Width - LogicalToDeviceUnits(40)), 0);
        KeepPromptVisible();
    }

    /// <summary>
    /// The upper split panel holds the hint, the options row and the prompt box. Its minimum
    /// was a flat 110px — less than the hint and the options row come to on their own once the
    /// hint wraps — so shrinking the window clamped the splitter to a height where the prompt
    /// box was a sliver you could not type in. Size the minimum from what is actually in there.
    ///
    /// Both assignments throw when they exceed what the container can give, so the figure is
    /// clamped to the room that is left after Panel2's own minimum and the splitter.
    /// </summary>
    private void KeepPromptVisible()
    {
        if (_split == null || _options == null) return;

        int room = _split.Height - _split.Panel2MinSize - _split.SplitterWidth;
        if (room <= 0) return;                     // mid-resize, or a window too short to care

        // PreferredSize, not Height: this runs from Resize, before layout has applied the wrap
        // width just set above, so Height is still the previous size's answer and the panel
        // comes out short by however many lines the hint gained.
        int hint = _promptHint.GetPreferredSize(new Size(_promptHint.MaximumSize.Width, 0)).Height;
        int need = hint + _options.PreferredSize.Height + LogicalToDeviceUnits(72);
        _split.Panel1MinSize = Math.Min(need, room);
        if (_split.SplitterDistance < _split.Panel1MinSize)
            _split.SplitterDistance = _split.Panel1MinSize;
    }

    private string PromptHint() => _isSequence
        ? "e.g. Sweep the generator from 100 Hz to 100 kHz in 20 log steps and record the "
        + "scope's peak-to-peak voltage at each one."
        : "e.g. Set a 1 kHz sine at 2 Vpp on channel 1, turn the output on, wait a second, "
        + "then read it back.";

    private void SetTooltips()
    {
        _tips.SetToolTip(_prompt, "Plain English. Say what you want measured or set, and at "
            + "what values — the more specific, the less the model has to guess.");
        _tips.SetToolTip(_generate, "Send the request, the command catalogs and the script "
            + "language to your AI connection.");
        _tips.SetToolTip(_use, "Put this script into the editor. It is not run — you still "
            + "press Run yourself.");
        _tips.SetToolTip(_revise, "Send the script that is in the editor, so the model changes "
            + "it rather than starting again.");
        _tips.SetToolTip(_includeOutput, "Send the output pane as well, errors included. This "
            + "is what makes \"it failed, fix it\" answerable.");
        _tips.SetToolTip(_result, "The model's draft. It has been checked against the "
            + "catalogs; anything it could not find is listed below.");
        _tips.SetToolTip(_notes, "What the model said it did, and what the catalog check "
            + "found. Selectable, so a flagged command can be copied.");
    }

    private async Task GenerateAsync()
    {
        if (!HasPrompt || _cts != null) return;

        _cts = new CancellationTokenSource();
        Busy(true, "Writing…");

        try
        {
            var author = new ScriptAuthor(new AiClient());
            AuthoredScript written = await author.WriteAsync(
                _prompt.Text.Trim(),
                _instruments,
                _isSequence,
                _connection,
                _apiKey,
                _revise.Checked ? _currentScript : null,
                _includeOutput.Checked ? _recentOutput : null,
                _cts.Token);

            if (IsDisposed) return;

            _result.Text = written.Script;
            _use.Enabled = true;
            ShowFindings(written);
            _status.Text = "Written. Read it, then press Use This Script.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled.";
        }
        catch (AiException ex)
        {
            _status.Text = "Failed.";
            MessageBox.Show(this, ex.Message, "Write a Script with AI",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            _status.Text = "Failed.";
            MessageBox.Show(this, ex.GetType().Name + ": " + ex.Message,
                "Write a Script with AI", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            if (!IsDisposed) Busy(false, _status.Text);
        }
    }

    /// <summary>The model's own note, and — louder — whatever failed the catalog check.</summary>
    private void ShowFindings(AuthoredScript written)
    {
        // The check first, the model's own account second. Only so much of this is visible
        // before it scrolls, and what must be read is what the catalog says — not what the
        // thing being checked says about itself.
        var parts = new List<string>();

        if (written.Undocumented.Count > 0)
        {
            parts.Add($"⚠ {written.Undocumented.Count} line(s) use commands that are in no "
                    + "catalog here. Check them against the instrument's guide before running:"
                    + "\r\n    " + string.Join("\r\n    ", written.Undocumented.Take(8))
                    + (written.Undocumented.Count > 8 ? "\r\n    …" : ""));
        }
        else if (written.Script.Length > 0)
        {
            // Worth saying plainly, because "no warnings" reads as "verified" otherwise. The
            // check compares command headers against the catalog; the arguments after them
            // are the model's, and only the instrument can judge those.
            parts.Add("Every command header here is in the catalog. The values after them "
                    + "were not checked — read the script before you run it.");
        }

        if (written.Notes.Length > 0) parts.Add(written.Notes);

        _notes.ForeColor = written.Undocumented.Count > 0
            ? Color.FromArgb(150, 40, 0) : SystemColors.GrayText;
        _notes.Text = string.Join("\r\n\r\n", parts);
        _notes.Visible = parts.Count > 0;
        if (_notes.Visible) _notes.Height = NotesHeight();
    }

    /// <summary>
    /// As tall as the note needs, up to four lines, after which it scrolls.
    ///
    /// The cap is the point: a chatty model wrote a four-sentence note that pushed the script
    /// it was about down to two visible lines. The script is what the reader came for.
    /// </summary>
    private int NotesHeight()
    {
        int width = Math.Max(_notes.ClientSize.Width, 200);
        int line = TextRenderer.MeasureText("Ag", _notes.Font).Height;
        int needed = TextRenderer.MeasureText(
            _notes.Text, _notes.Font, new Size(width, 0), TextFormatFlags.WordBreak).Height;

        return Math.Min(needed, line * 4) + line / 2;
    }

    private void UseResult()
    {
        if (_result.TextLength == 0) return;
        Script = _result.Text;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Busy(bool busy, string status)
    {
        _progress.Visible = busy;
        _status.Text = status;
        _generate.Enabled = !busy && HasPrompt;
        _use.Enabled = !busy && _result.TextLength > 0;
        _prompt.ReadOnly = busy;
        _revise.Enabled = !busy && _currentScript.Trim().Length > 0;
        _includeOutput.Enabled = !busy && _recentOutput.Trim().Length > 0;
        Cursor = busy ? Cursors.AppStarting : Cursors.Default;
    }

    /// <summary>
    /// Open the writer for an editor, or explain why it cannot be opened.
    ///
    /// Both callers need the same three checks — a connection exists, a key is stored, an
    /// instrument is addressable — so they live here rather than twice.
    /// </summary>
    public static string? Ask(IWin32Window owner,
                              IReadOnlyList<ScriptContextInstrument> instruments,
                              bool isSequence, string currentScript, string recentOutput)
    {
        UserSettings settings = SettingsStore.Load();
        string? key = SecretStore.Unprotect(settings.AiApiKeyProtected);

        if (settings.Ai == null || string.IsNullOrWhiteSpace(key))
        {
            MessageBox.Show(owner,
                "No AI connection is set up yet.\n\nAdd one under Tools ▸ AI Connection — "
              + "you supply your own provider and key, and the key is stored encrypted for "
              + "your Windows account.",
                "Write a Script with AI", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        if (instruments.Count == 0)
        {
            MessageBox.Show(owner,
                "Connect an instrument first.\n\nThe model is given that instrument's command "
              + "catalog to write from; without one it would be inventing SCPI, which is the "
              + "one thing this app does not do.",
                "Write a Script with AI", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        using var dlg = new ScriptAiForm(settings.Ai, key!, instruments, isSequence,
                                         currentScript, recentOutput);
        if (owner is Form f && f.Icon != null) dlg.Icon = f.Icon;
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.Script : null;
    }
}
