using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// Pins SPEC §10 — "never invent SCPI" — to something a test can check.
///
/// Every command the app puts in front of a user, whether as a quick-command button
/// or a line in a bundled script, must be an instance of a syntax template documented
/// in that family's catalog, and each catalog is transcribed from a vendor programming
/// guide. Adding a button with a plausible-looking but undocumented command now fails
/// the build rather than surfacing on someone's bench as -113 "undefined header".
/// </summary>
public class CatalogCoverageTests
{
    /// <summary>The families that ship a curated catalog, and so can be checked.</summary>
    private static readonly InstrumentFamily[] Catalogued =
    {
        InstrumentFamily.Oscilloscope,
        InstrumentFamily.SiglentGenerator,
        InstrumentFamily.ScpiGenerator,
        InstrumentFamily.Multimeter,
        InstrumentFamily.PowerSupply,
        InstrumentFamily.ElectronicLoad,
        InstrumentFamily.SpectrumAnalyzer,
        InstrumentFamily.TektronixScope,
        InstrumentFamily.KeysightScope,
        InstrumentFamily.KeysightPowerSupply,
        InstrumentFamily.KeithleySmu,
        InstrumentFamily.KeithleyDmm,
        InstrumentFamily.RohdeScope,
        InstrumentFamily.RohdePowerSupply,
        InstrumentFamily.SiglentScope,
        InstrumentFamily.FlukeMultimeter,
        InstrumentFamily.GwInstekScope,
        InstrumentFamily.ChromaPowerSupply,
        InstrumentFamily.BkElectronicLoad,
        InstrumentFamily.RohdeSpectrumAnalyzer,
        InstrumentFamily.ChromaElectronicLoad,
        InstrumentFamily.RohdeFslAnalyzer,
        InstrumentFamily.RigolMultimeter,
        InstrumentFamily.RigolElectronicLoad,
        InstrumentFamily.RigolSpectrumAnalyzer,
        InstrumentFamily.RohdeFsvAnalyzer,
        InstrumentFamily.KeysightMultimeter,
        InstrumentFamily.GwInstekScopeB,
        InstrumentFamily.ChromaModularLoad,
        InstrumentFamily.BkPowerSupply,
        InstrumentFamily.BkPowerSupply9130,
        InstrumentFamily.RohdeFswAnalyzer,
        InstrumentFamily.RohdeFsuAnalyzer,
        InstrumentFamily.RohdeFspAnalyzer,
        InstrumentFamily.RohdeFsqAnalyzer,
    };

    public static TheoryData<InstrumentFamily> CataloguedFamilies()
    {
        var data = new TheoryData<InstrumentFamily>();
        foreach (InstrumentFamily f in Catalogued) data.Add(f);
        return data;
    }

    /// <summary>
    /// The list above is every family that ships a catalog — enforced, because it is
    /// hand-maintained and nothing else notices when it falls behind. The FSW landed with
    /// 2,309 entries, the largest catalog here, and sat outside this list: every guard in
    /// this file ran for thirty-one catalogs, reported green, and had never read a line of
    /// the thirty-second. A green run that skips a catalog looks identical to one that
    /// checked it, which is why the list itself has to be under test.
    /// </summary>
    [Fact]
    public void The_catalogued_family_list_is_exactly_the_families_with_catalogs()
    {
        var listed = Catalogued.ToHashSet();

        var actual = Enum.GetValues<InstrumentFamily>()
            .Where(f => CommandReference.ForFamily(f)?.Commands.Count > 0)
            .ToHashSet();

        var missing = actual.Except(listed).ToList();
        Assert.True(missing.Count == 0,
            "families with a catalog but absent from CataloguedFamilies — their catalogs "
            + $"are checked by nothing in this file: {string.Join(", ", missing)}");

        var phantom = listed.Except(actual).ToList();
        Assert.True(phantom.Count == 0,
            $"families listed here but shipping no catalog: {string.Join(", ", phantom)}");
    }

    [Theory]
    [MemberData(nameof(CataloguedFamilies))]
    public void Every_catalogued_family_loads(InstrumentFamily family)
    {
        CommandReference? r = CommandReference.ForFamily(family);
        Assert.NotNull(r);
        Assert.NotEmpty(r!.Commands);
        Assert.False(string.IsNullOrWhiteSpace(r.Instrument));
        Assert.False(string.IsNullOrWhiteSpace(r.Source));
    }

