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
/// It is a conversation rather than a single question, because that is how a script gets
/// written: "now do the same at 5 V", "that failed, drop the trigger line". Every exchange
/// stays on screen and goes back to the model with the next request, so a follow-up is a
/// follow-up rather than a fresh start. What that costs is shown above the transcript, and
/// Clear is how it is paid back — see <see cref="Conversations"/>.
///
/// Nothing is applied until the user has read it. That is the same rule the datasheet
/// extractor follows and for the same reason (SPEC §10, §11b): a model writing SCPI is
/// guessing unless it has been handed the catalog, and even then the result is a draft.
/// Each answer carries its own <c>Use This Script</c>, under the script it takes.
/// </summary>
public sealed class ScriptAiForm : Form
{
    /// <summary>
    /// The conversations, one per language, kept for the life of the app.
    ///
    /// Static because this window does not outlive one answer: Use This Script closes it. A
    /// chat held in the form would therefore be a chat of exactly one turn — you could see
    /// the history right up until the moment you took a draft and needed it.
    ///
    /// Keyed on the language rather than shared, because the two editors speak different
    /// ones: a transcript of single-instrument scripts is a poor thing to hand a model being
    /// asked for DEVICE and WITH.
    /// </summary>
    private static readonly Dictionary<bool, List<ScriptTurn>> Conversations = new();

    private static List<ScriptTurn> ConversationFor(bool isSequence)
    {
        if (!Conversations.TryGetValue(isSequence, out List<ScriptTurn>? turns))
            Conversations[isSequence] = turns = [];
        return turns;
    }

    // The connections to choose from, and their keys. A list rather than one, because the model
    // is a choice — and this is the window where you would notice that a draft wants the better
    // one.
    private readonly AiConnections _book;
    private readonly IReadOnlyDictionary<string, string> _keys;
    private readonly ComboBox _using = new();

    /// <summary>
    /// How hard to work the model, beside which model it is.
    ///
    /// Which model and how hard to work it are one decision taken twice, and a window that
    /// offers the first without the second sends you to the settings box for the other half.
    /// This is the window where the second half earns its keep: a sweep with a WITH block and
    /// a FOR in it is not transcription.
    /// </summary>
    private readonly ComboBox _effort = new();

    private AiConnection _connection;
    private string _apiKey;
    private bool _picking;
    private readonly IReadOnlyList<ScriptContextInstrument> _instruments;
    private readonly bool _isSequence;
    private readonly string _currentScript;
    private readonly string _recentOutput;

    /// <summary>This window's share of <see cref="Conversations"/> — the same list, not a copy.</summary>
    private readonly List<ScriptTurn> _turns;

    private readonly TextBox _prompt = new();
    private readonly CheckBox _revise = new();
    private readonly CheckBox _includeOutput = new();
    private readonly Button _generate = new();
    private readonly Button _clear = new();
    private readonly Label _context = new();

    /// <summary>
    /// The conversation, oldest first, as controls rather than as text.
    ///
    /// Controls because every answer carries a button. A rich text box could show the same
    /// words and could not put a <c>Use This Script</c> under each of them, and one button at
    /// the foot of the window can only ever mean the newest answer — which in a conversation
    /// is the wrong one as often as it is the right one.
    /// </summary>
    private readonly Panel _scroll = new();
    private readonly TableLayoutPanel _cards = new();

    /// <summary>Labels that wrap, so their wrap width can be reset when the window changes.</summary>
    private readonly List<Label> _wrapping = [];

    /// <summary>Script boxes, whose height is however many lines they wrapped to.</summary>
    private readonly List<TextBox> _scripts = [];

    /// <summary>What the transcript costs to send, which is what Clear is for.</summary>
    private readonly Label _ledger = new();

    private readonly Font _askFont = new("Segoe UI", 9.75f, FontStyle.Bold);
    private readonly Font _bodyFont = new("Segoe UI", 9.75f);
    private readonly Font _codeFont = new("Consolas", 9.75f);

    private static readonly Color Warning = Color.FromArgb(150, 40, 0);
    private static readonly Color Rule = Color.FromArgb(224, 226, 230);
    private static readonly Color Sunken = Color.FromArgb(248, 248, 250);

