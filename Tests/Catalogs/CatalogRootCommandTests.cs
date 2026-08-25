using System;
using System.Collections.Generic;
using System.Linq;
using LabEquipmentController;

namespace LabEquipmentController.Tests;

/// <summary>
/// A guide prints a command and then a table of the values its parameter accepts, one value
/// per row with a sentence explaining each. A line-based extractor reads those rows as
/// commands, and the catalog gains entries like
///
///     TRIGger   "Selected channel external trigger signal is used as the digital output source."
///     RUN       "A trigger event starts the whole arbitrary sequences (with all repetitions)."
///
/// which are not commands at all — they are the values of <c>DIO:OUTPut:SOURce</c> and
/// <c>ARBitrary:TRIGgered:MODE</c>, wearing their own glosses. Eleven shipped in the R&amp;S
/// power-supply catalog, twenty in the R&amp;S scope, six in the FSIQ, and every existing guard
/// passed them: <see cref="CatalogCoverageTests.No_catalog_command_is_a_truncated_line"/> sees
/// a balanced, well-spelled header, and <see cref="CatalogWrappedLineTests"/> looks for a
/// description repeated word for word from a longer entry — but a value's gloss is its own
/// text, shared with nothing.
///
/// What gives them away is the missing colon. So every family names the colon-free commands
/// its guide actually heads, and anything else without one is a value that got promoted.
///
/// The list is not a formality. Vendors differ completely here: Tektronix heads fifty-one
/// commands at the root, Siglent's generator twenty-one — <c>COUP</c>, <c>STL</c>,
/// <c>Comm_HeaDeR</c> — while R&amp;S writes a strict tree in which only <c>ABORt</c> stands
/// alone. Guessing a rule instead of reading the guides would trade real junk for dozens of
/// false failures.
///
/// Every name below was checked against its guide: found as a heading with a block of its
/// own, not as a row inside a list of alternatives. The Fluke catalog is the one exception
/// and is marked where it appears.
///
/// The three families added first were each added *after* junk shipped in them. The rest were
/// added by reading the guides in one pass, which is the order this should have gone in.
/// </summary>
public class CatalogRootCommandTests
{
    /// <summary>
    /// Families whose guide has been read, each with the colon-free commands it heads.
    /// A family here may carry no other bare mnemonic; a family absent from it is unchecked.
    /// </summary>
    public static TheoryData<InstrumentFamily, string[]> RootCommands()
    {
        var data = new TheoryData<InstrumentFamily, string[]>();
        foreach (var (family, roots) in Table) data.Add(family, roots);
        return data;
    }