    /// <summary>
    /// Catalogs are transcribed in bulk from PDFs, and a PDF's idea of a line is not the
    /// author's. A long command wraps, and the tail — "EF}" left over from "…|MIN|MAX|DEF}" —
    /// lands on a line of its own where a parser will read it as the next command or as the
    /// description. Both failures are quiet: the catalog still loads, the library still lists
    /// the entry, and it is simply wrong.
    ///
    /// Deliberately weaker than <see cref="ScpiSyntax.IsValidTemplate"/>, which gates AI
    /// output and can afford to over-reject. Real catalogs carry plenty it refuses —
    /// "C&lt;n&gt;:BSWV", ":CURSor:X2", "[SENSe:]VOLTage:{AC|DC}:RANGe" — so this checks only
    /// what is a defect under any vendor's notation.
    /// </summary>
    [Theory]
    [MemberData(nameof(CataloguedFamilies))]
    public void No_catalog_command_is_a_truncated_line(InstrumentFamily family)
    {
        CommandReference reference = CommandReference.ForFamily(family)!;

        var malformed = reference.Commands
            .Select(c => c.Syntax)
            .Where(s => !IsWellFormed(s))
            .ToList();

        Assert.True(malformed.Count == 0,
            $"{family}: malformed catalog entries: {string.Join(" | ", malformed)}");
    }

    /// <summary>
    /// One entry is one command, and ';' is what separates two of them.
    ///
    /// The Tektronix manual prints a parameter range on the same line as the syntax —
    /// <c>POWer:REFLevel:ABSolute:HIGH &lt;NR3&gt;; Ranges={D,-1e6,+1E6}</c> — so an
    /// extraction that takes the line whole welds the annotation onto the command. Seven
    /// entries shipped that way until August 2026. It is invisible to
    /// <see cref="IsWellFormed"/>, whose delimiters all balance, and invisible to a sweep
    /// that asks whether the string is in the guide, because it is: the guide wrote it.
    ///
    /// What it is not is sendable. An instrument reads the tail as a second command and
    /// answers with an error, and anyone who copies the entry out of the library gets that
    /// error. The range is worth keeping — in the description, where it cannot be sent.
    /// </summary>
    [Theory]
    [MemberData(nameof(CataloguedFamilies))]
    public void No_catalog_entry_carries_a_second_command(InstrumentFamily family)
    {
        CommandReference reference = CommandReference.ForFamily(family)!;

        var joined = reference.Commands
            .Select(c => c.Syntax)
            .Where(s => s.Contains(';'))
            .ToList();

        Assert.True(joined.Count == 0,
            $"{family}: syntax carrying a ';' and so more than one command: "
            + string.Join(" | ", joined));
    }

    /// <summary>
    /// Is the description a description, or is it whatever the page happened to hold?
    ///
    /// Every catalog is built by pulling text out of a PDF, and a PDF has no idea which of
    /// its words belong to the command and which belong to the page. Four separate repairs in
    /// August 2026 were all this same mistake wearing different clothes:
    ///
    /// <list type="bullet">
    /// <item>a running head — "SENSe Subsystem R&amp;S FSL", or the FSV's whole
    ///   "Operating Manual 1307.9331.12 17 461 R&amp;S FSV Remote Control" — dropped wherever
    ///   the page broke, which was often mid-sentence. 213 entries.</item>
    /// <item>a pointer instead of a description: "For details refer to … on page 6.194." and
    ///   nothing else. 75 entries.</item>
    /// <item>the first cell of the parameter table that follows the text — "Parameter number
    ///   of the new limit line", "Bit No Meaning".</item>
    /// <item>a sentence from the next column of a two-column summary, which is how
    ///   :TRACe:DATA? came to be described as "See :TRACe:ACTual:STARt?".</item>
    /// </list>
    ///
    /// None of it is visible to a sweep that asks whether the command is in the guide — the
    /// command is fine, and the stray text is in the guide too. It only shows up by reading,
    /// which is why it survived four sweeps. What it costs is a user reading the library and
    /// being told a page number instead of what the command does.
    ///
    /// The rules are deliberately narrow. "Short" is not one of them: the Rigol analyzer
    /// answers "None." for a command that takes no parameters, and 27 entries say so, which
    /// is terse rather than wrong. Only shapes that cannot be a description at all fail here.
    /// </summary>
    [Theory]
    [MemberData(nameof(CataloguedFamilies))]
    public void Every_description_is_a_description(InstrumentFamily family)
    {
        CommandReference reference = CommandReference.ForFamily(family)!;

        var bad = reference.Commands
            .Select(c => (c.Syntax, Fault: DescriptionFault(c.Description)))
            .Where(p => p.Fault != null)
            .Select(p => $"{p.Syntax} — {p.Fault}")
            .ToList();

        Assert.True(bad.Count == 0,
            $"{family}: {bad.Count} description(s) are not descriptions:{Environment.NewLine}"
            + string.Join(Environment.NewLine, bad));
    }

