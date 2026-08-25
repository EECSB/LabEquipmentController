namespace LabEquipmentController.Web.Client.Shared;

/// <summary>
/// The tools a console can open in a window of its own — the desktop's <c>ScriptForm</c>,
/// <c>CommandReferenceForm</c>, <c>ScreenCaptureForm</c>, <c>WaveformForm</c>,
/// <c>MultimeterReadoutForm</c> and <c>DatasheetExtractForm</c>.
/// </summary>
/// <remarks>
/// Public and out here rather than private to the console, because the console has the buttons
/// and the page has the windows. See <see cref="ConsoleTools"/> for why they are not in the
/// same place.
/// </remarks>
public enum ConsoleTool
{
    Scripts,
    Catalog,
    Screen,
    Waveform,
    Meter,
    Ai,
}
