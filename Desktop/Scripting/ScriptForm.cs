using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// A simple SCPI script editor / runner. Built entirely in code (no designer file):
/// it's a text editor over <see cref="ScriptRunner"/>, run against whatever instrument
/// the main window currently has connected.
/// </summary>
public sealed class ScriptForm : Form
{
    private const string SampleScript =
        "# SCPI script — one command per line.\r\n" +
        "# '#' or '//' begin a comment.  DELAY <ms> pauses.\r\n" +
        "# REPEAT <n> ... END repeats a block.  PRINT <text> logs a message.\r\n" +
        "\r\n" +
        "PRINT Identifying instrument...\r\n" +
        "*IDN?\r\n" +
        "\r\n" +
        "# Read the identity three times, half a second apart\r\n" +
        "REPEAT 3\r\n" +
        "    *IDN?\r\n" +
        "    DELAY 500\r\n" +
        "END\r\n" +
        "\r\n" +
        "PRINT Done.\r\n";

    /// <summary>
    /// Ready-made scripts offered by the "Examples" dropdown, for the instrument this
    /// editor belongs to. See <see cref="ScriptExamples"/> — they live in Core so the
    /// commands sit next to the catalogs they were transcribed from, and can be tested.
    /// </summary>
    private readonly IReadOnlyList<ScriptExample> _examplesList;


    private readonly Func<IInstrumentClient?> _getClient;

    /// <summary>Which instrument this editor drives, shown in the title bar. One editor is
    /// opened per connected instrument, so the windows have to be tellable apart.</summary>
    private readonly string? _instrument;

    /// <summary>
    /// The instrument's *IDN? reply, which is what selects a command catalog.
    ///
    /// Only the AI writer needs it: the family alone names a catalog, but the model is
    /// better served by being told the actual model number it is writing for.
    /// </summary>
    private readonly string _identity;

    private readonly ScriptEditor _editor = new();
    private readonly RichTextBox _output = new();
    private readonly ResultsPanel _results = new();

    /// <summary>The log/results splitter, so Shown can place it once the form has a width.</summary>
    private SplitContainer? _lower;

    private readonly Button _btnRun = new();
    private readonly Button _btnStop = new();
    private readonly ComboBox _examples = new();
    private readonly Label _status = new();
    private Button? _btnNew, _btnOpen, _btnSave, _btnSaveAs, _btnSaveLog, _btnClearLog, _btnAi,
                    _btnSnippets;

    private readonly ToolTip _tips = new();
    private CancellationTokenSource? _runCts;
    private string? _path;
    private bool _dirty;
    private bool _loadingExample;

    /// <summary>Raised when a run starts (true) or ends (false), so the host can lock its own console.</summary>
    public event Action<bool>? RunStateChanged;

    /// <summary>
    /// Closing the window normally just hides it, so the script survives between openings.
    /// Cleared by <see cref="AllowClose"/> when the instrument it belongs to goes away.
    /// </summary>
    private bool _hideOnUserClose = true;

    /// <summary>Let the next Close actually close the window instead of hiding it.</summary>
    public void AllowClose() => _hideOnUserClose = false;

    public ScriptForm(Func<IInstrumentClient?> getClient, string? instrument = null,
                      InstrumentFamily family = InstrumentFamily.Generic,
                      string? identity = null)
    {
        _getClient = getClient;
        _instrument = instrument;
        _identity = identity ?? "";
        _examplesList = ScriptExamples.ForFamily(family);

        // Match the main form's scaling so this window grows correctly on high-DPI
        // displays (a code-built form must set these itself — the designer normally does).
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;

        Text = "Script Editor";
        // Taller by default: the window is split editor-over-output at 62/38, so at the old
        // 640 a script of any length left the output pane about eight lines high — and the
        // output is where a run reports itself.
        //
        // Twice that again in each direction: a script is read as a whole, and the pane
        // below it carries a line per command. OnLoad clamps this to the screen it opens on.
        ClientSize = new Size(1960, 1640);
        MinimumSize = new Size(520, 400);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        KeyPreview = true;

        BuildUi();

        _editor.Text = SampleScript;
        _dirty = false;
        UpdateTitle();
    }

