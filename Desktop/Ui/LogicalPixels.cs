using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// Paddings and margins in logical pixels, for controls no auto-scale pass will reach.
///
/// A form built inside SuspendLayout has its numbers scaled for it (SPEC §14). Anything built
/// at run time and added afterwards does not: the console, the results pane and its plot, and
/// whatever a window builds after its own pass. Those scale their own numbers, and
/// <c>LogicalToDeviceUnits</c> takes a number or a Size, where a padding is four numbers.
/// </summary>
internal static class LogicalPixels
{
    /// <summary>A padding or margin given in logical pixels, at this control's DPI.</summary>
    public static Padding Scaled(this Control c, int left, int top, int right, int bottom)
        => new(c.LogicalToDeviceUnits(left), c.LogicalToDeviceUnits(top),
               c.LogicalToDeviceUnits(right), c.LogicalToDeviceUnits(bottom));

    /// <summary>The same on all four sides.</summary>
    public static Padding Scaled(this Control c, int all) => c.Scaled(all, all, all, all);

    /// <summary>A padding or margin held in logical pixels, at this control's DPI.</summary>
    public static Padding Scaled(this Control c, Padding logical)
        => c.Scaled(logical.Left, logical.Top, logical.Right, logical.Bottom);
}
