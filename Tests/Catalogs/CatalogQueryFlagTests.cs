using System.Collections.Generic;
using System.Linq;
using LabEquipmentController;

namespace LabEquipmentController.Tests;

/// <summary>
/// <see cref="CommandRef.IsQuery"/> has to agree with the syntax it sits beside.
///
/// It drifted in three catalogs and nothing noticed: 27 Chroma entries carried a '?' and
/// said they were not queries — <c>*IDN?</c> among them — while 59 FSV entries said they
/// were, including <c>*OPC</c> and settable commands with parameters.
///
/// Nothing was mis-sent, which is why it went unseen for so long. Every runtime path decides
/// by reading the command text through <c>ScpiClient.IsQuery</c>, so the flag only reaches
/// the Command Library's display and the catalog the AI is shown. Wrong data rather than
/// wrong behaviour — and a catalog that tells you <c>*IDN?</c> is not a query is still
/// telling you something false about an instrument.
///
/// The rule is the whole of it: a query is a command whose *header* ends in a question mark.
/// The header, not the line — "SELect &lt;1|2&gt;?" put the mark on the parameter clause, which
/// made a command no instrument answers, and this is what found it.
/// </summary>
public class CatalogQueryFlagTests
{
    /// <summary>The command itself, before any parameters.</summary>
    private static string Header(string syntax) => syntax.Split(' ')[0];

    [Theory]
    [MemberData(nameof(CatalogCoverageTests.CataloguedFamilies), MemberType = typeof(CatalogCoverageTests))]
    public void The_query_flag_agrees_with_the_syntax(InstrumentFamily family)
    {
        IReadOnlyList<CommandRef> commands = CommandReference.ForFamily(family)!.Commands;

        var wrong = commands
            .Where(c => c.IsQuery != Header(c.Syntax).EndsWith('?'))
            .Select(c => $"{c.Syntax} — IsQuery={c.IsQuery}, header {(Header(c.Syntax).EndsWith('?') ? "ends" : "does not end")} in '?'")
            .ToList();

        Assert.True(wrong.Count == 0,
            $"{family}: {wrong.Count} entries whose query flag contradicts their syntax:"
            + System.Environment.NewLine + string.Join(System.Environment.NewLine, wrong.Take(10)));
    }

    /// <summary>
    /// A question mark belongs to the command, never to its arguments. The FSL shipped
    /// "SYSTem:COMMunicate:PRINter:SELect &lt;1|2&gt;?" — the derived query form built by
    /// appending '?' to the whole line rather than to the header, which spells a command
    /// that does not exist.
    /// </summary>
    [Theory]
    [MemberData(nameof(CatalogCoverageTests.CataloguedFamilies), MemberType = typeof(CatalogCoverageTests))]
    public void No_question_mark_hides_in_a_parameter_clause(InstrumentFamily family)
    {
        IReadOnlyList<CommandRef> commands = CommandReference.ForFamily(family)!.Commands;

        var misplaced = commands
            .Where(c => c.Syntax.TrimEnd().EndsWith('?') && !Header(c.Syntax).EndsWith('?'))
            .Select(c => c.Syntax)
            .ToList();

        Assert.True(misplaced.Count == 0,
            $"{family}: the '?' is on the parameter clause rather than the command: "
            + string.Join(" | ", misplaced.Take(10)));
    }
}
