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
///
/// **Laid out as the page's window is** (<c>Tools/Assistant.razor</c>), and **Write a Script with
/// AI is this window** with a <see cref="ScriptWriterJob"/> given to it, as it is on the page, so
/// the two cannot come to differ: what differs is who is asked and where the chats are kept.
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
    private bool _filesOpen = true;
    private bool _showFolded;
    private bool _stale;

    private readonly Button _model = new();
    private readonly ContextMenuStrip _modelMenu = new();
    private readonly Button _rung = new();
    private readonly ContextMenuStrip _rungMenu = new();
    private readonly CheckBox _revise = new();
    private readonly CheckBox _includeOutput = new();
    private readonly TextBox _prompt = new();

    /// <summary>What the empty box is for. Not the box's own placeholder, which Windows takes away while the box has the caret, and it nearly always has.</summary>
    private readonly Label _promptHint = new();

    private readonly Button _send = new();
    private readonly Button _attach = new();
    private readonly Button _screenshot = new();
    private readonly Button _toolsButton = new();
    private readonly Button _meterToggle = new();
    private readonly Button _newChat = new();
    private readonly ContextMenuStrip _toolMenu = new();
    private readonly FlowLayoutPanel _chips = new();
    private readonly Label _attachProblem = new();
    private readonly TableLayoutPanel _meter = new();
    private readonly Label _estimate = new();
    private readonly Label _spent = new();
    private readonly LinkLabel _rates = new();
    private readonly Panel _scroll = new();
    private readonly TableLayoutPanel _cards = new();
    private readonly Label _notice = new();
    private readonly ToolTip _tips = new();

    /// <summary>The effort picked for the next question, "" for the model's default; never saved.</summary>
    private string _effortChoice = "";

    private bool _meterOn = true;

    /// <summary>Set when this window is Write a Script with AI: which editor it is writing for.</summary>
    private ScriptWriterJob? _writer;

    /// <summary>What goes with every request whatever is asked, the catalogs mostly, as measured when the switches last moved.</summary>
    private int _promptCharacters;

    /// <summary>What the empty conversation's opening words were last drawn from, so they are drawn again only when it changes.</summary>
    private string? _introDrawn;

    /// <summary>Script boxes on screen, as wide as the conversation.</summary>
    private readonly List<TextBox> _scripts = [];

    private readonly Panel _compactBar = new();
    private readonly Label _compactText = new();
    private readonly Button _compact = new();
    private readonly Button _notNow = new();

    private readonly ListBox _slash = new();
    private List<SlashCommand> _slashShown = [];
    private string? _slashPutAwayFor;

    private readonly RoundedPanel _chatsPanel = new();
    private readonly Panel _chatScroll = new();
    private readonly Label _keptWhere = new();
    private readonly Button _chatsStrip = new();

    private readonly RoundedPanel _filesPanel = new();
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

    private readonly Font _smallBold = new("Segoe UI", 8.25f, FontStyle.Bold);

    private static readonly Color Warning = Color.FromArgb(150, 40, 0);

    /// <summary>What an empty conversation offers to start from, each put in the box by a press rather than sent.</summary>
    private static readonly string[] Examples =
    [
        AssistantPrompt.StatusQuestion,
        AssistantPrompt.ErrorsQuestion,
        "Write a script that reads every instrument once and prints the results",
    ];

    /// <summary>One of the window's slash commands: its name, what it does, why it cannot run now (or null), and what it does with any words after its name.</summary>
    private sealed record SlashCommand(string Name, string What, string? WhyNot, Action<string> Run);

    /// <summary>A proposed script's Use This Script was pressed: which kind, for which instrument, and the script.</summary>
    public event Action<string, BenchInstrument?, string>? ScriptUsed;

    /// <summary>The window asked for AI Connection: <c>/settings</c>, or a link in a line about a missing key.</summary>
    public event Action? SettingsRequested;

    public AssistantForm(IAiService ai, SessionRegistry sessions, Action<InstrumentSession, string, string?> logToConsole)
        : this(ai, sessions, logToConsole, null)
    {
    }

    internal AssistantForm(IAiService ai, SessionRegistry sessions, Action<InstrumentSession, string, string?> logToConsole, ScriptWriterJob? writer)
    {
        _ai = ai;
        _writer = writer;
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
        Text = Named;
        ClientSize = new Size(1100, 860);
        MinimumSize = new Size(860, 620);
        StartPosition = FormStartPosition.CenterParent;
        AllowDrop = true;

        BuildUi();
        ResumeLayout(false);
        PerformLayout();
    }

    private string Named => _writer is null ? "AI Assistant" : "Write a Script with AI";

    /// <summary>A script editor opened this window: it writes for that one now.</summary>
    internal void Aim(ScriptWriterJob job)
    {
        _writer = job;
        _revise.Checked = EditorScript().Trim().Length > 0;
        _includeOutput.Checked = EditorOutput().Trim().Length > 0;
        ShowContext();
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    // ------------------------------------------------------------------- building

    private void BuildUi()
    {
        BackColor = ChatLook.Paper;

        // --- the conversation, which takes what the panels leave ---
        _scroll.Dock = DockStyle.Fill;
        _scroll.AutoScroll = true;
        _scroll.BackColor = ChatLook.Paper;
        _scroll.Padding = new Padding(8);
        _scroll.AllowDrop = true;

        _cards.Dock = DockStyle.Top;
        _cards.ColumnCount = 1;
        _cards.AutoSize = true;
        _cards.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _cards.BackColor = ChatLook.Paper;
        _cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _scroll.Controls.Add(_cards);

        var talk = new RoundedPanel { Dock = DockStyle.Fill, Padding = new Padding(3) };
        talk.Controls.Add(_scroll);

        // Three boxes in a row, and the question box under all three, as the page has them.
        // Docked in reverse: the conversation first, then each panel and its arrow from the
        // inside out.
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 10, 10, 0) };
        body.Controls.Add(talk);
        body.Controls.Add(BuildChatsPanel());
        // On a window too narrow for both, the chats' arrow is a choice of the chats over the
        // files, which shut to make the room; otherwise it would open nothing.
        body.Controls.Add(Strip(_chatsStrip, DockStyle.Left, "›", "Show the chats kept " + _ai.KeptWhere, () =>
        {
            _chatsOpen = true;
            if (_filesOpen && !RoomForBoth) _filesOpen = false;
            ShowPanels();
        }));
        body.Controls.Add(BuildFilesPanel());
        body.Controls.Add(Strip(_filesStrip, DockStyle.Right, "‹", "Show the files in this conversation", () => { _filesOpen = true; ShowPanels(); }));

        Controls.Add(body);
        Controls.Add(BuildCompactBar());
        Controls.Add(BuildComposer());

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
            ButtonStyle.Normalize(this, _compact, _notNow);
            FitBar();
            _slash.ItemHeight = Math.Max(_bold.Height, _body.Height) + LogicalToDeviceUnits(8);
            ShowPanels();
            await PickConnectionAsync();
        };

        Shown += (_, _) =>
        {
            ShowContext();
            DrawAll();
            FitPrompt();
            _prompt.Focus();
        };
        VisibleChanged += (_, _) =>
        {
            if (!Visible) return;
            ShowContext();
            if (_stale) DrawAll();
        };
        Activated += async (_, _) =>
        {
            ShowContext();
            // Another AI window may have picked another model while this one was behind it.
            AiConnections now = await _ai.LoadConnectionsAsync();
            if (!_chat.Generating && now.Selected?.Id != _connection?.Id) await PickConnectionAsync();
        };
    }

    /// <summary>
    /// The box and everything about the next question in one rounded frame, the words on top and a
    /// slim row of borderless controls along its foot; over it, what is waiting to go and anything
    /// the window has to say; under it, what the next question costs.
    /// </summary>
    private Control BuildComposer()
    {
        var composer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 8, 10, 0),
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

        // One line about what just happened and the conversation does not show, gone with the next key.
        _notice.AutoSize = true;
        _notice.ForeColor = Warning;
        _notice.Margin = new Padding(0, 0, 0, 4);
        _notice.Visible = false;
        _notice.UseMnemonic = false;
        _wrapping.Add(_notice);

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
        _prompt.BorderStyle = BorderStyle.None;
        _prompt.BackColor = ChatLook.Paper;
        _prompt.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _prompt.Font = new Font("Segoe UI", 10f);
        // Disabled so a press on it goes through to the box under it.
        _promptHint.Text = _writer is null
            ? "Ask about the bench, or type / for commands"
            : "Say what the script should do, or type / for commands";
        _promptHint.AutoSize = true;
        _promptHint.Enabled = false;
        _promptHint.BackColor = ChatLook.Paper;
        _prompt.Controls.Add(_promptHint);
        _prompt.Margin = new Padding(4, 4, 4, 6);
        _prompt.TextChanged += (_, _) =>
        {
            if (_prompt.TextLength > 0) Say("");
            _promptHint.Visible = _prompt.TextLength == 0;
            UpdateSlash();
            UpdateEnabled();
            FitPrompt();
            ShowEstimate();
        };
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

        var frame = new RoundedTable
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            BackColor = ChatLook.Paper,
            Edge = Color.FromArgb(173, 181, 189),
            Radius = 8,
            Padding = new Padding(6, 5, 6, 5),
            Margin = new Padding(0),
        };
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var line = new Panel
        {
            Height = 1,
            BackColor = ChatLook.Rule,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 0, 0, 4),
        };

        // The end of the row keeps its width and the start takes what is left, wrapping in a
        // narrow window rather than cutting anything off. The row's height is worked out here from
        // the width it has: a wrapping row measured by a table is measured at some other width.
        var bar = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };

        var start = new FlowLayoutPanel { WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        BarButton(_attach, "Attach pictures, PDFs or text files to the next question. A picture can be pasted into the box, and files dropped on the window.", (_, _) => ChooseFiles());
        BarButton(_screenshot, "Attach a picture of the screen this window is on. The window steps aside while it is taken.", async (_, _) => await TakeScreenshotAsync());
        start.Controls.Add(_attach);
        start.Controls.Add(_screenshot);

        if (_writer is null)
        {
            // How far the model may go on its own. Never remembered: every start of the app is on
            // the middle rung, so a restart never finds Change without asking already on.
            MenuButton(_rung,
                "Ask before every step: every call waits for you, reads included.\n"
              + "Ask before changes: reads run at once; each send waits for you.\n"
              + "Change without asking: sends run at once too. There is no undo on an instrument.",
                (_, _) => ShowRungMenu());
            ShowRung();
            start.Controls.Add(_rung);
        }
        else
        {
            BarSwitch(_revise, "Revise the current script", "Send the script that is in the editor, so the model changes it rather than starting again.");
            BarSwitch(_includeOutput, "Include the last run's output", "Send the output pane as well, errors included. This is what makes \"it failed, fix it\" answerable.");
            _revise.Checked = EditorScript().Trim().Length > 0;
            _includeOutput.Checked = EditorOutput().Trim().Length > 0;
            start.Controls.Add(_revise);
            start.Controls.Add(_includeOutput);
        }

        var end = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Right,
            Margin = new Padding(12, 0, 0, 0),
        };

        // Send and Stop are one button, in one place: a double-click on Send must not stop what
        // it sent, so a click arriving as the second of a pair is let go.
        _send.FlatStyle = FlatStyle.Flat;
        _send.UseVisualStyleBackColor = false;
        _send.FlatAppearance.BorderSize = 0;
        _send.BackColor = ChatLook.ButtonGrey;
        _send.FlatAppearance.MouseOverBackColor = ChatLook.ButtonGreyDown;
        _send.FlatAppearance.MouseDownBackColor = ChatLook.ButtonGreyDown;
        _send.Margin = new Padding(3, 0, 0, 0);
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

        BarButton(_meterToggle, "", (_, _) =>
        {
            _meterOn = !_meterOn;
            _meter.Visible = _meterOn;
            _meterToggle.BackColor = _meterOn ? ChatLook.ButtonGrey : ChatLook.Paper;
            ShowEstimate();
        });
        _meterToggle.BackColor = ChatLook.ButtonGrey;

        MenuButton(_model, "Which saved model answers, and how hard it is asked to think, for this question only. Thinking is billed as output.", (_, _) => ShowModelMenu());

        // The tools this conversation may use: switched off, one is not offered, is refused if
        // called anyway, and is named in the prompt; kept through New Chat, as somebody who
        // switched send off wants it off in the next conversation too.
        BarButton(_toolsButton, _writer is null
            ? "Switch tools off for this conversation. A tool switched off is not offered, and the model is told so."
            : "Compact the conversation", (_, _) => ShowToolMenu());

        end.Controls.Add(_send);
        end.Controls.Add(_meterToggle);
        end.Controls.Add(_model);
        end.Controls.Add(_toolsButton);

        bar.Controls.Add(start);
        bar.Controls.Add(end);
        bar.Layout += (_, _) =>
        {
            int room = Math.Max(1, bar.ClientSize.Width - end.Width);
            int height = Math.Max(start.GetPreferredSize(new Size(room, 0)).Height, end.PreferredSize.Height);
            if (bar.Height != height) bar.Height = height;
        };

        frame.Controls.Add(_prompt, 0, 0);
        frame.Controls.Add(line, 0, 1);
        frame.Controls.Add(bar, 0, 2);

        // A press in the frame's spare room puts the caret in the box.
        foreach (Control room in new Control[] { frame, bar, start, end })
            room.Click += (_, _) => _prompt.Focus();

        _meter.ColumnCount = 3;
        _meter.RowCount = 1;
        _meter.AutoSize = true;
        _meter.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _meter.Dock = DockStyle.Fill;
        _meter.Margin = new Padding(6, 4, 6, 6);
        _meter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _meter.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _meter.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _estimate.AutoSize = true;
        _estimate.ForeColor = SystemColors.GrayText;
        _estimate.Margin = new Padding(0);
        _wrapping.Add(_estimate);

        _spent.AutoSize = true;
        _spent.ForeColor = SystemColors.GrayText;
        _spent.Margin = new Padding(12, 0, 12, 0);
        _spent.Visible = false;

        _rates.Text = "rates";
        _rates.AutoSize = true;
        _rates.Margin = new Padding(0);
        _rates.Visible = false;
        _rates.LinkClicked += (_, _) =>
        {
            if (_connection is not null && AiPricing.PricingPage(_connection) is string page)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true });
        };
        _tips.SetToolTip(_rates, "Where the provider publishes its rates.");

        _meter.Controls.Add(_estimate, 0, 0);
        _meter.Controls.Add(_spent, 1, 0);
        _meter.Controls.Add(_rates, 2, 0);

        _tips.SetToolTip(_prompt, _writer is null
            ? "Enter sends, Shift+Enter starts a new line. A slash at the start lists the window's commands."
            : "Plain English. Say what you want measured or set, and at what values: the more specific, the less the model has to guess. Enter sends it, Shift+Enter starts a new line.");

        composer.Controls.Add(_slash, 0, 0);
        composer.Controls.Add(_notice, 0, 1);
        composer.Controls.Add(_attachProblem, 0, 2);
        composer.Controls.Add(_chips, 0, 3);
        composer.Controls.Add(frame, 0, 4);
        composer.Controls.Add(_meter, 0, 5);
        return composer;
    }

    /// <summary>One of the row's picture buttons: no border until it is pointed at.</summary>
    private void BarButton(Button button, string tip, EventHandler click)
    {
        Flatten(button);
        button.Margin = new Padding(1, 0, 1, 0);
        button.Click += click;
        if (tip.Length > 0) _tips.SetToolTip(button, tip);
    }

    /// <summary>One of the row's worded buttons, which opens a menu: the model, the rung.</summary>
    private void MenuButton(Button button, string tip, EventHandler click)
    {
        Flatten(button);
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Margin = new Padding(2, 0, 2, 0);
        button.Click += click;
        _tips.SetToolTip(button, tip);
    }

    private static void Flatten(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = ChatLook.Paper;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = ChatLook.Hover;
        button.FlatAppearance.MouseDownBackColor = ChatLook.Pressed;
    }

    private void BarSwitch(CheckBox box, string text, string tip)
    {
        box.Text = text;
        box.AutoSize = true;
        box.Margin = new Padding(8, 0, 6, 0);
        box.CheckedChanged += (_, _) =>
        {
            MeasureWriter();
            ShowEstimate();
        };
        _tips.SetToolTip(box, tip);
    }

    /// <summary>
    /// The row's sizes and pictures, once the window has scaled: everything here is 30 tall at
    /// 100%, the pictures drawn straight to the screen's size so they stay sharp.
    /// </summary>
    private void FitBar()
    {
        int side = LogicalToDeviceUnits(30);
        int glyph = LogicalToDeviceUnits(18);

        foreach ((Button button, string icon) in new[] { (_attach, "paperclip"), (_screenshot, "screenshot"), (_toolsButton, "sliders"), (_meterToggle, "info") })
        {
            button.Padding = new Padding(0);
            button.Size = new Size(side, side);
            button.Image = AppIcons.Drawn(icon, glyph);
            button.TextImageRelation = TextImageRelation.ImageBeforeText;
        }

        _send.Size = new Size(side, side);
        ShowSendFace();
        _promptHint.Location = new Point(LogicalToDeviceUnits(1), 0);

        foreach (Button button in new[] { _model, _rung })
        {
            button.Padding = this.Scaled(6, 0, 6, 0);
            button.MinimumSize = new Size(0, side);
            button.MaximumSize = new Size(0, side);
        }

        foreach (CheckBox box in new[] { _revise, _includeOutput })
        {
            box.Margin = this.Scaled(8, 0, 6, 0);
            ButtonStyle.CentreInRow(box, side);
        }

        _newChat.Padding = this.Scaled(6, 2, 8, 2);
        _newChat.Image = AppIcons.Drawn("plus", LogicalToDeviceUnits(16));
        _newChat.TextImageRelation = TextImageRelation.ImageBeforeText;
    }

    private void ShowSendFace()
    {
        bool busy = _chat.Generating;
        _send.Image = AppIcons.Drawn(busy ? "stopSquare" : "arrowUp", LogicalToDeviceUnits(16));
        _tips.SetToolTip(_send, busy ? "Stop" : _writer is null ? "Send the question (Enter)" : "Write the script (Enter)");
        _send.AccessibleName = busy ? "Stop" : _writer is null ? "Send" : "Write Script";
    }

    /// <summary>The suggestion to compact, priced both ways: what the history costs every question now, and the compaction once.</summary>
    private Control BuildCompactBar()
    {
        _compactBar.Dock = DockStyle.Bottom;
        _compactBar.Padding = new Padding(10, 8, 10, 0);
        _compactBar.Visible = false;

        var inner = new RoundedTable
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = ChatLook.SuggestFill,
            Edge = ChatLook.SuggestEdge,
            Radius = 4,
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
        _chatsPanel.Gutter = new Padding(0, 0, 8, 0);
        _chatsPanel.Padding = new Padding(8, 8, 16, 6);

        Flatten(_newChat);
        _newChat.Text = "New chat";
        _newChat.AutoSize = true;
        _newChat.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _newChat.Margin = new Padding(0);
        _newChat.Anchor = AnchorStyles.Left;
        _newChat.Click += (_, _) => NewChat();
        _tips.SetToolTip(_newChat, "Start a new conversation. This one stays in the list below, and what was spent stays counted.");

        var head = PanelHead(null, "Hide the chats", () => { _chatsOpen = false; ShowPanels(); }, _newChat);

        var heading = Heading("Chats");
        heading.Padding = new Padding(2, 8, 0, 4);

        _keptWhere.Dock = DockStyle.Bottom;
        _keptWhere.AutoSize = true;
        _keptWhere.Font = _small;
        _keptWhere.ForeColor = SystemColors.GrayText;
        _keptWhere.Padding = new Padding(2, 6, 0, 0);
        _keptWhere.Text = $"Kept {_ai.KeptWhere}";
        _tips.SetToolTip(_keptWhere, $"Every chat is kept {_ai.KeptWhere} from its first question on, after every answer, with its files.");

        _chatScroll.Dock = DockStyle.Fill;
        _chatScroll.AutoScroll = true;

        _chatsPanel.Controls.Add(_chatScroll);
        _chatsPanel.Controls.Add(_keptWhere);
        _chatsPanel.Controls.Add(heading);
        _chatsPanel.Controls.Add(head);
        return _chatsPanel;
    }

    private Control BuildFilesPanel()
    {
        _filesPanel.Dock = DockStyle.Right;
        _filesPanel.Width = 260;
        _filesPanel.Gutter = new Padding(8, 0, 0, 0);
        _filesPanel.Padding = new Padding(18, 8, 10, 6);

        var head = PanelHead("In this conversation", "Hide the files", () => { _filesOpen = false; ShowPanels(); }, null);

        _fileScroll.Dock = DockStyle.Fill;
        _fileScroll.AutoScroll = true;

        _filesFoot.Dock = DockStyle.Bottom;
        _filesFoot.AutoSize = true;
        _filesFoot.Font = _small;
        _filesFoot.ForeColor = SystemColors.GrayText;
        _filesFoot.Padding = new Padding(0, 6, 0, 0);
        _filesFoot.MaximumSize = new Size(230, 0);

        _filesPanel.Controls.Add(_fileScroll);
        _filesPanel.Controls.Add(_filesFoot);
        _filesPanel.Controls.Add(head);
        return _filesPanel;
    }

    /// <summary>A panel's heading: its name or its button, and the cross that closes it.</summary>
    private TableLayoutPanel PanelHead(string? title, string closeTip, Action close, Button? button)
    {
        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        if (button is not null) head.Controls.Add(button, 0, 0);
        else head.Controls.Add(new Label { Text = title, Font = _smallBold, AutoSize = true, Margin = new Padding(0, 4, 0, 0) }, 0, 0);
        LinkLabel cross = Link("✕", closeTip, close, new Padding(4, 4, 0, 0));
        cross.LinkColor = SystemColors.GrayText;
        head.Controls.Add(cross, 1, 0);
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
        strip.BackColor = ChatLook.Paper;
        strip.UseVisualStyleBackColor = false;
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
    private bool RoomForBoth => ClientSize.Width >= LogicalToDeviceUnits(240 + 260 + 460);

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

        string? had = _connection?.Id;
        _connection = _book.Selected?.Clone();
        if (_connection?.Id != had || !AiModels.EffortLevels(_connection).Contains(_effortChoice)) _effortChoice = "";
        ShowModel();

        UpdateEnabled();
        DrawAll();
    }

    private void ShowModel()
        => _model.Text = (_connection?.EffectiveModel ?? "No model") + "  ▾";

    /// <summary>
    /// The saved models with a check on the one picked and their provider beside each, the effort
    /// for the next question, and the way to AI Connection.
    /// </summary>
    private void ShowModelMenu()
    {
        if (_chat.Generating) return;

        _modelMenu.Items.Clear();
        List<string> labels = [.. _book.Labels()];
        for (int i = 0; i < _book.Items.Count; i++)
        {
            AiConnection saved = _book.Items[i];
            var item = new ToolStripMenuItem(labels[i]) { Checked = saved.Id == _connection?.Id };
            item.Click += async (_, _) =>
            {
                await _ai.SelectAsync(saved.Id);
                await PickConnectionAsync();
            };
            _modelMenu.Items.Add(item);
        }
        if (_modelMenu.Items.Count > 0) _modelMenu.Items.Add(new ToolStripSeparator());

        IReadOnlyList<string> levels = AiModels.EffortLevels(_connection);
        if (levels.Count > 0)
        {
            var effort = new ToolStripMenuItem("Effort: " + (_effortChoice.Length == 0 ? DefaultEffortLabel() : Cap(_effortChoice)));
            foreach (string level in new[] { "" }.Concat(levels))
            {
                string chosen = level;
                var item = new ToolStripMenuItem(level.Length == 0 ? DefaultEffortLabel() : Cap(level)) { Checked = level == _effortChoice };
                item.Click += (_, _) =>
                {
                    _effortChoice = chosen;
                    ShowEstimate();
                };
                effort.DropDownItems.Add(item);
            }
            _modelMenu.Items.Add(effort);
        }

        var manage = new ToolStripMenuItem("Manage models…");
        manage.Click += (_, _) => SettingsRequested?.Invoke();
        _modelMenu.Items.Add(manage);
        _modelMenu.Show(_model, Point.Empty, ToolStripDropDownDirection.AboveRight);
    }

    private void ShowRung() => _rung.Text = AssistantPrompt.RungLabel(_chat.Mode) + "  ▾";

    /// <summary>In force at once, an answer in the middle of coming included: a card waiting that the new rung would not have stopped for is let go there and then (AiChat.Mode).</summary>
    private void ShowRungMenu()
    {
        _rungMenu.Items.Clear();
        foreach (ToolApprovalMode mode in AssistantPrompt.Rungs)
        {
            ToolApprovalMode chosen = mode;
            var item = new ToolStripMenuItem(AssistantPrompt.RungLabel(mode)) { Checked = mode == _chat.Mode };
            item.Click += (_, _) =>
            {
                _chat.Mode = chosen;
                ShowRung();
                ShowEstimate();
            };
            _rungMenu.Items.Add(item);
        }
        _rungMenu.Show(_rung, Point.Empty, ToolStripDropDownDirection.AboveRight);
    }

    private string DefaultEffortLabel()
    {
        string said = AiModels.DefaultEffort(_connection);
        return said.Length == 0 ? "Default" : $"Default ({said})";
    }

    private string ChosenEffort() => _effortChoice;

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
            Say(why);
            return;
        }

        AiConnection connection = _connection!;
        int shown = _shown;
        if (fromBox) _prompt.Text = "";
        Say("");

        ChatOutcome outcome;
        if (_writer is ScriptWriterJob job)
        {
            ILlmProvider writer = job.Asking(connection, ChosenEffort(),
                _revise.Checked ? EditorScript() : null, _includeOutput.Checked ? EditorOutput() : null);
            outcome = await _chat.SendAsync(text, writer, "", null);
            if (outcome == ChatOutcome.Failed && _prompt.TextLength == 0) _prompt.Text = text;
        }
        else
        {
            ILlmProvider provider = _ai.CreateProvider(connection, ChosenEffort());
            _benchNow = BenchNow();
            string system = AssistantPrompt.Build(_benchNow, _chat.Mode, _chat.ToolsOff, Where, DateTime.Now);
            outcome = await _chat.SendAsync(text, provider, system, _tools);

            // A question that failed gave its files back to the box; its words go back with them
            // when the box is still empty, so asking again is one press.
            if (outcome == ChatOutcome.Failed && _chat.GaveFilesBack && _prompt.TextLength == 0)
                _prompt.Text = text;
        }

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
            Say(cannot);
            return;
        }
        if (_connection is null || _ai.WhyNotAsk(_connection) is not null)
        {
            Say(WhyNotAskNow() ?? NoConnection);
            return;
        }

        IReadOnlyList<string> levels = AiModels.EffortLevels(_connection);
        ILlmProvider provider = _ai.CreateProvider(_connection, levels.Count > 0 ? levels[0] : "");
        int shown = _shown;

        ChatOutcome outcome = await _chat.CompactAsync(provider);
        Say(outcome switch
        {
            ChatOutcome.Stopped => "Compacting was stopped. The conversation is as it was.",
            ChatOutcome.Failed => "Compacting failed. The conversation is as it was.",
            _ => "",
        });
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

    /// <summary>One line over the box about what the conversation does not show, or nothing.</summary>
    private void Say(string words)
    {
        _notice.Text = words;
        _notice.Visible = words.Length > 0;
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
        var commands = new List<SlashCommand>
        {
            new("/new", "Start a new chat. This one stays in the chats list.", answering, _ => NewChat()),
            new("/compact", "Put a briefing in place of the conversation so far, to send less with every question.",
                _chat.WhyNotCompact() ?? (_connection is null ? NoConnection : _ai.WhyNotAsk(_connection)), none => _ = CompactAsync()),
            new("/export", "Save this conversation as text.",
                answering ?? (_chat.Turns.Count == 0 ? "Nothing has been said yet." : null), _ => SaveConversation()),
        };
        if (_writer is null) commands.Add(new("/tools", "Choose the tools this conversation may use.", answering, _ => ShowToolMenu()));
        commands.Add(new("/screenshot", "Attach a picture of the screen this window is on.", null, none => _ = TakeScreenshotAsync()));
        commands.Add(new("/settings", "Open AI Connection, where the connections and their keys are kept.", null, _ => SettingsRequested?.Invoke()));
        if (_writer is null)
        {
            commands.Add(new("/status", "Ask what is open and what each instrument reads now.", asking,
                words => _ = SendAsync(With(AssistantPrompt.StatusQuestion, words), fromBox: false)));
            commands.Add(new("/errors", "Ask for every instrument's error queue, read and explained.", asking,
                words => _ = SendAsync(With(AssistantPrompt.ErrorsQuestion, words), fromBox: false)));
            commands.Add(new("/script", "Start a request for a script, to finish in the box.", null, _ => StartRequest(AssistantPrompt.ScriptStart)));
        }
        return commands;
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
            Say($"{command.Name}: {why}");
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
        foreach (var (heading, tools) in _writer is not null ? [] : new[]
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

        if (_toolMenu.Items.Count > 0) _toolMenu.Items.Add(new ToolStripSeparator());
        var allOn = new ToolStripMenuItem("Every tool on") { Enabled = _chat.ToolsOff.Count > 0 };
        allOn.Click += (_, _) =>
        {
            _chat.ToolsOff.Clear();
            _toolMenu.Close();
            ShowToolCount();
            ShowEstimate();
        };
        if (_writer is null) _toolMenu.Items.Add(allOn);
        var compact = new ToolStripMenuItem("Compact the conversation") { Enabled = _chat.WhyNotCompact() is null, ToolTipText = _chat.WhyNotCompact() ?? "" };
        compact.Click += async (_, _) =>
        {
            _toolMenu.Close();
            await CompactAsync();
        };
        _toolMenu.Items.Add(compact);

        _toolMenu.Show(_toolsButton, Point.Empty, ToolStripDropDownDirection.AboveRight);
    }

    private static void KeepOpenWhileTicking(object? sender, ToolStripDropDownClosingEventArgs e)
    {
        if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked
            && sender is ContextMenuStrip menu && menu.Items.OfType<ToolStripMenuItem>().Any(i => i.Selected && i.CheckOnClick))
            e.Cancel = true;
    }

    /// <summary>The picture alone until a tool is off, and then how many beside it.</summary>
    private void ShowToolCount()
    {
        int side = LogicalToDeviceUnits(30);
        int off = _chat.ToolsOff.Count;
        _toolsButton.Text = off == 0 ? "" : off.ToString(CultureInfo.InvariantCulture);
        _toolsButton.Width = off == 0 ? side : side + LogicalToDeviceUnits(16);
    }

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
            Bench = BenchLineNow(),
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
            Say("The chat could not be kept: " + ex.Message);
        }
    }

    /// <summary>What this chat is about, as its row in the list says it.</summary>
    private string BenchLineNow() => _writer is ScriptWriterJob job
        ? string.Join(", ", job.Instruments.Select(i => i.Alias.Length > 0 ? $"{i.Alias} {i.Model}" : i.Model))
        : AssistantPrompt.BenchLine(BenchNow());

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
        Say("");
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
            Say("That chat could not be read: " + ex.Message);
            return;
        }
        if (kept is null)
        {
            Say("That chat is gone: it was deleted, or its file will not read.");
            await DrawChatsAsync();
            return;
        }
        if (_chat.Generating) return;

        _shown++;
        _benchNow = BenchNow();
        _chat.Restore(kept.Unkept(), AssistantPrompt.ReopenedLine(kept.Bench, BenchLineNow(), DateTime.Now));
        _chatId = kept.Id;
        _created = kept.Created == default ? DateTime.Now : kept.Created;
        _confirmingDelete = null;
        _showFolded = false;
        ClearPictures();
        Say("");
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
            Say("That chat could not be deleted: " + ex.Message);
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
            Say("That chat is gone: it was deleted, or its file will not read.");
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
            Say("Saved to " + dlg.FileName + ".");
        }
        catch (Exception ex)
        {
            Say("Could not save it: " + ex.Message);
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
            Say("The chats could not be read: " + ex.Message);
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
    /// A kept chat's row: its title and when it was last kept, with what was on the bench, and its
    /// two picture buttons, shown under the pointer and on the chat on screen. Delete asks in the
    /// row, not in a dialog. Nothing is switched while an answer is coming: a press then only says why.
    /// </summary>
    private Control ChatRow(ChatSummary chat, bool busy)
    {
        bool current = chat.Id == _chatId;
        bool confirming = chat.Id == _confirmingDelete;

        var row = new RoundedTable
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = current ? ChatLook.Chosen : ChatLook.Paper,
            Padding = this.Scaled(6, 4, 4, 4),
            Margin = new Padding(0),
            Height = _body.Height + _small.Height + LogicalToDeviceUnits(16),
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
            Text = (chat.Bench.Length > 0 ? chat.Bench + " · " : "") + When(chat.Updated),
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
            Visible = current || confirming,
        };
        var pointed = new List<Control> { row, title, when, actions };
        if (confirming)
        {
            actions.Controls.Add(new Label { Text = "Delete it?", Font = _small, AutoSize = true, Margin = this.Scaled(0, 1, 4, 0) });
            actions.Controls.Add(SmallLink("Delete", "Delete this chat for good", () => _ = DeleteChatAsync(chat.Id)));
            actions.Controls.Add(SmallLink("Keep", "Keep it", () => { _confirmingDelete = null; DrawChatRows(); }));
        }
        else
        {
            Button save = RowButton("download", busy ? WhyBusy : "Save this chat as text: every turn, each call with what it answered, and what each answer cost.",
                () => { if (!_chat.Generating) _ = ExportChatAsync(chat.Id); });
            Button delete = RowButton("trash", busy ? WhyBusy : "Delete this chat. It asks first, here in the row.",
                () => { if (!_chat.Generating) { _confirmingDelete = chat.Id; DrawChatRows(); } });
            actions.Controls.Add(save);
            actions.Controls.Add(delete);
            pointed.Add(save);
            pointed.Add(delete);
        }

        row.Controls.Add(title, 0, 0);
        row.Controls.Add(when, 0, 1);
        row.Controls.Add(actions, 1, 0);
        row.SetRowSpan(actions, 2);

        // Each child takes the pointer from the row, so the row's own leaving says nothing: what
        // counts is whether the pointer is still inside it.
        foreach (Control c in pointed)
        {
            c.MouseEnter += (_, _) => actions.Visible = true;
            c.MouseLeave += (_, _) =>
            {
                if (!current && !confirming && !row.ClientRectangle.Contains(row.PointToClient(Cursor.Position)))
                    actions.Visible = false;
            };
        }

        string tip = busy ? WhyBusy : current ? "The chat on screen." : $"Open it and carry on: {chat.Title}";
        foreach (Control c in new Control[] { row, title, when })
        {
            c.Cursor = current ? Cursors.Default : Cursors.Hand;
            c.Click += async (_, _) =>
            {
                if (_chat.Generating) { Say(WhyBusy); return; }
                await OpenChatAsync(chat.Id);
            };
            _tips.SetToolTip(c, tip);
        }
        return row;
    }

    private Button RowButton(string icon, string tip, Action click)
    {
        var button = new Button
        {
            Size = new Size(LogicalToDeviceUnits(24), LogicalToDeviceUnits(24)),
            Margin = new Padding(0),
            Image = AppIcons.Drawn(icon, LogicalToDeviceUnits(15)),
        };
        Flatten(button);
        button.BackColor = Color.Transparent;
        button.Click += (_, _) => click();
        _tips.SetToolTip(button, tip);
        return button;
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

        var rows = new List<Control>();
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
        if (pending.Count == 0) rows.Add(Hint("None. Attach one with the paperclip, paste a picture into the box, or drop files here."));
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
        Font = _smallBold,
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

    /// <summary>What the title bar and the opening words say this window is working on, and what the next question will carry.</summary>
    private void ShowContext()
    {
        string working;
        if (_writer is ScriptWriterJob)
        {
            MeasureWriter();
            working = BenchLineNow();
        }
        else
        {
            _benchNow = BenchNow();
            working = AssistantPrompt.BenchLine(_benchNow);
        }
        Text = working.Length == 0 ? Named : $"{Named}  ·  {working}";

        if (Visible && _chat.Turns.Count == 0 && IntroKey() != _introDrawn) DrawCardsOnly();
        ShowEstimate();
    }

    private string IntroKey() => _writer is null ? AssistantPrompt.Context(_benchNow) : WriterSummary() + _book.Items.Count;

    private string EditorScript() => _writer is { Editor.IsDisposed: false } job ? job.CurrentScript() : "";

    private string EditorOutput() => _writer is { Editor.IsDisposed: false } job ? job.RecentOutput() : "";

    /// <summary>What goes with every request whatever is asked. Measured when it can change, not at every key: it is the catalogs.</summary>
    private void MeasureWriter()
    {
        if (_writer is not ScriptWriterJob job) return;

        string script = EditorScript();
        string output = EditorOutput();
        if (script.Trim().Length == 0) _revise.Checked = false;
        if (output.Trim().Length == 0) _includeOutput.Checked = false;
        _revise.Enabled = !_chat.Generating && script.Trim().Length > 0;
        _includeOutput.Enabled = !_chat.Generating && output.Trim().Length > 0;

        _promptCharacters = new ScriptAuthor(new AiClient()).PromptCharacters(
            job.Instruments, job.IsSequence, _revise.Checked ? script : null, _includeOutput.Checked ? output : null);
    }

    private string WriterSummary()
    {
        if (_writer is not ScriptWriterJob job) return "";
        var lines = new List<string>();
        foreach (ScriptContextInstrument i in job.Instruments)
        {
            int count = i.Reference?.Commands.Count ?? 0;
            string name = i.Alias.Length > 0 ? $"{i.Alias}: {i.Model}" : i.Model;
            lines.Add(count > 0
                ? $"• {name}: {count:N0} documented commands"
                : $"• {name}: no catalog, so only *IDN?, *RST, *CLS and *OPC? can be checked");
        }
        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// What the next question will carry, before it is sent (the instructions, the tools or the
    /// catalogs, the conversation and its files, the box), in tokens and, where the model has a
    /// rate, dollars; what has been spent; and where the rates are published.
    /// </summary>
    private void ShowEstimate()
    {
        if (_connection is null)
        {
            _estimate.Text = NoConnection;
            _spent.Visible = _rates.Visible = false;
            _tips.SetToolTip(_meterToggle, NoConnection);
            return;
        }

        long tokens;
        if (_writer is null)
        {
            string system = AssistantPrompt.Build(_benchNow, _chat.Mode, _chat.ToolsOff, Where, DateTime.Now);
            tokens = _chat.EstimateTokens(system, _tools.Tools, _connection, _prompt.Text);
        }
        else
        {
            tokens = AiEstimate.TokensIn(_promptCharacters) + _chat.EstimateTokens("", [], _connection, _prompt.Text);
        }

        LlmPrice? price = AiPricing.For(_connection, Today);
        string cost = price is null ? "" : $", {AiPricing.Money(price.CostOfInput(tokens))} to send";
        string line = $"Next request: about {AiPricing.About(tokens)} tokens{cost}";
        _estimate.Text = line;
        _tips.SetToolTip(_estimate, _writer is null ? "Sent again with every round of tool calls." : "The catalogs, the conversation so far, its files and the box.");

        LlmUsage used = _chat.Used;
        _spent.Visible = used.Reported;
        _spent.Text = _chat.Cost is decimal dollars
            ? $"Spent: {AiPricing.Money(dollars)}"
            : $"Spent: {AiPricing.Count(used.InputTokens)} tokens in, {AiPricing.Count(used.OutputTokens)} out";
        _rates.Visible = AiPricing.PricingPage(_connection) is not null;

        string about = line + (used.Reported ? "\n" + _spent.Text : "");
        _tips.SetToolTip(_meterToggle, about + (_meterOn
            ? "\nPress to take the costs off the line under the box."
            : "\nPress to keep the costs on a line under the box."));
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
        _send.Enabled = busy || (_prompt.Text.Trim().Length > 0 && (slash || (_connection is not null && WhyNotSend() is null)));
        ShowSendFace();
        _model.Enabled = !busy;
        _toolsButton.Enabled = !busy;
        _newChat.Enabled = !busy;
        if (_writer is not null)
        {
            _revise.Enabled = !busy && EditorScript().Trim().Length > 0;
            _includeOutput.Enabled = !busy && EditorOutput().Trim().Length > 0;
        }
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
        _scripts.Clear();

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
                for (int i = 0; i < folded; i++) AddTurn(turns[i]);
                _dimming = false;
            }
        }
        for (int i = folded; i < turns.Count; i++) AddTurn(turns[i]);
        if (turns.Count == 0) _cards.Controls.Add(Intro());

        if (_chat.Compacting) _cards.Controls.Add(Note("Compacting the conversation…"));
        else if (_chat.Generating) _cards.Controls.Add(Note("Thinking…"));

        _cards.ResumeLayout();
        _scroll.ResumeLayout();
        FitLabelWidths();
        if (atBottom) _scroll.AutoScrollPosition = new Point(0, _scroll.DisplayRectangle.Height);
    }

    private void AddTurn(ChatTurn turn)
    {
        if (turn.Kind == ChatTurn.Assistant && _writer is not null && WrittenScript.Read(turn.Text) is WrittenScript written)
        {
            _cards.Controls.Add(WrittenCard(written));
            if (written.Notes.Length > 0 || AiChat.UsageLine(turn).Length > 0) _cards.Controls.Add(Reply(written.Notes, turn));
            return;
        }
        _cards.Controls.Add(Card(turn));
    }

    /// <summary>What an empty conversation says: what the window works on, and something to start from.</summary>
    private Control Intro()
    {
        _introDrawn = IntroKey();
        var intro = Stack(ChatLook.Paper);

        if (_writer is ScriptWriterJob job)
        {
            intro.Controls.Add(Words(
                "The model is given the command catalogs below and told to use nothing else. Check what it writes before you run it. "
              + "Attach a photo of the wiring, a datasheet or a screenshot, or type / for the window's commands.", _body));
            intro.Controls.Add(Words(WriterSummary(), _body));
            intro.Controls.Add(Pill(job.IsSequence
                ? "Sweep the generator from 100 Hz to 100 kHz in 20 log steps and record the scope's peak-to-peak voltage at each one."
                : "Set a 1 kHz sine at 2 Vpp on channel 1, turn the output on, wait a second, then read it back."));
            intro.Controls.Add(Words("What comes back appears here, and each new request is asked with everything above it, so a follow-up like “now do the same at 5 V” means something.", _body));
        }
        else
        {
            intro.Controls.Add(Words(
                "Ask about the bench, or have the assistant work it: it reads the instruments with the bench's own tools, and can write a script for you to run. "
              + "Attach a datasheet or a screenshot, or type / for the window's commands.", _body));
            foreach (string example in Examples) intro.Controls.Add(Pill(example));
            intro.Controls.Add(Words(AssistantPrompt.Context(_benchNow), _body, _benchNow.Count == 0 ? Warning : null));
            intro.Controls.Add(Words(
                "What you type, and what the assistant reads from the instruments, pictures of their screens included, "
              + "is sent to the provider of the model picked below. Nothing is sent until you press Send.", _body));
        }

        if (_book.Items.Count == 0)
            intro.Controls.Add(Link("There is no model yet. Add one, with your own key, under AI Connection.", "Open AI Connection", () => SettingsRequested?.Invoke(), this.Scaled(0, 6, 0, 0)));
        return intro;
    }

    /// <summary>An example to start from: put in the box by a press rather than sent.</summary>
    private Control Pill(string example)
    {
        var pill = new Bubble
        {
            BackColor = ChatLook.Paper,
            Edge = ChatLook.Rule,
            Radius = 12,
            Padding = this.Scaled(10, 4, 10, 3),
            Margin = this.Scaled(0, 4, 0, 6),
            Cursor = Cursors.Hand,
        };
        Label words = Words(example, _body);
        words.Cursor = Cursors.Hand;
        pill.Controls.Add(words);
        void Put(object? sender, EventArgs e)
        {
            _prompt.Text = example;
            _prompt.SelectionStart = _prompt.TextLength;
            _prompt.Focus();
        }
        pill.Click += Put;
        words.Click += Put;
        _tips.SetToolTip(pill, "Put this in the box, to send or change first");
        _tips.SetToolTip(words, "Put this in the box, to send or change first");
        return pill;
    }

    private Control Card(ChatTurn turn) => turn.Kind switch
    {
        ChatTurn.User => Question(turn),
        ChatTurn.Assistant => Reply(turn.Text, turn),
        ChatTurn.Tool => ToolCard(turn),
        ChatTurn.Error => Problem(turn),
        ChatTurn.Paused => PausedCard(turn),
        ChatTurn.Summary => Briefing(turn),
        _ => Note(turn.Text),
    };

    /// <summary>A question: on the right in blue, with the files it went with over it.</summary>
    private Control Question(ChatTurn turn)
    {
        FlowLayoutPanel column = Column(right: true);
        if (turn.Attachments.Count > 0)
        {
            var files = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.Right, Margin = this.Scaled(0, 0, 0, 3) };
            foreach (AiAttachment file in turn.Attachments)
            {
                Bubble pill = Balloon(ChatLook.Paper, ChatLook.ChangeEdge, 9);
                pill.Padding = this.Scaled(7, 1, 7, 0);
                pill.Margin = this.Scaled(4, 0, 0, 0);
                pill.Controls.Add(Words(file.Name + (file.Excluded ? ", left out" : ""), _small, file.Excluded ? SystemColors.GrayText : null));
                files.Controls.Add(pill);
            }
            column.Controls.Add(files);
        }
        Bubble bubble = Balloon(ChatLook.AskedFill);
        bubble.Anchor = AnchorStyles.Right;
        bubble.Controls.Add(Words(turn.Text, _body, wide: false));
        column.Controls.Add(bubble);
        return column;
    }

    /// <summary>An answer: on the left in grey, and what it was billed under it.</summary>
    private Control Reply(string text, ChatTurn turn)
    {
        FlowLayoutPanel column = Column(right: false);
        if (text.Length > 0)
        {
            Bubble bubble = Balloon(ChatLook.AnsweredFill);
            bubble.Controls.Add(Words(text, _body, wide: false));
            column.Controls.Add(bubble);
        }
        AddUsage(column, turn);
        return column;
    }

    private void AddUsage(Control to, ChatTurn turn)
    {
        if (AiChat.UsageLine(turn) is not { Length: > 0 } usage) return;
        Label line = Words(usage, _small, SystemColors.GrayText);
        line.Margin = this.Scaled(2, 3, 0, 0);
        to.Controls.Add(line);
    }

    private Control Problem(ChatTurn turn)
    {
        Bubble box = CardBox(ChatLook.ErrorFill, ChatLook.ErrorEdge);
        box.Controls.Add(Words(turn.Text, _body, ChatLook.ErrorInk));
        AddUsage(box, turn);
        return box;
    }

    private Control PausedCard(ChatTurn turn)
    {
        Bubble box = CardBox(ChatLook.ReadFill, ChatLook.ReadEdge);
        box.Controls.Add(Words(turn.Text, _body));
        var go = new Button();
        ButtonStyle.Apply(go, "Continue", (_, _) => _ = SendAsync("Continue", fromBox: false));
        go.Enabled = !_chat.Generating && ReferenceEquals(turn, _chat.Turns.LastOrDefault());
        _tips.SetToolTip(go, "Carry on for as many rounds again. Each round sends the conversation again.");
        Primary(go);
        box.Controls.Add(go);
        return box;
    }

    /// <summary>The briefing a compaction put in place of the conversation before it, and the files it keeps unticked.</summary>
    private Control Briefing(ChatTurn turn)
    {
        Bubble box = CardBox(ChatLook.SuggestFill, ChatLook.SuggestEdge);
        box.Controls.Add(Words("Briefing: the conversation above, compacted. The model is sent this in its place.", _bold));
        box.Controls.Add(Words(turn.Text, _body));
        if (turn.Attachments.Count > 0)
            box.Controls.Add(Words("Kept from before it, unticked in the files panel: " + string.Join(", ", turn.Attachments.Select(a => a.Name)), _small, SystemColors.GrayText));
        AddUsage(box, turn);
        return box;
    }

    /// <summary>What a card says happened, which depends on the kind of call and on how it ended.</summary>
    private static string CardTitle(ChatTurn turn)
    {
        string what = turn.ToolName.Replace('_', ' ');
        if (turn.Proposes && turn.Decision is not false) return turn.Decision is null ? $"Wants to {what}" : "Proposed a script";
        if (turn.Decision is null) return turn.Changes ? $"Wants to change an instrument: {what}" : $"Wants to look: {what}";
        if (turn.Decision == false) return $"Asked to {what}";
        return turn.Changes ? $"Changed an instrument: {what}" : $"Looked: {what}";
    }

    /// <summary>
    /// A tool call: amber for one that reads, blue for one that changes an instrument. What the
    /// model asked for, as it asked for it; whether it ran on its own, waits for an answer here, or
    /// was answered; and what the model was told, the picture it was shown included. A proposed
    /// script carries its own Use This Script.
    /// </summary>
    private Control ToolCard(ChatTurn turn)
    {
        bool waiting = AssistantPrompt.Waiting(turn);
        Bubble box = turn.Changes ? CardBox(ChatLook.ChangeFill, ChatLook.ChangeEdge) : CardBox(ChatLook.ReadFill, ChatLook.ReadEdge);

        string state = AssistantPrompt.CallState(turn);
        box.Controls.Add(Words(CardTitle(turn), _bold));
        box.Controls.Add(Words($"{turn.ToolName}   {AssistantPrompt.CallLine(turn.ArgumentsJson)}", _code));
        if (state.Length > 0) box.Controls.Add(Words(state, _small, turn.Decision == false ? Warning : SystemColors.GrayText));

        if (waiting)
        {
            var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = this.Scaled(0, 4, 0, 0) };
            var approve = new Button();
            var decline = new Button();
            ButtonStyle.Apply(approve, turn.Changes ? "Approve and Send" : "Approve", (_, _) => _chat.Decide(turn, true));
            ButtonStyle.Apply(decline, "Decline", (_, _) => _chat.Decide(turn, false));
            Primary(approve);
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
                Margin = this.Scaled(0, 4, 0, 0),
            });
        }

        if (turn.Proposal is string script)
        {
            box.Controls.Add(ScriptBox(script));
            var use = new Button();
            ButtonStyle.Apply(use, "Use This Script", async (_, _) => await UseScriptAsync(turn.ArgumentsJson, script));
            _tips.SetToolTip(use, "Put it in the script editor, to read and run there. Nothing runs until you press Run.");
            Primary(use);
            box.Controls.Add(use);
        }
        return box;
    }

    /// <summary>
    /// What the writer wrote: the script, the button that takes it under the script it takes, and
    /// what the catalog check said. The check before the model's own note, which comes after the
    /// card: what must be read is what the catalog says, not what the thing being checked says about itself.
    /// </summary>
    private Control WrittenCard(WrittenScript written)
    {
        Bubble card = CardBox(ChatLook.ReadFill, ChatLook.ReadEdge);
        card.Controls.Add(Words("Proposed a script", _bold));
        card.Controls.Add(ScriptBox(written.Script));

        var use = new Button();
        ButtonStyle.Apply(use, "Use This Script", (_, _) => UseWritten(written.Script));
        _tips.SetToolTip(use, "Put this script into the editor. It is not run: you still press Run yourself.");
        Primary(use);
        card.Controls.Add(use);

        if (written.Undocumented.Count > 0)
        {
            card.Controls.Add(Words(
                $"⚠ {written.Undocumented.Count} line(s) use commands that are in no catalog here. Check them against the instrument's guide before running: "
              + string.Join(", ", written.Undocumented.Take(8)) + (written.Undocumented.Count > 8 ? ", …" : ""),
                _small, ChatLook.ErrorInk));
        }
        else
        {
            card.Controls.Add(Words("Every command header here is in the catalog. The values after them were not checked: read the script before you run it.", _small));
        }
        return card;
    }

    private void UseWritten(string script)
    {
        if (_writer is not ScriptWriterJob job) return;
        if (job.Editor.IsDisposed)
        {
            Say("That editor has been closed. Copy the script from the card instead.");
            return;
        }
        Hide();
        job.Editor.Activate();
        job.Use(script);
    }

    /// <summary>Every line of a script shown up to twenty, and a scroll bar down the side only past that; one along the foot only when a line is wider than the box (<see cref="FitScriptBox"/>).</summary>
    private TextBox ScriptBox(string script)
    {
        int lines = script.Split('\n').Length;
        var text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = lines > 20 ? ScrollBars.Vertical : ScrollBars.None,
            BorderStyle = BorderStyle.FixedSingle,
            Font = _code,
            BackColor = ChatLook.Paper,
            Text = script.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            Height = Math.Min(20, lines) * _code.Height + LogicalToDeviceUnits(8),
            Width = LogicalToDeviceUnits(600),
            Margin = this.Scaled(0, 4, 0, 6),
            TabStop = false,
        };
        _scripts.Add(text);
        return text;
    }

    private void FitScriptBox(TextBox box, int width)
    {
        string[] lines = box.Lines;
        int widest = lines.Length == 0 ? 0 : lines.Max(l => TextRenderer.MeasureText(l, _code, Size.Empty, TextFormatFlags.NoPrefix).Width);
        bool wide = widest > width - LogicalToDeviceUnits(10) - (lines.Length > 20 ? SystemInformation.VerticalScrollBarWidth : 0);

        box.Width = width;
        ScrollBars wanted = (lines.Length > 20 ? ScrollBars.Vertical : ScrollBars.None) | (wide ? ScrollBars.Horizontal : ScrollBars.None);
        if (box.ScrollBars != wanted) box.ScrollBars = wanted;
        box.Height = Math.Min(20, lines.Length) * _code.Height + (wide ? SystemInformation.HorizontalScrollBarHeight : 0) + LogicalToDeviceUnits(8);
    }

    private void Primary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = ChatLook.Accent;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = ChatLook.AccentDown;
        button.FlatAppearance.MouseDownBackColor = ChatLook.AccentDown;
        button.Margin = this.Scaled(0, 2, 6, 4);
    }

    private async Task UseScriptAsync(string argumentsJson, string script)
    {
        (bool sequence, string? named) = AssistantPrompt.ProposalTarget(argumentsJson);

        BenchInstrument? instrument = sequence ? null : await _tools.FindInstrumentAsync(named, CancellationToken.None);
        if (!sequence && instrument is null)
        {
            Say("That script's instrument is not open any more. Copy it from the card instead.");
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
        var box = Stack(ChatLook.Paper);
        box.Controls.Add(Words(text, _small, SystemColors.GrayText));
        return box;
    }

    // Everything from here is built at run time, after the window's scaling pass, so its sizes are
    // scaled by hand (SPEC §14).

    private FlowLayoutPanel Stack(Color back) => new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top,
        BackColor = _dimming ? ChatLook.Paper : back,
        Padding = this.Scaled(2, 2, 2, 2),
        Margin = this.Scaled(0, 0, 0, 8),
    };

    /// <summary>A turn that sits to one side of the conversation and is as wide as what it says.</summary>
    private FlowLayoutPanel Column(bool right) => new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Anchor = (right ? AnchorStyles.Right : AnchorStyles.Left) | AnchorStyles.Top,
        BackColor = ChatLook.Paper,
        Padding = new Padding(0),
        Margin = this.Scaled(0, 0, 0, 8),
    };

    private Bubble Balloon(Color fill, Color? edge = null, int radius = 10) => new()
    {
        BackColor = _dimming ? ChatLook.Paper : fill,
        Edge = _dimming ? ChatLook.Rule : edge,
        Radius = radius,
        Padding = this.Scaled(10, 6, 10, 5),
        Margin = new Padding(0),
    };

    /// <summary>A card the width of the conversation.</summary>
    private Bubble CardBox(Color fill, Color edge)
    {
        Bubble box = Balloon(fill, edge, 4);
        box.Dock = DockStyle.Top;
        box.Margin = this.Scaled(0, 0, 0, 8);
        return box;
    }

    /// <summary>Words that wrap: to the conversation's width, or, in a bubble (<paramref name="wide"/> false), four fifths of it.</summary>
    private Label Words(string text, Font font, Color? color = null, bool wide = true)
    {
        var label = new Label
        {
            Text = text,
            Font = font,
            AutoSize = true,
            ForeColor = _dimming ? SystemColors.GrayText : color ?? SystemColors.ControlText,
            Margin = this.Scaled(0, 0, 0, 2),
            UseMnemonic = false,
            Tag = wide ? null : "bubble",
        };
        _wrapping.Add(label);
        return label;
    }

    private void FitLabelWidths()
    {
        int width = Math.Max(LogicalToDeviceUnits(200), _scroll.ClientSize.Width - LogicalToDeviceUnits(48));
        int across = Math.Max(LogicalToDeviceUnits(200), ClientSize.Width - LogicalToDeviceUnits(40));
        foreach (Label label in _wrapping.Where(l => !l.IsDisposed))
        {
            int most = label == _notice || label == _attachProblem ? across
                     : label == _estimate ? across - LogicalToDeviceUnits(260)
                     : label == _compactText ? across - LogicalToDeviceUnits(220)
                     : label.Tag is "bubble" ? width * 4 / 5
                     : width;
            label.MaximumSize = new Size(most, 0);
        }
        // The conversation's padding and the card's, each side.
        int inside = Math.Max(LogicalToDeviceUnits(200), _scroll.ClientSize.Width - LogicalToDeviceUnits(44));
        foreach (TextBox box in _scripts.Where(b => !b.IsDisposed)) FitScriptBox(box, inside);
    }

    private void FitPrompt()
    {
        int lines = Math.Clamp(_prompt.GetLineFromCharIndex(_prompt.TextLength) + 1, 1, PromptMaxLines);
        _prompt.Height = lines * _prompt.Font.Height + LogicalToDeviceUnits(6);
        _prompt.ScrollBars = _prompt.GetLineFromCharIndex(_prompt.TextLength) + 1 > PromptMaxLines ? ScrollBars.Vertical : ScrollBars.None;
    }
}
