using System;
using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// Placing a splitter without taking the window down.
/// </summary>
internal static class SplitLayout
{
    /// <summary>
    /// Put the splitter a fraction of the way across, or leave it where it is when the panels'
    /// own minimums leave nowhere legal to put it.
    ///
    /// <para>
    /// SplitterDistance throws — an unhandled InvalidOperationException, straight out of a
    /// constructor or a Shown handler — whenever the value falls outside
    /// [Panel1MinSize, extent − Panel2MinSize − SplitterWidth]. The extent when a window is
    /// first shown is not the one the designer had in mind: a narrow window, a restored size
    /// from a smaller screen, or a DPI where the minimums are raw pixels and the width is not
    /// yet what it will be. Two panels with 160px minimums need 320 plus the splitter before
    /// any fraction is legal at all, and below that every fraction throws.
    /// </para>
    ///
    /// <para>
    /// A splitter left at its default is a cosmetic disappointment; a window that will not
    /// open is not. So this clamps into the legal range and gives up quietly when there is
    /// none.
    /// </para>
    /// </summary>
    /// <summary>
    /// Apply panel minimums, but only once the container is big enough to hold them.
    ///
    /// <para>
    /// Setting these in an object initialiser looks harmless and is not: a SplitContainer
    /// starts at its default 150px, and assigning a minimum that will not fit makes WinForms
    /// move the splitter to compensate and throw out of the property setter — a crash while
    /// building the window, long before anyone sees it. Two 160px minimums cannot both hold
    /// until the container is 326px across, which it is not while it is being constructed.
    /// </para>
    ///
    /// <para>
    /// Call this once the control has a real size. Until then the defaults apply, which are
    /// small enough never to throw.
    /// </para>
    /// </summary>
    public static void SetMinimums(SplitContainer split, int panel1, int panel2)
    {
        int extent = split.Orientation == Orientation.Vertical ? split.Width : split.Height;
        if (extent < panel1 + panel2 + split.SplitterWidth) return;

        split.Panel1MinSize = panel1;
        split.Panel2MinSize = panel2;
    }

    public static void SetFraction(SplitContainer split, double fraction)
    {
        // Vertical orientation means a vertical bar with the panels side by side, so the
        // distance is measured across the width.
        int extent = split.Orientation == Orientation.Vertical ? split.Width : split.Height;

        int low = split.Panel1MinSize;
        int high = extent - split.Panel2MinSize - split.SplitterWidth;
        if (high < low) return;

        split.SplitterDistance = Math.Clamp((int)(extent * fraction), low, high);
    }
}
