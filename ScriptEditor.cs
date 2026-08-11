using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// The script editor: coloured as you type, with completion and Tab-expanded snippets.
///
/// Both windows use this one control, because both edit the same language — see
/// <see cref="ScriptLanguage"/>, which is where the words themselves live. A plain TextBox
/// cannot colour text, so this is a <see cref="RichTextBox"/>, and everything below exists
/// to make a RichTextBox behave like an editor rather than a word processor:
///
/// <list type="bullet">
/// <item>recolouring moves the selection, so the caret is saved and put back;</item>
/// <item>recolouring raises TextChanged, so re-entry is guarded;</item>
/// <item>recolouring repaints, so redraw is suspended around it (WM_SETREDRAW) — without
///       that the window flickers on every keystroke;</item>
/// <item>only the edited line is recoloured. Doing the whole script per keystroke is
///       visibly slow by about forty lines.</item>
/// </list>
/// </summary>
public sealed class ScriptEditor : RichTextBox
{
    private const int WM_SETREDRAW = 0x000B;
    private const int EM_GETSCROLLPOS = 0x04DD;
    private const int EM_SETSCROLLPOS = 0x04DE;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref Point lParam);

    private ScriptLanguage _language = ScriptLanguage.ForScript;
    private bool _painting;              // re-entrancy guard for the colouring pass

    /// <summary>
    /// Instrument names the script has declared, refreshed whenever the whole thing is
    /// repainted. The tokenizer needs them to tell "gen:" from "C1:" — see
    /// <see cref="ScriptLanguage.Tokenize"/>. Recomputed rather than tracked: a DEVICE
    /// line can be edited anywhere, and the scan is a regex over a few dozen lines.
    /// </summary>
    private IReadOnlyCollection<string> _aliases = Array.Empty<string>();
    private int _lastLine = -1;
    private int _lastLineCount = -1;

    private readonly ListBox _completions = new();
    private readonly ToolTip _completionTip = new();
    private bool _completing;

    /// <summary>
    /// Whether the completion list is showing. A window that closes on Esc has to know:
    /// Esc dismisses the list first, and closing the window out from under someone
    /// half-way through a word is not what they pressed it for.
    /// </summary>
    public bool CompletionVisible => _completions.Visible;

    /// <summary>The placeholder spans of a just-inserted snippet, in Tab order.</summary>
    private readonly List<(int Start, int Length)> _fields = new();
    private int _field = -1;

    /// <summary>Catalog commands offered in completion. Set by the window that owns this.</summary>
    /// <remarks>
    /// Hidden from the designer serializer: this control is only ever built in code, and the
    /// analyzer otherwise refuses a property it cannot write into a .Designer.cs.
    /// </remarks>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<IEnumerable<string>>? CommandSource { get; set; }

    public ScriptEditor()
    {
        AcceptsTab = true;
        WordWrap = false;
        ScrollBars = RichTextBoxScrollBars.Both;
        DetectUrls = false;
        HideSelection = false;
        Font = new Font("Consolas", 10.5f);

        _completions.Visible = false;
        _completions.IntegralHeight = false;
        _completions.Font = new Font("Consolas", 9.5f);
        _completions.DrawMode = DrawMode.OwnerDrawFixed;
        _completions.DrawItem += Completions_DrawItem;
        _completions.Click += (_, _) => AcceptCompletion();
        _completions.KeyDown += Completions_KeyDown;
    }

    /// <summary>
    /// Assigning the whole text recolours the whole text.
    ///
    /// This is the path a file, an example and an AI draft all take, and none of them can be
    /// told from a keystroke by looking at the caret: assignment leaves it at the top, which
    /// is exactly where it already was. An AI-written script therefore arrived with only its
    /// first line coloured. Doing it here means no caller has to remember to ask.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public override string Text
    {
        get => base.Text;
        set { base.Text = value; RepaintAll(); }
    }

    /// <summary>Which dialect this editor is for. Set once, before any text is loaded.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ScriptLanguage Language
    {
        get => _language;
        set { _language = value; RepaintAll(); }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (Parent != null && !Parent.Controls.Contains(_completions))
        {
            Parent.Controls.Add(_completions);
            _completions.BringToFront();
        }
        RepaintAll();
    }

    // -------------------------------------------------------------------- colouring

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (_painting) return;

        RefreshAliases();

        // Is this one keystroke, or a whole script arriving at once?
        //
        // The caret alone does not tell you. Assigning Text — which is how a file, an example
        // and an AI draft all get here — leaves the caret at the top, so the caret-moved test
        // saw "still on line 0" and recoloured only the first line. The line *count* is what
        // separates the two cases: typing changes it by one at most, and pasting forty lines
        // changes it by forty.
        int lines = Lines.Length;
        int line = GetLineFromCharIndex(SelectionStart);

        if (Math.Abs(lines - _lastLineCount) > 1 || Math.Abs(line - _lastLine) > 1 || lines == 0)
            RepaintAll();
        else
        {
            RepaintLine(line);
            if (line > 0) RepaintLine(line - 1);
        }

        _lastLine = line;
        _lastLineCount = lines;

        if (_completing) UpdateCompletions();
    }

    /// <summary>
    /// Re-read which instrument names the script declares.
    ///
    /// On every change rather than only on a full repaint: a DEVICE line can be typed at any
    /// moment, and the line that uses the alias is often the very next one. The scan is a
    /// per-line pass over a script measured in tens of lines.
    /// </summary>
    private void RefreshAliases()
        => _aliases = _language.IsSequence
            ? ScriptLanguage.DeclaredAliases(Text)
            : Array.Empty<string>();

    /// <summary>Recolour everything. Used on load, on paste, and when the dialect changes.</summary>
    public void RepaintAll()
    {
        if (!IsHandleCreated || _painting) return;
        RefreshAliases();

        int caret = SelectionStart, len = SelectionLength;
        var scroll = new Point();
        SendMessage(Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref scroll);

        _painting = true;
        SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            SelectAll();
            SelectionColor = ForeColor;

            int offset = 0;
            foreach (string line in Lines)
            {
                PaintLine(line, offset);
                offset += line.Length + 1;   // the newline the Lines split removed
            }
        }
        finally
        {
            SelectionStart = caret;
            SelectionLength = len;
            SelectionColor = ForeColor;
            SendMessage(Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref scroll);
            SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            _painting = false;
            Invalidate();
        }
    }

    private void RepaintLine(int index)
    {
        if (!IsHandleCreated || _painting) return;
        if (index < 0 || index >= Lines.Length) return;

        int caret = SelectionStart, len = SelectionLength;
        int start = GetFirstCharIndexFromLine(index);
        if (start < 0) return;

        _painting = true;
        SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            string line = Lines[index];
            Select(start, line.Length);
            SelectionColor = ForeColor;
            PaintLine(line, start);
        }
        finally
        {
            SelectionStart = caret;
            SelectionLength = len;
            SelectionColor = ForeColor;
            SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            _painting = false;
            Invalidate();
        }
    }

    private void PaintLine(string line, int offset)
    {
        foreach (ScriptToken t in _language.Tokenize(line, _aliases))
        {
            Select(offset + t.Start, t.Length);
            SelectionColor = ColourOf(t.Kind);
        }
    }

    /// <summary>
    /// Restrained on purpose. A script is read to check what it will do to the hardware, and
    /// a page of seven colours is harder to read than a page of three. Keywords and comments
    /// carry the shape; the SCPI is left alone because it is the content.
    /// </summary>
    public static Color ColourOf(ScriptTokenKind kind) => kind switch
    {
        ScriptTokenKind.Comment  => Color.FromArgb(0, 128, 64),
        ScriptTokenKind.Keyword  => Color.FromArgb(0, 0, 200),
        ScriptTokenKind.Alias    => Color.FromArgb(140, 0, 140),
        ScriptTokenKind.Variable => Color.FromArgb(180, 80, 0),
        ScriptTokenKind.Number   => Color.FromArgb(110, 90, 0),
        ScriptTokenKind.Operator => Color.FromArgb(120, 120, 120),
        _ => Color.FromArgb(20, 20, 20),
    };

    // -------------------------------------------------------------------- snippets

    /// <summary>
    /// Put a snippet in at the caret, select its first placeholder, and arm Tab to walk the
    /// rest. This is what the Snippets dropdown calls, and what Tab-on-a-trigger calls.
    /// </summary>
    public void InsertSnippet(ScriptSnippet snippet)
    {
        string body = snippet.Body.Replace("\r\n", "\n").Replace("\n", "\r\n");

        // Match the indentation of the line the caret is on, so a snippet dropped inside a
        // FOR block lines up with the block instead of jumping to column zero.
        int lineIndex = GetLineFromCharIndex(SelectionStart);
        string current = lineIndex < Lines.Length ? Lines[lineIndex] : "";
        string indent = new(current.TakeWhile(char.IsWhiteSpace).ToArray());
        if (indent.Length > 0)
            body = body.Replace("\r\n", "\r\n" + indent).TrimEnd();

        int at = SelectionStart;
        SelectedText = body;

        _fields.Clear();
        foreach ((int s, int l) in ScriptSnippet.PlaceholdersIn(body)) _fields.Add((at + s, l));
        _field = -1;

        RepaintAll();
        if (!NextField()) SelectionStart = at + body.Length;
        Focus();
    }

    /// <summary>Select the next placeholder. False when there are none left.</summary>
    private bool NextField()
    {
        if (_field + 1 >= _fields.Count) { _fields.Clear(); _field = -1; return false; }

        _field++;
        (int start, int length) = _fields[_field];
        if (start + length > TextLength) { _fields.Clear(); _field = -1; return false; }

        // Select the placeholder including its « » marks, so typing replaces the lot.
        Select(start, length);
        return true;
    }

    /// <summary>
    /// Placeholders move as earlier ones are replaced. Rather than track edits, the
    /// remaining ones are found again in the text — there are only a handful, and being
    /// right after an arbitrary edit is worth more than being clever.
    /// </summary>
    private void RefindFields(int from)
    {
        _fields.Clear();
        _field = -1;

        string text = Text;
        for (int i = from; i < text.Length; i++)
        {
            if (text[i] != ScriptSnippet.PlaceholderOpen) continue;
            int close = text.IndexOf(ScriptSnippet.PlaceholderClose, i + 1);
            if (close < 0) break;
            _fields.Add((i, close - i + 1));
            i = close;
        }
    }

    // ------------------------------------------------------------------ completion

    protected override bool IsInputKey(Keys keyData)
        => keyData == Keys.Tab || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_completing)
        {
            switch (e.KeyCode)
            {
                case Keys.Down:
                    _completions.SelectedIndex = Math.Min(_completions.SelectedIndex + 1,
                                                          _completions.Items.Count - 1);
                    e.Handled = e.SuppressKeyPress = true;
                    return;
                case Keys.Up:
                    _completions.SelectedIndex = Math.Max(_completions.SelectedIndex - 1, 0);
                    e.Handled = e.SuppressKeyPress = true;
                    return;
                case Keys.Enter:
                case Keys.Tab:
                    AcceptCompletion();
                    e.Handled = e.SuppressKeyPress = true;
                    return;
                case Keys.Escape:
                    HideCompletions();
                    e.Handled = e.SuppressKeyPress = true;
                    return;
            }
        }

        // Ctrl+Space is the universal "show me what fits here".
        if (e.Control && e.KeyCode == Keys.Space)
        {
            ShowCompletions(force: true);
            e.Handled = e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Tab && !e.Shift)
        {
            // Tab does three things, most specific first: walk a snippet's placeholders,
            // expand a trigger word, and only then insert an actual tab.
            if (_fields.Count > 0)
            {
                RefindFields(0);
                if (NextField()) { e.Handled = e.SuppressKeyPress = true; return; }
            }

            string word = WordBeforeCaret();
            ScriptSnippet? snippet = _language.Snippets.FirstOrDefault(
                s => s.Trigger.Equals(word, StringComparison.OrdinalIgnoreCase));

            if (snippet != null)
            {
                Select(SelectionStart - word.Length, word.Length);
                SelectedText = "";
                InsertSnippet(snippet);
                e.Handled = e.SuppressKeyPress = true;
                return;
            }
        }

        if (e.KeyCode == Keys.Escape && _fields.Count > 0)
        {
            _fields.Clear();
            _field = -1;
            e.Handled = e.SuppressKeyPress = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        // Offer completions once there is something to filter on, and never mid-word-deletion
        // storm — a popup that appears on Backspace is a popup that gets in the way.
        if (!_completing && e.KeyCode is not (Keys.Back or Keys.Delete or Keys.Escape)
            && WordBeforeCaret().Length >= 2)
            ShowCompletions(force: false);
    }

    private string WordBeforeCaret()
    {
        int caret = SelectionStart;
        string text = Text;
        int i = Math.Min(caret, text.Length);
        int start = i;

        while (start > 0 && (char.IsLetterOrDigit(text[start - 1])
                             || text[start - 1] is '_' or '*' or '$' or ':'))
            start--;

        return text[start..i];
    }

    private void ShowCompletions(bool force)
    {
        string prefix = WordBeforeCaret();
        if (!force && prefix.Length < 2) return;

        IReadOnlyList<ScriptCompletion> items = _language.Complete(
            Text, prefix, CommandSource?.Invoke());

        if (items.Count == 0) { HideCompletions(); return; }

        _completions.BeginUpdate();
        _completions.Items.Clear();
        foreach (ScriptCompletion c in items.Take(200)) _completions.Items.Add(c);
        _completions.EndUpdate();
        _completions.SelectedIndex = 0;

        Point caretPos = GetPositionFromCharIndex(SelectionStart);
        int itemHeight = _completions.Font.Height + 6;
        _completions.ItemHeight = itemHeight;
        _completions.Size = new Size(LogicalToDeviceUnits(360),
                                     Math.Min(8, _completions.Items.Count) * itemHeight + 4);
        _completions.Location = new Point(
            Math.Min(Left + caretPos.X, Parent!.ClientSize.Width - _completions.Width),
            Math.Min(Top + caretPos.Y + Font.Height + 4,
                     Parent.ClientSize.Height - _completions.Height));

        _completions.Visible = true;
        _completions.BringToFront();
        _completing = true;
    }

    private void UpdateCompletions()
    {
        if (WordBeforeCaret().Length == 0) { HideCompletions(); return; }
        ShowCompletions(force: true);
    }

    private void HideCompletions()
    {
        _completions.Visible = false;
        _completing = false;
    }

    private void AcceptCompletion()
    {
        if (_completions.SelectedItem is not ScriptCompletion chosen) { HideCompletions(); return; }

        string word = WordBeforeCaret();
        HideCompletions();

        if (word.Length > 0)
        {
            Select(SelectionStart - word.Length, word.Length);
            SelectedText = "";
        }

        if (chosen.Snippet != null) InsertSnippet(chosen.Snippet);
        else
        {
            SelectedText = chosen.Text + (chosen.Kind == ScriptCompletionKind.Alias ? " " : "");
            RepaintLine(GetLineFromCharIndex(SelectionStart));
        }

        Focus();
    }

    private void Completions_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { AcceptCompletion(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape) { HideCompletions(); Focus(); e.Handled = true; }
    }

    /// <summary>Name on the left, what-kind-of-thing in grey on the right.</summary>
    private void Completions_DrawItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _completions.Items.Count) return;

        var item = (ScriptCompletion)_completions.Items[e.Index]!;
        bool selected = (e.State & DrawItemState.Selected) != 0;

        using var name = new SolidBrush(selected ? SystemColors.HighlightText : ColourOf(KindColour(item.Kind)));
        using var detail = new SolidBrush(selected ? SystemColors.HighlightText : SystemColors.GrayText);

        var r = e.Bounds;
        e.Graphics.DrawString(item.Text, _completions.Font, name, r.Left + 4, r.Top + 2);

        int used = (int)e.Graphics.MeasureString(item.Text, _completions.Font).Width;
        e.Graphics.DrawString(item.Detail, _completions.Font, detail, r.Left + 8 + used, r.Top + 2);

        e.DrawFocusRectangle();
    }

    private static ScriptTokenKind KindColour(ScriptCompletionKind kind) => kind switch
    {
        ScriptCompletionKind.Keyword  => ScriptTokenKind.Keyword,
        ScriptCompletionKind.Snippet  => ScriptTokenKind.Keyword,
        ScriptCompletionKind.Alias    => ScriptTokenKind.Alias,
        ScriptCompletionKind.Variable => ScriptTokenKind.Variable,
        _ => ScriptTokenKind.Plain,
    };

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        if (!_completions.Focused) HideCompletions();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _completions.Dispose(); _completionTip.Dispose(); }
        base.Dispose(disposing);
    }
}
