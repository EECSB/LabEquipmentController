using System;
using System.Linq;
using LabEquipmentController;

namespace LabEquipmentController.Tests;

/// <summary>
/// Vendor guides have typos, and SPEC §10 offers no comfortable way to handle one: a
/// dropped letter transcribed faithfully is a command the instrument rejects, and a dropped
/// letter corrected is SCPI nobody documented. The rule these pin down is that the catalog
/// does the first and says so — <see cref="CommandRef.Syntax"/> is what the guide prints,
/// <see cref="CommandRef.GuideMisprint"/> is the doubt and the likely correction.
/// </summary>
public class GuideMisprintTests
{
    private static CommandReference Bk9130() =>
        CommandReference.ForFamily(InstrumentFamily.BkPowerSupply9130)!;

    [Theory]
    [InlineData("B&K Precision, 9130B, 123456, V1.06-V1.04")]
    [InlineData("B&K PRECISION,9131B,123456,1.0")]
    [InlineData("B&K Precision,9132B,000001,V1.00")]
    public void The_triple_output_supplies_reach_their_own_catalog(string identity)
    {
        Assert.Equal(InstrumentFamily.BkPowerSupply9130, InstrumentProfile.FamilyForIdentity(identity));
    }

    [Fact]
    public void The_9200B_series_is_left_where_it_was()
    {
        // Two B&K supply lines, two guides, two catalogs. A 9200B must not start being
        // offered the 9130B's channel-selection commands, which it has no equivalent of.
        Assert.Equal(InstrumentFamily.BkPowerSupply,
                     InstrumentProfile.FamilyForIdentity("B&K PRECISION,9201B,123456,1.0"));
    }

    [Fact]
    public void The_dropped_letters_are_transcribed_as_printed()
    {
        var syntax = Bk9130().Commands.Select(c => c.Syntax).ToList();

        // The guide prints these. They are almost certainly wrong, and they are what it prints.
        Assert.Contains("STATus:QUEStionable:INSTument:ENABle?", syntax);
        Assert.Contains("STATus:OPERation:INSTrument:ISUMmay1:ENABle?", syntax);
        Assert.Contains("[SOURce:]VOLTage:PROTection:TRIPed?", syntax);
    }

    [Fact]
    public void The_correction_is_not_quietly_added_alongside_the_misprint()
    {
        // The tempting fix — ship both spellings and let one work — invents a command the
        // guide never printed, which is the half of SPEC §10 that is easy to talk yourself
        // out of. The correction belongs in the flag, where it is labelled as a guess.
        var syntax = Bk9130().Commands.Select(c => c.Syntax).ToList();

        Assert.DoesNotContain("STATus:QUEStionable:INSTrument:ENABle?", syntax);
        Assert.DoesNotContain("STATus:OPERation:INSTrument:ISUMmary1:ENABle?", syntax);
        Assert.DoesNotContain("[SOURce:]VOLTage:PROTection:TRIPped?", syntax);
    }

    [Fact]
    public void Every_misprint_is_flagged_where_the_user_will_see_it()
    {
        var unflagged = Bk9130().Commands
            .Where(c => c.Syntax.Contains("INSTument")
                     || c.Syntax.Contains("ISUMmay")
                     || c.Syntax.Contains("TRIPed"))
            .Where(c => c.GuideMisprint == null)
            .Select(c => c.Syntax)
            .ToList();

        Assert.True(unflagged.Count == 0,
            "misprinted entries with no flag: " + string.Join(" | ", unflagged));
    }

    [Fact]
    public void A_flag_says_something_useful()
    {
        // A flag that only says "this looks wrong" leaves the user no better off. Each one
        // has to carry enough to act on: what is printed, and what to try instead.
        var useless = Bk9130().Commands
            .Where(c => c.GuideMisprint != null && c.GuideMisprint.Trim().Length < 40)
            .Select(c => c.Syntax)
            .ToList();

        Assert.True(useless.Count == 0,
            "flags too short to act on: " + string.Join(" | ", useless));
    }

    [Fact]
    public void The_catalog_says_it_is_unverified()
    {
        // No 9130B has ever been on this bench, so nothing in here may claim otherwise.
        Assert.DoesNotContain(Bk9130().Commands, c => c.BenchVerified);
        Assert.DoesNotContain(Bk9130().Commands, c => c.AiExtracted);
    }