    private static readonly (InstrumentFamily Family, string[] Roots)[] Table =
    {
        // Tektronix heads more commands at the root than any other vendor here — the whole
        // status, waveform-transfer and front-panel surface is colon-free.
        (InstrumentFamily.TektronixScope, new[]
            { "ACQuire", "ALIas", "ALLEv", "AUTOSet", "AUXin", "BUS", "BUSY", "CH<x>",
              "CLEARMenu", "CURSor", "CURVe", "DATE", "DATa", "DESE", "DESkew", "DISplay",
              "EVENT", "EVMsg", "EVQty", "FACtory", "FILESystem", "HARDCopy", "HEADer",
              "HIStogram", "HORizontal", "ID", "LANGuage", "LOCk", "MARK", "MATHVAR", "MATH[1]",
              "MEASUrement", "MESSage", "NEWpass", "PAUSe", "REF<x>", "REM", "SEARCH", "SELect",
              "SET", "TEKSecure", "TIMe", "TOTaluptime", "TRIGger", "UNLock", "USBTMC",
              "VERBose", "WAVFrm", "WFMInpre", "WFMOutpre", "ZOOm" }),

        // The SDG guide writes its own shorthand — PACP beside ParaCoPy, CHDR beside
        // Comm_HeaDeR — and heads both spellings, so the catalog carries both.
        (InstrumentFamily.SiglentGenerator, new[]
            { "BUZZ", "CASCADE", "COUP", "CURRPRT", "Comm_HeaDeR", "EQPHASE", "FCNT", "KEY",
              "LAGG", "MODE", "NBFM", "OUT_BOTHCH", "PACP", "ParaCoPy", "ROSC", "SCFG", "SCSV",
              "STL", "VKEY", "VOLTPRT", "VOLTSTAT" }),

        // The 62000L heads several commands in both the long and abbreviated spelling.
        (InstrumentFamily.ChromaPowerSupply, new[]
            { "APPLy", "CURRent", "DISP", "DISPLay", "DISPlay", "OUTP", "OUTPut", "VOLTage" }),

        // The one family whose guide is not in this checkout — the 8845A manual is linked in
        // SPEC §10 but not kept here, and this catalog was transcribed by hand rather than
        // extracted. These nine are the standard SCPI measurement verbs a DMM heads at the
        // root, and they are pinned so the shape cannot drift, not because a guide was read.
        (InstrumentFamily.FlukeMultimeter, new[]
            { "ABORt", "CONFigure", "DISPlay", "FETCh", "FETCh2", "INITiate", "R", "READ" }),

        (InstrumentFamily.Multimeter, new[] { "ABORt", "CONFigure", "FETCh", "R", "READ" }),
        (InstrumentFamily.KeysightMultimeter, new[] { "CONFigure", "FETCh", "R", "READ" }),
        (InstrumentFamily.KeysightPowerSupply, new[] { "ABORt", "APPLy", "BUS" }),
        (InstrumentFamily.BkPowerSupply9130, new[] { "CHANnel" }),
        (InstrumentFamily.ChromaElectronicLoad, new[] { "MODE" }),
        (InstrumentFamily.ChromaModularLoad, new[] { "MODE" }),
        (InstrumentFamily.ElectronicLoad, new[] { "DHCP" }),

        // NGL200/NGM200, NGE100 and HMP: READ? and APPLy? are the only colon-free commands
        // in any of the three manuals.
        (InstrumentFamily.RohdePowerSupply, new[] { "READ", "APPLy" }),

        // RTB2000: the acquisition-control verbs, and only those. The guide heads each in
        // its own right, and spells RUNContinous a letter short of Continuous, which is
        // transcribed as printed. Everything else that reached this catalog without a colon
        // was a value out of a list: TIMeout beside OFF, COUNter beside SQUarewave,
        // EVENt beside CONDition.
        (InstrumentFamily.RohdeScope, new[]
            { "AUToscale", "RUN", "RUNContinous", "RUNSingle", "SINGle", "STOP" }),

        // Every R&S analyzer writes a strict tree: ABORt is the only command outside it.
        (InstrumentFamily.RohdeFsiqAnalyzer, new[] { "ABORt" }),
        (InstrumentFamily.RohdeFslAnalyzer, new[] { "ABORt" }),
        (InstrumentFamily.RohdeFspAnalyzer, new[] { "ABORt" }),
        (InstrumentFamily.RohdeFsqAnalyzer, new[] { "ABORt" }),
        (InstrumentFamily.RohdeFsuAnalyzer, new[] { "ABORt" }),
        (InstrumentFamily.RohdeFswAnalyzer, new[] { "ABORt" }),
        (InstrumentFamily.RohdeSpectrumAnalyzer, new[] { "ABORt" }),

        // Every remaining family, with nothing allowed. These catalogs have no colon-free
        // entry today, and an empty list says so rather than leaving them unchecked — a
        // family missing from this table is a family where the next promoted value would
        // ship unnoticed, which is exactly how the first three got theirs.
        (InstrumentFamily.BkElectronicLoad, Array.Empty<string>()),
        (InstrumentFamily.BkPowerSupply, Array.Empty<string>()),
        (InstrumentFamily.GwInstekScope, Array.Empty<string>()),
        (InstrumentFamily.GwInstekScopeB, Array.Empty<string>()),
        (InstrumentFamily.KeithleyDmm, Array.Empty<string>()),
        (InstrumentFamily.KeithleySmu, Array.Empty<string>()),
        (InstrumentFamily.KeysightScope, Array.Empty<string>()),
        (InstrumentFamily.Oscilloscope, Array.Empty<string>()),
        (InstrumentFamily.PowerSupply, Array.Empty<string>()),
        (InstrumentFamily.RigolElectronicLoad, Array.Empty<string>()),
        (InstrumentFamily.RigolMultimeter, Array.Empty<string>()),
        (InstrumentFamily.RigolSpectrumAnalyzer, Array.Empty<string>()),
        (InstrumentFamily.RohdeFsvAnalyzer, Array.Empty<string>()),
        (InstrumentFamily.ScpiGenerator, Array.Empty<string>()),
        (InstrumentFamily.SiglentScope, Array.Empty<string>()),
        (InstrumentFamily.SpectrumAnalyzer, Array.Empty<string>()),
    };

    /// <summary>
    /// The table covers every catalogued family. Absence is what let the first three ship
    /// their junk, so absence is what this refuses to allow.
    /// </summary>
    [Fact]
    public void Every_catalogued_family_is_listed()
    {
        var listed = Table.Select(t => t.Family).ToHashSet();
        var missing = Enum.GetValues<InstrumentFamily>()
            .Where(f => CommandReference.ForFamily(f)?.Commands.Count > 0)
            .Where(f => !listed.Contains(f))
            .ToList();

        Assert.True(missing.Count == 0,
            "families with a catalog but no entry in RootCommands — their colon-free entries "
            + "are checked by nothing: " + string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(RootCommands))]
    public void No_entry_is_a_bare_mnemonic(InstrumentFamily family, string[] roots)
    {
        IReadOnlyList<CommandRef> commands = CommandReference.ForFamily(family)!.Commands;

        var strays = commands
            .Select(c => c.Syntax)
            .Where(s => !s.StartsWith('*'))
            .Where(s => !Header(s).Contains(':'))
            .Where(s => !roots.Contains(Header(s).TrimEnd('?'), StringComparer.Ordinal))
            .ToList();

        Assert.True(strays.Count == 0,
            $"{family}: colon-free entries that are probably parameter values rather than "
            + $"commands: {string.Join(" | ", strays)}");
    }

    /// <summary>
    /// A name listed for a family that no longer has it. Left alone, the list would keep
    /// vouching for commands that had been removed — and the point of it is that it says
    /// what the guide documents, not what the catalog happens to contain.
    /// </summary>
    [Theory]
    [MemberData(nameof(RootCommands))]
    public void Every_named_root_command_is_still_in_the_catalog(InstrumentFamily family, string[] roots)
    {
        var present = CommandReference.ForFamily(family)!.Commands
            .Select(c => Header(c.Syntax).TrimEnd('?'))
            .ToHashSet(StringComparer.Ordinal);

        var absent = roots.Where(r => !present.Contains(r)).ToList();

        Assert.True(absent.Count == 0,
            $"{family}: named here but no longer in the catalog: {string.Join(", ", absent)}");
    }

    /// <summary>The command header — everything before the first space.</summary>
    private static string Header(string syntax) => syntax.Split(' ')[0];
}
