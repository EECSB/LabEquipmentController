using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LabEquipmentController;

namespace LabEquipmentController.Tests;

/// <summary>
/// A description is one sentence, and a guide wraps it at the margin like any other
/// paragraph. Every extractor here used to stop at the line the sentence started on
/// whenever the next line began with something command-shaped, because the check that
/// recognises a command reads only the first token — so
///
///     This command activates or deactivates the external generator selected with
///     SOUR:EXT&lt;1|2&gt;:FREQ:SWE ON in the selected window.
///
/// shipped as "…the external generator selected with." The emit step ends every
/// description with a full stop, which is what made the cut look deliberate rather than
/// like damage: nothing about "…selected with." says the rest is missing.
///
/// A sentence ending on "the", "of" or "by" is not a sentence, and one ending inside a
/// SCPI name — "…defined by the EXPort:" — is not either. Both are counted here.
///
/// The counts are frozen rather than required to be zero. Fifty-four entries are still
/// truncated in catalogs whose guides are not in this checkout, so they cannot be repaired
/// from the source they came from and inventing the missing words would be worse than
/// leaving the cut visible. Freezing the number keeps them counted and keeps a new one from
/// slipping in beside them: a catalog absent from this table must have none at all.
/// </summary>
public class CatalogDescriptionTests
{
    /// <summary>
    /// Empty, and meant to stay that way. All 129 were repaired from the guides they came
    /// from once every guide was in hand — the last eighteen when the InfiniiVision 3000T
    /// Programmer's Guide arrived, since its URL serves a landing page rather than the file.
    ///
    /// A family belongs here only when its guide cannot be had and the missing words would
    /// have to be invented. Adding a number is an admission, not a fix.
    /// </summary>
    private static readonly Dictionary<InstrumentFamily, int> Outstanding = new();

    /// <summary>Ends on a word no sentence ends on.</summary>
    private static readonly Regex OpenClause =
        new(@"(?:^|\s)(the|a|an|of|and|with|by)\.$", RegexOptions.Compiled);

    /// <summary>
    /// English stands a preposition at the end of a sentence after certain verbs, and
    /// "…the type of information that a test report consists of." is a whole sentence that
    /// happens to finish on the same word a severed one would. One entry in 23,163 reads
    /// like this, and counting it made the tally of real damage wrong — which matters,
    /// because the number below is what says how much is left to repair.
    ///
    /// Kept deliberately narrow: only the verbs that actually strand, immediately before the
    /// preposition. Anything looser starts excusing real cuts, and a cut that goes uncounted
    /// is the failure this whole file exists to prevent.
    /// </summary>
    private static readonly Regex Stranded =
        new(@"\b(consists?|consisting|comprises?|comprised|composed|made up)\s+of\.$", RegexOptions.Compiled);

    /// <summary>
    /// Ends inside a SCPI name: a mnemonic, then a colon. Case matters — the mnemonic is
    /// what marks it as a name rather than prose, since "the following commands:" ends in a
    /// colon too and is a whole sentence introducing a list the guide prints as a table.
    /// A drive letter ("the internal hard disk C:.") is one capital, not two, and stays out.
    ///
    /// The class carries ':' so the name may be a whole path. Without it this matched only a
    /// single mnemonic — "the command SENS:" — and six descriptions cut after a deep one,
    /// "…using command MMEM:DATA:" among them, sat in the catalogs reading as finished
    /// sentences. A guard that cannot see a whole class of the damage it exists to catch is
    /// worth less than its green run suggests.
    /// </summary>
    private static readonly Regex CutName =
        new(@"(?:^|\s)[A-Z]{2,}[A-Za-z0-9<>|.\[\]:]*:\.?$", RegexOptions.Compiled);

    private static bool IsTruncated(string description)
    {
        string d = description.Trim();
        if (Stranded.IsMatch(d)) return false;
        return d.Length < 12 || OpenClause.IsMatch(d) || CutName.IsMatch(d);
    }

    [Theory]
    [MemberData(nameof(CatalogCoverageTests.CataloguedFamilies), MemberType = typeof(CatalogCoverageTests))]
    public void No_description_stops_mid_sentence(InstrumentFamily family)
    {
        IReadOnlyList<CommandRef> commands = CommandReference.ForFamily(family)!.Commands;

        // A sentence the *guide* left unfinished is not damage done here. The FSIQ prints
        // "…the positive peak value if the calculation and." and stops, on the page and in
        // its index alike. Transcribing that faithfully and saying so in GuideMisprint is
        // the house rule; completing it would be inventing the clause the vendor lost.
        var cut = commands
            .Where(c => c.GuideMisprint == null)
            .Where(c => IsTruncated(c.Description))
            .ToList();
        int allowed = Outstanding.TryGetValue(family, out int n) ? n : 0;

        Assert.True(cut.Count == allowed,
            $"{family}: expected {allowed} truncated descriptions, found {cut.Count}."
            + (cut.Count > allowed
                ? " A new one has appeared: " + string.Join(" | ", cut.Take(5).Select(c => $"{c.Syntax} => \"{c.Description}\""))
                : " Some were repaired — lower the number in Outstanding to match."));
    }