    /// <summary>
    /// No query form the guide says does not exist.
    ///
    /// The rs parser adds a query to every setting command, because R&S states that rule in
    /// its own SCPI conventions and marks the exceptions in a per-entry Usage field. Older
    /// R&S manuals have no Usage field and write the exception as a sentence instead — the
    /// FSL guide has none of the former and 152 of the latter, "This command is an event and
    /// therefore has no *RST value and no query". Read only the field, and the rule invents a
    /// query for everything: *RST?, and "position the marker to the next peak?".
    ///
    /// The test is the pair, not the sentence alone. A command whose own name ends in a
    /// question mark carries the same boilerplate — MMEMory:CATalog? is printed with the "?"
    /// and means there is no *further* query — so the sentence by itself proves nothing. What
    /// gives an invented one away is that the set form is in the catalog beside it: the
    /// parser emitted both, and the guide documents only one. Five shipped this way, four in
    /// the FSL catalog and one in the FPC's.
    /// </summary>
    [Theory]
    [MemberData(nameof(CataloguedFamilies))]
    public void No_query_form_the_guide_says_does_not_exist(InstrumentFamily family)
    {
        CommandReference reference = CommandReference.ForFamily(family)!;

        var present = reference.Commands.Select(c => c.Syntax).ToHashSet(StringComparer.Ordinal);

        var invented = reference.Commands
            .Where(c => c.Syntax.EndsWith('?')
                     && c.Description != null
                     && Regex.IsMatch(c.Description, @"\bis an ""?event""?\b|\bhas no query\b|\bno query\b",
                                      RegexOptions.IgnoreCase)
                     && present.Contains(c.Syntax[..^1]))
            .Select(c => c.Syntax)
            .ToList();

        Assert.True(invented.Count == 0,
            $"{family}: {invented.Count} query form(s) the guide says do not exist:"
            + Environment.NewLine + string.Join(Environment.NewLine, invented));
    }

    /// <summary>
    /// Did a character survive the trip from the PDF to here?
    ///
    /// Two ways it does not. A symbol pdftotext cannot map becomes U+FFFD, so "0 °C" ships
    /// as "0 &#xFFFD;C" — 47 entries across five catalogs did, and the library drew every
    /// one as a black diamond. And UTF-8 read as if it were a single-byte codepage turns
    /// "°" into "Â°" and "—" into "â&#x20AC;&#x201D;"; that is not the guide's doing but
    /// this project's, and it has happened twice — once to three catalogs at once, from a
    /// PowerShell `Get-Content` that defaulted to ANSI.
    ///
    /// Neither is visible to a test that only reads the JSON as text and checks the
    /// commands, which is why both survived a long time. Both are trivially visible here.
    /// </summary>
    [Theory]
    [MemberData(nameof(CataloguedFamilies))]
    public void No_text_lost_its_characters_on_the_way_in(InstrumentFamily family)
    {
        CommandReference reference = CommandReference.ForFamily(family)!;

        var damaged = reference.Commands
            .Select(c => (c.Syntax, Text: $"{c.Category} {c.Syntax} {c.Description} {c.Example}"))
            .Where(p => Mojibake().IsMatch(p.Text))
            .Select(p => p.Syntax)
            .ToList();

        Assert.True(damaged.Count == 0,
            $"{family}: {damaged.Count} entr(ies) carry a replacement character or mis-decoded "
            + $"UTF-8: {string.Join(" | ", damaged)}");
    }

    /// <summary>
    /// U+FFFD, and the two sequences a UTF-8 byte pair makes when read as Latin-1 or
    /// CP1252: U+00C2 before punctuation, and U+00E2 U+20AC before anything.
    /// </summary>
    private static Regex Mojibake() => new("[\uFFFD]|\u00C2[\u0020-\u00BF]|\u00E2\u20AC");

