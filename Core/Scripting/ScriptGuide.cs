using System.Collections.Generic;

namespace LabEquipmentController;

/// <summary>One section of the guide: a heading, a paragraph, and a script that shows it.</summary>
public sealed record ScriptGuideSection(string Heading, string Prose, string Example);

/// <summary>
/// The whole script language explained, in one place both front ends read.
///
/// The text used to live inside the WinForms <c>ScriptReferenceForm</c>, which was fine while
/// that form was the only thing showing it. It is not: the web build shows the same guide, and
/// a second copy of a page of prose is a second copy that drifts. Whoever edits a paragraph
/// here edits it for both.
///
/// The examples are real commands for real instruments, not placeholders. That is the point —
/// a reference that says "&lt;command&gt;" teaches the shape and nothing else, and the shape was
/// never the hard part.
///
/// What is <em>not</em> here is how a particular editor behaves — Tab expanding a snippet,
/// Ctrl+Space, F5. That belongs to the editor describing it, and the two editors differ.
/// </summary>
public static class ScriptGuide
{
    /// <summary>What the guide is called, which depends on which language it is about.</summary>
    public static string Title(bool isSequence)
        => isSequence ? "Multi-instrument scripts" : "Instrument scripts";

    /// <summary>The paragraph under the title.</summary>
    public static string Lead(bool isSequence)
        => isSequence
            ? "This language is this application's own. The commands inside it are SCPI, which "
            + "is a real standard; everything around them was invented here. Every example below "
            + "is a real command for a real instrument."
            : "This language is this application's own. The lines that are not one of the words "
            + "below are sent to the instrument as they stand, and a line containing '?' is a "
            + "query whose reply is shown.";

