using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The embedded catalogs are byte-for-byte the files in Core/CommandData.
///
/// MSBuild's incremental build compares timestamps, and a catalog restored from a snapshot
/// carries the snapshot's timestamp — older than the assembly — so the build declares
/// itself up to date and keeps the previous file's bytes embedded. Every test then runs
/// against a catalog that is no longer in the tree, and passes or fails on the strength of
/// it. That happened twice in one day here: once reporting a failure that the on-disk
/// catalog had already fixed, and once the other way around, a pass that meant nothing.
/// Both were only caught by deleting obj/ and bin/ on a hunch.
///
/// This test turns the hunch into a check: if any embedded catalog differs from its file,
/// the fix is a clean rebuild, and the message says so.
/// </summary>
public class EmbeddedCatalogFreshnessTests
{
    [Fact]
    public void The_embedded_catalogs_are_the_files_on_disk()
    {
        string? dataDir = FindCommandData();
        if (dataDir == null) return;   // running from a published test drop, no source tree to compare against

        var assembly = typeof(CommandReference).Assembly;
        var stale = new List<string>();

        foreach (string file in Directory.GetFiles(dataDir, "*.json").OrderBy(f => f))
        {
            string resource = "commands." + Path.GetFileName(file);

            using Stream? s = assembly.GetManifestResourceStream(resource);
            if (s == null)
            {
                stale.Add($"{Path.GetFileName(file)} — on disk but not embedded at all");
                continue;
            }

            using var ms = new MemoryStream();
            s.CopyTo(ms);
            byte[] embedded = ms.ToArray();
            byte[] disk = File.ReadAllBytes(file);

            if (!embedded.AsSpan().SequenceEqual(disk))
                stale.Add($"{Path.GetFileName(file)} — embedded {embedded.Length:N0} bytes, on disk {disk.Length:N0}");
        }

        Assert.True(stale.Count == 0,
            "the built assembly embeds a different catalog than the tree holds — the "
            + "incremental build kept an old copy (a restored file's timestamp is older than "
            + "the DLL, so MSBuild sees nothing to do). Delete Core/obj and Core/bin and "
            + $"rebuild:{Environment.NewLine}{string.Join(Environment.NewLine, stale)}");
    }

    /// <summary>Walk up from the test assembly to the repo. Null when there isn't one.</summary>
    private static string? FindCommandData()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Core", "CommandData");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