    private void BuildUi()
    {
        // --- toolbar ---  (auto-sized so it scales with DPI without clipping)
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,   // wrap rather than clip if the window is narrow
            Padding = new Padding(6),
        };
        Button Tool(string text, EventHandler onClick)
        {
            var b = new Button();
            ButtonStyle.Apply(b, text, onClick);
            toolbar.Controls.Add(b);
            return b;
        }
        _btnNew = Tool("New", (_, _) => NewScript());
        _btnOpen = Tool("Open…", (_, _) => OpenScript());
        _btnSave = Tool("Save", (_, _) => SaveScript());
        _btnSaveAs = Tool("Save As…", (_, _) => SaveScriptAs());

        // Examples dropdown: pick one to load it into the editor. Owner-drawn so its
        // height can be raised (a DropDownList combo is otherwise font-height-locked) to
        // match the toolbar buttons — see NormalizeToolbarHeights.
        _examples.DropDownStyle = ComboBoxStyle.DropDownList;
        _examples.DrawMode = DrawMode.OwnerDrawFixed;
        _examples.DrawItem += Examples_DrawItem;
        _examples.Width = 240;   // wide enough for the longest per-instrument example name
        _examples.Margin = new Padding(12, 2, 6, 0);
        _examples.Items.Add("Examples…");
        foreach (ScriptExample ex in _examplesList) _examples.Items.Add(ex.Name);
        _examples.SelectedIndex = 0;
        _examples.SelectedIndexChanged += OnExampleSelected;
        toolbar.Controls.Add(_examples);

        // Snippets and Script with AI sit beside the examples, because all three answer the
        // same question — where a script comes from when you do not have one yet.
        _btnSnippets = Tool("Snippets ▾", (_, _) => { });
        SnippetMenu.Attach(_btnSnippets, _editor, ScriptLanguage.ForScript);

        _btnAi = Tool("Script with AI…", (_, _) => WriteWithAi());

        // Run/Stop are labelled like every other button in the app. They used to be
        // icon-only squares with the labels in tooltips, which made them the two odd
        // buttons in the window — half the width of their neighbours (SPEC §14).
        ButtonStyle.Apply(_btnRun, "Run", (_, _) => _ = RunScriptAsync());
        _tips.SetToolTip(_btnRun, "Run the script (F5).");
        toolbar.Controls.Add(_btnRun);

        ButtonStyle.Apply(_btnStop, "Stop", (_, _) => _runCts?.Cancel());
        _btnStop.Enabled = false;
        _tips.SetToolTip(_btnStop, "Stop the running script.");
        toolbar.Controls.Add(_btnStop);

