using System;
using System.Collections.Generic;

namespace LabEquipmentController;

/// <summary>
/// Guessing the unit of a recorded column, so a plot can label its axis without being told.
///
/// A guess, deliberately: SCPI says what was asked for, not what came back, and a catalogued
/// command is not the only thing that can end up in a results table. Everything here is
/// offered to the user as a starting point and overridden by whatever they type — which is
/// also the escape hatch for the cases this gets wrong.
/// </summary>
public static class MeasurementUnit
{
    /// <summary>
    /// The unit a SCPI measurement command produces, or null when nothing here recognises it.
    ///
    /// Matched on the mnemonic rather than the whole command, because the same measurement is
    /// spelled a dozen ways across vendors — MEASure:VOLTage:DC?, :MEAS:VOLT:DC?, VAL1? — and
    /// what they share is the root.
    /// </summary>
    public static string? ForCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        string c = command.ToUpperInvariant();

        // Ordered by how specific the mnemonic is. VOLT before PER, so a voltage query that
        // mentions an aperture is still a voltage.
        if (Has(c, "FREQ")) return "Hz";
        if (Has(c, "VPP", "VMAX", "VMIN", "VRMS", "VAMP", "VTOP", "VBAS")) return "V";
        if (Has(c, "VOLT", "DIOD")) return "V";
        if (Has(c, "CURR")) return "A";
        if (Has(c, "FRES", "RES", "CONT")) return "Ω";     // ohm
        if (Has(c, "CAP")) return "F";
        if (Has(c, "TEMP")) return "°C";
        if (Has(c, "PER", "WIDT", "RISE", "FALL", "DEL")) return "s";
        if (Has(c, "DUTY")) return "%";
        if (Has(c, "POW")) return "W";

        return null;
    }

    /// <summary>
    /// A unit written into a column heading — "Vout (Vrms)" gives "Vrms". Scripts that declare
    /// COLUMNS usually say the unit there, and what the author wrote beats anything inferred.
    /// </summary>
    public static string? ForColumn(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        int open = name.LastIndexOf('(');
        int close = name.LastIndexOf(')');
        if (open < 0 || close < open + 2) return null;

        string inner = name[(open + 1)..close].Trim();
        return inner.Length is > 0 and <= 8 ? inner : null;
    }

    /// <summary>
    /// The best guess for a table: what the column heading says, else what the commands in a
    /// "Command" column were asking for. Null when neither offers anything.
    /// </summary>
    /// <param name="columnName">Heading of the column being plotted.</param>
    /// <param name="commands">Command text recorded alongside the values, if any.</param>
    public static string? Guess(string? columnName, IEnumerable<string>? commands)
    {
        string? fromName = ForColumn(columnName);
        if (fromName != null) return fromName;

        if (commands == null) return null;

        // The first command that anything recognises. A table whose rows are all the same
        // query is the common case; a mixed one gets the unit of whatever came first, which
        // is as good a default as any and is meant to be corrected by hand.
        foreach (string command in commands)
        {
            string? unit = ForCommand(command);
            if (unit != null) return unit;
        }
        return null;
    }

    private static bool Has(string haystack, params string[] needles)
    {
        foreach (string n in needles)
            if (haystack.Contains(n, StringComparison.Ordinal)) return true;
        return false;
    }
}
