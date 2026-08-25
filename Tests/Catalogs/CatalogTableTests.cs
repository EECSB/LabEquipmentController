using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LabEquipmentController.Tests;

/// <summary>
/// docs/SPEC.md carries a row per catalog: entries, bench ticks, cross-checks. It has said
/// underneath that the counts are generated rather than kept by hand, and that was half
/// true — they were read out of the catalogs once, by hand, and every edit since drifted
/// away from them. Marking 1,338 more entries as cross-checked would have left twenty rows
/// reading 0 while the text above claimed otherwise.
///
/// `node tools/catalog-table.js` regenerates the table. This checks the result, so the
/// claim is enforced rather than repeated: a table nobody verifies is a table that is wrong
/// a few edits later, and a stale row here reads exactly like a measured one.
/// </summary>
public class CatalogTableTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "LabEquipmentController.slnx"))) d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }

    /// <summary>Row heading to catalog file — the same map tools/catalog-table.js uses.</summary>
    private static readonly Dictionary<string, string> Family = new()
    {
        ["R&S FSW analyzer"] = "rohde-fsw-analyzer",
        ["R&S FSU analyzer"] = "rohde-fsu-analyzer",
        ["R&S FSP analyzer"] = "rohde-fsp-analyzer",
        ["R&S FSQ analyzer"] = "rohde-fsq-analyzer",
        ["R&S FSL analyzer"] = "rohde-fsl-analyzer",
        ["R&S FSV analyzer"] = "rohde-fsv-analyzer",
        ["R&S FSIQ analyzer"] = "rohde-fsiq-analyzer",
        ["R&S scope"] = "rohde-scope",
        ["R&S spectrum analyzer"] = "rohde-spectrum-analyzer",
        ["R&S power supply"] = "rohde-power-supply",
        ["Tektronix scope"] = "tektronix-scope",
        ["Keysight scope"] = "keysight-scope",
        ["Keysight multimeter"] = "keysight-multimeter",
        ["Keysight power supply"] = "keysight-power-supply",
        ["Oscilloscope"] = "oscilloscope",
        ["Siglent scope"] = "siglent-scope",
        ["Siglent generator"] = "siglent-generator",
        ["Rigol spectrum analyzer"] = "rigol-spectrum-analyzer",
        ["Rigol multimeter"] = "rigol-multimeter",
        ["Rigol electronic load"] = "rigol-electronic-load",
        ["Waveform generator"] = "scpi-generator",
        ["GW Instek GDS-1000B scope"] = "gwinstek-gds1000b-scope",
        ["GW Instek scope"] = "gwinstek-scope",
        ["Keithley multimeter"] = "keithley-dmm",
        ["Keithley SMU"] = "keithley-smu",
        ["Chroma electronic load"] = "chroma-electronic-load",
        ["Chroma modular load"] = "chroma-modular-load",
        ["Chroma power supply"] = "chroma-power-supply",
        ["Power supply"] = "power-supply",
        ["Electronic load"] = "electronic-load",
        ["Multimeter"] = "multimeter",
        ["Spectrum analyzer"] = "spectrum-analyzer",
        ["B&K triple-output supply"] = "bk-power-supply-9130b",
        ["B&K electronic load"] = "bk-electronic-load",
        ["B&K power supply"] = "bk-power-supply",
        ["Fluke multimeter"] = "fluke-multimeter",
    };

    private static (int entries, int bench, int cross) Counts(string root, string file)
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Core", "CommandData", file + ".json")));
        JsonElement cmds = doc.RootElement.GetProperty("commands");
        int n = 0, bench = 0, cross = 0;
        foreach (JsonElement c in cmds.EnumerateArray())
        {
            n++;
            if (c.TryGetProperty("benchVerified", out JsonElement b) && b.GetBoolean()) bench++;
            if (c.TryGetProperty("crossChecked", out JsonElement x) && x.GetBoolean()) cross++;
        }
        return (n, bench, cross);
    }

    [Fact]
    public void The_catalog_table_in_the_spec_matches_the_catalogs()
    {
        string root = RepoRoot();
        string spec = Path.Combine(root, "docs", "SPEC.md");
        if (!File.Exists(spec)) return;             // toolchain and docs are not shipped

        var rowPattern = new Regex(@"^\| ([^|]+?) \| *(\d+) \| *(\d+) \| *(\d+) \|");
        var wrong = new List<string>();
        var seen = new HashSet<string>();

        foreach (string line in File.ReadAllLines(spec))
        {
            Match m = rowPattern.Match(line);
            if (!m.Success) continue;
            string row = m.Groups[1].Value;
            Assert.True(Family.ContainsKey(row), $"SPEC.md has a catalog row with no mapping: \"{row}\"");
            seen.Add(row);

            var (entries, bench, cross) = Counts(root, Family[row]);
            int rEntries = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int rBench = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            int rCross = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);

            if (rEntries != entries || rBench != bench || rCross != cross)
                wrong.Add($"{row}: table says {rEntries}/{rBench}/{rCross}, catalogs say {entries}/{bench}/{cross}");
        }

        Assert.True(wrong.Count == 0,
            "docs/SPEC.md catalog table is stale — run `node tools/catalog-table.js`:\n  "
            + string.Join("\n  ", wrong));

        var absent = Family.Keys.Except(seen).ToList();
        Assert.True(absent.Count == 0,
            "families with a catalog but no row in the SPEC table: " + string.Join(", ", absent));
    }

    /// <summary>The sentence above the table counts the same things, and drifts the same way.</summary>
    [Fact]
    public void The_totals_sentence_matches_the_catalogs()
    {
        string root = RepoRoot();
        string spec = Path.Combine(root, "docs", "SPEC.md");
        if (!File.Exists(spec)) return;

        var totals = Family.Values.Select(f => Counts(root, f)).ToList();
        // Invariant, not the machine's culture: the document is written one way, and a
        // build in a locale that groups with dots would otherwise fail on a correct table.
        string N(int v) => v.ToString("N0", CultureInfo.InvariantCulture);
        string expected = $"{Family.Count} catalogs, {N(totals.Sum(t => t.entries))} entries, of which "
            + $"{totals.Sum(t => t.bench)} carry a bench tick and {N(totals.Sum(t => t.cross))} a cross-check.";

        Assert.Contains(expected, File.ReadAllText(spec));
    }
}
