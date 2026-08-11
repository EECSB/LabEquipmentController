using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// The recorded rows of a run, as a table and as a curve, with the two buttons that keep or
/// discard them.
///
/// One control rather than three copies: the sequence window, the single-instrument script
/// window and each console tab all show the same thing, and the only difference between them
/// is what puts rows in. Splitting the table and the plot across three files would have meant
/// three CSV writers and three chances for the columns and the curve to disagree.
/// </summary>
internal sealed class ResultsPanel : UserControl
{
    private readonly ListView _results = new();
    private readonly ResultPlotPanel _plot = new();
    private readonly List<SequenceRow> _rows = new();
    private readonly Button _btnClear = new();
    private readonly Button _btnSaveCsv = new();
    private readonly FlowLayoutPanel _tools = new();
    private readonly ToolTip _tips = new();

    /// <summary>
    /// Top and bottom insets of the console's command row, which sits alongside this panel's
    /// button strip in the same window. Kept here as one value both can be measured against
    /// rather than as a number repeated in two files that drifted apart.
    /// </summary>
    internal static readonly Padding CommandRowInset = new(6, 4, 6, 6);

    /// <summary>Stem of the suggested CSV file name — the script's name, usually.</summary>
    // Never placed from the designer, so it has nothing to serialise; saying so is what
    // stops the WinForms analyser treating it as a design-time property.
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string FileStem { get; set; } = "results";

    /// <summary>What the host should put on its status line. Saving is the only thing worth
    /// announcing; everything else here is visible in the table.</summary>
    public event EventHandler<string>? Status;

    /// <summary>The buttons, so the host can size them with the rest of its own (SPEC §14).
    /// The plot's Screenshot is in here too — it is not on this control, but it is a button
    /// in the same window and has to match.</summary>
    public Button[] Buttons => new[] { _btnClear, _btnSaveCsv, _plot.ScreenshotButton };

    public bool HasRows => _rows.Count > 0;

    /// <summary>Rows recorded so far — a run reports how many it added.</summary>
    public int RowCount => _rows.Count;

    /// <summary>
    /// Height of the Results/Plot tab band. The console's queue bar is matched to it so the
    /// two panes start their content on the same line.
    /// </summary>
    public int TabStripHeight => _views?.DisplayRectangle.Top ?? 0;

    public ResultsPanel()
    {
        _results.Dock = DockStyle.Fill;
        _results.View = View.Details;
        _results.FullRowSelect = true;
        _results.GridLines = true;

        // Table and plot are the same data twice, so they share one space rather than
        // splitting it: the numbers are what gets exported and the curve is what gets read,
        // and neither is worth half a pane while you are looking at the other.
        var views = new TabControl { Dock = DockStyle.Fill };

        var tableTab = new TabPage("Results") { UseVisualStyleBackColor = true };
        tableTab.Controls.Add(_results);

        var plotTab = new TabPage("Plot") { UseVisualStyleBackColor = true };
        _plot.Dock = DockStyle.Fill;
        plotTab.Controls.Add(_plot);

        views.TabPages.Add(tableTab);
        views.TabPages.Add(plotTab);

        views.SelectedIndexChanged += (_, _) => PlaceTools(views.SelectedIndex);

        _tools.Dock = DockStyle.Bottom;
        _tools.AutoSize = true;
        _tools.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _tools.FlowDirection = FlowDirection.RightToLeft;
        _tools.WrapContents = false;
        // The same vertical insets the console's command row uses, so Save CSV and Clear
        // Results sit on the line Clear Log and Save Log do. With no bottom inset they were
        // flush against the edge of the pane and read as half a row lower than the pair
        // beside them.
        _tools.Padding = new Padding(0, CommandRowInset.Top, 0, CommandRowInset.Bottom);

        ButtonStyle.Apply(_btnClear, "Clear Results", (_, _) => Clear());
        _tools.Controls.Add(_btnClear);

        ButtonStyle.Apply(_btnSaveCsv, "Save CSV", (_, _) => SaveCsv());
        _tools.Controls.Add(_btnSaveCsv);

        // Fill first, then docked edges (this project's convention for docking order).
        Controls.Add(views);
        Controls.Add(_tools);
        _views = views;

        _tips.SetToolTip(_results, "Rows recorded by the run. RECORD in a script appends one; "
                                 + "a console records every numeric reply.");
        _tips.SetToolTip(_btnClear, "Discard the recorded rows and the curve. The log is left "
                                  + "alone — it is the record of how these were produced.");
        _tips.SetToolTip(_btnSaveCsv, "Save the recorded rows as a CSV file.");
    }

    private TabControl _views = null!;

    /// <summary>
    /// Clear Results and Save CSV belong to the table, so they show with it and not with the
    /// plot. Both act on the rows: emptying them or writing them out is a thing you do to the
    /// list you are looking at. The plot has its own button for the one thing that is about
    /// the picture — see ResultPlotPanel.SaveImage.
    /// </summary>
    private void PlaceTools(int tabIndex) => _tools.Visible = tabIndex == 0;

