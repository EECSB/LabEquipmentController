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
/// follow-up rather than a fresh start. What that costs is shown under the box, and Clear is
/// how it is paid back — see <see cref="Conversations"/>.
///
/// Laid out as the AI assistant is (<see cref="AssistantForm"/>), because it is the same kind of
/// window: what the model is told about across the top, the conversation as cards in a sunken
/// pane, and under it the box, one row of pickers and switches with the button that sends at
/// its right end, and the cost of the next request in grey beneath. Two AI windows in one app
/// that put the same controls in different places are two layouts to learn for one job.
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

    /// <summary>Script boxes, as wide as the conversation pane.</summary>
    private readonly List<TextBox> _scripts = [];

    /// <summary>What the transcript costs to send, which is what Clear is for: under the box, as the assistant's estimate is.</summary>
    private readonly Label _ledger = new();

    // The assistant's own four faces, so the two windows' cards read alike.
    private readonly Font _bold = new("Segoe UI", 9.75f, FontStyle.Bold);
    private readonly Font _body = new("Segoe UI", 9.75f);
    private readonly Font _code = new("Consolas", 9.75f);
    private readonly Font _small = new("Segoe UI", 8.25f);

    private static readonly Color Warning = Color.FromArgb(150, 40, 0);
    private static readonly Color Sunken = Color.FromArgb(248, 248, 250);

    /// <summary>How tall the request box may grow before it scrolls instead.</summary>
    private const int PromptMaxLines = 8;

    /// <summary>How many of a script's lines show before its box scrolls, as in the assistant's cards.</summary>
    private const int ScriptMaxLines = 20;

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

        // Layout suspended across the whole build, as the designer does it, so the auto-scale
        // pass runs once over the finished window. Set without it, the pass ran as AutoScaleMode
        // was set, over an empty form, and never again: the fonts followed the screen and every
        // number below stayed in screen pixels. The numbers are logical pixels at 96 DPI now.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9f);
        Text = "Write a Script with AI";
        StartPosition = FormStartPosition.CenterParent;

        BuildUi();
        ResumeLayout(false);
        PerformLayout();

        // The window's own size, after the pass and scaled by the screen's DPI rather than by
        // the font's height, which the pass stretches further than the DPI going up. Both were
        // measured on a 175% screen, as 1320×920 and 920×700 there, and are that again there.
        //
        // Bigger than it was: at 1020×700 (175%) the draft showed six lines, which is not
        // enough of a script to read before agreeing to run it — and reading it is the whole
        // point of this window. Clamped to the screen in Load, so a large default is safe.
        ClientSize = LogicalToDeviceUnits(new Size(754, 526));
        // The smallest size where every control is whole and both text boxes are worth typing
        // in. The row under the box wraps rather than cutting off, so this is about height.
        MinimumSize = LogicalToDeviceUnits(new Size(546, 400));
    }

    private void BuildUi()
    {
        // --- what the model will be told about, so the user can see it before asking ---
        _context.Dock = DockStyle.Top;
        _context.AutoSize = true;
        _context.Padding = new Padding(12, 12, 12, 6);
        _context.Text = ContextSummary();
        _wrapping.Add(_context);

        // --- the conversation, which takes the window ---
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

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
        host.Controls.Add(_scroll);

        // --- the composer, as the assistant's: the box, one row under it, the cost beneath ---
        var composer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 8, 12, 0),
        };
        composer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _composer = composer;

        // One line, and as many as the text needs.
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
            UpdateEnabled();
            FitPrompt();
        };
        // Enter sends, because this is a chat and that is what Enter does in one; Shift+Enter
        // is the newline. SuppressKeyPress rather than Handled alone, or the box takes the
        // line break anyway and Windows dings on the way past.
        _prompt.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || e.Shift) return;
            e.Handled = e.SuppressKeyPress = true;
            if (_cts == null && HasPrompt) _ = GenerateAsync();
        };

        // Write Script keeps its own width at the right and the rest of the row takes what is
        // left, wrapping onto another line in a narrow window rather than cutting its end off.
        // The row's height is worked out here from the width it has, as the assistant's is: a
        // wrapping row measured by a table is measured at some other width.
        var options = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 6, 0, 0),
        };

        var pickers = new FlowLayoutPanel
        {
            WrapContents = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };

        // Which connection to spend. Choosing here chooses everywhere: there is one selection
        // for the app, so this window, the assistant and the datasheet extractor cannot
        // disagree about which key is being spent.
        Picker(_using, 200);
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
        pickers.Controls.Add(Labelled("Using:", _using));

        Picker(_effort, 160);
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
        pickers.Controls.Add(Labelled("Effort:", _effort));

        // What else travels with the request, in the row where the assistant has Changes.
        _revise.Text = "Revise the current script";
        _revise.AutoSize = true;
        _revise.Checked = _currentScript.Trim().Length > 0;
        _revise.Enabled = _currentScript.Trim().Length > 0;
        _revise.Margin = new Padding(0, 5, 18, 4);
        pickers.Controls.Add(_revise);

        _includeOutput.Text = "Include the last run's output";
        _includeOutput.AutoSize = true;
        _includeOutput.Checked = _recentOutput.Trim().Length > 0;
        _includeOutput.Enabled = _recentOutput.Trim().Length > 0;
        _includeOutput.Margin = new Padding(0, 5, 18, 4);
        pickers.Controls.Add(_includeOutput);

        // In the row with the other buttons, where the assistant keeps Attach Files and Tools.
        ButtonStyle.Apply(_clear, "Clear", (_, _) => ClearConversation());
        _clear.Margin = new Padding(0, 2, 6, 4);
        pickers.Controls.Add(_clear);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Right,
            Margin = new Padding(12, 0, 0, 0),
        };
        // Write Script and Stop are one button, in one place, as the assistant's Send is: a
        // double-click on Write Script must not stop what it sent, so a click arriving as the
        // second of a pair is let go.
        ButtonStyle.Apply(_generate, "Write Script");
        _generate.Enabled = false;
        _generate.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (_cts != null)
            {
                if (e.Clicks < 2) _cts.Cancel();
                return;
            }
            if (_generate.Enabled) _ = GenerateAsync();
        };
        buttons.Controls.Add(_generate);

        options.Controls.Add(pickers);
        options.Controls.Add(buttons);
        options.Layout += (_, _) =>
        {
            int room = Math.Max(1, options.ClientSize.Width - buttons.Width);
            int height = Math.Max(pickers.GetPreferredSize(new Size(room, 0)).Height, buttons.PreferredSize.Height);
            if (options.Height != height) options.Height = height;
        };

        _ledger.AutoSize = true;
        _ledger.ForeColor = SystemColors.GrayText;
        _ledger.Margin = new Padding(0, 4, 0, 6);
        _wrapping.Add(_ledger);

        composer.Controls.Add(_prompt, 0, 0);
        composer.Controls.Add(options, 0, 1);
        composer.Controls.Add(_ledger, 0, 2);

        _progress.Dock = DockStyle.Bottom;
        _progress.Height = 4;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.Visible = false;

        _status.Dock = DockStyle.Bottom;
        // Height comes from the font, once the form has passed its own down (see the
        // Load handler). A flat 22 was right at 100% and cut the descenders off at 175%,
        // back when this window scaled its fonts without scaling its own layout.
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(12, 0, 0, 0);
        _status.Text = "Ready.";

        // Fill first, then docked edges, from the middle outwards.
        var center = new Panel { Dock = DockStyle.Fill };
        center.Controls.Add(host);
        center.Controls.Add(_context);
        center.Controls.Add(composer);

        Controls.Add(center);
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
        // text needs, and only MaximumSize makes it wrap.
        Resize += (_, _) => FitLabelWidths();
        _scroll.SizeChanged += (_, _) => FitLabelWidths();

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
            // Again now the window has scaled: the example written into the box during the
            // build sized it in screen pixels, and the scaling pass then stretched that.
            FitPrompt();
        };

        Shown += (_, _) =>
        {
            DrawTranscript();
            FitLabelWidths();     // now that the labels have a real width to wrap against
            FitPrompt();
            _prompt.Focus();
            // Caret at the end of the example rather than in front of it: this box takes focus
            // when the window opens, and typing at position zero would push the worked request
            // along in front of whatever is being written.
            _prompt.SelectionStart = _prompt.TextLength;
        };

        // A request in flight has to be let go before the window that owns it disappears.
        FormClosing += (_, _) => _cts?.Cancel();
    }

    /// <summary>A label with the list it names, kept together when the row wraps — the assistant's own.</summary>
    private static FlowLayoutPanel Labelled(string label, Control control)
    {
        var pair = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 0, 18, 4),
        };
        pair.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        pair.Controls.Add(control);
        return pair;
    }

    private static void Picker(ComboBox combo, int width)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.DrawItem += (_, e) => ButtonStyle.DrawComboItem(combo, e);
        combo.Width = width;
        combo.Margin = new Padding(0, 2, 0, 2);
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
        _wrapping.RemoveAll(l => l != _context && l != _ledger);
        _scripts.Clear();
        foreach (Control old in _cards.Controls.Cast<Control>().ToList()) old.Dispose();
        _cards.Controls.Clear();
        _cards.RowStyles.Clear();
        _cards.RowCount = 0;

        if (_turns.Count == 0)
        {
            AddRow(_cards, Note(
                "Nothing asked yet. What comes back appears here, and each new request is asked "
              + "with everything above it, so a follow-up like “now do the same at 5 V” means "
              + "something."));
        }

        foreach (ScriptTurn turn in _turns)
        {
            AddRow(_cards, Question(turn));
            AddRow(_cards, Answer(turn));
        }

        _cards.ResumeLayout(true);

        _ledger.Text = LedgerText();
        UpdateEnabled();
        FitLabelWidths();
        ScrollToEnd();
    }

    /// <summary>What was asked, on the window's own white, as the assistant shows a question.</summary>
    private Control Question(ScriptTurn turn)
    {
        var box = Stack(SystemColors.Window);
        box.Controls.Add(Words("You", _bold));
        box.Controls.Add(Words(turn.Request.Trim(), _body));
        return box;
    }

    /// <summary>
    /// What was written, the button that takes it, and what the catalog check said — as the
    /// assistant shows a proposed script.
    /// </summary>
    private Control Answer(ScriptTurn turn)
    {
        var box = Stack(Color.Transparent);

        string script = turn.Script.Trim();
        if (script.Length > 0)
        {
            // Every line shown up to twenty, and a scroll bar down the side only past that; one
            // along the foot always, since a script's lines are not wrapped.
            int lines = script.Split('\n').Length;
            var text = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = lines > ScriptMaxLines ? ScrollBars.Both : ScrollBars.Horizontal,
                Font = _code,
                BackColor = SystemColors.Window,
                Text = script.Replace("\r\n", "\n").Replace("\n", "\r\n"),
                // Built after the window has scaled, so scaled here (SPEC §14).
                Height = Math.Min(ScriptMaxLines, lines) * _code.Height
                         + SystemInformation.HorizontalScrollBarHeight + LogicalToDeviceUnits(8),
                Width = LogicalToDeviceUnits(600),
                Margin = new Padding(0, LogicalToDeviceUnits(4), 0, 0),
                TabStop = false,
            };
            _scripts.Add(text);
            box.Controls.Add(text);

            // Under the script it takes, in the answer it belongs to. One at the foot of the
            // window could only ever mean the newest, which in a conversation is the wrong one
            // as often as it is the right one.
            var take = new Button { Margin = new Padding(0, LogicalToDeviceUnits(4), 0, LogicalToDeviceUnits(2)) };
            ButtonStyle.Apply(take, "Use This Script", (_, _) => UseScript(turn.Script));
            _tips.SetToolTip(take, "Put this script into the editor. It is not run — you still "
                                 + "press Run yourself.");
            box.Controls.Add(take);
        }

        foreach (string finding in Findings(turn))
        {
            box.Controls.Add(Words(finding, _small,
                turn.Undocumented is { Count: > 0 } ? Warning : SystemColors.GrayText));
        }

        return box;
    }

    private Control Note(string text)
    {
        var box = Stack(Color.Transparent);
        box.Controls.Add(Words(text, _small, SystemColors.GrayText));
        return box;
    }

    private FlowLayoutPanel Stack(Color back) => new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top,
        BackColor = back,
        Padding = new Padding(8, 6, 8, 6),
        Margin = new Padding(0, 0, 0, 6),
    };

    private Label Words(string text, Font font, Color? color = null)
    {
        var label = new Label
        {
            Text = text,
            Font = font,
            AutoSize = true,
            ForeColor = color ?? SystemColors.ControlText,
            Margin = new Padding(0, 0, 0, 2),
            UseMnemonic = false,
        };
        _wrapping.Add(label);
        return label;
    }

    private static void AddRow(TableLayoutPanel table, Control child)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(child, 0, table.RowCount++);
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
        return $"{_turns.Count} turn(s): {size} of transcript goes with every request. "
             + "Clear to start again without it.";
    }

    /// <summary>
    /// Forget the conversation.
    ///
    /// The whole thing: what is left is what a freshly-opened window would have had, which is
    /// what "clear" has to mean for the figure under the box to be true afterwards.
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
    /// Re-wrap everything that wraps, to the width the window is now: the cards' labels and
    /// script boxes to the conversation pane, less its padding and a scroll bar; the context
    /// and the cost line to the pane's full width, as the assistant's are.
    /// </summary>
    private void FitLabelWidths()
    {
        int width = Math.Max(LogicalToDeviceUnits(200), _scroll.ClientSize.Width - LogicalToDeviceUnits(48));
        int across = Math.Max(LogicalToDeviceUnits(200), _scroll.Width + LogicalToDeviceUnits(4));
        foreach (Label label in _wrapping.Where(l => !l.IsDisposed))
            label.MaximumSize = new Size(label == _context || label == _ledger ? across : width, 0);
        foreach (TextBox box in _scripts.Where(b => !b.IsDisposed))
            box.Width = width;
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
            + "catalogs and the script language to your AI connection (Enter). While it is "
            + "writing, Stop.");
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
            _status.Text = "Stopped.";
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
        _prompt.ReadOnly = busy;
        _revise.Enabled = !busy && _currentScript.Trim().Length > 0;
        _includeOutput.Enabled = !busy && _recentOutput.Trim().Length > 0;
        Cursor = busy ? Cursors.AppStarting : Cursors.Default;
        UpdateEnabled();
    }

    /// <summary>Write Script reads Stop while a request is out, as the assistant's Send does.</summary>
    private void UpdateEnabled()
    {
        bool busy = _cts != null;
        _generate.Text = busy ? "Stop" : "Write Script";
        _generate.Enabled = busy || HasPrompt;
        _clear.Enabled = !busy && _turns.Count > 0;
        _using.Enabled = !busy;
        _effort.Enabled = !busy;
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