        // --- editor / output split ---
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            Panel1MinSize = 120,
            Panel2MinSize = 100,
        };

        _editor.Dock = DockStyle.Fill;
        _editor.Language = ScriptLanguage.ForScript;
        _editor.CommandSource = CatalogCommands;
        _editor.TextChanged += (_, _) => { if (!_dirty) { _dirty = true; UpdateTitle(); } };
        split.Panel1.Controls.Add(_editor);

        _output.Dock = DockStyle.Fill;
        _output.ReadOnly = true;
        _output.BackColor = Color.FromArgb(24, 24, 24);
        _output.ForeColor = Color.Gainsboro;
        _output.Font = new Font("Consolas", 9.5f);
        _output.DetectUrls = false;

        // The log's own buttons, under the log. The output pane is the record of what a run
        // actually did — every command sent, every reply, every PRINT — and the controls for
        // keeping or discarding it belong beside it rather than up in the toolbar among the
        // controls for the script.
        var logTools = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 6, 6, 0),
        };

        _btnClearLog = new Button();
        ButtonStyle.Apply(_btnClearLog, "Clear Log", (_, _) => _output.Clear());
        logTools.Controls.Add(_btnClearLog);

        _btnSaveLog = new Button();
        ButtonStyle.Apply(_btnSaveLog, "Save Log", (_, _) => SaveOutputLog());
        logTools.Controls.Add(_btnSaveLog);

        // Log on the left, recorded rows on the right — the same arrangement the
        // multi-instrument window uses, because a script moved between the two windows
        // records the same way and should look like it does.
        // The panel minimums are applied in Shown, not here: assigning them to a container
        // still at its default 150px throws out of the property setter (SplitLayout.SetMinimums).
        var lower = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
        };

        lower.Panel1.Controls.Add(_output);
        lower.Panel1.Controls.Add(logTools);

        _results.Dock = DockStyle.Fill;
        _results.Status += (_, text) => _status.Text = text;
        lower.Panel2.Controls.Add(_results);

        split.Panel2.Controls.Add(lower);
        _lower = lower;

        // --- status ---
        _status.Dock = DockStyle.Bottom;
        // Height comes from the font, once the form has passed its own down (see the
        // Load handler). A flat 22 was right at 100% and cut the descenders off at 175%:
        // these windows scale their fonts without scaling their own layout.
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(6, 0, 0, 0);
        _status.Text = "Ready.";

        // Fill first, then docked edges (this project's convention for docking order).
        Controls.Add(split);
        Controls.Add(toolbar);
        Controls.Add(_status);

        SetTooltips();

        // Set the split after the form has its size so SplitterDistance is in range,
        // and put the caret at the top instead of leaving the whole script selected.
        Shown += (_, _) =>
        {
            SplitLayout.SetFraction(split, 0.62);
            if (_lower != null)
            {
                SplitLayout.SetMinimums(_lower, 160, 160);
                SplitLayout.SetFraction(_lower, 0.5);   // even halves, as in the console
            }
            _status.Height = _status.PreferredHeight + LogicalToDeviceUnits(6);
            _editor.Select(0, 0);
            _editor.Focus();
            SetToolbarIcons();          // after the handle exists so DPI scaling is known
            _results.ApplyIcons();      // ...including the results pane's two, before measuring
            NormalizeToolbarHeights();   // then make every toolbar control one height
            _results.PinToolHeight();    // then pin: the button height is what it uses
        };

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) { e.Handled = true; _ = RunScriptAsync(); }
            else if (e.Control && e.KeyCode == Keys.S) { e.Handled = true; SaveScript(); }
        };
        FormClosing += ScriptForm_FormClosing;
    }

    /// <summary>
    /// Never open bigger than the screen it lands on. The size in the constructor is what the
    /// editor and the panes below it want; a 1366x768 laptop cannot give it, and a window
    /// taller than the desktop puts the status line and the resize grip out of reach.
    ///
    /// Before Shown, which is where the splitter fractions are set — those are taken from the
    /// height this window actually ends up with.
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        Rectangle work = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min(Math.Max(Width, MinimumSize.Width), work.Width),
                        Math.Min(Math.Max(Height, MinimumSize.Height), work.Height));
    }

    /// <summary>
    /// Write the output pane to a file — the same job, and the same shape, as the console's
    /// Save Log beside it.
    ///
    /// The RichTextBox's plain <c>Text</c> is what gets written, not its RTF: a run log is
    /// read in a text editor or pasted into a report, and the colouring carries nothing the
    /// words do not already say.
    /// </summary>
    private void SaveOutputLog()
    {
        if (_output.TextLength == 0)
        {
            MessageBox.Show(this, "There is nothing in the output pane yet.", "Save Log",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "Save script output",
            Filter = "Text file (*.txt)|*.txt|Log file (*.log)|*.log|All files (*.*)|*.*",
            FileName = SuggestedLogFileName(),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllText(dlg.FileName, _output.Text);
            _status.Text = "Output saved to " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the output:\n" + ex.Message, "Save Log",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// "script-log-DS2202-blink.txt" — the instrument and the script, because a bench
    /// session leaves several of these and they are otherwise indistinguishable.
    /// </summary>
    private string SuggestedLogFileName()
    {
        string Clean(string s)
        {
            foreach (char bad in Path.GetInvalidFileNameChars()) s = s.Replace(bad, '-');
            return s.Trim();
        }

        string model = Clean(_instrument ?? "");
        string script = _path is null ? "" : Clean(Path.GetFileNameWithoutExtension(_path));

        string name = "script-log";
        if (model.Length > 0) name += "-" + model;
        if (script.Length > 0) name += "-" + script;
        return name + ".txt";
    }

    /// <summary>
    /// Describe every control in a hover tooltip. Anything added to this window should get
    /// an entry here (Run/Stop are tipped where they are built, since their labels are icons).
    /// </summary>
    private void SetTooltips()
    {
        _tips.AutoPopDelay = 15000;   // these are sentences; give them time to be read

        if (_btnNew != null) _tips.SetToolTip(_btnNew, "Start a new, empty script.");
        if (_btnOpen != null) _tips.SetToolTip(_btnOpen, "Open a script file from disk.");
        if (_btnSave != null) _tips.SetToolTip(_btnSave, "Save the script (Ctrl+S).");
        if (_btnSaveAs != null) _tips.SetToolTip(_btnSaveAs, "Save the script under a new name.");
        if (_btnSaveLog != null) _tips.SetToolTip(_btnSaveLog, "Save the output pane to a text file — "
                                                            + "every command sent and every reply, as the run produced them.");
        if (_btnClearLog != null) _tips.SetToolTip(_btnClearLog, "Empty the output pane. The script itself is untouched.");
        if (_btnSnippets != null) _tips.SetToolTip(_btnSnippets,
            "Every word the language has, and what each is for. Click one and it is "
          + "written into the editor with its blanks selected — Tab moves to the next.\r\n\r\n"
          + "Typing the short name and pressing Tab does the same thing, and Ctrl+Space "
          + "offers whatever fits where the caret is.");
        if (_btnAi != null) _tips.SetToolTip(_btnAi, "Describe what you want and have it written "
                                                  + "for you, from this instrument's own command "
                                                  + "catalog. The draft is shown for review — "
                                                  + "nothing is run.");

        _tips.SetToolTip(_examples, "Load a ready-made example script. This replaces the "
                                  + "current script, so save your work first.");
        _tips.SetToolTip(_editor, "One SCPI command per line. '#' or '//' start a comment, "
                                + "DELAY <ms> pauses, REPEAT <n> … END repeats a block, and "
                                + "PRINT <text> writes to the output below.");
        _tips.SetToolTip(_output, "Output from the running script: commands sent, replies "
                                + "received, and any errors.");
        _tips.SetToolTip(_status, "Whether a script is running, and how it finished.");
    }

    /// <summary>Give the toolbar buttons their glyphs (called once the DPI is known).</summary>
    private void SetToolbarIcons()
    {
        void Ico(Button? b, string name)
        {
            if (b != null) ButtonStyle.SetIcon(this, b, name);
        }
        void Drawn(Button? b, string name)
        {
            if (b != null) ButtonStyle.SetDrawnIcon(this, b, name);
        }
        Ico(_btnNew, "new");
        Ico(_btnOpen, "openFile");
        Drawn(_btnSave, "save");
        Drawn(_btnSaveAs, "save");
        Ico(_btnRun, "startClock");
        Ico(_btnStop, "stopClock");
        Drawn(_btnSaveLog, "save");
        Ico(_btnClearLog, "reset");
        Drawn(_btnAi, "ai");
        Ico(_btnSnippets, "new");
    }

    /// <summary>This instrument's documented commands, for the completion popup.</summary>
    private IEnumerable<string> CatalogCommands()
        => CommandReference.ForIdentity(_identity)?.Commands.Select(c => c.Syntax)
           ?? Enumerable.Empty<string>();

    /// <summary>
    /// Hand the request, this instrument's catalog, the current script and the run log to the
    /// user's AI connection, and put what comes back in the editor — after they have read it.
    ///
    /// The output pane goes across on purpose. "It failed with -113" is only answerable by
    /// something that can see the failure, and that log is the only place it exists.
    /// </summary>
    private void WriteWithAi()
    {
        var instrument = new ScriptContextInstrument(
            Alias: "",                       // one instrument, so lines carry no prefix
            Model: InstrumentProfile.ParseIdentity(_identity).Model is { Length: > 0 } m
                   ? m : (_instrument ?? "the connected instrument"),
            Identity: _identity,
            Reference: CommandReference.ForIdentity(_identity));

        string? written = ScriptAiForm.Ask(
            this, new[] { instrument }, isSequence: false, _editor.Text, _output.Text);
        if (written == null) return;

        // Replacing the editor outright is a change worth being able to undo, and a TextBox's
        // own undo does not survive a programmatic assignment.
        if (_editor.TextLength > 0
            && MessageBox.Show(this,
                "Replace the script in the editor with the one that was written?\n\n"
              + "Save it first if you want to keep it.",
                "Script with AI", MessageBoxButtons.OKCancel, MessageBoxIcon.Question)
               != DialogResult.OK)
            return;

        _editor.Text = written;
        _editor.Select(0, 0);
        _editor.Focus();
        _status.Text = "Script written by AI — read it before running it.";
    }

    /// <summary>
    /// Make every toolbar control one common height, tall enough to show the glyphs fully,
    /// with aligned tops. The owner-drawn ComboBox is grown via ItemHeight; the AutoSize
    /// buttons keep their auto width but are pinned to the ComboBox's resulting height via
    /// Min/MaxSize. Margins are squared up so the tops line up in the flow row.
    /// </summary>
    private void NormalizeToolbarHeights()
    {
        // The app-wide button height (SPEC §14) leads here, and the combo is grown to match
        // it. It used to be the other way round — the combo's height was the target — which
        // made this window's buttons a different size from every other window's.
        int h = ButtonStyle.Normalize(this, _btnNew, _btnOpen, _btnSave, _btnSaveAs,
                                            _btnRun, _btnStop, _btnAi, _btnSnippets);

        // The log and results buttons live under their own panes, not in this row, but they
        // still have to be the same height as every other button in the app (SPEC §14).
        ButtonStyle.Normalize(this, _btnSaveLog, _btnClearLog);
        ButtonStyle.Normalize(this, _results.Buttons);

        ButtonStyle.MatchHeight(_examples, h);

        // Group gaps, the same idea as the multi-instrument window: the file buttons, then
        // the three ways a script arrives, then running it. A gap on the first control of
        // each group is what makes them read as groups. The log buttons are not here — they
        // live under the log.
        int gap = LogicalToDeviceUnits(24);
        _examples.Margin = new Padding(gap, 0, 6, 0);
        if (_btnRun != null)
            _btnRun.Margin = new Padding(gap, _btnRun.Margin.Top,
                                         _btnRun.Margin.Right, _btnRun.Margin.Bottom);

        // Anything else in the row that is shorter than a button sits on the same centre line.
        foreach (Control c in _examples.Parent!.Controls)
            if (c is not Button && c != _examples) ButtonStyle.CentreInRow(c, h);
    }

    private void Examples_DrawItem(object? sender, DrawItemEventArgs e)
        => ButtonStyle.DrawComboItem(_examples, e);

    // ------------------------------------------------------------------- run

    private async Task RunScriptAsync()
    {
        if (_runCts != null) return;   // already running

        IInstrumentClient? client = _getClient();
        if (client is not { IsConnected: true })
        {
            MessageBox.Show(this, "This instrument isn't connected. Reconnect to it in the "
                + "main window, then run the script again.",
                "Run Script", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _runCts = new CancellationTokenSource();
        SetRunning(true);
        Append("--- run started ---", ScriptOutputKind.Info);

        // Headings before the first row, so the table is not renamed halfway through a run.
        _results.SetColumns(ScriptRunner.Columns(_editor.Text));

        try
        {
            await ScriptRunner.RunAsync(
                _editor.Text, client,
                (text, kind) => OnUi(() => Append(text, kind)),
                row => OnUi(() => _results.AddRow(row)),
                _runCts.Token);
            Append("--- run complete ---", ScriptOutputKind.Info);
            _status.Text = "Run complete.";
        }
        catch (OperationCanceledException)
        {
            Append("--- run stopped ---", ScriptOutputKind.Info);
            _status.Text = "Stopped.";
        }
        catch (Exception ex)
        {
            Append("--- run failed: " + ex.Message + " ---", ScriptOutputKind.Error);
            _status.Text = "Run failed.";
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
            SetRunning(false);
        }
    }

    private void SetRunning(bool running)
    {
        _btnRun.Enabled = !running;
        _btnStop.Enabled = running;
        if (running) _status.Text = "Running…";
        RunStateChanged?.Invoke(running);
    }

    /// <summary>
    /// Do this on the UI thread, wherever the caller happens to be.
    ///
    /// <see cref="ScriptRunner"/> is UI-free and awaits with <c>ConfigureAwait(false)</c>, as
    /// Core should, so once a round-trip actually yields its callbacks arrive on a thread-pool
    /// thread — and a WinForms control touched from there throws. Marshalling belongs on this
    /// side of the line. The same fix, for the same reason, is in <see cref="SequenceForm"/>,
    /// where it was found.
    /// </summary>
    private void OnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) Invoke(action);
        else action();
    }

    private void Append(string text, ScriptOutputKind kind)
    {
        Color color = kind switch
        {
            ScriptOutputKind.Command => Color.Silver,
            ScriptOutputKind.Response => Color.MediumSpringGreen,
            ScriptOutputKind.Error => Color.Tomato,
            _ => Color.Gray,
        };
        _output.SelectionStart = _output.TextLength;
        _output.SelectionLength = 0;
        _output.SelectionColor = color;
        _output.AppendText(text + Environment.NewLine);
        _output.SelectionColor = _output.ForeColor;
        _output.ScrollToCaret();
    }

    // ------------------------------------------------------------- file i/o

    private void NewScript()
    {
        if (!ConfirmDiscardIfDirty()) return;
        _editor.Text = "";
        _path = null;
        _dirty = false;
        UpdateTitle();
    }

    private void OnExampleSelected(object? sender, EventArgs e)
    {
        if (_loadingExample) return;          // re-entrancy guard for the reset below
        int i = _examples.SelectedIndex;
        if (i <= 0) return;                   // the "Examples…" prompt

        var (name, script) = _examplesList[i - 1];
        if (ConfirmDiscardIfDirty())
        {
            _editor.Text = script;
            _editor.Select(0, 0);
            _path = null;                     // loaded example is a fresh, untitled script
            _dirty = false;
            UpdateTitle();
            _status.Text = "Loaded example: " + name;
        }

        // Reset to the prompt so the same example can be picked again.
        _loadingExample = true;
        _examples.SelectedIndex = 0;
        _loadingExample = false;
    }

    private void OpenScript()
    {
        if (!ConfirmDiscardIfDirty()) return;
        using var dlg = new OpenFileDialog { Filter = ScriptFilter, Title = "Open Script" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _editor.Text = File.ReadAllText(dlg.FileName);
            _path = dlg.FileName;
            _dirty = false;
            UpdateTitle();
            _status.Text = "Opened " + Path.GetFileName(_path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open file:\n" + ex.Message, "Open Script",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private bool SaveScript()
    {
        if (_path == null) return SaveScriptAs();
        return WriteTo(_path);
    }

    private bool SaveScriptAs()
    {
        using var dlg = new SaveFileDialog { Filter = ScriptFilter, Title = "Save Script", DefaultExt = "scpi" };
        if (_path != null) dlg.FileName = Path.GetFileName(_path);
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        return WriteTo(dlg.FileName);
    }

    private bool WriteTo(string path)
    {
        try
        {
            File.WriteAllText(path, _editor.Text);
            _path = path;
            _dirty = false;
            UpdateTitle();
            _status.Text = "Saved " + Path.GetFileName(path);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save file:\n" + ex.Message, "Save Script",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private const string ScriptFilter = "SCPI script (*.scpi;*.txt)|*.scpi;*.txt|All files (*.*)|*.*";

    private bool ConfirmDiscardIfDirty()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show(this, "Save changes to the current script?", "Script Editor",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return r switch
        {
            DialogResult.Yes => SaveScript(),
            DialogResult.No => true,
            _ => false,
        };
    }

    private void UpdateTitle()
    {
        string name = _path == null ? "Untitled" : Path.GetFileName(_path);
        string who = string.IsNullOrWhiteSpace(_instrument) ? "" : _instrument + " — ";
        Text = $"Script Editor — {who}{name}{(_dirty ? " *" : "")}";
    }

    private void ScriptForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _runCts?.Cancel();
        if (!ConfirmDiscardIfDirty()) { e.Cancel = true; return; }
        // Hide instead of disposing, so the editor's contents survive between openings.
        if (e.CloseReason == CloseReason.UserClosing && _hideOnUserClose)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