    /// <summary>
    /// Give the table its headings.
    ///
    /// Authoritative rather than first-wins. It used to return early whenever the table
    /// already had columns, which was fine until something removed them: a console declares
    /// Time / Command / Value once when it is built, so after Clear Results the next reading
    /// arrived at a table with no columns and <see cref="AddRow"/> numbered them Column 1,
    /// Column 2, Column 3. The names are not decoration — the plot finds the command column
    /// by name, and the CSV writes them as its header row.
    ///
    /// Existing rows still stop a rename, which is what the early return was protecting:
    /// re-labelling columns over data that was recorded under different ones would describe
    /// the rows wrongly rather than describe them late.
    /// </summary>
    public void SetColumns(IReadOnlyList<string> columns)
    {
        if (columns.Count == 0) return;

        var current = _results.Columns.Cast<ColumnHeader>().Select(c => c.Text).ToList();
        if (current.SequenceEqual(columns, StringComparer.Ordinal)) return;
        if (_results.Items.Count > 0) return;

        _results.Columns.Clear();
        foreach (string c in columns) _results.Columns.Add(c, 160);
    }

    public void AddRow(SequenceRow row)
    {
        // A run need not declare COLUMNS; if it did not, number them from the first row
        // rather than dropping the data on the floor.
        while (_results.Columns.Count < row.Values.Count)
            _results.Columns.Add($"Column {_results.Columns.Count + 1}", 160);

        var item = new ListViewItem(row.Values.Count > 0 ? row.Values[0] : "");
        for (int i = 1; i < row.Values.Count; i++) item.SubItems.Add(row.Values[i]);
        _results.Items.Add(item);
        _results.EnsureVisible(_results.Items.Count - 1);
        _rows.Add(row);

        // Redrawn per row, so a sweep draws itself as it runs — which is when it is most
        // useful: a response going somewhere wrong is visible on the tenth point, not after
        // the fortieth.
        _plot.Show(_results.Columns.Cast<ColumnHeader>().Select(c => c.Text).ToList(), _rows);
    }

    /// <summary>
    /// Empty the table and the plot. The caller's log is deliberately untouched.
    ///
    /// The headings stay. They are what the table is, not what is in it — a console names
    /// its three columns once and then runs for hours, and taking the names away with the
    /// rows left the next reading to invent Column 1, Column 2, Column 3. A run that wants
    /// different headings sets them; <see cref="SetColumns"/> replaces them once the rows
    /// are gone.
    /// </summary>
    public void Clear()
    {
        _results.Items.Clear();
        _rows.Clear();
        _plot.Show(_results.Columns.Cast<ColumnHeader>().Select(c => c.Text).ToList(), _rows);
    }

    /// <summary>
    /// Give the two buttons their glyphs. Call after the handle exists, so the icons are
    /// rendered at this window's DPI — same order the other windows use.
    /// </summary>
    public void ApplyIcons()
    {
        ButtonStyle.SetDrawnIcon(this, _btnSaveCsv, "save");
        ButtonStyle.SetIcon(this, _btnClear, "reset");
    }

    /// <summary>Size the button strip to the buttons, once the host has normalised them.</summary>
    public void PinToolHeight()
    {
        // Normalize gives every button the app's minimum width, which is sized for a word.
        // Screenshot carries only a glyph, so it is squared off instead — a button three
        // times wider than the picture on it reads as one waiting for a label.
        Button shot = _plot.ScreenshotButton;
        if (shot.Height > 0) shot.MinimumSize = new Size(shot.Height, shot.Height);

        int tallest = 0;
        foreach (Control c in _tools.Controls) tallest = Math.Max(tallest, c.Height);
        if (tallest == 0) return;
        _tools.AutoSize = false;
        // Vertical, not just Top: leaving the bottom inset out of the height is what pressed
        // the buttons against the edge of the pane in the first place.
        _tools.Height = tallest + _tools.Padding.Vertical;
    }

    private void SaveCsv()
    {
        if (_rows.Count == 0)
        {
            MessageBox.Show(this, "There are no results yet.",
                "Save CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "Save results",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = FileStem + "-results.csv",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var sb = new StringBuilder();
            if (_results.Columns.Count > 0)
                sb.AppendLine(string.Join(",",
                    _results.Columns.Cast<ColumnHeader>().Select(c => Csv(c.Text))));
            foreach (SequenceRow r in _rows) sb.AppendLine(string.Join(",", r.Values.Select(Csv)));

            File.WriteAllText(dlg.FileName, sb.ToString());
            Status?.Invoke(this, "Results saved to " + dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the results:\n" + ex.Message, "Save CSV",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Quote a field only when it needs it, so a plain number stays a plain number.</summary>
    private static string Csv(string s)
        => s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
}