    /// <summary>How tall the request box may grow before it scrolls instead.</summary>
    private const int PromptMaxLines = 8;

    private TableLayoutPanel? _composer;
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly ToolTip _tips = new();

    private CancellationTokenSource? _cts;

    /// <summary>The script to put in the editor, or null if the user closed without using one.</summary>
    public string? Script { get; private set; }

    public ScriptAiForm(AiConnections book, IReadOnlyDictionary<string, string> keys,
                        IReadOnlyList<ScriptContextInstrument> instruments,
                        bool isSequence, string currentScript, string recentOutput)
    {
        _book = book.Clone();
        _keys = keys;
        _connection = (_book.Selected ?? new AiConnection()).Clone();
        _apiKey = _keys.TryGetValue(_connection.Id, out string? key) ? key : "";
        _instruments = instruments;
        _isSequence = isSequence;
        _currentScript = currentScript ?? "";
        _recentOutput = recentOutput ?? "";
        _turns = ConversationFor(isSequence);

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
        // box or the transcript above it with no room. Measured instead — this is the smallest
        // size where every control is whole and both text boxes are worth typing in.
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

        // --- what the conversation costs, over the conversation it is about ---
        _ledger.Dock = DockStyle.Top;
        _ledger.AutoSize = true;
        _ledger.ForeColor = SystemColors.GrayText;
        _ledger.Padding = new Padding(12, 0, 12, 4);

        // --- the conversation ---
        //
        // The transcript takes the window and the composer keeps its own height, which is the
        // way round every chat is laid out: the history scrolls and the box you type in does
        // not move.
        _scroll.Dock = DockStyle.Fill;
        _scroll.AutoScroll = true;
        _scroll.BorderStyle = BorderStyle.FixedSingle;
        _scroll.BackColor = Sunken;
        _scroll.Padding = new Padding(10, 8, 10, 8);

        _cards.Dock = DockStyle.Top;
        _cards.ColumnCount = 1;
        _cards.AutoSize = true;
        _cards.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _cards.BackColor = Color.Transparent;
        _cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _scroll.Controls.Add(_cards);

        // Clear in the transcript's own top corner, over the first line of it — the nearest
        // thing this pane has to a header. On the host rather than inside the scroller, so it
        // stays put at the bottom of a long conversation.
        ButtonStyle.Apply(_clear, "Clear", (_, _) => ClearConversation());
        _clear.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
        host.Controls.Add(_scroll);
        host.Controls.Add(_clear);
        _clear.BringToFront();
        host.Resize += (_, _) => PlaceClear(host);

        // --- the composer ---
        var composer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            // A clear step down from the transcript, rather than the two touching.
            Padding = new Padding(12, 10, 12, 0),
        };
        composer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _composer = composer;

