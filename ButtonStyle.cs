using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// One set of metrics for every button in the app.
///
/// Before this existed, each window sized its buttons its own way: the console took the
/// tallest control of a row, the script editor took its Examples combo's height, and the
/// main window used three designer sizes (23, 20 and 23 px) with three glyph sizes (13, 12
/// and 15) and one button in a different FlatStyle. The result was buttons of visibly
/// different heights from window to window and, in the main window, within one window.
///
/// The rule now: build a button with <see cref="Apply"/>, give it a glyph with
/// <see cref="SetIcon"/>, and pin it in <c>OnLoad</c> with <see cref="Normalize"/>. The
/// height comes from the shared padding, the shared glyph size and the window's font, so
/// every window lands on the same number instead of on whatever its own tallest control
/// happened to be. Widths stay free — they follow the label, which is the one thing that
/// legitimately differs.
///
/// Kept in the app rather than Core because it depends on WinForms.
/// </summary>
internal static class ButtonStyle
{
    /// <summary>Glyph size in logical pixels — the same on every button in the app.</summary>
    private const int IconLogical = 16;

    /// <summary>Shortest a button may be, so a short label doesn't leave a stub.</summary>
    private const int MinWidthLogical = 76;

    /// <summary>The DPI the constants above are expressed in.</summary>
    private const int BaseDpi = 96;

    /// <summary>
    /// Scale a logical size for a control's display. Does what <c>LogicalToDeviceUnits</c>
    /// does, but from outside the control — this class is not a Control.
    /// </summary>
    public static int Scale(Control c, int logical)
        => (int)Math.Round(logical * c.DeviceDpi / (double)BaseDpi);

    /// <summary>
    /// Per-glyph optical correction, as a fraction of the nominal size.
    ///
    /// The bundled artwork does not share a common margin. The play triangle and the stop
    /// square are solid shapes drawn edge to edge in their 64×64 canvas, while the magnifier,
    /// the file glyphs and the drawn ones are thin outlines inset from theirs. Rendered at one
    /// nominal size the solid ones read as roughly twice the weight of the outlines — on the
    /// console's Run/Stop/Single row they looked like blocks. Drawing them smaller is what
    /// makes them look the same size; matching the numbers is not the same as matching the eye.
    /// </summary>
    private static readonly Dictionary<string, double> Optical = new()
    {
        ["stopClock"] = 0.60,    // a solid square: the heaviest shape in the set
        ["startClock"] = 0.68,   // a solid triangle
        ["stepClock"] = 0.76,    // a thick arrow, corner to corner
        ["reset"] = 0.78,        // ditto, as a ring
        ["connect"] = 0.76,      // a busy diagonal shape, corner to corner
        ["program"] = 0.84,
        ["new"] = 0.84,
    };

    /// <summary>Nominal glyph size for a control's display scale.</summary>
    public static int IconPx(Control c) => Scale(c, IconLogical);

    /// <summary>Glyph size for one named glyph, after its optical correction.</summary>
    private static int IconPx(Control c, string name)
        => (int)Math.Round(IconPx(c) * (Optical.TryGetValue(name, out double f) ? f : 1.0));

    /// <summary>
    /// Metrics applied when the button is built. The height is deliberately left alone here
    /// and settled later by <see cref="Normalize"/>, once the handle exists and the glyph
    /// is on — a glyph is what makes a button taller than its label alone.
    /// </summary>
    public static void Apply(Button b, string text, EventHandler? onClick = null)
    {
        b.Text = text;
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.FlatStyle = FlatStyle.Standard;
        b.Padding = new Padding(8, 3, 8, 3);
        // 8 below, not 4: in a wrapping strip this is the gap between rows, and at four
        // pixels the rows read as one block of buttons rather than several.
        b.Margin = new Padding(0, 0, 6, 8);
        b.UseVisualStyleBackColor = true;
        if (onClick != null) b.Click += onClick;
    }

    /// <summary>Put a bundled glyph on a button, left of its label.</summary>
    public static void SetIcon(Control owner, Button b, string name)
        => SetImage(b, AppIcons.Get(name, IconPx(owner, name)));

    /// <summary>Put a runtime-drawn glyph on a button, left of its label.</summary>
    public static void SetDrawnIcon(Control owner, Button b, string name)
        => SetImage(b, AppIcons.Drawn(name, IconPx(owner, name)));