    /// <summary>What is wrong with this description, or null when nothing is.</summary>
    private static string? DescriptionFault(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "empty";

        string d = description.Trim();

        // Punctuation and brackets with no word in them: the extractor kept the scaffolding
        // and lost the sentence, as in "]]].".
        if (!d.Any(char.IsLetter)) return "no words in it";

        // A page header carries the subsystem or the document number and the model.
        if (Regex.IsMatch(d, @"Subsystem\s+R&S|Operating Manual\s+[\d.]+\s+\d|Programming Guide\s+\d"))
            return "carries a page header";

        // A reference is a fine thing to end on; it is not a description on its own.
        if (Regex.IsMatch(d, @"^(For (details|further details) refer to|Refer to|See )"))
            return "points at the description instead of being one";

        // The tail of one, where the sentence it belonged to ended on the previous page:
        // "On page 1212." The FSW shipped two of these past the rule above, because they
        // do not begin with a verb of reference — they begin with the page.
        if (Regex.IsMatch(d, @"^(On|At|See)\s+page\s+[\d.]+\.?$", RegexOptions.IgnoreCase))
            return "is a page number and nothing else";

        // The parameter table that follows the text, or a column heading from it.
        if (Regex.IsMatch(d, @"^(Parameter\b|Bit No\b|Return\s*$)"))
            return "is a fragment of the parameter table";

        return null;
    }

    /// <summary>
    /// Unbalanced delimiters mean the line was cut; a leading delimiter means it is a
    /// parameter group that lost the command it belonged to.
    /// </summary>
    private static bool IsWellFormed(string syntax)
    {
        if (string.IsNullOrWhiteSpace(syntax)) return false;
        if (syntax[0] is '{' or '<') return false;

        foreach ((char open, char close) in new[] { ('{', '}'), ('[', ']'), ('<', '>') })
            if (syntax.Count(c => c == open) != syntax.Count(c => c == close))
                return false;

        // The header is everything before the first parameter. It has to contain a mnemonic.
        string header = syntax.Split(' ')[0];
        return header.Any(char.IsLetter);
    }

    [Theory]
    [MemberData(nameof(CataloguedFamilies))]
    public void Quick_commands_are_documented_in_the_catalog(InstrumentFamily family)
    {
        CommandReference reference = CommandReference.ForFamily(family)!;
        List<string> templates = reference.Commands.Select(c => c.Syntax).ToList();
        InstrumentProfile profile = ProfileFor(family);

        var undocumented = profile.Commands
            .Select(q => q.Command)
            .Where(cmd => !ScpiSyntax.MatchesAny(cmd, templates))
            .ToList();

        Assert.True(undocumented.Count == 0,
            $"{family}: quick commands not found in the catalog: {string.Join(", ", undocumented)}");
    }

    [Theory]
    [MemberData(nameof(CataloguedFamilies))]
    public void Readout_queries_are_documented_in_the_catalog(InstrumentFamily family)
    {
        CommandReference reference = CommandReference.ForFamily(family)!;
        List<string> templates = reference.Commands.Select(c => c.Syntax).ToList();

        var undocumented = ProfileFor(family).ReadoutFunctions
            .Select(f => f.Query)
            .Where(q => !ScpiSyntax.MatchesAny(q, templates))
            .ToList();

        Assert.True(undocumented.Count == 0,
            $"{family}: readout queries not found in the catalog: {string.Join(", ", undocumented)}");
    }

    [Theory]
    [MemberData(nameof(CataloguedFamilies))]
    public void Script_example_commands_are_documented_in_the_catalog(InstrumentFamily family)
    {
        CommandReference reference = CommandReference.ForFamily(family)!;
        List<string> templates = reference.Commands.Select(c => c.Syntax).ToList();

        var undocumented = new List<string>();
        foreach (ScriptExample example in ScriptExamples.ForFamily(family))
            foreach (string cmd in ScpiLinesOf(example.Script))
                if (!ScpiSyntax.MatchesAny(cmd, templates))
                    undocumented.Add($"{example.Name}: {cmd}");

        Assert.True(undocumented.Count == 0,
            $"{family}: script lines not found in the catalog: {string.Join("; ", undocumented)}");
    }

    /// <summary>
    /// The SCPI lines of a script, skipping the interpreter's own keywords. Mirrors
    /// how <see cref="ScriptRunner"/> classifies a line (SPEC §9).
    /// </summary>
    private static IEnumerable<string> ScpiLinesOf(string script)
    {
        foreach (string raw in script.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#') || line.StartsWith("//")) continue;

            string head = line.Split(' ')[0].ToUpperInvariant();
            if (head is "DELAY" or "WAIT" or "PRINT" or "ECHO" or "LOG"
                     or "REPEAT" or "END" or "ENDREPEAT") continue;

            yield return line;
        }
    }

    private static InstrumentProfile ProfileFor(InstrumentFamily family)
        => InstrumentProfile.ForIdentity(IdentityFor(family));

