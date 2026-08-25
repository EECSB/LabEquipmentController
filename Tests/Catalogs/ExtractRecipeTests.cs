using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LabEquipmentController.Tests;

/// <summary>
/// A catalog's config says which extracted files it was built from — `groups`, and the
/// `restrictTo` index — but those files live in `parsed/`, which is regenerated output and
/// is not committed. For a long time nothing recorded the step that produces them: which
/// manual, which reader, and any `--from` flag. That step existed only for as long as
/// somebody remembered running it.
///
/// The cost was quiet. Nine catalogs were written up as unrebuildable because they had no
/// config at all, which was true and beside the point: no catalog could be rebuilt from its
/// manual, config or not, because the recipe was gone. Configs looked complete, and pointed
/// at files that could not be remade.
///
/// Each config now carries a `parse` block, one step per input, and `rebuild.js` runs it.
/// This keeps the two in step: name an input and you must say how it is made. Absence is
/// what went unnoticed before, so absence is what this checks.
/// </summary>
public class ExtractRecipeTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "LabEquipmentController.slnx"))) d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }

    private static string CfgDir => Path.Combine(RepoRoot(), "tools", "scpi-extract", "cfg");

    public static TheoryData<string> Configs()
    {
        var data = new TheoryData<string>();
        if (Directory.Exists(CfgDir))
            foreach (string f in Directory.GetFiles(CfgDir, "*.json").OrderBy(x => x))
                data.Add(Path.GetFileName(f));
        return data;
    }

    /// <summary>The toolchain is not shipped, so a checkout without it skips rather than fails.</summary>
    [Fact]
    public void The_extract_configs_are_where_this_expects_them()
    {
        if (!Directory.Exists(CfgDir)) return;
        Assert.NotEmpty(Directory.GetFiles(CfgDir, "*.json"));
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void Every_extracted_input_says_how_it_is_produced(string configFile)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(CfgDir, configFile)));
        JsonElement root = doc.RootElement;

        var inputs = new List<string>();
        if (root.TryGetProperty("groups", out JsonElement groups))
            foreach (JsonElement g in groups.EnumerateArray())
                if (g.TryGetProperty("file", out JsonElement f)) inputs.Add(f.GetString()!);
        if (root.TryGetProperty("restrictTo", out JsonElement r)) inputs.Add(r.GetString()!);

        // A config may be supplements-only — the Fluke catalog is curated by hand and
        // extracts nothing, so it has no recipe to record.
        if (inputs.Count == 0) return;

        Assert.True(root.TryGetProperty("parse", out JsonElement parse),
            $"{configFile} builds from {string.Join(", ", inputs)} but records no parse recipe. "
            + "Those files are regenerated and not committed, so without the recipe the catalog "
            + "cannot be rebuilt from its guide.");

        var covered = parse.EnumerateArray()
            .Where(s => s.TryGetProperty("input", out _))
            .ToDictionary(s => s.GetProperty("input").GetString()!, s => s);

        foreach (string input in inputs)
            Assert.True(covered.ContainsKey(input), $"{configFile}: no parse step for {input}.");

        foreach (JsonElement step in covered.Values)
        {
            Assert.True(step.TryGetProperty("manual", out JsonElement m) && m.GetString()!.Length > 0,
                $"{configFile}: a parse step names no manual.");
            Assert.True(step.TryGetProperty("style", out JsonElement s) && s.GetString()!.Length > 0,
                $"{configFile}: a parse step names no style.");
            // How far the recipe is trusted is part of the recipe: "measured" was run here,
            // "documented" is written down somewhere, "inferred" is the reader the vendor's
            // guides use and has never been run against this one.
            Assert.True(step.TryGetProperty("known", out JsonElement k)
                        && k.GetString() is "measured" or "documented" or "inferred",
                $"{configFile}: a parse step must say how its style is known.");
        }
    }
}
