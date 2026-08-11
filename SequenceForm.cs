using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// Editor and runner for a script that drives several instruments at once. The window is
/// called <b>Multi-Instrument Scripts</b> in the UI; the type, the runner and the <c>.seq</c>
/// files keep the older "sequence" name, which is what the language itself still calls one.
///
/// Unlike <see cref="ScriptForm"/>, which belongs to one console, this window belongs to
/// the bench: it is opened from Tools and addresses whatever is connected by name. The
/// motivating job is a swept measurement — a generator stepping frequency while a meter
/// reads at each step — which no per-instrument script can express, because the two
/// instruments have to alternate inside one loop.
/// </summary>
public sealed class SequenceForm : Form
{
    private readonly SessionRegistry _sessions;

    private readonly ScriptEditor _editor = new();
    private readonly RichTextBox _output = new();
    private readonly ResultsPanel _results = new();
    private readonly Label _devices = new();
    private readonly Label _status = new();

    // Held so its height can be pinned once the buttons have been normalised — see the
    // Shown handler. The results pane looks after its own.
    private FlowLayoutPanel? _logTools;

    private readonly Button _btnRun = new();
    private readonly Button _btnStop = new();
    private readonly Button _btnOpen = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnAi = new();
    private readonly Button _btnSnippets = new();
    private readonly Button _btnSaveLog = new();
    private readonly Button _btnClearLog = new();
    private readonly ComboBox _examples = new();

    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    private readonly System.Windows.Forms.Timer _deviceWatch = new() { Interval = 1000 };

    private CancellationTokenSource? _runCts;
    private string? _path;
    private bool _loadingExample;

    public SequenceForm(SessionRegistry sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9f);
        Text = "Multi-Instrument Scripts";
        // Four panes share this window — script, device bindings, output and results — and
        // at the old 1120×840 the script was about eight lines and the results table four
        // rows. It is clamped to the screen in OnLoad, so a large default costs nothing on
        // a small display.
        ClientSize = new Size(1990, 1400);
        MinimumSize = new Size(700, 520);
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;

        BuildUi();

        _editor.Text = SequenceExamples.All[0].Script;
        UpdateDevices();

