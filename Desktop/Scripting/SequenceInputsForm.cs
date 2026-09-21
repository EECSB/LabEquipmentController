using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// Asks for the values a script's INPUT lines declare, just before it runs.
///
/// A script that measures at 5 V and one that measures at 12 V should be one script run twice,
/// not two scripts — so the values that differ are asked for here rather than edited into the
/// text. Shown only when the script declares any; a script with no INPUT line never sees it.
///
/// What is typed is checked by <see cref="SequenceRunner.TryBindInputs"/>, the same rule the
/// web build and <c>lec seq</c> check against, so a value this box accepts is a value any of
/// the three will run — and one it refuses is refused before an instrument has been touched.
/// </summary>
public sealed class SequenceInputsForm : Form
{
    private readonly string _script;
    private readonly List<(SequenceInput Input, TextBox Box)> _rows = new();
    private readonly Label _problem = new();
    private readonly Button _ok = new();

    /// <summary>What was typed, by input name, once the dialog has been accepted.</summary>
    public IReadOnlyDictionary<string, string> Values { get; private set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <param name="script">The script about to run, read for its INPUT lines.</param>
    /// <param name="given">What was typed last time, to fill the boxes again.</param>
    public SequenceInputsForm(string script, IReadOnlyDictionary<string, string>? given = null)
    {
        _script = script ?? throw new ArgumentNullException(nameof(script));

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9f);
        Text = "Values for this run";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        IReadOnlyList<SequenceInput> declared = SequenceRunner.Inputs(script);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            Padding = new Padding(16, 14, 16, 12),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        foreach (SequenceInput input in declared)
        {
            var name = new Label
            {
                AutoSize = true,
                UseMnemonic = false,
                Text = input.Name,
                Margin = new Padding(0, 6, 10, 0),
            };

            var box = new TextBox
            {
                Width = 140,
                Margin = new Padding(0, 3, 10, 3),
                Text = Prefill(input, given),
            };
            box.TextChanged += (_, _) => Recheck();

            var takes = new Label
            {
                AutoSize = true,
                UseMnemonic = false,
                ForeColor = SystemColors.GrayText,
                Text = Takes(input),
                Margin = new Padding(0, 6, 0, 0),
            };

            body.Controls.Add(name, 0, _rows.Count);
            body.Controls.Add(box, 1, _rows.Count);
            body.Controls.Add(takes, 2, _rows.Count);
            _rows.Add((input, box));
        }

        _problem.AutoSize = true;
        _problem.UseMnemonic = false;
        _problem.MaximumSize = new Size(420, 0);
        _problem.ForeColor = Color.FromArgb(176, 0, 32);
        _problem.Margin = new Padding(0, 8, 0, 0);
        body.Controls.Add(_problem, 0, _rows.Count);
        body.SetColumnSpan(_problem, 3);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 10, 0, 0),
        };

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        _ok.Text = "Run";
        _ok.DialogResult = DialogResult.OK;
        _ok.AutoSize = true;
        _ok.Margin = new Padding(8, 0, 0, 0);
        _ok.Click += (_, _) => Take();

        buttons.Controls.Add(_ok);
        buttons.Controls.Add(cancel);
        body.Controls.Add(buttons, 0, _rows.Count + 1);
        body.SetColumnSpan(buttons, 3);

        Controls.Add(body);
        AcceptButton = _ok;
        CancelButton = cancel;

        Recheck();
    }

    /// <summary>Size to the rows, as AboutForm does rather than guessing a client size.</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ClientSize = Controls[0].PreferredSize;

        if (_rows.Count > 0)
            _rows[0].Box.Focus();
    }

    /// <summary>What was typed before, or the default the line declared.</summary>
    private static string Prefill(SequenceInput input, IReadOnlyDictionary<string, string>? given)
    {
        if (given != null)
        {
            foreach (var (name, value) in given)
                if (name.Equals(input.Name, StringComparison.OrdinalIgnoreCase))
                    return value;
        }

        return input.Default ?? "";
    }

    /// <summary>The declaration in a few words, beside the box.</summary>
    private static string Takes(SequenceInput input)
    {
        var said = new List<string> { input.Kind };

        if (input.Unit.Length > 0)
            said.Add(input.Unit);

        if (input.Min is { } low && input.Max is { } high)
            said.Add($"{Num(low)} to {Num(high)}");
        else if (input.Min is { } only)
            said.Add($"{Num(only)} or more");
        else if (input.Max is { } most)
            said.Add($"up to {Num(most)}");

        if (input.Required)
            said.Add("required");

        return string.Join(", ", said);
    }

    private static string Num(double v) => v.ToString("0.##########", CultureInfo.InvariantCulture);

    /// <summary>Everything typed, as the runner will be given it.</summary>
    private Dictionary<string, string> Typed()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (input, box) in _rows)
            if (box.Text.Trim().Length > 0)
                values[input.Name] = box.Text;

        return values;
    }

    /// <summary>
    /// Say what is wrong as it is typed, and grey out Run until nothing is.
    /// </summary>
    /// <remarks>
    /// The alternative — accept it and let the run report it — costs a run start, a held
    /// instrument and a log line to say a box is empty, which the box can say itself.
    /// </remarks>
    private void Recheck()
    {
        bool ok = SequenceRunner.TryBindInputs(_script, Typed(), out _, out string? why);
        _problem.Text = why ?? "";
        _problem.Visible = why != null;
        _ok.Enabled = ok;
    }

    private void Take()
    {
        if (SequenceRunner.TryBindInputs(_script, Typed(), out _, out _))
            Values = Typed();
        else
            DialogResult = DialogResult.None;
    }
}