        // One line, and as many as the text needs. It stood six deep whether or not there was
        // anything in it, which is a paragraph's worth of empty between the conversation and
        // the row that sends it; most requests here are one sentence.
        _prompt.Multiline = true;
        _prompt.WordWrap = true;
        _prompt.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _prompt.Font = new Font("Segoe UI", 10f);
        // And behind an emptied box, the same words as an example — which is all a placeholder
        // can be. The box itself opens on the real thing; see the end of BuildUi.
        _prompt.PlaceholderText = Hint;
        _prompt.Margin = new Padding(0);
        _prompt.TextChanged += (_, _) =>
        {
            _generate.Enabled = _cts == null && HasPrompt;
            FitPrompt();
        };
        // Enter sends, because this is a chat and that is what Enter does in one; Shift+Enter
        // is the newline. SuppressKeyPress rather than Handled alone, or the box takes the
        // line break anyway and Windows dings on the way past.
        _prompt.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || e.Shift) return;
            e.Handled = e.SuppressKeyPress = true;
            if (_generate.Enabled) _ = GenerateAsync();
        };

        // Everything that belongs to the *request* sits with the request: the four switches
        // that decide what gets sent and how, and the button that sends it.
        //
        // Two stacks and a button rather than one long line of everything. Across, five
        // controls do not fit the width of a window that can be dragged narrow, and a row that
        // wraps puts its own button on a line of its own with a field of nothing beside it.
        // Stacked, each pair keeps its own question: which model and how hard to work it in
        // one column, what else to send in the other.
        var options = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 6, 0, 0),
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

        // The model column. Two rows, one label column: the pickers line up under each other
        // rather than stepping in and out with the word in front of them.
        var models = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 24, 0),
        };
        models.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        models.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // Which connection to spend. Choosing here chooses everywhere: there is one selection
        // for the app, so this window and the datasheet extractor cannot disagree about which
        // key is being spent.
        models.Controls.Add(
            new Label { Text = "Using:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, 0, 0);
        _using.DropDownStyle = ComboBoxStyle.DropDownList;
        _using.DrawMode = DrawMode.OwnerDrawFixed;
        _using.DrawItem += (_, e) => ButtonStyle.DrawComboItem(_using, e);
        _using.Width = 240;
        _using.Margin = new Padding(0, 2, 0, 2);
        _picking = true;
        foreach (string label in _book.Labels()) _using.Items.Add(label);
        _using.SelectedIndex = _book.Items.FindIndex(c => c.Id == _connection.Id) is int at and >= 0
            ? at
            : (_book.Items.Count > 0 ? 0 : -1);
        _picking = false;
        _using.SelectedIndexChanged += (_, _) =>
        {
            if (_picking || _using.SelectedIndex < 0 || _using.SelectedIndex >= _book.Items.Count) return;

            _connection = _book.Items[_using.SelectedIndex].Clone();
            _apiKey = _keys.TryGetValue(_connection.Id, out string? picked) ? picked : "";
            _book.SelectedId = _connection.Id;
            AiBookStore.Select(_connection.Id);
            ShowEffort();       // effort belongs to the connection, so it changes with it
        };
        models.Controls.Add(_using, 1, 0);

        models.Controls.Add(
            new Label { Text = "Effort:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, 0, 1);
        _effort.DropDownStyle = ComboBoxStyle.DropDownList;
        _effort.DrawMode = DrawMode.OwnerDrawFixed;
        _effort.DrawItem += (_, e) => ButtonStyle.DrawComboItem(_effort, e);
        _effort.Width = 240;
        _effort.Margin = new Padding(0, 2, 0, 0);
        // Core's own scale, spelled once. Each provider translates it its own way — a word for
        // Gemini and an OpenAI-compatible endpoint, a token budget for Anthropic, and nothing
        // at all for Default, which is why Default is the default.
        foreach (AiEffort effort in Enum.GetValues<AiEffort>()) _effort.Items.Add(AiSettingsForm.EffortLabel(effort));
        ShowEffort();
        _effort.SelectedIndexChanged += (_, _) =>
        {
            if (_picking || _effort.SelectedIndex < 0) return;

            AiEffort picked = Enum.GetValues<AiEffort>()[_effort.SelectedIndex];
            _connection.Effort = picked;
            AiBookStore.SetEffort(_connection.Id, picked);
            if (_book.Find(_connection.Id) is { } mine) mine.Effort = picked;
        };
        models.Controls.Add(_effort, 1, 1);
        switches.Controls.Add(models);

        // And the column that decides what else travels with the request.
        var extras = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
        };

        _revise.Text = "Revise the current script";
        _revise.AutoSize = true;
        _revise.Checked = _currentScript.Trim().Length > 0;
        _revise.Enabled = _currentScript.Trim().Length > 0;
        _revise.Margin = new Padding(0, 6, 0, 2);
        extras.Controls.Add(_revise);

        _includeOutput.Text = "Include the last run's output";
        _includeOutput.AutoSize = true;
        _includeOutput.Checked = _recentOutput.Trim().Length > 0;
        _includeOutput.Enabled = _recentOutput.Trim().Length > 0;
        _includeOutput.Margin = new Padding(0, 2, 0, 0);
        extras.Controls.Add(_includeOutput);
        switches.Controls.Add(extras);

        ButtonStyle.Apply(_generate, "Write Script", (_, _) => _ = GenerateAsync());
        _generate.Enabled = false;
        _generate.Anchor = AnchorStyles.Right;
        _generate.Margin = new Padding(12, 0, 0, 0);

        options.Controls.Add(switches, 0, 0);
        options.Controls.Add(_generate, 1, 0);

        composer.Controls.Add(_prompt, 0, 0);
        composer.Controls.Add(options, 0, 1);

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

        // Fill first, then docked edges, from the middle outwards.
        Controls.Add(host);
        Controls.Add(_ledger);
        Controls.Add(_context);
        Controls.Add(composer);
        Controls.Add(_progress);
        Controls.Add(_status);

        SetTooltips();

        // The box opens on a worked request, written in rather than greyed behind it.
        //
        // A placeholder is a shape you cannot edit, cannot select and cannot send, and it goes
        // the moment you type over it — so the one hint about how much detail helps is read by
        // nobody. Written, it is a request that already works: press the button and see what
        // comes back, or change the two numbers in it and press the button. That is what both
        // editors do with their own examples, and for the same reason.
        //
        // After the button exists, so the TextChanged that this fires can enable it.
        _prompt.Text = Example;

        // An AutoSize label ignores the width its Dock gives it — it grows to whatever its
        // text needs, and only MaximumSize makes it wrap. The wrap width used to be worked out
        // once from ClientSize while the form was still at its 1320-wide default, so shrinking
        // the window left the text running hundreds of pixels off the right edge.
        Resize += (_, _) => FitLabelWidths();

        Load += (_, _) =>
        {
            // Fit the screen before anything is measured against the form's width. Not
            // Math.Clamp: it throws when the minimum exceeds the maximum, which is what a
            // screen smaller than MinimumSize gives.
            Size wa = Screen.GetWorkingArea(this).Size;
            Size = new Size(Math.Min(Math.Max(Width, MinimumSize.Width), wa.Width),
                            Math.Min(Math.Max(Height, MinimumSize.Height), wa.Height));

            _status.Height = _status.PreferredHeight + LogicalToDeviceUnits(6);
            ButtonStyle.Normalize(this, _generate, _clear);
            ButtonStyle.SetDrawnIcon(this, _generate, "ai");
            ButtonStyle.SetIcon(this, _clear, "reset");
            PlaceClear(host);
        };

        Shown += (_, _) =>
        {
            DrawTranscript();
            FitLabelWidths();     // now that the labels have a real width to wrap against
            FitPrompt();
            PlaceClear(host);
            _prompt.Focus();
            // Caret at the end of the example rather than in front of it: this box takes focus
            // when the window opens, and typing at position zero would push the worked request
            // along in front of whatever is being written.
            _prompt.SelectionStart = _prompt.TextLength;
        };

        // A request in flight has to be let go before the window that owns it disappears.
        FormClosing += (_, _) => _cts?.Cancel();
    }

    /// <summary>
    /// Esc closes it, which here means "do not use this draft" — the same thing the ✕ means,
    /// and the same key every other window in the app answers to. The conversation survives:
    /// closing the window is not the same as clearing it, which is what Clear is for.
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

    // ------------------------------------------------------------------- the conversation

    /// <summary>
    /// Rebuild the whole transcript.
    ///
    /// Whole rather than appending the newest turn, because Clear has to be able to empty it
    /// and one method that can always produce the right thing beats two that agree until they
    /// do not.
    /// </summary>
    private void DrawTranscript()
    {
        _cards.SuspendLayout();
        _wrapping.Clear();
        _scripts.Clear();
        foreach (Control old in _cards.Controls.Cast<Control>().ToList()) old.Dispose();
        _cards.Controls.Clear();
        _cards.RowStyles.Clear();
        _cards.RowCount = 0;

        if (_turns.Count == 0)
        {
            AddRow(_cards, Wrapping(
                "Nothing asked yet.\r\n\r\nWhat comes back appears here, and each new request "
              + "is asked with everything above it — so a follow-up like “now do the same at "
              + "5 V” means something.",
                _bodyFont, SystemColors.GrayText));
        }

        for (int i = 0; i < _turns.Count; i++) AddRow(_cards, BuildTurn(_turns[i], i));

        _cards.ResumeLayout(true);

        _ledger.Text = LedgerText();
        _clear.Enabled = _cts == null && _turns.Count > 0;
        FitLabelWidths();
        ScrollToEnd();
    }

    /// <summary>One exchange: what was asked, what was written, and a button that takes it.</summary>
    private Control BuildTurn(ScriptTurn turn, int index)
    {
        var card = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = new Padding(0, index == 0 ? 0 : LogicalToDeviceUnits(10), 0, 0),
            BackColor = Color.Transparent,
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        // A rule between turns rather than a box around each: a card inside a card is a stack
        // of frames, and what is wanted is to see where one answer stops.
        if (index > 0)
        {
            AddRow(card, new Panel
            {
                Height = 1,
                BackColor = Rule,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(10)),
            });
        }

        AddRow(card, Heading($"You  ·  {index + 1} of {_turns.Count}"));
        AddRow(card, Wrapping(turn.Request.Trim(), _bodyFont, SystemColors.WindowText));
        AddRow(card, Heading("Written"));

        var script = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            Font = _codeFont,
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 2, 0, 6),
            Text = turn.Script.Trim(),
            TabStop = false,
        };
        _scripts.Add(script);
        AddRow(card, script);

        // Under the script it takes, in the answer it belongs to. One at the foot of the
        // window could only ever mean the newest, which in a conversation is the wrong one as
        // often as it is the right one.
        var take = new Button { Anchor = AnchorStyles.Right, Margin = new Padding(0, 0, 0, 6) };
        ButtonStyle.Apply(take, "Use This Script", (_, _) => UseScript(turn.Script));
        take.Enabled = turn.Script.Trim().Length > 0;
        _tips.SetToolTip(take, "Put this script into the editor. It is not run — you still "
                             + "press Run yourself.");
        AddRow(card, take);

        foreach (string finding in Findings(turn))
        {
            AddRow(card, Wrapping(finding, _bodyFont,
                turn.Undocumented is { Count: > 0 } ? Warning : SystemColors.GrayText));
        }

        return card;
    }

    private static void AddRow(TableLayoutPanel table, Control child)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(child, 0, table.RowCount++);
    }

    private Label Heading(string text) => new()
    {
        Text = text,
        Font = _askFont,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 2),
        ForeColor = SystemColors.WindowText,
    };

    private Label Wrapping(string text, Font font, Color colour)
    {
        var label = new Label
        {
            Text = text,
            Font = font,
            ForeColor = colour,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
        };
        _wrapping.Add(label);
        return label;
    }

    /// <summary>
    /// What the catalog check said about a draft, and — second — what the model said about
    /// itself.
    ///
    /// The check first. Only so much of this is read, and what must be read is what the
    /// catalog says, not what the thing being checked says about itself.
    /// </summary>
    private static IEnumerable<string> Findings(ScriptTurn turn)
    {
        if (turn.Undocumented is { Count: > 0 } bad)
        {
            yield return $"⚠ {bad.Count} line(s) use commands that are in no catalog here. "
                       + "Check them against the instrument's guide before running:"
                       + "\r\n    " + string.Join("\r\n    ", bad.Take(8))
                       + (bad.Count > 8 ? "\r\n    …" : "");
        }
        else if (turn.Script.Trim().Length > 0)
        {
            // Worth saying plainly, because "no warnings" reads as "verified" otherwise. The
            // check compares command headers against the catalog; the arguments after them
            // are the model's, and only the instrument can judge those.
            yield return "Every command header here is in the catalog. The values after them "
                       + "were not checked — read the script before you run it.";
        }

        if (turn.Notes.Trim().Length > 0) yield return turn.Notes.Trim();
    }

    private void ScrollToEnd()
    {
        if (_cards.Controls.Count == 0) return;
        _scroll.ScrollControlIntoView(_cards.Controls[^1]);
    }

    /// <summary>Clear at the transcript's top right, inside its frame.</summary>
    private void PlaceClear(Control host)
    {
        int inset = LogicalToDeviceUnits(6);
        _clear.Location = new Point(
            host.ClientSize.Width - host.Padding.Right - _clear.Width - inset,
            host.Padding.Top + inset);
    }

    /// <summary>
    /// What the conversation costs, in the units the decision is made in.
    ///
    /// A count of turns alone does not answer "should I clear this?" — two turns carrying
    /// two-hundred-line scripts are worth more than ten carrying four. The figure is
    /// characters of transcript, which is not tokens but is the only honest number available
    /// here and moves with them.
    /// </summary>
    private string LedgerText()
    {
        if (_turns.Count == 0)
            return "No conversation yet. Only your request and the catalogs get sent.";

        int chars = _turns.Sum(
            t => t.Request.Length + t.Script.Length + t.Notes.Length
               + (t.Undocumented?.Sum(u => u.Length) ?? 0));

        string size = chars < 1024 ? $"{chars} characters" : $"about {chars / 1024.0:0.#} kB";
        return $"{_turns.Count} turn(s) — {size} of transcript goes with every request. "
             + "Clear to start again without it.";
    }

    /// <summary>
    /// Forget the conversation.
    ///
    /// The whole thing: what is left is what a freshly-opened window would have had, which is
    /// what "clear" has to mean for the figure above it to be true afterwards.
    /// </summary>
    private void ClearConversation()
    {
        if (_turns.Count == 0) return;

        _turns.Clear();
        DrawTranscript();
        _status.Text = "Conversation cleared. The next request goes on its own.";
        _prompt.Focus();
    }

    /// <summary>
    /// Re-wrap everything that wraps, to the width the window is now.
    ///
    /// The insets match each label's own surroundings: <see cref="_context"/> is docked to the
    /// form and carries 12px of its own padding on each side; the transcript's labels sit
    /// inside the scroller, which adds its own padding and a scrollbar's width. Floored so a
    /// label never goes to zero width and swallows its text.
    /// </summary>
    private void FitLabelWidths()
    {
        int floor = LogicalToDeviceUnits(200);
        int page = Math.Max(floor, ClientSize.Width - LogicalToDeviceUnits(24));

        _context.MaximumSize = new Size(page, 0);
        _ledger.MaximumSize = new Size(page, 0);

        int inner = Math.Max(floor, _cards.ClientSize.Width);
        foreach (Label label in _wrapping) label.MaximumSize = new Size(inner, 0);
        foreach (TextBox box in _scripts) FitScript(box);
    }

    /// <summary>A script box is as tall as the lines it wrapped to, and no taller.</summary>
    private void FitScript(TextBox box)
    {
        if (box.IsDisposed || box.Width <= 0) return;

        int line = TextRenderer.MeasureText("Ag", box.Font).Height;
        int lines = Math.Max(1, box.GetLineFromCharIndex(box.TextLength) + 1);
        int wanted = line * lines + LogicalToDeviceUnits(10);
        if (box.Height != wanted) box.Height = wanted;
    }

    /// <summary>
    /// The request box is as tall as what is in it, up to a ceiling.
    ///
    /// GetLineFromCharIndex counts display lines, so a pasted paragraph that wraps grows the
    /// box exactly as a pasted script with real newlines does.
    /// </summary>
    private void FitPrompt()
    {
        int line = TextRenderer.MeasureText("Ag", _prompt.Font).Height;
        int lines = Math.Clamp(_prompt.GetLineFromCharIndex(_prompt.TextLength) + 1,
                               1, PromptMaxLines);

        ScrollBars wanted = lines >= PromptMaxLines ? ScrollBars.Vertical : ScrollBars.None;
        if (_prompt.ScrollBars != wanted) _prompt.ScrollBars = wanted;

        int height = line * lines + LogicalToDeviceUnits(8);
        if (_prompt.Height == height) return;

        _prompt.Height = height;
        _composer?.PerformLayout();
    }

    /// <summary>
    /// The request the box opens on: a real one, for the language this editor speaks.
    /// </summary>
    private string Example => _isSequence
        ? "Sweep the generator from 100 Hz to 100 kHz in 20 log steps and record the scope's "
        + "peak-to-peak voltage at each one."
        : "Set a 1 kHz sine at 2 Vpp on channel 1, turn the output on, wait a second, then "
        + "read it back.";

    /// <summary>And the same words behind an emptied box, which is the one time they are
    /// needed again — said as an example there, because that is all a placeholder can be.</summary>
    private string Hint => "e.g. " + Example;

    /// <summary>Show the effort the current connection carries, without writing it back.</summary>
    private void ShowEffort()
    {
        bool was = _picking;
        _picking = true;
        _effort.SelectedIndex =
            Array.IndexOf(Enum.GetValues<AiEffort>(), _connection.Effort) is int at and >= 0 ? at : 0;
        _picking = was;
    }

    private void SetTooltips()
    {
        _tips.SetToolTip(_prompt, "Plain English. Say what you want measured or set, and at "
            + "what values — the more specific, the less the model has to guess. A follow-up "
            + "is read together with everything above it. Enter sends it; Shift+Enter starts "
            + "a new line.");
        _tips.SetToolTip(_using, "Which AI connection to spend. Set them up under Tools ▸ AI "
            + "Connection; the one picked here is the one every AI window uses.");
        _tips.SetToolTip(_effort, "How hard to ask the model to think before answering. Higher "
            + "is slower and costs more tokens; provider default sends nothing at all. A sweep "
            + "across several instruments is worth it, a one-line query is not.");
        _tips.SetToolTip(_generate, "Send the request, the conversation so far, the command "
            + "catalogs and the script language to your AI connection.");
        _tips.SetToolTip(_revise, "Send the script that is in the editor, so the model changes "
            + "it rather than starting again.");
        _tips.SetToolTip(_includeOutput, "Send the output pane as well, errors included. This "
            + "is what makes \"it failed, fix it\" answerable.");
        _tips.SetToolTip(_scroll, "Everything asked and everything written, oldest first. All "
            + "of it goes back with the next request, and each answer carries its own Use "
            + "This Script.");
        _tips.SetToolTip(_clear, "Forget the conversation. Nothing above is sent again, which "
            + "is how the request stops growing — and how a new subject starts clean.");
        _tips.SetToolTip(_ledger, "How much transcript is being sent with each request.");
    }

    private async Task GenerateAsync()
    {
        if (!HasPrompt || _cts != null) return;

        string asked = _prompt.Text.Trim();

        _cts = new CancellationTokenSource();
        Busy(true, "Thinking…");

        try
        {
            var author = new ScriptAuthor(new AiClient());
            AuthoredScript written = await author.WriteAsync(
                asked,
                _instruments,
                _isSequence,
                _connection,
                _apiKey,
                _revise.Checked ? _currentScript : null,
                _includeOutput.Checked ? _recentOutput : null,
                _turns.ToList(),
                _cts.Token);

            if (IsDisposed) return;

            _turns.Add(new ScriptTurn(
                asked, written.Script, written.Notes, written.Undocumented));

            // The box empties, because the request is now in the transcript above it and a
            // chat you have to clear by hand before each turn is a form, not a chat.
            _prompt.Clear();
            DrawTranscript();
            _status.Text = "Written. Read it, then press Use This Script — or ask for a change.";
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

    private void UseScript(string script)
    {
        if (script.Trim().Length == 0) return;
        Script = script;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Busy(bool busy, string status)
    {
        _progress.Visible = busy;
        _status.Text = status;
        _generate.Enabled = !busy && HasPrompt;
        _clear.Enabled = !busy && _turns.Count > 0;
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
        (AiConnections book, var keys) = AiBookStore.Load();

        // Something to spend: a connection, and a key on one of them. Which connection is the
        // window's own question — the picker beside its switches — so this only asks whether
        // there is anything to pick.
        if (book.Selected is null || keys.Count == 0)
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

        using var dlg = new ScriptAiForm(book, keys, instruments, isSequence,
                                         currentScript, recentOutput);
        if (owner is Form f && f.Icon != null) dlg.Icon = f.Icon;
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.Script : null;
    }
}