    private static void SetImage(Button b, Image? img)
    {
        if (img == null) return;   // a missing glyph must never break the UI
        b.Image = img;
        b.TextImageRelation = TextImageRelation.ImageBeforeText;
        b.ImageAlign = ContentAlignment.MiddleCenter;
        b.TextAlign = ContentAlignment.MiddleCenter;
    }

    /// <summary>
    /// The one button height for a window's font and display scale: what an auto-sized button
    /// with the shared padding and a glyph measures. Taken from a throwaway button, so the
    /// answer depends only on the font and the scale — never on which of a window's own
    /// buttons happens to carry the tallest label.
    /// </summary>
    public static int Height(Control owner)
    {
        using var probe = new Button();
        Apply(probe, "Ag");
        probe.Font = owner.Font;
        using var glyph = new Bitmap(IconPx(owner), IconPx(owner));
        SetImage(probe, glyph);
        return probe.PreferredSize.Height;
    }

    /// <summary>
    /// Pin buttons to the shared height and minimum width, leaving their widths otherwise
    /// free. Call from <c>OnLoad</c>, after the glyphs are set. Returns the height, for
    /// sizing the row around them.
    /// </summary>
    public static int Normalize(Control owner, params Button?[] buttons)
    {
        int h = Height(owner);
        foreach (Button? b in buttons)
        {
            if (b == null) continue;
            b.MinimumSize = new Size(Scale(owner, MinWidthLogical), h);
            b.MaximumSize = new Size(0, h);   // width 0 = unconstrained
        }
        return h;
    }

    // ------------------------------------------------------- combo boxes in a button row

    /// <summary>
    /// Grow a combo box to the shared button height, so a row of buttons and pickers reads as
    /// one strip. A DropDownList combo is font-height-locked and ignores <c>Height</c>, so it
    /// has to be <see cref="DrawMode.OwnerDrawFixed"/> — its closed height then follows
    /// ItemHeight, plus whatever border the system draws on top.
    /// </summary>
    public static void MatchHeight(ComboBox combo, int h)
    {
        // Iterate rather than solve: the border the system adds around ItemHeight is only
        // observable after a change, and computing it once left the combo a few pixels
        // taller than the buttons beside it.
        for (int i = 0; i < 4 && combo.Height != h; i++)
            combo.ItemHeight = Math.Max(1, combo.ItemHeight + (h - combo.Height));
    }

    /// <summary>
    /// Centre a control on a row of buttons, through its top margin.
    ///
    /// A FlowLayoutPanel aligns its children to the top of the row, so anything shorter than
    /// the buttons — a label, or a NumericUpDown, which clamps itself to a font-driven height
    /// in <c>UpDownBase.SetBoundsCore</c> and cannot be grown at all — otherwise floats above
    /// the line. Call it after the buttons are normalised, with the height Normalize returned.
    /// </summary>
    public static void CentreInRow(Control c, int h)
    {
        Padding m = c.Margin;
        c.Margin = new Padding(m.Left, Math.Max(0, (h - c.Height) / 2), m.Right, m.Bottom);
    }

    /// <summary>
    /// Paint one item of a combo grown by <see cref="MatchHeight"/>.
    ///
    /// The closed box is painted in the control's own colours. WinForms hands the handler
    /// the *selection* colours for the closed box whenever the combo has focus, so drawing
    /// what it gives you puts a blue bar across the control the moment it is tabbed into.
    /// </summary>
    public static void DrawComboItem(ComboBox combo, DrawItemEventArgs e)
    {
        bool closed = (e.State & DrawItemState.ComboBoxEdit) != 0;
        if (closed)
        {
            using var back = new SolidBrush(combo.BackColor);
            e.Graphics.FillRectangle(back, e.Bounds);
        }
        else
        {
            e.DrawBackground();
        }

        if (e.Index >= 0)
        {
            Font font = e.Font ?? combo.Font;
            string text = combo.GetItemText(combo.Items[e.Index]) ?? "";
            var pt = new Point(e.Bounds.Left + 3, e.Bounds.Top + (e.Bounds.Height - font.Height) / 2);
            TextRenderer.DrawText(e.Graphics, text, font, pt, closed ? combo.ForeColor : e.ForeColor);
        }

        if (!closed) e.DrawFocusRectangle();
    }
}