    /// <summary>
    /// An *IDN? string that classifies as the given family. Shared with
    /// <see cref="WaveformDialectTests"/>, which checks the capture commands against the
    /// same roster so a new family cannot be added to one list and missed by the other.
    /// </summary>
    public static string IdentityFor(InstrumentFamily family) => family switch
    {
        InstrumentFamily.Oscilloscope     => "RIGOL TECHNOLOGIES,DS2202,X,1.0",
        InstrumentFamily.SiglentGenerator => "Siglent Technologies,SDG2042X,X,1.0",
        InstrumentFamily.ScpiGenerator    => "Rigol Technologies,DG1022Z,X,1.0",
        InstrumentFamily.Multimeter       => "Siglent Technologies,SDM3065X,X,1.0",
        InstrumentFamily.PowerSupply      => "RIGOL TECHNOLOGIES,DP832,X,1.0",
        InstrumentFamily.ElectronicLoad   => "Siglent Technologies,SDL1020X,X,1.0",
        InstrumentFamily.SpectrumAnalyzer => "Siglent Technologies,SSA3021X,X,1.0",
        InstrumentFamily.TektronixScope   => "TEKTRONIX,MDO4104C,C000,1.0",
        InstrumentFamily.KeysightScope    => "KEYSIGHT TECHNOLOGIES,MSO-X 3054T,MY000,1.0",
        InstrumentFamily.KeysightPowerSupply => "Keysight Technologies,E36313A,MY000,1.0",
        InstrumentFamily.KeithleySmu      => "KEITHLEY INSTRUMENTS,MODEL 2450,04000,1.0",
        InstrumentFamily.KeithleyDmm      => "KEITHLEY INSTRUMENTS,DMM6500,04000,1.0",
        InstrumentFamily.RohdeScope       => "Rohde&Schwarz,RTB2004,1333.1005k04/000,1.0",
        InstrumentFamily.RohdePowerSupply => "Rohde&Schwarz,NGL202,3638.3376k03/000,1.0",
        InstrumentFamily.SiglentScope     => "Siglent Technologies,SDS1104X-E,SDS000,1.0",
        InstrumentFamily.FlukeMultimeter  => "FLUKE,8846A,1234567,1.0",
        InstrumentFamily.GwInstekScope    => "GW INSTEK,GDS-2204E,GEQ000,1.0",
        InstrumentFamily.ChromaPowerSupply => "Chroma ATE,62012P-80-60,000000,1.0",
        InstrumentFamily.BkElectronicLoad => "B&K PRECISION,8600,123456,1.0",
        InstrumentFamily.RohdeSpectrumAnalyzer => "Rohde&Schwarz,FPC1500,1328.6660k03/000,1.0",
        InstrumentFamily.ChromaElectronicLoad  => "Chroma ATE,63206A-150-500,000000,1.0",
        InstrumentFamily.RohdeFslAnalyzer      => "Rohde&Schwarz,FSL6,1300.2502K06,2.30",
        InstrumentFamily.RigolMultimeter       => "Rigol Technologies,DM3058E,DM3R000,1.0",
        InstrumentFamily.RigolElectronicLoad   => "RIGOL TECHNOLOGIES,DL3021,DL3A000,1.0",
        InstrumentFamily.RigolSpectrumAnalyzer => "RIGOL TECHNOLOGIES,DSA815,DSA8A000,1.0",
        InstrumentFamily.RohdeFsvAnalyzer      => "Rohde&Schwarz,FSV30,1321.3008K30,3.20",
        InstrumentFamily.KeysightMultimeter    => "Keysight Technologies,34461A,MY000,1.0",
        InstrumentFamily.GwInstekScopeB        => "GW INSTEK,GDS-1104B,GEQ000,1.0",
        InstrumentFamily.ChromaModularLoad     => "Chroma ATE,63640-80-80,000000,1.0",
        InstrumentFamily.BkPowerSupply         => "B&K PRECISION,9201B,123456,1.0",
        InstrumentFamily.BkPowerSupply9130     => "B&K Precision, 9130B, 123456, V1.06-V1.04",
        InstrumentFamily.RohdeFswAnalyzer      => "Rohde&Schwarz,FSW26,1312.8000K26,5.20",
        InstrumentFamily.RohdeFsuAnalyzer      => "Rohde&Schwarz,FSU26,1166.1660K26,4.71",
        InstrumentFamily.RohdeFspAnalyzer      => "Rohde&Schwarz,FSP13,1164.4391K13,4.70",
        InstrumentFamily.RohdeFsqAnalyzer      => "Rohde&Schwarz,FSQ26,1313.9100K26,4.75",
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };
}
