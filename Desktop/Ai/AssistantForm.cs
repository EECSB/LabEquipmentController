using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// The AI assistant: a conversation with a model that works the bench through the tools in
/// <see cref="BenchToolRunner"/> (SPEC §11d).
///
/// Reads run as the model asks for them; a <c>send</c>, the one tool that changes an instrument,
/// waits on a card for Approve, on the rung every visit starts on. Every call is a card either way,
/// with what it was told, so what happened to the bench is on screen whether or not anybody was
/// asked. A script is only ever put forward, with a Use This Script of its own, and runs by the
/// editor's Run (SPEC §11c).
///
/// The conversation is <see cref="AiChat"/>, and everything it stands on (the connections, the
/// provider, where chats are kept) is <see cref="IAiService"/>, so a test drives a whole
/// conversation with a stand-in provider and a fake bench, and the page's window is the same
/// conversation drawn in a browser. **Hidden rather than closed**, as GDS Viewer's floating window
/// is: the conversation outlives closing the window, and only New Chat starts another.
///
/// Around the conversation, as the AI guide lays it out: the kept chats down the left, the
/// conversation's files on the right, each closed by its cross and opened again by the arrow in
/// the margin; the suggestion to compact, priced, over the box; and a slash at the start of the box
/// for the window's commands.
/// </summary>
public sealed class AssistantForm : Form
{
    private const string Where = "the desktop app";
    private const string NoConnection = "No AI connection is set up. Tools ▸ AI Connection… adds one.";
    private const string WhyBusy = "Not while an answer is coming. Stop it first.";
    private const int PromptMaxLines = 8;

    private readonly AiChat _chat = new();
    private readonly IAiService _ai;
    private readonly SessionRegistry _sessions;
    private readonly SessionBench _bench;
    private readonly BenchToolRunner _tools;

    private AiConnections _book = new();
    private AiConnection? _connection;
    private bool _picking;
    private IReadOnlyList<BenchInstrument> _benchNow = [];

    // The chat on screen, as it is kept: its id once its first question was asked, when it began,
    // and a count of the chats put on screen, so an answer that lands after another chat was opened
    // is not kept under that one's id.
    private string? _chatId;
    private DateTime _created;
    private int _shown;
    private List<ChatSummary> _chatList = [];
    private string? _confirmingDelete;

    // Open or shut is the person's to choose, and holds across conversations.
    private bool _chatsOpen = true;
    private bool _filesOpen;
    private bool _showFolded;
    private bool _stale;

    private readonly ComboBox _using = new();
    private readonly ComboBox _effort = new();
    private readonly ComboBox _rung = new();
    private readonly TextBox _prompt = new();
    private readonly Button _send = new();
    private readonly Button _attach = new();
    private readonly Button _screenshot = new();
    private readonly Button _toolsButton = new();
    private readonly Button _newChat = new();
    private readonly ContextMenuStrip _toolMenu = new();
    private readonly FlowLayoutPanel _chips = new();
    private readonly Label _attachProblem = new();
    private readonly Label _estimate = new();
    private readonly Label _context = new();
    private readonly Panel _scroll = new();
    private readonly TableLayoutPanel _cards = new();
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly ToolTip _tips = new();

    private readonly Panel _compactBar = new();
    private readonly Label _compactText = new();
    private readonly Button _compact = new();
    private readonly Button _notNow = new();

    private readonly ListBox _slash = new();
    private List<SlashCommand> _slashShown = [];
    private string? _slashPutAwayFor;

    private readonly Panel _chatsPanel = new();
    private readonly Panel _chatScroll = new();
    private readonly Label _keptWhere = new();
    private readonly Button _chatsStrip = new();

    private readonly Panel _filesPanel = new();
    private readonly Panel _fileScroll = new();
    private readonly Label _filesFoot = new();
    private readonly Button _filesStrip = new();

    /// <summary>Labels that wrap, so their wrap width can follow the window.</summary>
    private readonly List<Label> _wrapping = [];

    /// <summary>A card's picture, decoded once rather than at every redraw.</summary>
    private readonly Dictionary<ChatTurn, Image> _pictures = [];

    /// <summary>Set while the cards a compaction folded away are drawn, which are dimmed.</summary>
    private bool _dimming;

    private readonly Font _bold = new("Segoe UI", 9.75f, FontStyle.Bold);
    private readonly Font _body = new("Segoe UI", 9.75f);
    private readonly Font _code = new("Consolas", 9.75f);
    private readonly Font _small = new("Segoe UI", 8.25f);

    private static readonly Color Warning = Color.FromArgb(150, 40, 0);
    private static readonly Color Rule = Color.FromArgb(224, 226, 230);
    private static readonly Color Sunken = Color.FromArgb(248, 248, 250);
    private static readonly Color Waiting = Color.FromArgb(255, 248, 225);
    private static readonly Color Chosen = Color.FromArgb(232, 238, 248);

    /// <summary>One of the window's slash commands: its name, what it does, why it cannot run now (or null), and what it does with any words after its name.</summary>
    private sealed record SlashCommand(string Name, string What, string? WhyNot, Action<string> Run);

    /// <summary>A proposed script's Use This Script was pressed: which kind, for which instrument, and the script.</summary>
    public event Action<string, BenchInstrument?, string>? ScriptUsed;

    /// <summary>The window asked for AI Connection: <c>/settings</c>, or a link in a line about a missing key.</summary>
    public event Action? SettingsRequested;

    public AssistantForm(IAiService ai, SessionRegistry sessions, Action<InstrumentSession, string, string?> logToConsole)
    {
        _ai = ai;
        _sessions = sessions;
        _bench = new SessionBench(() => [.. _sessions.Sessions]);
        _bench.Exchanged += (session, command, reply) => OnUi(() => logToConsole(session, command, reply));
        _tools = new BenchToolRunner(_bench) { Off = _chat.ToolsOff };

        // Built with layout suspended, as the designer builds a form, so the scaling to the
        // screen's DPI runs once at ResumeLayout over the whole window. Without it the scaling
        // runs as AutoScaleMode is set, over an empty form, and never again: the fonts come out
        // at the screen's size and every width in pixels at 96 DPI's.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9f);
        Text = "AI Assistant";
        ClientSize = new Size(1100, 860);
        MinimumSize = new Size(860, 620);
        StartPosition = FormStartPosition.CenterParent;
        AllowDrop = true;

        BuildUi();
        ResumeLayout(false);
        PerformLayout();
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    // ------------------------------------------------------------------- building

    private void BuildUi()
    {
        // --- what the model will be told about, so the person can see it before asking ---
        _context.Dock = DockStyle.Top;
        _context.AutoSize = true;
        _context.Padding = new Padding(12, 12, 12, 6);
        _wrapping.Add(_context);

        // --- the conversation, which takes the window ---
        _scroll.Dock = DockStyle.Fill;
        _scroll.AutoScroll = true;
        _scroll.BorderStyle = BorderStyle.FixedSingle;
        _scroll.BackColor = Sunken;
        _scroll.Padding = new Padding(10, 8, 10, 8);
        _scroll.AllowDrop = true;

        _cards.Dock = DockStyle.Top;
        _cards.ColumnCount = 1;
        _cards.AutoSize = true;
        _cards.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _cards.BackColor = Color.Transparent;
        _cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _scroll.Controls.Add(_cards);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
        host.Controls.Add(_scroll);

        var center = new Panel { Dock = DockStyle.Fill };
        center.Controls.Add(host);
        center.Controls.Add(_context);
        center.Controls.Add(BuildCompactBar());
        center.Controls.Add(BuildComposer());

        _progress.Dock = DockStyle.Bottom;
        _progress.Height = 4;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.Visible = false;

        _status.Dock = DockStyle.Bottom;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(12, 0, 0, 0);
        _status.Text = "Ready.";

        // Docked in reverse: the status and the bar first, across the whole foot; then the panels
        // and their arrows, the window's height above the foot; then the conversation between them.
        Controls.Add(center);
        Controls.Add(BuildChatsPanel());
        // On a window too narrow for both, the chats' arrow is a choice of the chats over the
        // files, which shut to make the room; otherwise it would open nothing.
        Controls.Add(Strip(_chatsStrip, DockStyle.Left, "›", "Show the chats kept " + _ai.KeptWhere, () =>
        {
            _chatsOpen = true;
            if (_filesOpen && !RoomForBoth) _filesOpen = false;
            ShowPanels();
        }));
        Controls.Add(BuildFilesPanel());
        Controls.Add(Strip(_filesStrip, DockStyle.Right, "‹", "Show the files in this conversation", () => { _filesOpen = true; ShowPanels(); }));
        Controls.Add(_progress);
        Controls.Add(_status);

        _tips.SetToolTip(_using, "Which saved AI connection answers. They are set up under Tools ▸ AI Connection…");
        _tips.SetToolTip(_effort, "How hard the model is asked to think, for this question only. Thinking is billed as output.");
        _tips.SetToolTip(_rung,
            "Ask before every step: every call waits for you, reads included.\n"
          + "Ask before changes: reads run at once; each send waits for you.\n"
          + "Change without asking: sends run at once too. There is no undo on an instrument.");
        _tips.SetToolTip(_attach, "Attach pictures, PDFs or text files to the next question. A picture can be pasted into the box, and files dropped on the window.");
        _tips.SetToolTip(_screenshot, "Attach a picture of the screen this window is on. The window steps aside while it is taken.");
        _tips.SetToolTip(_toolsButton, "Switch tools off for this conversation. A tool switched off is not offered, and the model is told so.");
        _tips.SetToolTip(_send, "Send the question (Enter). While an answer is coming, Stop.");
        _tips.SetToolTip(_prompt, "Enter sends, Shift+Enter starts a new line. A slash at the start lists the window's commands.");

        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AttachPaths(paths); };
        _scroll.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        _scroll.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AttachPaths(paths); };

