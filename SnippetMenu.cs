using System;
using System.Drawing;
using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// The Snippets button: every word the language has, what each is for, and one click to put
/// it in the editor.
///
/// This is the language reference, in the place where it is needed. The language is local to
/// this app (<see cref="ScriptLanguage"/>) — nobody arrives already knowing that a sweep is
/// spelled <c>FOR f = 100 TO 100k POINTS 40 LOG</c> — and a reference in a separate window is
/// a reference nobody opens. A menu that writes the thing for you is read every time.
/// </summary>
public static class SnippetMenu
{
    /// <summary>
    /// Hang a snippet menu off <paramref name="button"/>, inserting into
    /// <paramref name="editor"/>.
    /// </summary>
    public static void Attach(Button button, ScriptEditor editor, ScriptLanguage language)
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };

        foreach (ScriptSnippet snippet in language.Snippets)
        {
            var item = new ToolStripMenuItem($"{snippet.Title}")
            {
                // Two lines' worth of information in one row: what it is, and what it is for.
                ToolTipText = snippet.Summary
                            + "\r\n\r\nType \"" + snippet.Trigger + "\" and press Tab for the same thing."
                            + "\r\n\r\n" + snippet.Body.Replace("\n", "\r\n"),
                Tag = snippet,
            };
            item.Click += (_, _) => editor.InsertSnippet(snippet);
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());

        var help = new ToolStripMenuItem("What all of this means…");
        help.Click += (_, _) =>
        {
            using var dlg = new ScriptReferenceForm(language);
            Form? owner = button.FindForm();
            if (owner?.Icon != null) dlg.Icon = owner.Icon;
            dlg.ShowDialog(owner);
        };
        menu.Items.Add(help);

        button.Click += (_, _) => menu.Show(button, new Point(0, button.Height));
    }

}
