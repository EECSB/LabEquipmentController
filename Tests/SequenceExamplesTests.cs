using System;
using System.Collections.Generic;
using System.Linq;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// SPEC §10 for sequences.
///
/// The single-instrument examples are checked by <see cref="CatalogCoverageTests"/>, which
/// knows which family a script belongs to because the script belongs to one. A sequence
/// does not: its lines are addressed to different instruments, so each has to be checked
/// against a different catalog. That is the whole reason a DEVICE line names a model.
///
/// This is not a formality. Writing these examples, the generator's frequency command was
/// "C1:BSWV FREQ" from memory and "C1:BSWV FRQ" in Siglent's guide — a sequence that ships
/// with the first spelling silently does nothing to the instrument it was pointed at.
/// </summary>
public class SequenceExamplesTests
{
    /// <summary>
    /// Raw string literals carry bare newlines, and a WinForms TextBox renders those as
    /// nothing — the whole sequence arrives as one very long line, which is how this was
    /// found. The examples normalise on the way out; this keeps them that way.
    /// </summary>
    [Fact]
    public void Example_scripts_use_line_endings_a_text_box_understands()
        => Assert.All(SequenceExamples.All, ex =>
        {
            Assert.Contains("\r\n", ex.Script);
            Assert.DoesNotContain("\n", ex.Script.Replace("\r\n", ""));
        });

    [Fact]
    public void There_are_examples_and_each_one_names_its_instruments()
    {
        Assert.NotEmpty(SequenceExamples.All);
        Assert.All(SequenceExamples.All, ex =>
        {
            Assert.False(string.IsNullOrWhiteSpace(ex.Name));
            Assert.NotEmpty(ex.Identities);
            Assert.NotEmpty(SequenceRunner.Requirements(ex.Script));
        });
    }

    /// <summary>Every DEVICE line has an identity behind it, or the check below is vacuous.</summary>
    [Fact]
    public void Every_declared_model_has_a_matching_identity()
    {
        foreach (SequenceExample ex in SequenceExamples.All)
            foreach ((string alias, string model) in SequenceRunner.Requirements(ex.Script))
                Assert.True(IdentityFor(ex, model) != null,
                    $"{ex.Name}: DEVICE {alias} : {model} has no identity in the example's list.");
    }

    [Theory]
    [MemberData(nameof(ExampleNames))]
    public void Every_command_is_documented_in_the_catalog_of_the_instrument_it_is_sent_to(string name)
    {
        SequenceExample ex = SequenceExamples.All.Single(e => e.Name == name);

        // alias → the catalog of whatever that alias resolves to
        var catalogs = new Dictionary<string, CommandReference?>(StringComparer.OrdinalIgnoreCase);
        foreach ((string alias, string model) in SequenceRunner.Requirements(ex.Script))
        {
            string? idn = IdentityFor(ex, model);
            catalogs[alias] = idn == null ? null : CommandReference.ForIdentity(idn);
        }

        var undocumented = new List<string>();

        foreach ((string alias, string command) in ScpiLinesOf(ex.Script, catalogs.Keys))
        {
            CommandReference? reference = catalogs.GetValueOrDefault(alias);
            Assert.True(reference != null, $"{ex.Name}: no catalog for \"{alias}\".");

            List<string> templates = reference!.Commands.Select(c => c.Syntax).ToList();
            if (!ScpiSyntax.MatchesAny(command, templates))
                undocumented.Add($"{alias}: {command}");
        }

        Assert.True(undocumented.Count == 0,
            $"{ex.Name}: not in the catalog: {string.Join("; ", undocumented)}");
    }

    public static TheoryData<string> ExampleNames()
    {
        var data = new TheoryData<string>();
        foreach (SequenceExample ex in SequenceExamples.All) data.Add(ex.Name);
        return data;
    }

    /// <summary>The identity in the example's list whose model is the one the script names.</summary>
    private static string? IdentityFor(SequenceExample ex, string model)
        => ex.Identities.FirstOrDefault(idn =>
        {
            string m = InstrumentProfile.ParseIdentity(idn).Model;
            return m.Equals(model, StringComparison.OrdinalIgnoreCase)
                || m.StartsWith(model, StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>
    /// The SCPI lines of a sequence and which alias each is addressed to, mirroring how
    /// <see cref="SequenceRunner"/> decides: a prefix, then the enclosing WITH, then the
    /// only declared instrument. Keywords and the '-&gt; name' capture are stripped.
    /// </summary>
    private static IEnumerable<(string Alias, string Command)> ScpiLinesOf(
        string script, IEnumerable<string> aliases)
    {
        var known = new HashSet<string>(aliases, StringComparer.OrdinalIgnoreCase);
        var withStack = new Stack<string?>();
        string? target = known.Count == 1 ? known.First() : null;
        string? blockTarget = null;

        foreach (string raw in script.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;

            string upper = line.ToUpperInvariant();

            if (upper.StartsWith("WITH "))
            {
                withStack.Push(blockTarget);
                blockTarget = line[4..].Trim();
                continue;
            }
            if (upper == "END" || upper == "ENDREPEAT")
            {
                if (withStack.Count > 0) blockTarget = withStack.Pop();
                continue;
            }
            if (upper.StartsWith("DEVICE") || upper.StartsWith("COLUMNS")
                || upper.StartsWith("FOR ") || upper.StartsWith("REPEAT")
                || upper.StartsWith("DELAY") || upper.StartsWith("WAIT")
                || upper.StartsWith("PRINT") || upper.StartsWith("ECHO")
                || upper.StartsWith("LOG ") || upper.StartsWith("RECORD"))
                continue;

            string alias = blockTarget ?? target ?? "";
            string command = line;

            int colon = line.IndexOf(':');
            if (colon > 0 && known.Contains(line[..colon].Trim()))
            {
                alias = line[..colon].Trim();
                command = line[(colon + 1)..].Trim();
            }

            int arrow = command.IndexOf("->", StringComparison.Ordinal);
            if (arrow >= 0) command = command[..arrow].Trim();

            // A swept value stands where a number goes; the catalog's own placeholder is
            // what the matcher expects to see there.
            command = System.Text.RegularExpressions.Regex.Replace(command, @"\$\w+", "1");

            if (command.Length > 0) yield return (alias, command);
        }
    }
}