        Resize += (_, _) =>
        {
            FitPanels();
            FitLabelWidths();
        };
        _toolMenu.Closing += KeepOpenWhileTicking;
        _chat.Changed += OnChatChanged;
        _ai.Chats.Changed += OnChatsChanged;
        _ai.ConnectionsChanged += OnConnectionsChanged;

        // Closing hides it: the conversation, the files waiting and what was spent stay for the
        // next visit. Only the main window closing it, or the app ending, closes it for good.
        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Hide();
        };
        FormClosed += (_, _) =>
        {
            _chat.Cancel();
            _chat.Changed -= OnChatChanged;
            _ai.Chats.Changed -= OnChatsChanged;
            _ai.ConnectionsChanged -= OnConnectionsChanged;
            ClearPictures();
        };

        Load += async (_, _) =>
        {
            Size wa = Screen.GetWorkingArea(this).Size;
            Size = new Size(Math.Min(Math.Max(Width, MinimumSize.Width), wa.Width),
                            Math.Min(Math.Max(Height, MinimumSize.Height), wa.Height));
            _status.Height = _status.PreferredHeight + LogicalToDeviceUnits(6);
            ButtonStyle.Normalize(this, _send, _attach, _screenshot, _toolsButton, _newChat, _compact, _notNow);
            ButtonStyle.SetDrawnIcon(this, _send, "ai");
            ButtonStyle.SetDrawnIcon(this, _screenshot, "camera");
            _slash.ItemHeight = Math.Max(_bold.Height, _body.Height) + LogicalToDeviceUnits(8);
            ShowPanels();
            await PickConnectionAsync();
        };

        Shown += (_, _) =>
        {
            // The box opens on a question that works as it stands, selected, so Enter sends it and
            // the first key typed replaces it.
            _prompt.Text = _chat.Turns.Count == 0 ? AssistantPrompt.StatusQuestion : "";
            ShowContext();
            DrawAll();
            FitPrompt();
            _prompt.Focus();
            _prompt.SelectAll();
        };
        VisibleChanged += (_, _) =>
        {
            if (!Visible) return;
            ShowContext();
            if (_stale) DrawAll();
        };
        Activated += (_, _) => ShowContext();
    }

    private Control BuildComposer()
    {
        var composer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 8, 12, 0),
        };
        composer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        // The files waiting to go with the next question, each with its size and a cross.
        _chips.AutoSize = true;
        _chips.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _chips.Dock = DockStyle.Fill;
        _chips.Margin = new Padding(0, 0, 0, 4);
        _chips.Visible = false;

        _attachProblem.AutoSize = true;
        _attachProblem.ForeColor = Warning;
        _attachProblem.Margin = new Padding(0, 0, 0, 4);
        _attachProblem.Visible = false;
        _wrapping.Add(_attachProblem);

        // The window's commands, listed while a slash starts the box (see SlashCommands).
        _slash.Dock = DockStyle.Fill;
        _slash.DrawMode = DrawMode.OwnerDrawFixed;
        _slash.IntegralHeight = false;
        _slash.TabStop = false;
        _slash.BorderStyle = BorderStyle.FixedSingle;
        _slash.Margin = new Padding(0, 0, 0, 4);
        _slash.Visible = false;
        _slash.DrawItem += DrawSlashItem;
        _slash.MouseClick += (_, e) =>
        {
            int i = _slash.IndexFromPoint(e.Location);
            if (i >= 0 && i < _slashShown.Count) RunSlash(_slashShown[i], "");
        };

        _prompt.Multiline = true;
        _prompt.WordWrap = true;
        _prompt.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _prompt.Font = new Font("Segoe UI", 10f);
        _prompt.PlaceholderText = "Ask about the bench, or type / for the window's commands";
        _prompt.Margin = new Padding(0);
        _prompt.TextChanged += (_, _) => { UpdateSlash(); UpdateEnabled(); FitPrompt(); ShowEstimate(); };
        _prompt.KeyDown += (_, e) =>
        {
            if (_slash.Visible && _slashShown.Count > 0)
            {
                if (e.KeyCode is Keys.Up or Keys.Down)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    int next = _slash.SelectedIndex + (e.KeyCode == Keys.Up ? -1 : 1);
                    _slash.SelectedIndex = (next + _slashShown.Count) % _slashShown.Count;
                    return;
                }
                if (e.KeyCode == Keys.Enter && !e.Shift)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    RunSlash(_slashShown[Math.Max(0, _slash.SelectedIndex)], "");
                    return;
                }
            }
            // Enter sends and Shift+Enter breaks the line, as in any chat.
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.Handled = e.SuppressKeyPress = true;
                if (!_chat.Generating) SendFromBox();
                return;
            }
            // A picture on the clipboard, pasted into the box, goes with the question. Text on the
            // clipboard as well wins: cells copied out of a spreadsheet bring a picture of
            // themselves along, and the cells are what was meant.
            if (e.KeyCode == Keys.V && e.Control && !Clipboard.ContainsText()
                && (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList()))
            {
                e.Handled = e.SuppressKeyPress = true;
                PasteFiles();
            }
        };

        // Send keeps its own width at the right and the pickers take what is left, so with both
        // panels open it is the pickers that wrap, never the button that goes. The row's height is
        // worked out here from the width it has: a wrapping row measured by a table is measured
        // at some other width, and leaves a gap under it or cuts its last line off.
        var options = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 6, 0, 0),
        };

        // The pickers and the file buttons in one row that wraps, each label kept with its list,
        // so a narrow window takes another line rather than cutting the end of the row off.
        var pickers = new FlowLayoutPanel
        {
            WrapContents = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };

        Picker(_using, 200);
        _using.SelectedIndexChanged += async (_, _) =>
        {
            if (_picking || _using.SelectedIndex < 0 || _using.SelectedIndex >= _book.Items.Count) return;
            await _ai.SelectAsync(_book.Items[_using.SelectedIndex].Id);
            await PickConnectionAsync();
        };
        pickers.Controls.Add(Labelled("Using:", _using));

        Picker(_effort, 160);
        _effort.SelectedIndexChanged += (_, _) => ShowEstimate();
        pickers.Controls.Add(Labelled("Effort:", _effort));

        // How far the model may go on its own. Never remembered: every start of the app is on the
        // middle rung, so a restart never finds Change without asking already on.
        Picker(_rung, 180);
        foreach (ToolApprovalMode mode in AssistantPrompt.Rungs) _rung.Items.Add(AssistantPrompt.RungLabel(mode));
        _rung.SelectedIndex = AssistantPrompt.Rungs.ToList().IndexOf(_chat.Mode);
        // In force at once, an answer in the middle of coming included: a card waiting that the
        // new rung would not have stopped for is let go there and then (AiChat.Mode).
        _rung.SelectedIndexChanged += (_, _) =>
        {
            if (_rung.SelectedIndex >= 0) _chat.Mode = AssistantPrompt.Rungs[_rung.SelectedIndex];
            ShowEstimate();
        };
        pickers.Controls.Add(Labelled("Changes:", _rung));

        ButtonStyle.Apply(_attach, "Attach Files…", (_, _) => ChooseFiles());
        ButtonStyle.Apply(_screenshot, "Screenshot", async (_, _) => await TakeScreenshotAsync());
        // The tools this conversation may use: switched off, one is not offered, is refused if
        // called anyway, and is named in the prompt; kept through New Chat, as somebody who
        // switched send off wants it off in the next conversation too.
        ButtonStyle.Apply(_toolsButton, "Tools…", (_, _) => ShowToolMenu());
        foreach (Button b in new[] { _attach, _screenshot, _toolsButton })
        {
            b.Margin = new Padding(0, 2, 6, 4);
            pickers.Controls.Add(b);
        }

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.RightToLeft,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Margin = new Padding(12, 0, 0, 0),
        };
        // Send and Stop are one button, in one place: a double-click on Send must not stop what
        // it sent, so a click arriving as the second of a pair is let go.
        ButtonStyle.Apply(_send, "Send");
        _send.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (_chat.Generating)
            {
                if (e.Clicks < 2) _chat.Cancel();
                return;
            }
            if (_send.Enabled) SendFromBox();
        };
        buttons.Controls.Add(_send);

        buttons.Dock = DockStyle.Right;
        options.Controls.Add(pickers);
        options.Controls.Add(buttons);
        options.Layout += (_, _) =>
        {
            int room = Math.Max(1, options.ClientSize.Width - buttons.Width);
            int height = Math.Max(pickers.GetPreferredSize(new Size(room, 0)).Height, buttons.PreferredSize.Height);
            if (options.Height != height) options.Height = height;
        };

        _estimate.AutoSize = true;
        _estimate.ForeColor = SystemColors.GrayText;
        _estimate.Margin = new Padding(0, 4, 0, 6);
        _wrapping.Add(_estimate);

        composer.Controls.Add(_chips, 0, 0);
        composer.Controls.Add(_attachProblem, 0, 1);
        composer.Controls.Add(_slash, 0, 2);
        composer.Controls.Add(_prompt, 0, 3);
        composer.Controls.Add(options, 0, 4);
        composer.Controls.Add(_estimate, 0, 5);
        return composer;
    }

    /// <summary>The suggestion to compact, priced both ways: what the history costs every question now, and the compaction once.</summary>
    private Control BuildCompactBar()
    {
        _compactBar.Dock = DockStyle.Bottom;
        _compactBar.Padding = new Padding(12, 8, 12, 0);
        _compactBar.Visible = false;

        var inner = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Waiting,
            Padding = new Padding(8, 6, 8, 2),
        };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _compactText.AutoSize = true;
        _compactText.Margin = new Padding(0, 4, 12, 4);
        _wrapping.Add(_compactText);
        ButtonStyle.Apply(_compact, "Compact", async (_, _) => await CompactAsync());
        ButtonStyle.Apply(_notNow, "Not Now", (_, _) =>
        {
            _chat.CompactSuggestionDismissed = true;
            ShowCompactSuggestion();
        });
        _tips.SetToolTip(_compact, "Put a briefing in place of the conversation so far. What it stands in for stays on screen, and is not sent again.");
        _tips.SetToolTip(_notNow, "Put the suggestion away until the conversation is next compacted, or a new one starts.");
        inner.Controls.Add(_compactText, 0, 0);
        inner.Controls.Add(_compact, 1, 0);
        inner.Controls.Add(_notNow, 2, 0);
        _compactBar.Controls.Add(inner);
        _compactBar.Height = inner.PreferredSize.Height + _compactBar.Padding.Vertical;
        inner.SizeChanged += (_, _) => _compactBar.Height = inner.Height + _compactBar.Padding.Vertical;
        return _compactBar;
    }

    private Control BuildChatsPanel()
    {
        _chatsPanel.Dock = DockStyle.Left;
        _chatsPanel.Width = 240;
        _chatsPanel.Padding = new Padding(10, 10, 8, 6);
        _chatsPanel.Paint += (_, e) =>
        {
            using var pen = new Pen(Rule);
            e.Graphics.DrawLine(pen, _chatsPanel.Width - 1, 0, _chatsPanel.Width - 1, _chatsPanel.Height);
        };

        var head = PanelHead("Chats", "Hide the chats", () => { _chatsOpen = false; ShowPanels(); }, _newChat);
        ButtonStyle.Apply(_newChat, "New Chat", (_, _) => NewChat());
        _newChat.Margin = new Padding(0, 0, 6, 0);
        _tips.SetToolTip(_newChat, "Start a new conversation. This one stays in the list below, and what was spent stays counted.");

        _keptWhere.Dock = DockStyle.Top;
        _keptWhere.AutoSize = true;
        _keptWhere.Font = _small;
        _keptWhere.ForeColor = SystemColors.GrayText;
        _keptWhere.Padding = new Padding(0, 6, 0, 6);
        _keptWhere.Text = $"Kept {_ai.KeptWhere}.";
        _tips.SetToolTip(_keptWhere, $"Every chat is kept {_ai.KeptWhere} from its first question on, after every answer, with its files.");

        _chatScroll.Dock = DockStyle.Fill;
        _chatScroll.AutoScroll = true;

        _chatsPanel.Controls.Add(_chatScroll);
        _chatsPanel.Controls.Add(_keptWhere);
        _chatsPanel.Controls.Add(head);
        return _chatsPanel;
    }

    private Control BuildFilesPanel()
    {
        _filesPanel.Dock = DockStyle.Right;
        _filesPanel.Width = 260;
        _filesPanel.Padding = new Padding(10, 10, 10, 6);
        _filesPanel.Paint += (_, e) =>
        {
            using var pen = new Pen(Rule);
            e.Graphics.DrawLine(pen, 0, 0, 0, _filesPanel.Height);
        };

        var head = PanelHead("Files", "Hide the files", () => { _filesOpen = false; ShowPanels(); }, null);

        _fileScroll.Dock = DockStyle.Fill;
        _fileScroll.AutoScroll = true;

        _filesFoot.Dock = DockStyle.Bottom;
        _filesFoot.AutoSize = true;
        _filesFoot.Font = _small;
        _filesFoot.ForeColor = SystemColors.GrayText;
        _filesFoot.Padding = new Padding(0, 6, 0, 0);
        _filesFoot.MaximumSize = new Size(240, 0);

        _filesPanel.Controls.Add(_fileScroll);
        _filesPanel.Controls.Add(_filesFoot);
        _filesPanel.Controls.Add(head);
        return _filesPanel;
    }

    /// <summary>A panel's heading: its name, a button where it has one, and the cross that closes it.</summary>
    private TableLayoutPanel PanelHead(string title, string closeTip, Action close, Button? button)
    {
        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.Controls.Add(new Label { Text = title, Font = _bold, AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, 0, 0);
        if (button is not null) head.Controls.Add(button, 1, 0);
        head.Controls.Add(Link("✕", closeTip, close, new Padding(4, 6, 0, 0)), 2, 0);
        return head;
    }

    /// <summary>
    /// The arrow in the window's margin that opens a closed panel: the whole margin is the button.
    ///
    /// The arrow sits in the middle of the margin, top to bottom, at twice the window's text
    /// size and in the text's own colour. It was a grey arrow of the window's text size in the
    /// top corner of a 22-pixel margin, which could not be seen as something to press.
    /// </summary>
    private Button Strip(Button strip, DockStyle side, string arrow, string tip, Action open)
    {
        strip.Dock = side;
        strip.Width = 32;
        strip.Text = arrow;
        strip.Font = new Font(Font.FontFamily, Font.SizeInPoints * 2f, FontStyle.Bold, GraphicsUnit.Point);
        strip.TextAlign = ContentAlignment.MiddleCenter;
        strip.FlatStyle = FlatStyle.Flat;
        strip.FlatAppearance.BorderSize = 0;
        strip.FlatAppearance.MouseOverBackColor = SystemColors.ControlLight;
        strip.FlatAppearance.MouseDownBackColor = SystemColors.ControlDark;
        strip.ForeColor = SystemColors.ControlText;
        strip.Padding = new Padding(0);
        strip.TabStop = false;
        strip.Click += (_, _) => open();
        _tips.SetToolTip(strip, tip);
        return strip;
    }

    private LinkLabel Link(string text, string tip, Action click, Padding margin)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            Margin = margin,
            LinkBehavior = LinkBehavior.HoverUnderline,
            UseMnemonic = false,
        };
        link.LinkClicked += (_, _) => click();
        _tips.SetToolTip(link, tip);
        return link;
    }

    /// <summary>A label and its list, kept on one line of the wrapping row.</summary>
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

    private void Picker(ComboBox combo, int width)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.DrawItem += (_, e) => ButtonStyle.DrawComboItem(combo, e);
        combo.Width = width;
        combo.Margin = new Padding(0, 2, 0, 2);
    }

    /// <summary>
    /// Esc closes the window, which keeps the conversation: New Chat is what starts another. While
    /// the command list is up, Esc puts it away instead and Tab writes the marked command's name out.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_slash.Visible && _prompt.Focused && _slashShown.Count > 0)
        {
            if (keyData == Keys.Tab)
            {
                string name = _slashShown[Math.Max(0, _slash.SelectedIndex)].Name;
                _prompt.Text = name + " ";
                _prompt.SelectionStart = _prompt.TextLength;
                return true;
            }
            if (keyData == Keys.Escape)
            {
                _slashPutAwayFor = _prompt.Text;
                HideSlash();
                return true;
            }
        }
        if (keyData == Keys.Escape)
        {
            Hide();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// The panels as the person left them, open or shut. A window too narrow for both and the
    /// conversation between them keeps the files and folds the chats to their arrow for as long as
    /// it is that narrow, without changing what was chosen: wider again, the chats come back.
    /// </summary>
    private void ShowPanels()
    {
        FitPanels(force: true);
        if (_chatsShown) _ = DrawChatsAsync();
        DrawFiles();
        FitLabelWidths();
    }

    /// <summary>Whether the chats panel is on screen now, which a narrow window can make differ from <see cref="_chatsOpen"/>.</summary>
    private bool _chatsShown;

    /// <summary>Whether the window is wide enough for both panels and the conversation between them.</summary>
    private bool RoomForBoth => ClientSize.Width >= LogicalToDeviceUnits(240 + 260 + 620);

    /// <summary>Shows and hides the panels and their arrows to fit the window's width; called as it is resized.</summary>
    private void FitPanels(bool force = false)
    {
        bool chatsShown = _chatsOpen && (!_filesOpen || RoomForBoth);
        if (!force && chatsShown == _chatsShown) return;

        bool cameBack = chatsShown && !_chatsShown;
        _chatsShown = chatsShown;

        SuspendLayout();
        _chatsPanel.Visible = chatsShown;
        _chatsStrip.Visible = !chatsShown;
        _filesPanel.Visible = _filesOpen;
        _filesStrip.Visible = !_filesOpen;
        ResumeLayout();
        if (cameBack && !force) DrawChatRows();
    }

    // ------------------------------------------------------------------- the connection

    private async Task PickConnectionAsync()
    {
        _book = await _ai.LoadConnectionsAsync();

        _picking = true;
        _using.Items.Clear();
        foreach (string label in _book.Labels()) _using.Items.Add(label);
        _connection = _book.Selected?.Clone();
        _using.SelectedIndex = _connection is null ? -1 : _book.Items.FindIndex(c => c.Id == _connection.Id);

        // The levels the model takes, chosen per question and never saved; the default says what it is.
        _effort.Items.Clear();
        _effort.Items.Add(DefaultEffortLabel());
        foreach (string level in AiModels.EffortLevels(_connection)) _effort.Items.Add(Cap(level));
        _effort.SelectedIndex = 0;
        _effort.Enabled = _effort.Items.Count > 1;
        _picking = false;

        UpdateEnabled();
        DrawAll();
    }

    private string DefaultEffortLabel()
    {
        string said = AiModels.DefaultEffort(_connection);
        return said.Length == 0 ? "Default" : $"Default ({said})";
    }

    private string ChosenEffort()
        => _effort.SelectedIndex <= 0 ? "" : AiModels.EffortLevels(_connection)[_effort.SelectedIndex - 1];

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Why a question cannot be asked now, or null: no connection, no key, or a file the connection cannot be sent.</summary>
    private string? WhyNotAskNow()
    {
        if (_connection is null) return NoConnection;
        return _ai.WhyNotAsk(_connection) ?? WhyNotSend();
    }

    // ------------------------------------------------------------------- sending

    private void SendFromBox()
    {
        string text = _prompt.Text;
        if (TryRunSlash(text)) return;
        if (_send.Enabled) _ = SendAsync(text, fromBox: true);
    }

    private async Task SendAsync(string text, bool fromBox)
    {
        if (_chat.Generating || string.IsNullOrWhiteSpace(text)) return;
        if (WhyNotAskNow() is string why)
        {
            _status.Text = why;
            return;
        }

        AiConnection connection = _connection!;
        ILlmProvider provider = _ai.CreateProvider(connection, ChosenEffort());
        _benchNow = BenchNow();
        string system = AssistantPrompt.Build(_benchNow, _chat.Mode, _chat.ToolsOff, Where, DateTime.Now);
        int shown = _shown;

        if (fromBox) _prompt.Text = "";
        _status.Text = "Asking " + connection.EffectiveName + "…";
        ChatOutcome outcome = await _chat.SendAsync(text, provider, system, _tools);

        // A question that failed gave its files back to the box; its words go back with them
        // when the box is still empty, so asking again is one press.
        if (outcome == ChatOutcome.Failed && _chat.GaveFilesBack && _prompt.TextLength == 0)
            _prompt.Text = text;
        _status.Text = outcome switch
        {
            ChatOutcome.Stopped => "Stopped.",
            ChatOutcome.Failed => "The question failed; the reason is in the conversation.",
            ChatOutcome.Paused => "Paused at the limit on rounds. Continue carries on.",
            _ => SpentLine(),
        };
        if (outcome != ChatOutcome.NotSent) await KeepAsync(shown);
    }

    /// <summary>
    /// Compacts the conversation on the model picked, at its lowest effort: a briefing needs care
    /// with names rather than the model's best reasoning, and the connection is the one somebody
    /// saved with their own key; another is not theirs to be sent to.
    /// </summary>
    private async Task CompactAsync()
    {
        if (_chat.WhyNotCompact() is string cannot)
        {
            _status.Text = cannot;
            return;
        }
        if (_connection is null || _ai.WhyNotAsk(_connection) is not null)
        {
            _status.Text = WhyNotAskNow() ?? NoConnection;
            return;
        }

        IReadOnlyList<string> levels = AiModels.EffortLevels(_connection);
        ILlmProvider provider = _ai.CreateProvider(_connection, levels.Count > 0 ? levels[0] : "");
        int shown = _shown;

        _status.Text = "Compacting the conversation…";
        ChatOutcome outcome = await _chat.CompactAsync(provider);
        _status.Text = outcome switch
        {
            ChatOutcome.Answered => "Compacted: the briefing stands in for what came before it. " + SpentLine(),
            ChatOutcome.Stopped => "Compacting was stopped. The conversation is as it was.",
            ChatOutcome.Failed => "Compacting failed. The conversation is as it was.",
            _ => _status.Text,
        };
        if (outcome != ChatOutcome.NotSent) await KeepAsync(shown);
    }

    /// <summary>Why Send waits, or null: a file in the box that the picked model cannot be sent.</summary>
    private string? WhyNotSend()
    {
        if (_connection is null) return null;
        foreach (AiAttachment file in _chat.Pending)
        {
            if (AiAttachment.NotSentTo(_connection, file) is string reason) return reason;
        }
        return null;
    }

    /// <summary>The instruments open now, as the tools list them. The desktop's bench answers at once: it is the sessions this process holds.</summary>
    private IReadOnlyList<BenchInstrument> BenchNow() => _bench.ListAsync(CancellationToken.None).GetAwaiter().GetResult();

    private string SpentLine()
    {
        LlmUsage used = _chat.Used;
        if (!used.Reported) return "Ready.";
        string tokens = $"{AiPricing.Count(used.InputTokens)} tokens in, {AiPricing.Count(used.OutputTokens)} out";
        return _chat.Cost is decimal dollars
            ? $"Spent since the app started: {tokens}, {AiPricing.AboutMoney(dollars)}."
            : $"Spent since the app started: {tokens}.";
    }

    // ------------------------------------------------------------------- slash commands

    /// <summary>
    /// The window's commands, built in one place from the state its buttons are enabled by, so a
    /// command and its button cannot disagree. One that cannot run now stays listed, dimmed, saying
    /// why. Nothing reaches the model by its slash: a name that is no command is sent as written.
    /// </summary>
    private List<SlashCommand> SlashCommands()
    {
        string? answering = _chat.Generating ? "Not while an answer is coming." : null;
        string? asking = answering ?? WhyNotAskNow();
        return
        [
            new("/new", "Start a new chat. This one stays in the chats list.", answering, _ => NewChat()),
            new("/compact", "Put a briefing in place of the conversation so far, to send less with every question.",
                _chat.WhyNotCompact() ?? (_connection is null ? NoConnection : _ai.WhyNotAsk(_connection)), none => _ = CompactAsync()),
            new("/export", "Save this conversation as text.",
                answering ?? (_chat.Turns.Count == 0 ? "Nothing has been said yet." : null), _ => SaveConversation()),
            new("/tools", "Choose the tools this conversation may use.", answering, _ => ShowToolMenu()),
            new("/screenshot", "Attach a picture of the screen this window is on.", null, none => _ = TakeScreenshotAsync()),
            new("/settings", "Open AI Connection, where the connections and their keys are kept.", null, _ => SettingsRequested?.Invoke()),
            new("/status", "Ask what is open and what each instrument reads now.", asking,
                words => _ = SendAsync(With(AssistantPrompt.StatusQuestion, words), fromBox: false)),
            new("/errors", "Ask for every instrument's error queue, read and explained.", asking,
                words => _ = SendAsync(With(AssistantPrompt.ErrorsQuestion, words), fromBox: false)),
            new("/script", "Start a request for a script, to finish in the box.", null, _ => StartRequest(AssistantPrompt.ScriptStart)),
        ];
    }

    private static string With(string question, string words)
        => string.IsNullOrWhiteSpace(words) ? question : $"{question} {words.Trim()}";

    /// <summary>Runs the box's words as a command when they start with one written out in full; false when they are to be sent.</summary>
    private bool TryRunSlash(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith('/')) return false;

        int space = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        string name = space < 0 ? trimmed : trimmed[..space];
        SlashCommand? command = SlashCommands().FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (command is null) return false;

        RunSlash(command, space < 0 ? "" : trimmed[(space + 1)..]);
        return true;
    }

    private void RunSlash(SlashCommand command, string words)
    {
        HideSlash();
        if (command.WhyNot is string why)
        {
            _status.Text = $"{command.Name}: {why}";
            return;
        }
        _prompt.Text = "";
        _prompt.Focus();
        command.Run(words);
    }

    private void UpdateSlash()
    {
        string text = _prompt.Text;
        if (text != _slashPutAwayFor) _slashPutAwayFor = null;

        bool typing = text.StartsWith('/') && !text.Any(char.IsWhiteSpace) && _slashPutAwayFor is null;
        _slashShown = typing
            ? [.. SlashCommands().Where(c => c.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase))]
            : [];
        if (_slashShown.Count == 0)
        {
            HideSlash();
            return;
        }

        int marked = Math.Max(0, _slash.SelectedIndex);
        _slash.BeginUpdate();
        _slash.Items.Clear();
        foreach (SlashCommand command in _slashShown) _slash.Items.Add(command.Name);
        _slash.SelectedIndex = Math.Min(marked, _slashShown.Count - 1);
        _slash.Height = _slashShown.Count * _slash.ItemHeight + 4;
        _slash.EndUpdate();
        _slash.Visible = true;
    }

    private void HideSlash()
    {
        _slash.Visible = false;
        _slashShown = [];
    }

    private void DrawSlashItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _slashShown.Count) return;
        SlashCommand command = _slashShown[e.Index];
        bool marked = (e.State & DrawItemState.Selected) != 0;

        using (var back = new SolidBrush(marked ? SystemColors.Highlight : SystemColors.Window))
            e.Graphics.FillRectangle(back, e.Bounds);

        Color fore = marked ? SystemColors.HighlightText : command.WhyNot is null ? SystemColors.ControlText : SystemColors.GrayText;
        int nameWidth = LogicalToDeviceUnits(96);
        var nameBox = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, nameWidth, e.Bounds.Height);
        var whatBox = new Rectangle(nameBox.Right, e.Bounds.Y, e.Bounds.Width - nameWidth - 12, e.Bounds.Height);
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        TextRenderer.DrawText(e.Graphics, command.Name, _bold, nameBox, fore, flags);
        TextRenderer.DrawText(e.Graphics, command.WhyNot is null ? command.What : $"{command.What}  {command.WhyNot}", _body, whatBox, fore, flags);
    }

    private void StartRequest(string start)
    {
        _prompt.Text = start;
        _prompt.SelectionStart = _prompt.TextLength;
        _prompt.Focus();
    }

    // ------------------------------------------------------------------- the tool picker

    /// <summary>
    /// The tools, grouped by what they do, each ticked while it is on. The menu stays up while
    /// ticks are changed, as a checklist should, and the button counts what is off.
    /// </summary>
    private void ShowToolMenu()
    {
        if (_chat.Generating) return;

        _toolMenu.Items.Clear();
        foreach (var (heading, tools) in new[]
        {
            ("Reading", BenchToolRunner.All.Where(t => !t.Changes && !t.Proposes)),
            ("Changing an instrument", BenchToolRunner.All.Where(t => t.Changes)),
            ("Proposing", BenchToolRunner.All.Where(t => t.Proposes)),
        })
        {
            if (_toolMenu.Items.Count > 0) _toolMenu.Items.Add(new ToolStripSeparator());
            _toolMenu.Items.Add(new ToolStripLabel(heading) { ForeColor = SystemColors.GrayText });
            foreach (LlmTool tool in tools)
            {
                var item = new ToolStripMenuItem(tool.Name)
                {
                    Checked = !_chat.ToolsOff.Contains(tool.Name),
                    CheckOnClick = true,
                    ToolTipText = tool.Description.Split('\n')[0],
                };
                item.CheckedChanged += (_, _) =>
                {
                    if (item.Checked) _chat.ToolsOff.Remove(tool.Name);
                    else _chat.ToolsOff.Add(tool.Name);
                    ShowToolCount();
                    ShowEstimate();
                };
                _toolMenu.Items.Add(item);
            }
        }

        _toolMenu.Items.Add(new ToolStripSeparator());
        var allOn = new ToolStripMenuItem("Every tool on") { Enabled = _chat.ToolsOff.Count > 0 };
        allOn.Click += (_, _) =>
        {
            _chat.ToolsOff.Clear();
            _toolMenu.Close();
            ShowToolCount();
            ShowEstimate();
        };
        _toolMenu.Items.Add(allOn);
        var compact = new ToolStripMenuItem("Compact the conversation") { Enabled = _chat.WhyNotCompact() is null, ToolTipText = _chat.WhyNotCompact() ?? "" };
        compact.Click += async (_, _) =>
        {
            _toolMenu.Close();
            await CompactAsync();
        };
        _toolMenu.Items.Add(compact);

        _toolMenu.Show(_toolsButton, new Point(0, _toolsButton.Height));
    }

    private static void KeepOpenWhileTicking(object? sender, ToolStripDropDownClosingEventArgs e)
    {
        if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked
            && sender is ContextMenuStrip menu && menu.Items.OfType<ToolStripMenuItem>().Any(i => i.Selected && i.CheckOnClick))
            e.Cancel = true;
    }

    private void ShowToolCount()
        => _toolsButton.Text = _chat.ToolsOff.Count == 0 ? "Tools…" : $"Tools… ({_chat.ToolsOff.Count} off)";

    // ------------------------------------------------------------------- kept chats

    /// <summary>
    /// Keeps the chat on screen, from its first question on, after every answer and every
    /// compaction. Not when another chat was put on screen while the answer came: it is kept
    /// under the id of the chat it belonged to, or not at all.
    /// </summary>
    private async Task KeepAsync(int shown)
    {
        if (shown != _shown) return;

        IReadOnlyList<ChatTurn> turns = _chat.Turns;
        if (!turns.Any(t => t.Kind == ChatTurn.User)) return;

        _chatId ??= SavedChat.NewId();
        if (_created == default) _created = DateTime.Now;
        var chat = new SavedChat
        {
            Id = _chatId,
            Title = SavedChat.TitleFor(turns),
            Bench = AssistantPrompt.BenchLine(BenchNow()),
            Created = _created,
            Updated = DateTime.Now,
            Turns = SavedChat.Keep(turns),
        };
        try
        {
            await _ai.Chats.SaveAsync(chat);
        }
        catch (Exception ex)
        {
            _status.Text = "The chat could not be kept: " + ex.Message;
        }
    }

    private void NewChat()
    {
        if (_chat.Generating) return;
        _shown++;
        _chat.Clear();
        _chatId = null;
        _created = default;
        _confirmingDelete = null;
        _showFolded = false;
        ClearPictures();
        _status.Text = "New conversation. " + SpentLine();
        DrawAll();
        _ = DrawChatsAsync();
        _prompt.Focus();
    }

    /// <summary>
    /// Opens a kept chat and carries it on: its turns, with a line saying when it was opened again
    /// and, when the bench is another now, what it was about. That line goes to the model with the
    /// warning that the names the tools gave out were another session's.
    /// </summary>
    private async Task OpenChatAsync(string id)
    {
        if (_chat.Generating || id == _chatId) return;

        SavedChat? kept;
        try
        {
            kept = await _ai.Chats.LoadAsync(id);
        }
        catch (Exception ex)
        {
            _status.Text = "That chat could not be read: " + ex.Message;
            return;
        }
        if (kept is null)
        {
            _status.Text = "That chat is gone: it was deleted, or its file will not read.";
            await DrawChatsAsync();
            return;
        }
        if (_chat.Generating) return;

        _shown++;
        _benchNow = BenchNow();
        _chat.Restore(kept.Unkept(), AssistantPrompt.ReopenedLine(kept.Bench, AssistantPrompt.BenchLine(_benchNow), DateTime.Now));
        _chatId = kept.Id;
        _created = kept.Created == default ? DateTime.Now : kept.Created;
        _confirmingDelete = null;
        _showFolded = false;
        ClearPictures();
        _status.Text = "Opened: " + kept.Title;
        DrawAll();
        DrawChatRows();
    }

    private async Task DeleteChatAsync(string id)
    {
        _confirmingDelete = null;
        try
        {
            await _ai.Chats.DeleteAsync(id);
        }
        catch (Exception ex)
        {
            _status.Text = "That chat could not be deleted: " + ex.Message;
            DrawChatRows();
            return;
        }
        // The chat on screen deleted is the chat on screen gone: a new one takes its place.
        if (id == _chatId) NewChat();
        else await DrawChatsAsync();
    }

    private async Task ExportChatAsync(string id)
    {
        if (_chat.Generating) return;
        if (id == _chatId)
        {
            SaveConversation();
            return;
        }

        SavedChat? kept = await _ai.Chats.LoadAsync(id);
        if (kept is null)
        {
            _status.Text = "That chat is gone: it was deleted, or its file will not read.";
            await DrawChatsAsync();
            return;
        }
        // Put back into a conversation of its own only to be written out: no line, nothing sent.
        var other = new AiChat();
        other.Restore(kept.Unkept(), null);
        WriteTranscript(kept.Title, other.Transcript(kept.Title, DateTime.Now));
    }

    /// <summary>The conversation on screen as text, to read back or paste elsewhere another day.</summary>
    private void SaveConversation()
    {
        IReadOnlyList<ChatTurn> turns = _chat.Turns;
        if (turns.Count == 0 || _chat.Generating) return;
        string title = SavedChat.TitleFor(turns);
        WriteTranscript(title, _chat.Transcript(title, DateTime.Now));
    }

    private void WriteTranscript(string title, string transcript)
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Save the chat as text",
            Filter = "Text|*.txt",
            FileName = FileNameFor(title),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, transcript);
            _status.Text = "Saved to " + dlg.FileName + ".";
        }
        catch (Exception ex)
        {
            _status.Text = "Could not save it: " + ex.Message;
        }
    }

    private static string FileNameFor(string title)
    {
        char[] bad = Path.GetInvalidFileNameChars();
        string safe = new string(title.Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim().TrimEnd('.');
        if (safe.Length > 60) safe = safe[..60].Trim();
        return (safe.Length > 0 ? safe : "chat") + $" {DateTime.Now:yyyy-MM-dd}.txt";
    }

    private async Task DrawChatsAsync()
    {
        if (!_chatsOpen) return;
        try
        {
            _chatList = await _ai.Chats.ListAsync();
        }
        catch (Exception ex)
        {
            _chatList = [];
            _status.Text = "The chats could not be read: " + ex.Message;
        }
        DrawChatRows();
    }

    private void DrawChatRows()
    {
        if (IsDisposed || !_chatsOpen) return;
        bool busy = _chat.Generating;

        _chatScroll.SuspendLayout();
        foreach (Control c in _chatScroll.Controls.Cast<Control>().ToList()) c.Dispose();
        _chatScroll.Controls.Clear();

        var rows = new List<Control>();
        foreach (ChatSummary chat in _chatList) rows.Add(ChatRow(chat, busy));
        if (rows.Count == 0)
        {
            rows.Add(new Label
            {
                Text = "No chats kept yet.",
                Font = _small,
                ForeColor = SystemColors.GrayText,
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(2, 4, 0, 0),
            });
        }
        // Docked to the top in reverse, so the first in the list ends at the top.
        for (int i = rows.Count - 1; i >= 0; i--) _chatScroll.Controls.Add(rows[i]);
        _chatScroll.ResumeLayout();

        _newChat.Enabled = !busy;
        _tips.SetToolTip(_newChat, busy ? WhyBusy : "Start a new conversation. This one stays in the list below, and what was spent stays counted.");
    }

    /// <summary>
    /// A kept chat's row: its title and when it was last kept, with what was on the bench; Save and
    /// Delete of its own. Delete asks in the row, not in a dialog. Nothing is switched while an
    /// answer is coming: a press then only says why.
    /// </summary>
    private Control ChatRow(ChatSummary chat, bool busy)
    {
        bool current = chat.Id == _chatId;
        bool confirming = chat.Id == _confirmingDelete;

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = current ? Chosen : Color.Transparent,
            Padding = new Padding(6, 4, 4, 4),
            Margin = new Padding(0),
            Height = _body.Height + _small.Height + LogicalToDeviceUnits(22),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

        var title = new Label
        {
            Text = chat.Title,
            Font = current ? _bold : _body,
            ForeColor = busy && !current ? SystemColors.GrayText : SystemColors.ControlText,
            AutoSize = false,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            UseMnemonic = false,
        };
        var when = new Label
        {
            Text = When(chat.Updated) + (chat.Bench.Length > 0 ? " · " + chat.Bench : ""),
            Font = _small,
            ForeColor = SystemColors.GrayText,
            AutoSize = false,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            UseMnemonic = false,
        };

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
            Anchor = AnchorStyles.Right,
        };
        if (confirming)
        {
            actions.Controls.Add(new Label { Text = "Delete it?", Font = _small, AutoSize = true, Margin = new Padding(0, 1, 4, 0) });
            actions.Controls.Add(SmallLink("Delete", "Delete this chat for good", () => _ = DeleteChatAsync(chat.Id)));
            actions.Controls.Add(SmallLink("Keep", "Keep it", () => { _confirmingDelete = null; DrawChatRows(); }));
        }
        else
        {
            actions.Controls.Add(SmallLink("Save", busy ? WhyBusy : "Save this chat as text: every turn, each call with what it answered, and what each answer cost.",
                () => { if (!_chat.Generating) _ = ExportChatAsync(chat.Id); }));
            actions.Controls.Add(SmallLink("Delete", busy ? WhyBusy : "Delete this chat. It asks first, here in the row.",
                () => { if (!_chat.Generating) { _confirmingDelete = chat.Id; DrawChatRows(); } }));
        }

        row.Controls.Add(title, 0, 0);
        row.SetColumnSpan(title, 2);
        row.Controls.Add(when, 0, 1);
        row.Controls.Add(actions, 1, 1);

        string tip = busy ? WhyBusy : current ? "The chat on screen." : $"Open it and carry on: {chat.Title}";
        foreach (Control c in new Control[] { row, title, when })
        {
            c.Cursor = current ? Cursors.Default : Cursors.Hand;
            c.Click += async (_, _) =>
            {
                if (_chat.Generating) { _status.Text = WhyBusy; return; }
                await OpenChatAsync(chat.Id);
            };
            _tips.SetToolTip(c, tip);
        }
        return row;
    }

    private LinkLabel SmallLink(string text, string tip, Action click)
    {
        LinkLabel link = Link(text, tip, click, new Padding(0, 1, 6, 0));
        link.Font = _small;
        return link;
    }

    private static string When(DateTime kept)
    {
        DateTime local = kept.Kind == DateTimeKind.Utc ? kept.ToLocalTime() : kept;
        DateTime now = DateTime.Now;
        if (local.Date == now.Date) return local.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (local.Year == now.Year) return local.ToString("d MMM, HH:mm", CultureInfo.CurrentCulture);
        return local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    // ------------------------------------------------------------------- files

    private void ChooseFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Attach to the next question",
            Multiselect = true,
            Filter = "Pictures, PDFs and text|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.pdf;*.txt;*.csv;*.tsv;*.json;*.xml;*.md;*.log;*.yaml;*.yml;*.ini;*.htm;*.html;*.scpi;*.seq;*.docx|All files|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) AttachPaths(dlg.FileNames);
    }

    private void AttachPaths(IEnumerable<string> paths)
    {
        var problems = new List<string>();
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            long size;
            try { size = new FileInfo(path).Length; }
            catch (Exception ex) { problems.Add($"{name} could not be read: {ex.Message}"); continue; }

            if (AiAttachment.Refusal(name, "", size) is string refused) { problems.Add(refused); continue; }

            try
            {
                if (_chat.Attach(new AiAttachment(name, "", File.ReadAllBytes(path))) is string problem)
                    problems.Add(problem);
            }
            catch (Exception ex)
            {
                problems.Add($"{name} could not be read: {ex.Message}");
            }
        }
        ShowAttachProblem(problems);
    }

    /// <summary>
    /// A picture on the clipboard, as a PNG named for when it was pasted (a screenshot arrives
    /// with no name at all), or the files a copy in Explorer put there.
    /// </summary>
    private void PasteFiles()
    {
        if (Clipboard.ContainsFileDropList())
        {
            AttachPaths(Clipboard.GetFileDropList().Cast<string>());
            return;
        }

        using Image? picture = Clipboard.GetImage();
        if (picture is null) return;
        using var png = new MemoryStream();
        picture.Save(png, ImageFormat.Png);
        string name = $"pasted-{DateTime.Now:yyyy-MM-dd-HHmmss}.png";
        ShowAttachProblem(_chat.Attach(new AiAttachment(name, "image/png", png.ToArray())) is string problem ? [problem] : []);
    }

    /// <summary>
    /// A picture of the screen this window is on, attached as a PNG named for when it was taken.
    /// The window steps aside while it is taken, so the picture is of what is behind it: the bench's
    /// window, a console, a datasheet. Scaled to 1,568 pixels on its long side, which is what the
    /// largest provider scales a picture to anyway, so the bytes and the bill are no bigger than
    /// what is seen.
    /// </summary>
    private async Task TakeScreenshotAsync()
    {
        Rectangle bounds = Screen.FromControl(this).Bounds;
        Hide();
        try
        {
            await Task.Delay(400);
            using var shot = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(shot))
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            using Bitmap fitted = Fit(shot, 1568);
            using var png = new MemoryStream();
            fitted.Save(png, ImageFormat.Png);
            string name = $"screenshot-{DateTime.Now:yyyy-MM-dd-HHmmss}.png";
            ShowAttachProblem(_chat.Attach(new AiAttachment(name, "image/png", png.ToArray())) is string problem ? [problem] : []);
        }
        catch (Exception ex)
        {
            ShowAttachProblem([$"The screen could not be taken: {ex.Message}"]);
        }
        finally
        {
            Show();
            Activate();
        }
    }

    private static Bitmap Fit(Bitmap source, int longest)
    {
        double scale = Math.Min(1.0, longest / (double)Math.Max(source.Width, source.Height));
        var size = new Size(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
        var fitted = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        using Graphics g = Graphics.FromImage(fitted);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, new Rectangle(Point.Empty, size));
        return fitted;
    }

    private void ShowAttachProblem(IReadOnlyList<string> problems)
    {
        _attachProblem.Text = string.Join(" ", problems);
        _attachProblem.Visible = problems.Count > 0;
        DrawChips();
        DrawFiles();
        ShowEstimate();
    }

    private void DrawChips()
    {
        _chips.SuspendLayout();
        foreach (Control c in _chips.Controls.Cast<Control>().ToList()) c.Dispose();
        _chips.Controls.Clear();
        foreach (AiAttachment file in _chat.Pending)
        {
            var chip = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = SystemColors.Window,
                Margin = new Padding(0, 0, 6, 0),
                Padding = new Padding(4, 2, 2, 2),
            };
            chip.Controls.Add(new Label { Text = $"{file.Name}  {AiAttachment.Size(file.Data.Length)}", AutoSize = true, Margin = new Padding(0, 3, 4, 0), UseMnemonic = false });
            AiAttachment taken = file;
            chip.Controls.Add(Link("✕", "Take it off the next question", () => Unattach(taken), new Padding(0, 3, 0, 0)));
            _chips.Controls.Add(chip);
        }
        _chips.Visible = _chips.Controls.Count > 0;
        _chips.ResumeLayout();
    }

    private void Unattach(AiAttachment file)
    {
        _chat.Unattach(file);
        _attachProblem.Visible = false;
        DrawChips();
        DrawFiles();
        ShowEstimate();
        UpdateEnabled();
    }

    /// <summary>
    /// The conversation's files, ticked while they go with every question, and the files waiting in
    /// the box. An unticked file stays listed so it can be put back, and stops counting against
    /// the conversation's budget; ticking one in again is held to it.
    /// </summary>
    private void DrawFiles()
    {
        List<(ChatTurn Question, AiAttachment File)> sent = _chat.SentFiles();
        IReadOnlyList<AiAttachment> pending = _chat.Pending;

        _filesStrip.Text = sent.Count > 0 ? $"‹\n{sent.Count}" : "‹";
        _tips.SetToolTip(_filesStrip, sent.Count == 0
            ? "Show the files panel. No file has gone with a question yet."
            : $"Show the files: {sent.Count} in this conversation.");
        if (!_filesOpen || IsDisposed) return;

        bool busy = _chat.Generating;
        _fileScroll.SuspendLayout();
        foreach (Control c in _fileScroll.Controls.Cast<Control>().ToList()) c.Dispose();
        _fileScroll.Controls.Clear();

        var rows = new List<Control> { Heading("In this conversation") };
        if (sent.Count == 0) rows.Add(Hint("None yet. A file sent with a question is listed here, to be left out of the questions after it."));
        foreach (var (question, file) in sent)
        {
            var box = new CheckBox
            {
                Text = $"{file.Name}  {AiAttachment.Size(file.Data.Length)}",
                Checked = !file.Excluded,
                AutoSize = false,
                AutoEllipsis = true,
                Dock = DockStyle.Top,
                Height = _body.Height + LogicalToDeviceUnits(8),
                Enabled = !busy,
                UseMnemonic = false,
                ForeColor = file.Excluded ? SystemColors.GrayText : SystemColors.ControlText,
            };
            AiAttachment one = file;
            box.CheckedChanged += (_, _) =>
            {
                // Drawn again after this handler returns: the box being handled is one of the
                // controls the drawing throws away.
                if (_chat.Include(one, box.Checked) is string problem)
                {
                    BeginInvoke(new Action(() => { DrawFiles(); _filesFoot.Text = problem; }));
                    return;
                }
                BeginInvoke(new Action(() => { DrawFiles(); ShowEstimate(); DrawCardsOnly(); }));
            };
            string with = question.Kind == ChatTurn.Summary
                ? "Kept from before the briefing. Ticked, it goes with every question again."
                : $"Sent with: {Cut(question.Text, 160)}";
            _tips.SetToolTip(box, (file.Excluded ? "Left out: not sent with the questions after it. " : "") + with);
            rows.Add(box);
        }

        rows.Add(Heading("With the next question"));
        if (pending.Count == 0) rows.Add(Hint("Nothing waiting. Attach Files…, a paste or a drop puts one here."));
        foreach (AiAttachment file in pending)
        {
            var line = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Height = _body.Height + LogicalToDeviceUnits(8), Margin = new Padding(0) };
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            line.Controls.Add(new Label { Text = $"{file.Name}  {AiAttachment.Size(file.Data.Length)}", AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            AiAttachment taken = file;
            line.Controls.Add(Link("✕", "Take it off the next question", () => Unattach(taken), new Padding(4, 4, 0, 0)), 1, 0);
            rows.Add(line);
        }

        for (int i = rows.Count - 1; i >= 0; i--) _fileScroll.Controls.Add(rows[i]);
        _fileScroll.ResumeLayout();

        long tokens = _connection is null ? 0 : sent.Where(s => !s.File.Excluded).Sum(s => AiEstimate.Attachment(s.File, _connection));
        _filesFoot.Text = tokens > 0
            ? $"The ticked files add about {AiPricing.About(tokens)} tokens to every question."
            : sent.Count > 0 ? "No ticked file goes with the questions now." : "";
    }

    private Label Heading(string text) => new()
    {
        Text = text,
        Font = _bold,
        Dock = DockStyle.Top,
        AutoSize = true,
        Padding = new Padding(0, 8, 0, 4),
    };

    private Label Hint(string text)
    {
        var hint = new Label
        {
            Text = text,
            Font = _small,
            ForeColor = SystemColors.GrayText,
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(230), 0),
            Padding = new Padding(0, 0, 0, 4),
        };
        return hint;
    }

    private static string Cut(string text, int most)
    {
        string flat = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= most ? flat : flat[..most].TrimEnd() + "…";
    }

    // ------------------------------------------------------------------- what the model is told about

    private void ShowContext()
    {
        _benchNow = BenchNow();
        _context.Text = AssistantPrompt.Context(_benchNow);
        FitLabelWidths();
        ShowEstimate();
    }

    /// <summary>
    /// What the next question will carry, before it is sent (the instructions, the tools, the
    /// conversation and its files, the box), in tokens and, where the model has a rate, dollars.
    /// Said as what it is: a tool loop sends it again every round, and how many rounds is not knowable.
    /// </summary>
    private void ShowEstimate()
    {
        if (_connection is null)
        {
            _estimate.Text = NoConnection;
            return;
        }
        string system = AssistantPrompt.Build(_benchNow, _chat.Mode, _chat.ToolsOff, Where, DateTime.Now);
        long tokens = _chat.EstimateTokens(system, _tools.Tools, _connection, _prompt.Text);
        LlmPrice? price = AiPricing.For(_connection, Today);
        string cost = price is null ? "" : $" ({AiPricing.AboutMoney(price.CostOfInput(tokens))} to read)";
        _estimate.Text = $"The next question sends about {AiPricing.About(tokens)} tokens{cost}, and again with every round of tool calls.";
    }

    /// <summary>The suggestion to compact, when it is worth it, priced both ways.</summary>
    private void ShowCompactSuggestion()
    {
        bool worth = _connection is not null && _chat.WorthCompacting(_connection);
        if (worth)
        {
            long history = _chat.HistoryTokens(_connection!);
            LlmPrice? price = AiPricing.For(_connection!, Today);
            string each = price is null ? "" : $" ({AiPricing.AboutMoney(price.CostOfInput(history))} each time)";
            string once = AiChat.CompactCost(price, history) is decimal dollars ? $", for {AiPricing.AboutMoney(dollars)} once" : "";
            _compactText.Text = $"Every question now sends about {AiPricing.About(history)} tokens of history again{each}. "
                              + $"Compacting puts a briefing in its place{once}.";
        }
        _compactBar.Visible = worth;
    }

    // ------------------------------------------------------------------- the transcript

    private Task OnChatChanged()
    {
        OnUi(() =>
        {
            DrawAll();
            UpdateEnabled();
        });
        return Task.CompletedTask;
    }

    private Task OnChatsChanged()
    {
        OnUi(() => _ = DrawChatsAsync());
        return Task.CompletedTask;
    }

    private Task OnConnectionsChanged()
    {
        OnUi(() => _ = PickConnectionAsync());
        return Task.CompletedTask;
    }

    private void OnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    private void UpdateEnabled()
    {
        bool busy = _chat.Generating;
        bool slash = _prompt.Text.TrimStart().StartsWith('/');
        _send.Text = busy ? "Stop" : "Send";
        _send.Enabled = busy || (_prompt.Text.Trim().Length > 0 && (slash || (_connection is not null && WhyNotSend() is null)));
        _using.Enabled = !busy;
        _toolsButton.Enabled = !busy;
        _newChat.Enabled = !busy;
        _progress.Visible = busy;
        ShowToolCount();

        // A file the picked connection cannot be sent holds Send back, and says so over the box
        // until it is taken off or another connection is picked.
        string? cannot = busy ? null : WhyNotSend();
        if (cannot is not null)
        {
            _attachProblem.Text = cannot;
            _attachProblem.Visible = true;
        }
        else if (_sendProblem is not null && _attachProblem.Text == _sendProblem)
        {
            _attachProblem.Visible = false;
        }
        _sendProblem = cannot;

        if (_slash.Visible) UpdateSlash();
    }

    /// <summary>The reason over the box that Send waits, as last shown, so it goes when the reason does.</summary>
    private string? _sendProblem;

    private void ClearPictures()
    {
        foreach (Image image in _pictures.Values) image.Dispose();
        _pictures.Clear();
    }

    private void DrawAll()
    {
        if (IsDisposed) return;
        if (!Visible)
        {
            _stale = true;
            return;
        }
        _stale = false;

        DrawCardsOnly();
        DrawChips();
        DrawFiles();
        DrawChatRows();
        ShowCompactSuggestion();
        ShowEstimate();
    }

    private void DrawCardsOnly()
    {
        bool atBottom = _scroll.VerticalScroll.Value + _scroll.ClientSize.Height >= _scroll.DisplayRectangle.Height - 40;

        _scroll.SuspendLayout();
        _cards.SuspendLayout();
        foreach (Control c in _cards.Controls.Cast<Control>().ToList()) c.Dispose();
        _cards.Controls.Clear();
        _cards.RowStyles.Clear();
        _wrapping.RemoveAll(l => l.IsDisposed);

        // A copy: the run goes on adding to the conversation while this draws.
        IReadOnlyList<ChatTurn> turns = _chat.Turns;
        int folded = Math.Min(_chat.FoldedBefore, turns.Count);
        if (folded > 0)
        {
            _cards.Controls.Add(Link(
                _showFolded ? "Hide what came before the briefing" : $"Show the {folded} turns before the briefing",
                "What a compaction folded away: kept to read, and in the export, but no longer sent.",
                () => { _showFolded = !_showFolded; DrawCardsOnly(); },
                new Padding(0, 0, 0, 6)));
            if (_showFolded)
            {
                _dimming = true;
                for (int i = 0; i < folded; i++) _cards.Controls.Add(Card(turns[i]));
                _dimming = false;
            }
        }
        for (int i = folded; i < turns.Count; i++)
            _cards.Controls.Add(Card(turns[i]));
        if (turns.Count == 0)
            _cards.Controls.Add(Note("Nothing asked yet. Ask about the bench, attach a datasheet or a screenshot, ask for a script, or type / for the window's commands."));

        _cards.ResumeLayout();
        _scroll.ResumeLayout();
        FitLabelWidths();
        if (atBottom) _scroll.AutoScrollPosition = new Point(0, _scroll.DisplayRectangle.Height);
    }

    private Control Card(ChatTurn turn) => turn.Kind switch
    {
        ChatTurn.User => Question(turn),
        ChatTurn.Assistant => Answer(turn),
        ChatTurn.Tool => ToolCard(turn),
        ChatTurn.Error => Problem(turn),
        ChatTurn.Paused => PausedCard(turn),
        ChatTurn.Summary => Briefing(turn),
        _ => Note(turn.Text),
    };

    private Control Question(ChatTurn turn)
    {
        var box = Stack(SystemColors.Window);
        box.Controls.Add(Words("You", _bold));
        box.Controls.Add(Words(turn.Text, _body));
        if (turn.Attachments.Count > 0)
            box.Controls.Add(Words(string.Join("   ", turn.Attachments.Select(a => $"📎 {a.Name} ({AiAttachment.Size(a.Data.Length)}){(a.Excluded ? ", left out" : "")}")), _small, SystemColors.GrayText));
        return box;
    }

    private Control Answer(ChatTurn turn)
    {
        var box = Stack(Color.Transparent);
        box.Controls.Add(Words(turn.Text, _body));
        if (AiChat.UsageLine(turn) is { Length: > 0 } usage)
            box.Controls.Add(Words(usage, _small, SystemColors.GrayText));
        return box;
    }

    private Control Problem(ChatTurn turn)
    {
        var box = Stack(Color.Transparent);
        box.Controls.Add(Words(turn.Text, _body, Warning));
        if (AiChat.UsageLine(turn) is { Length: > 0 } usage)
            box.Controls.Add(Words(usage, _small, SystemColors.GrayText));
        return box;
    }

    private Control PausedCard(ChatTurn turn)
    {
        var box = Stack(Waiting);
        box.Controls.Add(Words(turn.Text, _body));
        var go = new Button();
        ButtonStyle.Apply(go, "Continue", (_, _) => _ = SendAsync("Continue", fromBox: false));
        go.Enabled = !_chat.Generating && ReferenceEquals(turn, _chat.Turns.LastOrDefault());
        _tips.SetToolTip(go, "Carry on for as many rounds again. Each round sends the conversation again.");
        box.Controls.Add(go);
        return box;
    }

    /// <summary>The briefing a compaction put in place of the conversation before it, and the files it keeps unticked.</summary>
    private Control Briefing(ChatTurn turn)
    {
        var box = Stack(SystemColors.Window);
        box.Controls.Add(Words("Briefing: the conversation above, compacted. The model is sent this in its place.", _bold));
        box.Controls.Add(Words(turn.Text, _body));
        if (turn.Attachments.Count > 0)
            box.Controls.Add(Words("Kept from before it, unticked in the files panel: " + string.Join(", ", turn.Attachments.Select(a => a.Name)), _small, SystemColors.GrayText));
        if (AiChat.UsageLine(turn) is { Length: > 0 } usage)
            box.Controls.Add(Words(usage, _small, SystemColors.GrayText));
        return box;
    }

    /// <summary>
    /// A tool call: what the model asked for, as it asked for it; whether it ran on its own, waits
    /// for an answer here, or was answered; and what the model was told, the picture it was shown
    /// included. A proposed script carries its own Use This Script.
    /// </summary>
    private Control ToolCard(ChatTurn turn)
    {
        bool waiting = AssistantPrompt.Waiting(turn);
        var box = Stack(waiting ? Waiting : SystemColors.Window);

        string state = AssistantPrompt.CallState(turn);
        box.Controls.Add(Words($"{turn.ToolName}   {AssistantPrompt.CallLine(turn.ArgumentsJson)}", _code));
        if (state.Length > 0) box.Controls.Add(Words(state, _small, turn.Decision == false ? Warning : SystemColors.GrayText));

        if (waiting)
        {
            var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            var approve = new Button();
            var decline = new Button();
            ButtonStyle.Apply(approve, turn.Changes ? "Approve and Send" : "Approve", (_, _) => _chat.Decide(turn, true));
            ButtonStyle.Apply(decline, "Decline", (_, _) => _chat.Decide(turn, false));
            row.Controls.Add(approve);
            row.Controls.Add(decline);
            box.Controls.Add(row);
        }

        if (turn.Result is string result)
            box.Controls.Add(Words(result, _body, turn.ResultIsError ? Warning : SystemColors.ControlText));

        if (turn.Png is string png && Picture(turn, png) is Image image)
        {
            int width = Math.Min(image.Width, LogicalToDeviceUnits(640));
            box.Controls.Add(new PictureBox
            {
                Image = image,
                SizeMode = PictureBoxSizeMode.Zoom,
                Width = width,
                Height = (int)(width * (double)image.Height / Math.Max(1, image.Width)),
                Margin = new Padding(0, 4, 0, 0),
            });
        }

        if (turn.Proposal is string script)
        {
            // Every line shown up to twenty, and a scroll bar down the side only past that; one
            // along the foot always, since a script's lines are not wrapped.
            int lines = script.Split('\n').Length;
            var text = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = lines > 20 ? ScrollBars.Both : ScrollBars.Horizontal,
                Font = _code,
                Text = script.Replace("\r\n", "\n").Replace("\n", "\r\n"),
                Height = Math.Min(20, lines) * _code.Height + SystemInformation.HorizontalScrollBarHeight + LogicalToDeviceUnits(8),
                Width = LogicalToDeviceUnits(600),
                Margin = new Padding(0, 4, 0, 0),
            };
            box.Controls.Add(text);
            var use = new Button();
            ButtonStyle.Apply(use, "Use This Script", async (_, _) => await UseScriptAsync(turn.ArgumentsJson, script));
            _tips.SetToolTip(use, "Put it in the script editor, to read and run there. Nothing runs until you press Run.");
            box.Controls.Add(use);
        }
        return box;
    }

    private async Task UseScriptAsync(string argumentsJson, string script)
    {
        (bool sequence, string? named) = AssistantPrompt.ProposalTarget(argumentsJson);

        BenchInstrument? instrument = sequence ? null : await _tools.FindInstrumentAsync(named, CancellationToken.None);
        if (!sequence && instrument is null)
        {
            _status.Text = "That script's instrument is not open any more. Copy it from the card instead.";
            return;
        }
        ScriptUsed?.Invoke(sequence ? "sequence" : "instrument", instrument, script);
    }

    private Image? Picture(ChatTurn turn, string base64)
    {
        if (_pictures.TryGetValue(turn, out Image? image)) return image;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(base64));
            using var loaded = Image.FromStream(stream);
            image = new Bitmap(loaded);
            _pictures[turn] = image;
            return image;
        }
        catch (Exception)
        {
            return null;
        }
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
        BackColor = _dimming ? Color.Transparent : back,
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
            ForeColor = _dimming ? SystemColors.GrayText : color ?? SystemColors.ControlText,
            Margin = new Padding(0, 0, 0, 2),
            UseMnemonic = false,
        };
        _wrapping.Add(label);
        return label;
    }

    private void FitLabelWidths()
    {
        int width = Math.Max(200, _scroll.ClientSize.Width - LogicalToDeviceUnits(48));
        int across = Math.Max(200, _scroll.Width + LogicalToDeviceUnits(4));
        foreach (Label label in _wrapping.Where(l => !l.IsDisposed))
        {
            bool composer = label == _context || label == _estimate || label == _attachProblem || label == _compactText;
            label.MaximumSize = new Size(composer ? across - (label == _compactText ? LogicalToDeviceUnits(220) : 0) : width, 0);
        }
    }

    private void FitPrompt()
    {
        int lines = Math.Clamp(_prompt.GetLineFromCharIndex(_prompt.TextLength) + 1, 1, PromptMaxLines);
        _prompt.Height = lines * _prompt.Font.Height + LogicalToDeviceUnits(10);
        _prompt.ScrollBars = _prompt.GetLineFromCharIndex(_prompt.TextLength) + 1 > PromptMaxLines ? ScrollBars.Vertical : ScrollBars.None;
    }
}