        // The bench changes while this window is open — instruments get connected and
        // disconnected from the main window — so the binding strip is re-checked rather
        // than read once. A second is far below noticing and far above busy-waiting.
        _deviceWatch.Tick += (_, _) => UpdateDevices();
        _deviceWatch.Start();
    }

    // --------------------------------------------------------------------------- ui

    private void BuildUi()
    {
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(6),
        };
        Button Tool(Button b, string text, EventHandler onClick)
        {
            ButtonStyle.Apply(b, text, onClick);
            toolbar.Controls.Add(b);
            return b;
        }

        // The toolbar reads as three groups, and the gaps are what say so: the file pair,
        // then the three ways a script comes into being, then running it. Save CSV and Clear
        // Results are not here at all — they belong to the results table, and they sit under
        // it (SPEC §6: a control goes with the thing it acts on).
        Tool(_btnOpen, "Open…", (_, _) => OpenSequence());
        Tool(_btnSave, "Save", (_, _) => SaveSequence());

        _examples.DropDownStyle = ComboBoxStyle.DropDownList;
        _examples.DrawMode = DrawMode.OwnerDrawFixed;
        _examples.DrawItem += (_, e) => ButtonStyle.DrawComboItem(_examples, e);
        _examples.Width = 260;
        _examples.Margin = new Padding(12, 2, 6, 0);
        _examples.Items.Add("Examples…");
        foreach (SequenceExample ex in SequenceExamples.All) _examples.Items.Add(ex.Name);
        _examples.SelectedIndex = 0;
        _examples.SelectedIndexChanged += OnExampleSelected;
        toolbar.Controls.Add(_examples);

        // Beside the examples, because all three answer the same question — where a script
        // comes from when you do not have one yet.
        Tool(_btnSnippets, "Snippets ▾", (_, _) => { });
        SnippetMenu.Attach(_btnSnippets, _editor, ScriptLanguage.ForSequence);

        Tool(_btnAi, "Script with AI…", (_, _) => WriteWithAi());

        Tool(_btnRun, "Run", (_, _) => _ = RunAsync());
        Tool(_btnStop, "Stop", (_, _) => _runCts?.Cancel());
        _btnStop.Enabled = false;

        // --- the binding strip: which instrument each alias resolved to, right now ---
        _devices.Dock = DockStyle.Top;
        _devices.AutoSize = false;
        _devices.Padding = new Padding(8, 4, 8, 6);
        _devices.UseMnemonic = false;   // model names contain '&' on some makers

        // --- editor over (output | results) ---
        var outer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
        };

        _editor.Dock = DockStyle.Fill;
        _editor.Language = ScriptLanguage.ForSequence;
        _editor.CommandSource = CatalogCommands;
        _editor.TextChanged += (_, _) => { if (!_loadingExample) UpdateDevices(); };
        outer.Panel1.Controls.Add(_editor);

        var lower = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
        };

        _output.Dock = DockStyle.Fill;
        _output.ReadOnly = true;
        _output.BackColor = Color.FromArgb(24, 24, 24);
        _output.ForeColor = Color.Gainsboro;
        _output.Font = new Font("Consolas", 9.5f);
        _output.DetectUrls = false;

        // --- log buttons, under the log they act on ---
        //
        // A pair each: these two for the run log, Save CSV and Clear Results for the table
        // beside it. They used to be one pair doing both jobs — Clear Results wiped the log
        // as well — which meant tidying the table threw away the record of how it was filled.
        _logTools = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0),
        };

        ButtonStyle.Apply(_btnClearLog, "Clear Log", (_, _) => _output.Clear());
        _logTools.Controls.Add(_btnClearLog);

        ButtonStyle.Apply(_btnSaveLog, "Save Log", (_, _) => SaveOutputLog());
        _logTools.Controls.Add(_btnSaveLog);

        lower.Panel1.Controls.Add(_output);
        lower.Panel1.Controls.Add(_logTools);
        // No bottom padding under either pane. The button rows below them already carry
        // their own top padding, and the status bar sits directly under that — six here
        // and six there stacked into a band of dead space between the buttons and "Ready.".
        lower.Panel1.Padding = new Padding(0, 0, 0, 0);

        // The table, the plot, their buttons and the CSV writer are one control shared with
        // the console and the single-instrument script window — see ResultsPanel. This window
        // had its own copy of all of it, which is two CSV writers and two chances for the
        // columns and the curve to disagree.
        _results.Dock = DockStyle.Fill;
        _results.Status += (_, text) => _status.Text = text;

        lower.Panel2.Controls.Add(_results);
        lower.Panel2.Padding = new Padding(0, 0, 0, 0);

        outer.Panel2.Controls.Add(lower);

        _status.Dock = DockStyle.Bottom;
        // Height comes from the font, once the form has passed its own down (see the
        // Load handler). A flat 22 was right at 100% and cut the descenders off at 175%:
        // these windows scale their fonts without scaling their own layout.
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(6, 0, 0, 0);
        _status.Text = "Ready.";

        Controls.Add(outer);
        Controls.Add(_devices);
        Controls.Add(toolbar);
        Controls.Add(_status);

        SetTooltips();

        Load += (_, _) =>
        {
            // Fit the screen before the splitters are measured against it. Not Math.Clamp:
            // it throws when the minimum exceeds the maximum, which is what a screen smaller
            // than MinimumSize gives.
            Size wa = Screen.GetWorkingArea(this).Size;
            Size = new Size(Math.Min(Math.Max(Width, MinimumSize.Width), wa.Width),
                            Math.Min(Math.Max(Height, MinimumSize.Height), wa.Height));
        };

        Shown += (_, _) =>
        {
            _status.Height = _status.PreferredHeight;
            SplitLayout.SetFraction(outer, 0.55);
            SplitLayout.SetFraction(lower, 0.5);    // log and results even, as in the console
            _devices.Height = LogicalToDeviceUnits(46);
            SetToolbarIcons();
            NormalizeToolbar();
            PinToolRowHeights();     // after Normalize: the button height is what they are sized to
            _editor.Select(0, 0);
            _editor.Focus();
        };

        void PinToolRowHeights()
        {
            // AutoSize gave each row its buttons plus their margins, leaving about eight dead
            // pixels under the buttons — and that band, on top of the status label's own
            // centring above its text, read as a stripe of empty window between the buttons
            // and "Ready.". Size the rows to the buttons and their top padding, nothing else.
            _results.PinToolHeight();
            foreach (FlowLayoutPanel? row in new[] { _logTools })
            {
                if (row == null || row.Controls.Count == 0) continue;

                int tallest = 0;
                foreach (Control c in row.Controls) tallest = Math.Max(tallest, c.Height);

                row.AutoSize = false;
                row.Height = tallest + row.Padding.Top;
            }
        }

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) { e.Handled = true; _ = RunAsync(); }
            else if (e.Control && e.KeyCode == Keys.S) { e.Handled = true; SaveSequence(); }
        };

        // Esc closes it, like every other window in the app. It goes through Close() rather
        // than round it, so the running-script guard below still gets its say — and the
        // editor sees the key first, so Esc dismisses a completion list before it reaches
        // here rather than closing the window out from under someone mid-word.
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && !_editor.CompletionVisible)
            {
                e.Handled = true;
                Close();
            }
        };

        FormClosing += (_, e) =>
        {
            if (_runCts != null)
            {
                // A half-finished sweep can leave a generator driving the circuit. Stopping
                // is the caller's decision, not something to do behind their back.
                if (MessageBox.Show(this, "A script is still running. Stop it and close?",
                        "Multi-Instrument Scripts", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                    != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _runCts.Cancel();
            }
            _deviceWatch.Stop();
        };
    }

    private void SetToolbarIcons()
    {
        void Ico(Button b, string name) => ButtonStyle.SetIcon(this, b, name);
        void Drawn(Button b, string name) => ButtonStyle.SetDrawnIcon(this, b, name);

        Ico(_btnOpen, "openFile");
        Drawn(_btnSave, "save");
        Ico(_btnRun, "startClock");
        Ico(_btnStop, "stopClock");
        _results.ApplyIcons();      // the results pane's two carry the same glyphs
        Drawn(_btnAi, "ai");
        Ico(_btnSnippets, "new");

        // Not on the toolbar, but they are buttons in this window and carry the same glyphs
        // their counterparts do elsewhere.
        Drawn(_btnSaveLog, "save");
        Ico(_btnClearLog, "reset");
    }

    private void NormalizeToolbar()
    {
        int h = ButtonStyle.Normalize(this, _btnOpen, _btnSave, _btnRun, _btnStop,
                                            _btnAi, _btnSnippets,
                                            _btnSaveLog, _btnClearLog);
        ButtonStyle.Normalize(this, _results.Buttons);
        ButtonStyle.MatchHeight(_examples, h);

        // A gap on the first control of a group is what makes three groups look like three
        // groups: file, then where a script comes from, then running it. Set here rather than
        // where the buttons are built so the whole row's spacing reads in one place.
        int gap = LogicalToDeviceUnits(24);
        _examples.Margin = new Padding(gap, 0, 6, 0);          // …then how a script arrives
        _btnRun.Margin = new Padding(gap, _btnRun.Margin.Top,   // …then running it
                                     _btnRun.Margin.Right, _btnRun.Margin.Bottom);
    }

    private void SetTooltips()
    {
        _tips.SetToolTip(_editor,
            "One script, several instruments at once.\r\n\r\n"
          + "DEVICE gen : SDG2042X    name an instrument by its model\r\n"
          + "gen: C1:OUTP ON          send a line to that one\r\n"
          + "WITH gen … END           ...or a whole block\r\n"
          + "FOR f = 100 TO 100k POINTS 40 LOG … END\r\n"
          + "dmm: MEAS:VOLT:AC? -> v  capture a reply as $v\r\n"
          + "RECORD $f, $v            append a row of results\r\n"
          + "COLUMNS Frequency, Vout  name the result columns\r\n"
          + "DELAY ms, PRINT text, REPEAT n … END");
        _tips.SetToolTip(_devices,
            "Which connected instrument each DEVICE line resolves to. Updated as instruments "
          + "are connected and disconnected in the main window.");
        _tips.SetToolTip(_btnRun, "Run the script (F5). Every instrument it uses is locked "
                                + "out of its own console while it runs.");
        _tips.SetToolTip(_btnStop, "Stop the running script. Results collected so far are kept.");
        _tips.SetToolTip(_btnOpen, "Open a multi-instrument script.");
        _tips.SetToolTip(_btnSave, "Save the script (Ctrl+S).");
        _tips.SetToolTip(_btnSaveLog, "Save the run log to a text file — every command sent and "
                                    + "every reply, as the run produced them.");
        _tips.SetToolTip(_btnClearLog, "Empty the log. The results table is left alone.");
        _tips.SetToolTip(_btnSnippets,
            "Every word the language has, and what each is for. Click one and it is "
          + "written into the editor with its blanks selected — Tab moves to the next.\r\n\r\n"
          + "Typing the short name and pressing Tab does the same thing, and Ctrl+Space "
          + "offers whatever fits where the caret is.");
        _tips.SetToolTip(_btnAi, "Describe the measurement you want and have the script "
                               + "written for you, from the command catalogs of the "
                               + "instruments now connected. The draft is shown for review — "
                               + "nothing is run.");
        _tips.SetToolTip(_examples, "Load a ready-made script. This replaces the editor's "
                                  + "contents, so save your work first.");
        _tips.SetToolTip(_results, "One row per RECORD. Save it as CSV to plot elsewhere.");
        _tips.SetToolTip(_output, "What the script sent, what came back, and any errors.");
        _tips.SetToolTip(_status, "Whether a script is running, and how it finished.");
    }

    // ------------------------------------------------------------------- device strip

    /// <summary>
    /// Re-read the DEVICE lines and say what each one currently resolves to.
    ///
    /// Shown before the sequence runs rather than discovered during it: a sweep that dies
    /// three lines in has already changed the instrument's state, and the point of naming
    /// instruments up front is to fail before anything has been sent.
    /// </summary>
    private void UpdateDevices()
    {
        IReadOnlyList<(string Alias, string Model)> needs = SequenceRunner.Requirements(_editor.Text);

        if (needs.Count == 0)
        {
            _devices.Text = "No instruments declared — start with  DEVICE gen : SDG2042X";
            _devices.ForeColor = SystemColors.GrayText;

            // Run stays available. A script with no DEVICE line can still be worth running —
            // PRINT, DELAY and RECORD need no instrument — and if it does carry a command the
            // runner refuses that line by name, which says more than a greyed-out button.
            // Returning without touching Run left it stuck in whatever state the *previous*
            // script had put it, which was a lie about this one.
            _btnRun.Enabled = _runCts == null;
            return;
        }

        var parts = new List<string>();
        bool allFound = true;
        foreach ((string alias, string model) in needs)
        {
            InstrumentSession? s = _sessions.FindForSequence(model);
            if (s == null) { allFound = false; parts.Add($"{alias} → {model}  (not connected)"); }
            else parts.Add($"{alias} → {model}  @ {s.Host}");
        }

        _devices.Text = string.Join("     ", parts);
        _devices.ForeColor = allFound ? SystemColors.ControlText : Color.Firebrick;
        _btnRun.Enabled = allFound && _runCts == null;
    }

    /// <summary>
    /// Every command the connected instruments accept, for the completion popup.
    ///
    /// The union across the bench rather than per-alias: knowing which instrument a
    /// half-typed line is addressed to means parsing a line that is, by definition,
    /// unfinished. Offering a scope command while typing a generator line is a smaller
    /// wrong than offering nothing — and the catalog check on a written script (SPEC §11c)
    /// is where a command sent to the wrong instrument actually gets caught.
    /// </summary>
    private IEnumerable<string> CatalogCommands()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (InstrumentSession s in _sessions.Sessions)
        {
            if (!s.IsConnected) continue;
            CommandReference? reference = CommandReference.ForIdentity(s.Identity);
            if (reference == null) continue;

            foreach (CommandRef c in reference.Commands)
                if (seen.Add(c.Syntax)) yield return c.Syntax;
        }
    }

    // ----------------------------------------------------------------------------- ai

    /// <summary>
    /// Write a sequence from a description, against everything currently on the bench.
    ///
    /// A sequence names its instruments, so the model has to be told what is available and
    /// what each one answers to. That comes from the connected sessions rather than from the
    /// script: on an empty editor there are no DEVICE lines yet, and writing them is most of
    /// what is being asked for.
    /// </summary>
    private void WriteWithAi()
    {
        IReadOnlyList<ScriptContextInstrument> bench = BenchForAi();

        string? written = ScriptAiForm.Ask(
            this, bench, isSequence: true, _editor.Text, _output.Text);
        if (written == null) return;

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
        UpdateDevices();
        _status.Text = "Script written by AI — read it before running it.";
    }

    /// <summary>
    /// Every connected instrument, with the alias a sequence should call it by.
    ///
    /// An alias already in the editor wins, so asking for a change to a working sequence does
    /// not silently rename its devices. Otherwise one is made from the instrument's kind —
    /// "gen", "dmm", "scope" — which is what someone would have typed anyway, made unique
    /// when the bench holds two of a kind.
    /// </summary>
    private IReadOnlyList<ScriptContextInstrument> BenchForAi()
    {
        // model → alias, from whatever the editor already declares
        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string alias, string model) in SequenceRunner.Requirements(_editor.Text))
            declared.TryAdd(model, alias);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bench = new List<ScriptContextInstrument>();

        foreach (InstrumentSession s in _sessions.Sessions)
        {
            if (!s.IsConnected) continue;

            (_, string model) = InstrumentProfile.ParseIdentity(s.Identity);
            if (model.Length == 0) model = s.Host;

            string alias = declared.FirstOrDefault(
                d => model.StartsWith(d.Key, StringComparison.OrdinalIgnoreCase)).Value
                ?? AliasFor(InstrumentProfile.FamilyForIdentity(s.Identity));

            // Two scopes on the bench would otherwise both be "scope", and the second
            // DEVICE line would quietly overwrite the first.
            string unique = alias;
            for (int n = 2; !used.Add(unique); n++) unique = alias + n;

            bench.Add(new ScriptContextInstrument(
                unique, model, s.Identity, CommandReference.ForIdentity(s.Identity)));
        }

        return bench;
    }

    /// <summary>The short name an engineer would give this kind of instrument.</summary>
    private static string AliasFor(InstrumentFamily family) => family switch
    {
        InstrumentFamily.SiglentGenerator or InstrumentFamily.ScpiGenerator => "gen",

        InstrumentFamily.Multimeter or InstrumentFamily.RigolMultimeter
            or InstrumentFamily.KeysightMultimeter or InstrumentFamily.KeithleyDmm
            or InstrumentFamily.FlukeMultimeter => "dmm",

        InstrumentFamily.PowerSupply or InstrumentFamily.KeysightPowerSupply
            or InstrumentFamily.RohdePowerSupply or InstrumentFamily.ChromaPowerSupply
            or InstrumentFamily.BkPowerSupply or InstrumentFamily.BkPowerSupply9130 => "psu",

        InstrumentFamily.ElectronicLoad or InstrumentFamily.BkElectronicLoad
            or InstrumentFamily.ChromaElectronicLoad or InstrumentFamily.ChromaModularLoad
            or InstrumentFamily.RigolElectronicLoad => "load",

        InstrumentFamily.SpectrumAnalyzer or InstrumentFamily.RigolSpectrumAnalyzer
            or InstrumentFamily.RohdeSpectrumAnalyzer or InstrumentFamily.RohdeFslAnalyzer
            or InstrumentFamily.RohdeFsvAnalyzer or InstrumentFamily.RohdeFswAnalyzer
            or InstrumentFamily.RohdeFsuAnalyzer or InstrumentFamily.RohdeFspAnalyzer
            or InstrumentFamily.RohdeFsqAnalyzer => "sa",

        InstrumentFamily.KeithleySmu => "smu",

        InstrumentFamily.Oscilloscope or InstrumentFamily.SiglentScope
            or InstrumentFamily.TektronixScope or InstrumentFamily.KeysightScope
            or InstrumentFamily.RohdeScope or InstrumentFamily.GwInstekScope
            or InstrumentFamily.GwInstekScopeB => "scope",

        _ => "inst",
    };

    // ------------------------------------------------------------------------ running

    private async Task RunAsync()
    {
        if (_runCts != null) return;

        var used = new List<InstrumentSession>();
        foreach ((_, string model) in SequenceRunner.Requirements(_editor.Text))
        {
            InstrumentSession? s = _sessions.FindForSequence(model);
            if (s != null && !used.Contains(s)) used.Add(s);
        }

        _runCts = new CancellationTokenSource();
        SetRunning(true, used);
        Append("--- script started ---", ScriptOutputKind.Info);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        int before = _results.RowCount;

        try
        {
            // Headings before the first row, so the table is not renamed mid-run.
            _results.SetColumns(SequenceRunner.Columns(_editor.Text));
            _results.FileStem = _path is null ? "sequence" : Path.GetFileNameWithoutExtension(_path);

            await SequenceRunner.RunAsync(
                _editor.Text,
                model => _sessions.FindForSequence(model)?.Client,
                (text, kind) => OnUi(() => Append(text, kind)),
                row => OnUi(() => _results.AddRow(row)),
                _runCts.Token);

            _status.Text = $"Finished in {clock.Elapsed.TotalSeconds:0.0} s — "
                         + $"{_results.RowCount - before} row(s) recorded.";
            Append("--- script finished ---", ScriptOutputKind.Info);
        }
        catch (OperationCanceledException)
        {
            _status.Text = $"Stopped after {clock.Elapsed.TotalSeconds:0.0} s — "
                         + $"{_results.RowCount - before} row(s) kept.";
            Append("--- stopped ---", ScriptOutputKind.Error);
        }
        catch (Exception ex)
        {
            _status.Text = "Script failed: " + ex.Message;
            Append($"{ex.GetType().Name}: {ex.Message}", ScriptOutputKind.Error);
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
            SetRunning(false, used);
        }
    }

    /// <summary>
    /// Lock every instrument the sequence touches out of its own console while it runs, and
    /// give them all back afterwards — two conversations on one connection collide, which
    /// is the same rule a single-instrument script already follows.
    /// </summary>
    private void SetRunning(bool running, List<InstrumentSession> used)
    {
        foreach (InstrumentSession s in used) s.IsBusy = running;

        _btnRun.Enabled = !running;
        _btnStop.Enabled = running;
        _editor.ReadOnly = running;
        _examples.Enabled = !running;
        if (running) _status.Text = "Running…";
        else UpdateDevices();
    }

    /// <summary>
    /// Do this on the UI thread, wherever the caller happens to be.
    ///
    /// <see cref="SequenceRunner"/> is UI-free and awaits with <c>ConfigureAwait(false)</c>,
    /// as Core should. So the moment a round-trip actually yields, its callbacks arrive on a
    /// thread-pool thread — and a WinForms control touched from there throws. Marshalling
    /// belongs on this side of the line, not in Core.
    ///
    /// This hid for a whole feature's worth of testing: a loopback stand-in answers without
    /// ever yielding, so the continuations stayed on the UI thread and everything worked. A
    /// real instrument on real Ethernet yields every time, and the run died on line two.
    ///
    /// <c>Invoke</c>, not <c>BeginInvoke</c>: the run log has to stay in step with the run,
    /// and the UI thread is pumping messages while it awaits, so there is nothing to deadlock
    /// against.
    /// </summary>
    private void OnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) Invoke(action);
        else action();
    }

    private void Append(string text, ScriptOutputKind kind)
    {
        Color colour = kind switch
        {
            ScriptOutputKind.Command => Color.FromArgb(120, 200, 255),
            ScriptOutputKind.Response => Color.FromArgb(0, 220, 160),
            ScriptOutputKind.Error => Color.Tomato,
            _ => Color.Gray,
        };

        _output.SelectionStart = _output.TextLength;
        _output.SelectionLength = 0;
        _output.SelectionColor = colour;
        _output.AppendText(text + "\r\n");
        _output.SelectionColor = _output.ForeColor;
        _output.ScrollToCaret();
    }

    // ------------------------------------------------------------------------ results

    /// <summary>
    /// Write the run log to a file — every command sent, every reply, every PRINT, as the run
    /// produced them. The same job the console and the script editor already do.
    /// </summary>
    private void SaveOutputLog()
    {
        if (_output.TextLength == 0)
        {
            MessageBox.Show(this, "There is nothing in the log yet.", "Save Log",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "Save run log",
            Filter = "Text file (*.txt)|*.txt|Log file (*.log)|*.log|All files (*.*)|*.*",
            FileName = (_path is null ? "script" : Path.GetFileNameWithoutExtension(_path)) + "-log.txt",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            // The plain text, not the RTF: a log is read in a text editor or pasted into a
            // report, and the colouring carries nothing the words do not already say.
            File.WriteAllText(dlg.FileName, _output.Text);
            _status.Text = "Log saved to " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the log:\n" + ex.Message, "Save Log",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }


    // -------------------------------------------------------------------- file / examples

    private void OnExampleSelected(object? sender, EventArgs e)
    {
        if (_examples.SelectedIndex <= 0) return;

        SequenceExample ex = SequenceExamples.All[_examples.SelectedIndex - 1];
        _loadingExample = true;
        _editor.Text = ex.Script;
        _loadingExample = false;
        _examples.SelectedIndex = 0;
        _path = null;
        UpdateDevices();
        _status.Text = "Loaded example: " + ex.Name;
    }

    private void OpenSequence()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Open script",
            Filter = "Multi-instrument script (*.seq;*.txt)|*.seq;*.txt|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _editor.Text = File.ReadAllText(dlg.FileName);
            _path = dlg.FileName;
            UpdateDevices();
            _status.Text = "Opened " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open:\n" + ex.Message, "Multi-Instrument Scripts",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SaveSequence()
    {
        if (_path == null)
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Save script",
                Filter = "Multi-instrument script (*.seq)|*.seq|Text file (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = "script.seq",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _path = dlg.FileName;
        }

        try
        {
            File.WriteAllText(_path, _editor.Text);
            _status.Text = "Saved " + _path;
        }
        catch (Exception ex)
        {
            _path = null;
            MessageBox.Show(this, "Could not save:\n" + ex.Message, "Multi-Instrument Scripts",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
