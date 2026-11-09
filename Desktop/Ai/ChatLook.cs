using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// What the AI windows are drawn with: the page's colours (<c>assistant.css</c>, each worked out
/// over white, since a WinForms control has no alpha to lay over its parent) and the rounded shapes
/// the page gets from a border radius.
/// </summary>
internal static class ChatLook
{
    public static readonly Color Paper = Color.White;
    public static readonly Color Rule = Color.FromArgb(222, 226, 230);
    public static readonly Color Accent = Color.FromArgb(13, 110, 253);
    public static readonly Color AccentDown = Color.FromArgb(10, 88, 202);

    /// <summary>The Send button and the (i) while it is on: the grey of the app's other buttons, not the page's blue.</summary>
    public static readonly Color ButtonGrey = Color.FromArgb(225, 225, 225);
    public static readonly Color ButtonGreyDown = Color.FromArgb(204, 204, 204);

    public static readonly Color Hover = Color.FromArgb(241, 243, 245);
    public static readonly Color Pressed = Color.FromArgb(222, 226, 230);
    public static readonly Color Chosen = Color.FromArgb(231, 240, 255);

    public static readonly Color AskedFill = Color.FromArgb(226, 235, 255);
    public static readonly Color AnsweredFill = Color.FromArgb(240, 240, 240);
    public static readonly Color ReadFill = Color.FromArgb(255, 249, 230);
    public static readonly Color ReadEdge = Color.FromArgb(217, 190, 104);
    public static readonly Color ChangeFill = Color.FromArgb(240, 245, 255);
    public static readonly Color ChangeEdge = Color.FromArgb(126, 167, 255);
    public static readonly Color ErrorFill = Color.FromArgb(252, 240, 240);
    public static readonly Color ErrorEdge = Color.FromArgb(236, 171, 171);
    public static readonly Color ErrorInk = Color.FromArgb(161, 38, 38);
    public static readonly Color SuggestFill = Color.FromArgb(238, 246, 242);
    public static readonly Color SuggestEdge = Color.FromArgb(152, 201, 178);

    /// <summary>
    /// Paints a control's background as a rounded shape, with what is behind the control in the
    /// corners. A gutter is room left outside the shape, inside the control: the gap between two
    /// docked panels, which docking has no margin for.
    /// </summary>
    public static void PaintRounded(Control control, Graphics g, Color fill, Color? edge, int radius, Padding gutter)
    {
        g.Clear(Behind(control));
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var box = new Rectangle(
            control.LogicalToDeviceUnits(gutter.Left),
            control.LogicalToDeviceUnits(gutter.Top),
            control.ClientSize.Width - control.LogicalToDeviceUnits(gutter.Horizontal) - 1,
            control.ClientSize.Height - control.LogicalToDeviceUnits(gutter.Vertical) - 1);
        if (box.Width <= 0 || box.Height <= 0) return;

        using GraphicsPath path = Rounded(box, control.LogicalToDeviceUnits(radius));
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        if (edge is Color line)
        {
            using var pen = new Pen(line);
            g.DrawPath(pen, path);
        }
    }

    private static Color Behind(Control control)
    {
        for (Control? c = control.Parent; c is not null; c = c.Parent)
            if (c.BackColor.A == 255) return c.BackColor;
        return Paper;
    }

    private static GraphicsPath Rounded(Rectangle box, int radius)
    {
        int across = Math.Min(radius * 2, Math.Min(box.Width, box.Height));
        var path = new GraphicsPath();
        if (across <= 1)
        {
            path.AddRectangle(box);
            return path;
        }
        path.AddArc(box.X, box.Y, across, across, 180, 90);
        path.AddArc(box.Right - across, box.Y, across, across, 270, 90);
        path.AddArc(box.Right - across, box.Bottom - across, across, across, 0, 90);
        path.AddArc(box.X, box.Bottom - across, across, across, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>A panel drawn as a rounded, bordered box, the chats', the files' and the conversation's.</summary>
internal sealed class RoundedPanel : Panel
{
    public RoundedPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = ChatLook.Paper;
    }

    /// <summary>Room left outside the box, in logical pixels: the gap to the panel beside it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Padding Gutter { get; set; }

    protected override void OnPaintBackground(PaintEventArgs e)
        => ChatLook.PaintRounded(this, e.Graphics, BackColor, ChatLook.Rule, 6, Gutter);
}

/// <summary>A table drawn as a rounded box: the frame round the question box, and a row of the chats' list.</summary>
internal sealed class RoundedTable : TableLayoutPanel
{
    public RoundedTable()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? Edge { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 6;

    protected override void OnPaintBackground(PaintEventArgs e)
        => ChatLook.PaintRounded(this, e.Graphics, BackColor, Edge, Radius, default);
}

/// <summary>A stack of controls on a rounded ground: a question, an answer, a card, an example to start from.</summary>
internal sealed class Bubble : FlowLayoutPanel
{
    public Bubble()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? Edge { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 8;

    protected override void OnPaintBackground(PaintEventArgs e)
        => ChatLook.PaintRounded(this, e.Graphics, BackColor, Edge, Radius, default);
}