    /// <summary>
    /// Every section, in reading order. The multi-instrument language is the single-instrument
    /// one plus the words that address, sweep and record, so it says those first and then the
    /// shared ones.
    /// </summary>
    public static IReadOnlyList<ScriptGuideSection> Sections(bool isSequence)
    {
        var common = new List<ScriptGuideSection>
        {
            new("Comments",
                "A '#' or a '//' begins one. Worth writing: a script that turns on an output "
              + "is read by someone deciding whether it is safe to run.",
                "# Frequency response of the input filter.\n"
              + "// Both forms work.\n"),

            new("Waiting",
                "DELAY pauses for a number of milliseconds. Instruments settle, and a reading "
              + "taken before they have is a reading of the previous state. WAIT is the same "
              + "word.",
                "DELAY 300\n"),

            new("Messages",
                "PRINT writes a line into the output pane, which is how a long run says where "
              + "it has got to. ECHO and LOG are the same word.",
                "PRINT Sweep complete. Save CSV to plot the response.\n"),

            new("Repeating",
                "REPEAT runs the block up to its END a fixed number of times, and may be "
              + "nested.",
                "REPEAT 3\n    *IDN?\n    DELAY 500\nEND\n"),
        };

        if (!isSequence)
        {
            common.Insert(0, new ScriptGuideSection(
                "Commands",
                "Any line that is not one of the words below is sent to the instrument exactly "
              + "as written. A line containing '?' is a query, and its reply appears in the "
              + "output pane.",
                "*IDN?\nC1:BSWV WVTP,SINE\nC1:OUTP ON\n"));

            common.Add(new ScriptGuideSection(
                "Putting it together",
                "Set something, let it settle, then measure it — the shape almost every script "
              + "takes.",
                "PRINT Configuring channel 1...\n"
              + "C1:BSWV WVTP,SINE\n"
              + "C1:BSWV FRQ,1000\n"
              + "C1:BSWV AMP,2\n"
              + "# Enable the output only after the settings are in\n"
              + "C1:OUTP ON\n"
              + "DELAY 500\n"
              + "C1:BSWV?\n"));

            return common;
        }

        var sequence = new List<ScriptGuideSection>
        {
            new("Naming the instruments",
                "DEVICE gives an instrument a short name and says which model it is. The model "
              + "is matched against whatever is connected, so a saved script still finds its "
              + "instruments after DHCP has moved them. Two of one model are not guessed "
              + "between: name one by the serial number its *IDN? reports, and the other line "
              + "finds the one that is left.",
                "DEVICE gen : SDG2042X\nDEVICE scope : DS2202\n"
              + "\n"
              + "# two meters of one model\n"
              + "DEVICE left : SDM36HCD801207\n"
              + "DEVICE right : SDM3065X\n"),

            new("Values from outside",
                "INPUT declares a value the script is given when it is run, and $name uses it "
              + "anywhere below. A kind after the colon says what may be given — number, "
              + "integer or text — with the unit after it, a default after '=', and a range "
              + "in brackets. An input with no default has to be given one, and nothing is sent "
              + "to any instrument until every value has been checked. It is what makes one "
              + "script two runs rather than two scripts.",
                "INPUT vset : number V = 5 (0 TO 30)\n"
              + "INPUT serial : text\n"
              + "\n"
              + "PRINT Testing $serial at $vset V\n"
              + "psu: VOLT $vset\n"),

            new("Addressing a line",
                "A command has to say which instrument it is for — by prefix, or by sitting "
              + "inside a WITH block. A line that does not is an error, not a default: guessing "
              + "which instrument to drive is not something this app does.",
                "gen: C1:OUTP ON\n"
              + "\n"
              + "WITH gen\n"
              + "    C1:BSWV WVTP,SINE\n"
              + "    C1:BSWV AMP,2\n"
              + "END\n"),

            new("Sweeping",
                "FOR steps a value and runs its block at each one. STEP gives an even spacing; "
              + "POINTS … LOG spaces them per decade, which is how a filter or a frequency "
              + "response is actually measured — a linear sweep from 100 Hz to 100 kHz spends "
              + "almost every point above 10 kHz and skims over the corner. Numbers may carry "
              + "an engineering suffix: 1k, 2.5M, 100m.",
                "FOR f = 20M TO 35M STEP 100k\n    gen: C1:BSWV FRQ,$f\nEND\n"
              + "\n"
              + "FOR f = 100 TO 100k POINTS 40 LOG\n    gen: C1:BSWV FRQ,$f\nEND\n"),

            new("Keeping a reading",
                "'-> name' captures a query's reply, and $name uses it further down. RECORD "
              + "appends a row to the results table; COLUMNS names those columns and belongs "
              + "near the top.",
                "COLUMNS Frequency (Hz), Vout (Vrms)\n"
              + "\n"
              + "scope: :MEASure:VRMS? CHANnel1 -> vout\n"
              + "RECORD $f, $vout\n"),
        };

        sequence.Add(new ScriptGuideSection(
            "Leaving the bench safe",
            "FINALLY runs its block at the end of the run, however the run ended — finished, "
          + "failed, timed out, or stopped by the Stop button. It is where the outputs go off. "
          + "Without it a generator set to 10 V by a run that died on the next line stays at "
          + "10 V until somebody walks over to it. ALWAYS is the same word. TIMEOUT gives the "
          + "whole run a deadline, which is not the same as the one each command already has: "
          + "an instrument that answers slowly forever, or a DELAY written with three noughts "
          + "too many, ends the run rather than holding the bench. The unit is required.",
            "TIMEOUT 5m\n"
          + "\n"
          + "FINALLY\n"
          + "    gen: C1:OUTP OFF\n"
          + "    psu: OUTPut CH1,OFF\n"
          + "END\n"));

        sequence.AddRange(common);

        sequence.Add(new ScriptGuideSection(
            "A whole measurement",
            "Declare the instruments, name the columns, sweep, record — then save the table as "
          + "CSV, or read the shape of it on the Plot tab.",
            "DEVICE gen : SDG2042X\n"
          + "DEVICE scope : DS2202\n"
          + "COLUMNS Frequency (Hz), Vout (Vrms)\n"
          + "\n"
          + "WITH gen\n"
          + "    C1:BSWV WVTP,SINE\n"
          + "    C1:BSWV AMP,2\n"
          + "    # Enable the output only after the settings are in\n"
          + "    C1:OUTP ON\n"
          + "END\n"
          + "\n"
          + "FOR f = 100 TO 100k POINTS 40 LOG\n"
          + "    gen: C1:BSWV FRQ,$f\n"
          + "    # Let the circuit and the scope settle\n"
          + "    DELAY 300\n"
          + "    scope: :MEASure:VRMS? CHANnel1 -> vout\n"
          + "    RECORD $f, $vout\n"
          + "END\n"
          + "\n"
          + "gen: C1:OUTP OFF\n"
          + "PRINT Sweep complete.\n"));

        return sequence;
    }
}
