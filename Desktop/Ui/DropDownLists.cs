using System;
using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// A dropdown's list as wide as its longest entry.
///
/// A ComboBox opens its list at its own width and cuts whatever is longer, and nothing in
/// WinForms widens it. Until 2026-11-08 the script editors' Examples lists and the network
/// Interface list both cut their longest entries (SPEC §14).
/// </summary>
internal static class DropDownLists
{
    /// <summary>
    /// Keep <paramref name="box"/>'s list as wide as its widest entry: now, and again whenever
    /// the box changes size or font, or opens, since its entries may have changed in between.
    /// </summary>
    public static void FitToEntries(ComboBox box)
    {
        box.DropDown += (_, _) => Fit(box);
        box.SizeChanged += (_, _) => Fit(box);
        box.FontChanged += (_, _) => Fit(box);
        Fit(box);
    }

    /// <summary>
    /// The width that shows <paramref name="box"/>'s widest entry whole: its text, the list's
    /// own scrollbar, and the inset either side of the text.
    /// </summary>
    public static int WidestEntry(ComboBox box)
    {
        int widest = 0;
        foreach (object item in box.Items)
            widest = Math.Max(widest, TextRenderer.MeasureText(box.GetItemText(item), box.Font).Width);

        return widest + Math.Max(SystemInformation.VerticalScrollBarWidth, box.LogicalToDeviceUnits(17))
                      + box.LogicalToDeviceUnits(8);
    }

    private static void Fit(ComboBox box)
    {
        // Never narrower than the box, and never wider than the screen it opens on.
        int want = Math.Max(box.Width, WidestEntry(box));
        want = Math.Min(want, Screen.FromControl(box).WorkingArea.Width);
        if (want > 0 && box.DropDownWidth != want) box.DropDownWidth = want;
    }
}