    private static CommandReference Bk9200() =>
        CommandReference.ForFamily(InstrumentFamily.BkPowerSupply)!;

    /// <summary>
    /// The 9200B guide writes each command's heading in full and its "Command syntax" line
    /// abbreviated. Transcribing the abbreviation makes the entry useless for matching:
    /// <see cref="ScpiSyntax"/> derives the short form from the template, so a template that
    /// is already short admits nothing else, and the spelling the guide's own heading uses
    /// stops being recognised. Five entries were like this.
    /// </summary>
    [Theory]
    [InlineData("SYSTem:ERRor?")]
    [InlineData("SYST:ERR?")]
    [InlineData("SYSTem:VERSion?")]
    [InlineData("SYSTem:REMote")]
    [InlineData("SYSTem:LOCal")]
    [InlineData("TRIGger:SOURce BUS")]
    [InlineData("TRIG:SOUR BUS")]
    public void Both_spellings_of_a_9200B_command_are_recognised(string command)
    {
        var templates = Bk9200().Commands.Select(c => c.Syntax).ToList();
        Assert.True(ScpiSyntax.MatchesAny(command, templates), command + " is not in the catalog");
    }

    [Theory]
    [InlineData("[SOURce:]CURRent[:LEVel][:IMMediate]:STEP[:INCRement]?")]
    [InlineData("[SOURce:]VOLTage[:LEVel][:IMMediate]:STEP[:INCRement]?")]
    [InlineData("[SOURce:]VOLTage[:LEVel]:TRIGgered[:AMPLitude] <NRf>")]
    [InlineData("[SOURce:]VOLTage[:LEVel]:TRIGgered[:AMPLitude]?")]
    [InlineData("STATus:OPERation[:EVENt]?")]
    public void The_9200B_guide_documents_these_and_so_does_the_catalog(string syntax)
    {
        // Each was documented in the manual and absent from the first transcription.
        Assert.Contains(syntax, Bk9200().Commands.Select(c => c.Syntax));
    }

    /// <summary>
    /// The first 9200B transcription paired eleven entries with a neighbour's description —
    /// VOLTage:LIMIt explained as a query of the OVP state, VOLTage:LEVel as the current
    /// protection state, APPLy as OVP again. The catalog still loaded and the library still
    /// listed them, which is what makes this worth a test: the failure is silent.
    /// </summary>
    [Fact]
    public void No_9200B_entry_wears_another_commands_description()
    {
        // "the OVP circuit", not bare "OVP": *SAV legitimately lists OVP among the settings
        // it stores, and a check that flags that is a check nobody will keep.
        var strays = Bk9200().Commands
            .Where(c => c.Description.Contains("OVP circuit", StringComparison.OrdinalIgnoreCase))
            .Where(c => !c.Syntax.Contains("PROTection", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Syntax)
            .ToList();

        Assert.True(strays.Count == 0,
            "entries describing the OVP circuit that are not protection commands: "
            + string.Join(" | ", strays));
    }

    [Theory]
    [InlineData("[SOURce:]VOLTage:LIMIt[:LEVel] <NRf>", "limit")]
    [InlineData("[SOURce:]VOLTage[:LEVel][:IMMediate][:AMPLitude] <NRf>", "output voltage")]
    [InlineData("[SOURce:]CURRent[:LEVel]:TRIGgered[:AMPLitude] <NRf>", "trigger")]
    [InlineData("[SOURce:]VOLTage:PROTection:CLEar", "Clears")]
    [InlineData("[SOURce:]APPLy <NRf>,<NRf>", "voltage and current")]
    public void A_9200B_description_is_about_its_own_command(string syntax, string expected)
    {
        CommandRef entry = Bk9200().Commands.Single(c => c.Syntax == syntax);
        Assert.Contains(expected, entry.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(CatalogCoverageTests.CataloguedFamilies), MemberType = typeof(CatalogCoverageTests))]
    public void No_catalog_flags_an_entry_without_explaining_it(InstrumentFamily family)
    {
        var empty = CommandReference.ForFamily(family)!.Commands
            .Where(c => c.GuideMisprint != null && string.IsNullOrWhiteSpace(c.GuideMisprint))
            .Select(c => c.Syntax)
            .ToList();

        Assert.True(empty.Count == 0,
            $"{family}: entries flagged with an empty note: {string.Join(" | ", empty)}");
    }
}