    /// <summary>
    /// A word broken across a line break, with the hyphen and the break both surviving into
    /// the description: "for pacing a burst of meas- urements". A guide hyphenates at the
    /// right margin, pdftotext keeps the hyphen and turns the newline into a space, and the
    /// result reads as damage in the Command Library and goes to the AI as vocabulary.
    ///
    /// 411 descriptions across eight catalogs carried one. They were repaired against the
    /// guides — a break is only closed where the guide writes the whole word somewhere it
    /// did not have to break — by tools/rejoin-split-words.js, which is where the nine the
    /// guides could not settle are recorded with the reasoning for each.
    ///
    /// A suspended hyphen is the one legitimate form of this shape: "the low- and
    /// high-frequency filters" means what it says, and joining it spells "lowand". None is
    /// in any catalog today. If one arrives, it belongs in the list below with the sentence
    /// that justifies it, not in a loosened pattern — the whole value of this guard is that
    /// it has no exceptions to hide behind.
    /// </summary>
    private static readonly string[] SuspendedHyphens = System.Array.Empty<string>();

    [Theory]
    [MemberData(nameof(CatalogCoverageTests.CataloguedFamilies), MemberType = typeof(CatalogCoverageTests))]
    public void No_description_carries_a_word_broken_across_a_line_break(InstrumentFamily family)
    {
        var broken = CommandReference.ForFamily(family)!.Commands
            .Where(c => Regex.IsMatch(c.Description, @"[A-Za-z]{2,}- [a-z]{2,}"))
            .Where(c => !SuspendedHyphens.Contains(c.Syntax))
            .Select(c => $"{c.Syntax} => \"{Regex.Match(c.Description, @"\S+- \S+").Value}\"")
            .ToList();

        Assert.True(broken.Count == 0,
            $"{family}: {broken.Count} description(s) carry a word broken across a line break "
            + "— run `node tools/rejoin-split-words.js . --apply`: "
            + string.Join(" | ", broken.Take(5)));
    }

    /// <summary>
    /// A guide's layout reaching the description. Two shapes, one cause — an extractor reading
    /// lines cannot tell the page's furniture from the sentence:
    ///
    ///     "Returns the currently selected count value for See Also averaging mode."
    ///     "1 Enable the delay (falling edge-falling edge) measurement function…"
    ///
    /// The first is Keysight's left margin label column, joined inline by pdftotext. The
    /// second is the tail of Rigol's "Description 1", which numbers a command's two forms —
    /// 323 entries in that catalog opened on a bare digit.
    ///
    /// Neither pattern is ever English. "See Also" and "Return Format" do not occur inside a
    /// sentence, and no description in any catalog begins with a number that belongs to it.
    /// The near misses are what kept this narrow: "Errors" and "Mode" match the same shape and
    /// are words — the FSL's "Modulation Errors measurement" and Tektronix's "Horizontal Delay
    /// Mode" name real things, and 46 correct descriptions would have been broken to fix none.
    /// They are cleaned per catalog by the tool and are deliberately not checked here.
    ///
    /// Found by sampling. 100 entries drawn at random and read against their guides put two of
    /// these in front of me, and counting the class behind each turned 2 into 419 — a quarter
    /// of every defect the sample projected, in patterns no amount of staring at the catalogs
    /// had suggested.
    /// </summary>
    [Theory]
    [MemberData(nameof(CatalogCoverageTests.CataloguedFamilies), MemberType = typeof(CatalogCoverageTests))]
    public void No_description_carries_the_guide_page_furniture(InstrumentFamily family)
    {
        var stray = CommandReference.ForFamily(family)!.Commands
            .Where(c => Regex.IsMatch(c.Description, @"[a-z,] (See Also|Return Format) [a-z]")
                     || Regex.IsMatch(c.Description.TrimStart(), @"^\d+\s+[A-Z]"))
            .Select(c => $"{c.Syntax} => \"{c.Description[..Math.Min(70, c.Description.Length)]}\"")
            .ToList();

        Assert.True(stray.Count == 0,
            $"{family}: {stray.Count} description(s) carry a label or number off the guide's page "
            + "— run `node tools/strip-layout-labels.js . --apply`: "
            + string.Join(" | ", stray.Take(5)));
    }
}
