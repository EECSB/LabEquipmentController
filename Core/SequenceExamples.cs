using System;
using System.Collections.Generic;

namespace LabEquipmentController;

/// <summary>
/// A ready-made sequence, and the instruments it was written against.
/// </summary>
/// <param name="Identities">
/// A representative *IDN? for each instrument the script names. Not used at runtime — a
/// sequence resolves its DEVICE lines against whatever is actually connected — but it is
/// what lets the catalog test know which command set each line should be checked against,
/// so a bundled example cannot ship SCPI no instrument accepts (SPEC §10).
/// </param>
public sealed record SequenceExample(string Name, string Script, IReadOnlyList<string> Identities)
{
    /// <summary>
    /// The script, with CRLF line endings.
    ///
    /// These are written as raw string literals, which carry bare '\n'. A WinForms TextBox
    /// treats those as nothing at all and renders the whole sequence as one very long line,
    /// so the normalisation happens here rather than at every place that displays one.
    /// </summary>
    public string Script { get; } = Script.Replace("\r\n", "\n").Replace("\n", "\r\n");
}

/// <summary>
/// The scripts offered in the Multi-Instrument Scripts window's Examples list.
///
/// Every SCPI line here is transcribed from the vendor guide for the instrument it is
/// addressed to, and <c>SequenceExamplesTests</c> checks that against the shipped catalogs.
/// Worth stating because the temptation is strong: a generator's frequency looks like it
/// ought to be "C1:BSWV FREQ", and on a Siglent SDG it is "C1:BSWV FRQ".
/// </summary>
public static class SequenceExamples
{
    private const string SdgIdentity = "Siglent Technologies,SDG2042X,SDG000,1.0";
    private const string SdmIdentity = "Siglent Technologies,SDM3065X,SDM000,1.0";
    private const string ScopeIdentity = "RIGOL TECHNOLOGIES,DS2202,DS2A000,1.0";

    public static IReadOnlyList<SequenceExample> All { get; } = new[]
    {
        // First in the list, and therefore what the window opens with. A swept response
        // across two instruments is the measurement this whole feature exists for, so it is
        // the one to be looking at before you have written anything.
        new SequenceExample(
            "Filter response — generator sweeps, scope reads",
            """
            # Frequency response of a filter, measured point by point.
            #
            # Wire the generator's output to the filter's input, and channel 1 of the
            # oscilloscope across the filter's output. Each step sets a frequency,
            # waits for the filter and scope to settle, then records the pair.

            DEVICE gen : SDG2042X
            DEVICE scope : DS2202
            COLUMNS Frequency (Hz), Vout (Vrms)

            WITH gen
                C1:BSWV WVTP,SINE
                C1:BSWV AMP,2
                # Enable Channel 1 generator output to feed the filter
                C1:OUTP ON
            END

            # Linear sweep across 20 MHz to 35 MHz with 100 kHz resolution
            FOR f = 20M TO 35M STEP 100k
                gen: C1:BSWV FRQ,$f
                DELAY 300
                scope: :MEASure:VRMS? CHANnel1 -> vout
                RECORD $f, $vout
            END

            gen: C1:OUTP OFF
            PRINT Sweep complete. Save CSV to plot the response.
            """,
            new[] { SdgIdentity, ScopeIdentity }),

        new SequenceExample(
            "Filter response — generator sweeps, meter reads",
            """
            # Frequency response of a filter, measured point by point.
            #
            # Wire the generator's output to the filter's input, and the meter across the
            # filter's output. Each step sets a frequency, waits for the filter and the
            # meter to settle, then records the pair. Save CSV afterwards to plot it.

            DEVICE gen : SDG2042X
            DEVICE dmm : SDM3065X
            COLUMNS Frequency (Hz), Vout (Vrms)

            WITH gen
                C1:BSWV WVTP,SINE
                C1:BSWV AMP,2
                C1:OUTP ON
            END

            # Log spacing, because a filter is read per decade — a linear sweep would spend
            # almost every point above 10 kHz and skim over the corner.
            FOR f = 100 TO 100000 POINTS 40 LOG
                gen: C1:BSWV FRQ,$f
                DELAY 300
                dmm: MEASure:VOLTage:AC? -> vout
                RECORD $f, $vout
            END

            gen: C1:OUTP OFF
            PRINT Sweep complete. Save CSV to plot the response.
            """,
            new[] { SdgIdentity, SdmIdentity }),

        new SequenceExample(
            "Filter response — generator sweeps, scope reads Vpp",
            """
            # The same sweep read with an oscilloscope instead of a meter, which gets you
            # amplitude at frequencies past where a bench DMM's AC bandwidth gives up.
            #
            # Generator to the filter input and to scope channel 2; filter output to
            # channel 1. Recording both channels gives gain rather than just output level.

            DEVICE gen   : SDG2042X
            DEVICE scope : DS2202
            COLUMNS Frequency (Hz), Vout pp (V), Vin pp (V)

            WITH gen
                C1:BSWV WVTP,SINE
                C1:BSWV AMP,2
                C1:OUTP ON
            END

            FOR f = 1k TO 1M POINTS 30 LOG
                gen: C1:BSWV FRQ,$f
                DELAY 200
                scope: :AUToscale
                DELAY 800
                scope: :MEASure:VPP? CHANnel1 -> vout
                scope: :MEASure:VPP? CHANnel2 -> vin
                RECORD $f, $vout, $vin
            END

            gen: C1:OUTP OFF
            """,
            new[] { SdgIdentity, ScopeIdentity }),

        new SequenceExample(
            "Amplitude linearity — step the level, read it back",
            """
            # How closely a meter follows the generator across its range. Useful as a check
            # of the pair before trusting either in a real measurement.

            DEVICE gen : SDG2042X
            DEVICE dmm : SDM3065X
            COLUMNS Set (Vpp), Measured (Vrms)

            WITH gen
                C1:BSWV WVTP,SINE
                C1:BSWV FRQ,1000
                C1:OUTP ON
            END

            FOR v = 0.2 TO 4 STEP 0.2
                gen: C1:BSWV AMP,$v
                DELAY 250
                dmm: MEASure:VOLTage:AC? -> reading
                RECORD $v, $reading
            END

            gen: C1:OUTP OFF
            """,
            new[] { SdgIdentity, SdmIdentity }),

        new SequenceExample(
            "Roll call — identify everything the sequence names",
            """
            # The smallest useful sequence: check that each instrument is the one you think
            # it is before running anything that changes its state.

            DEVICE gen : SDG2042X
            DEVICE dmm : SDM3065X

            gen: *IDN?
            dmm: *IDN?
            PRINT Both instruments answered.
            """,
            new[] { SdgIdentity, SdmIdentity }),
    };
}
