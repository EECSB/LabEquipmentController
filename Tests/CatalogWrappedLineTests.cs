using System;
using System.Collections.Generic;
using System.Linq;
using LabEquipmentController;

namespace LabEquipmentController.Tests;

/// <summary>
/// Guides wrap a long command name across two lines:
///
///     SEARCH:SEARCH&lt;x&gt;:TRIGger:A:BUS:B&lt;x&gt;:FLEXray:HEADER:
///     PAYLength?
///
/// and a line-based extractor reads the continuation as a command of its own. The catalog
/// then ships "PAYLength?", which no instrument answers, and loses the query form of the
/// command it came from. Nine entries in the Tektronix catalog were like this.
///
/// <see cref="CatalogCoverageTests.No_catalog_command_is_a_truncated_line"/> does not catch
/// it: a bare "PAYLength?" has balanced brackets and a letter in its header, so it is
/// well-formed in every way except being real.
/// </summary>
public class CatalogWrappedLineTests
{
    [Theory]
    [MemberData(nameof(CatalogCoverageTests.CataloguedFamilies), MemberType = typeof(CatalogCoverageTests))]
    public void No_entry_is_the_tail_of_another_entry(InstrumentFamily family)
    {
        IReadOnlyList<CommandRef> commands = CommandReference.ForFamily(family)!.Commands;

        // Group the multi-node entries by their last mnemonic. A single-node entry that
        // matches one of those *and repeats its description word for word* did not come
        // from its own heading; it came from the tail of that one.
        var byLeaf = commands
            .Where(c => Nodes(c.Syntax).Length > 1)
            .GroupBy(c => Leaf(c.Syntax), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var fragments = new List<string>();
        foreach (CommandRef c in commands)
        {
            string[] nodes = Nodes(c.Syntax);
            if (nodes.Length != 1 || c.Syntax.StartsWith('*')) continue;
            if (c.Description.Trim().Length <= 20) continue;
            if (!byLeaf.TryGetValue(nodes[0], out List<CommandRef>? owners)) continue;

            CommandRef? twin = owners.FirstOrDefault(
                o => string.Equals(o.Description.Trim(), c.Description.Trim(), StringComparison.Ordinal));
            if (twin != null) fragments.Add($"{c.Syntax} (tail of {twin.Syntax})");
        }

        Assert.True(fragments.Count == 0,
            $"{family}: entries that look like a wrapped line rather than a command: "
            + string.Join(" | ", fragments));
    }

    /// <summary>The header's mnemonics, with brackets, parameters and suffixes stripped.</summary>
    private static string[] Nodes(string syntax)
        => syntax.Split(' ')[0]
                 .Replace("[", "").Replace("]", "")
                 .TrimEnd('?')
                 .Split(':', StringSplitOptions.RemoveEmptyEntries)
                 .Select(Bare)
                 .ToArray();

    private static string Leaf(string syntax) => Nodes(syntax).LastOrDefault() ?? "";

    /// <summary>Drop a "&lt;x&gt;" channel placeholder and any trailing digits.</summary>
    private static string Bare(string node)
    {
        int lt = node.IndexOf('<');
        if (lt >= 0) node = node[..lt];
        return node.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
    }
}
