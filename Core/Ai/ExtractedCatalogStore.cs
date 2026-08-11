using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LabEquipmentController;

/// <summary>
/// Where AI-extracted commands live: one JSON file per instrument, under the user's own
/// AppData, entirely separate from the catalogs embedded in the assembly.
///
/// The separation is the point. SPEC section 10 says catalog commands are transcribed from
/// vendor guides, and <c>CatalogCoverageTests</c> enforces it against the embedded files.
/// Extracted commands are a different kind of thing — a draft read out of a datasheet by a
/// model — so they are stored elsewhere, marked <see cref="CommandRef.AiExtracted"/>, and
/// merged only at display time. Nothing here can write into <c>Core/CommandData</c>.
/// </summary>
public static class ExtractedCatalogStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>%AppData%\LabEquipmentController\extracted.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LabEquipmentController", "extracted");

    /// <summary>
    /// File name for an instrument. Keyed on whatever identifies it to the user — the model
    /// from *IDN? — reduced to something a file system will accept.
    /// </summary>
    public static string FileNameFor(string instrumentKey)
    {
        string safe = new((instrumentKey ?? "").Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
        safe = safe.Trim('-');
        if (safe.Length == 0) safe = "instrument";
        if (safe.Length > 80) safe = safe[..80];
        return safe + ".json";
    }

    public static string PathFor(string instrumentKey, string? directory = null)
        => Path.Combine(directory ?? DefaultDirectory, FileNameFor(instrumentKey));

    /// <summary>
    /// Read the extracted catalog for an instrument, or null when there is none. A corrupt
    /// file yields null rather than throwing — a bad cache must never stop a console opening.
    /// </summary>
    public static CommandReference? Load(string instrumentKey, string? directory = null)
    {
        try
        {
            string path = PathFor(instrumentKey, directory);
            if (!File.Exists(path)) return null;

            CommandReference? loaded =
                JsonSerializer.Deserialize<CommandReference>(File.ReadAllText(path), Options);
            if (loaded == null) return null;

            // Whatever the file claims, everything from here is AI-extracted. A hand-edited
            // file must not be able to promote its entries to looking bench-verified.
            return new CommandReference
            {
                Instrument = loaded.Instrument,
                Source = loaded.Source,
                Commands = loaded.Commands
                    .Select(c => c with { BenchVerified = false, CrossChecked = false, AiExtracted = true })
                    .ToList(),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Write the extracted catalog for an instrument, replacing any previous one.</summary>
    public static void Save(string instrumentKey, CommandReference reference, string? directory = null)
    {
        string dir = directory ?? DefaultDirectory;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, FileNameFor(instrumentKey));

        var toWrite = new CommandReference
        {
            Instrument = reference.Instrument,
            Source = reference.Source,
            Commands = reference.Commands
                .Select(c => c with { BenchVerified = false, CrossChecked = false, AiExtracted = true })
                .ToList(),
        };

        File.WriteAllText(path, JsonSerializer.Serialize(toWrite, Options));
    }

    /// <summary>Forget an instrument's extracted commands.</summary>
    public static void Delete(string instrumentKey, string? directory = null)
    {
        try
        {
            string path = PathFor(instrumentKey, directory);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* a failed delete is not worth interrupting the user over */ }
    }

    /// <summary>
    /// One reference for display: the curated catalog first, then extracted commands that add
    /// something. An extracted duplicate of a transcribed command is dropped — the
    /// transcribed one is better evidence, and showing both would imply two sources agreed
    /// when only one of them is a source.
    /// </summary>
    public static CommandReference Merge(CommandReference? curated, CommandReference? extracted)
    {
        if (extracted == null || extracted.Commands.Count == 0)
            return curated ?? Empty();
        if (curated == null || curated.Commands.Count == 0)
            return extracted;

        var known = new HashSet<string>(
            curated.Commands.Select(c => ScpiSyntax.HeaderOf(c.Syntax)),
            StringComparer.OrdinalIgnoreCase);

        List<CommandRef> extra = extracted.Commands
            .Where(c => !known.Contains(ScpiSyntax.HeaderOf(c.Syntax)))
            .ToList();

        if (extra.Count == 0) return curated;

        return new CommandReference
        {
            Instrument = curated.Instrument,
            Source = curated.Source + $"  Plus {extra.Count} command(s) extracted from a "
                                    + "datasheet by AI, marked with a diamond and unverified.",
            Commands = curated.Commands.Concat(extra).ToList(),
        };
    }

    private static CommandReference Empty() => new()
    {
        Instrument = "",
        Source = "",
        Commands = Array.Empty<CommandRef>(),
    };
}
