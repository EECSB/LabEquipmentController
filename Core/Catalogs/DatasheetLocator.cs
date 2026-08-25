using System;
using System.IO;
using System.Linq;

namespace LabEquipmentController;

/// <summary>
/// Finds a user's local copy of the programming guide behind a catalog.
///
/// The app does not ship these — they are the vendors' copyright — so the user downloads
/// them and points at a folder. That means the file is whatever the vendor's download
/// happened to be called, so matching is deliberately forgiving: the suggested name first,
/// then the catalog's own name, then any PDF whose name carries the same distinctive words.
/// Nothing here writes, moves or renames anything.
///
/// The collection is filed under a folder per manufacturer, matching the library's tree, and
/// that folder is looked in first. Subfolders are searched either way, so a flat folder —
/// which is how every collection starts — still works exactly as it did.
/// </summary>
public static class DatasheetLocator
{
    /// <summary>
    /// Full path to a local copy, or null. <paramref name="folder"/> may be null or missing,
    /// which simply means "no local copy" rather than an error.
    /// </summary>
    /// <param name="manufacturer">
    /// The maker as the library's tree names it. Where a subfolder of that name exists it is
    /// searched first, so two vendors using the same model number cannot pick each other's
    /// guide. Optional: without it the whole tree is searched as one.
    /// </param>
    public static string? Find(string? folder, CommandGuide? guide, string catalogName,
                               string? manufacturer = null)
    {
        if (guide == null || string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return null;

        string? maker = MakerFolder(folder, manufacturer);

        return (maker == null ? null : FindUnder(maker, guide, catalogName))
            ?? FindUnder(folder, guide, catalogName);
    }

    /// <summary>The subfolder named for this manufacturer, if there is one.</summary>
    private static string? MakerFolder(string folder, string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer)) return null;

        try
        {
            return Directory.GetDirectories(folder).FirstOrDefault(d => string.Equals(
                Path.GetFileName(d), manufacturer, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    private static string? FindUnder(string folder, CommandGuide guide, string catalogName)
    {
        string[] pdfs;
        try { pdfs = Directory.GetFiles(folder, "*.pdf", SearchOption.AllDirectories); }
        catch { return null; }   // an unreadable folder is "not found", not a crash
        if (pdfs.Length == 0) return null;

        // 1. Exactly what the library suggested, case-insensitively.
        string? hit = ByName(pdfs, guide.FileName)
                   ?? ByName(pdfs, catalogName + ".pdf");
        if (hit != null) return hit;

        // 2. The vendor's own filename. Match on the distinctive words of the suggested name —
        //    model numbers and the like — so "MSODS2000AProgrammingGuideEN_tcm17-2899.pdf"
        //    still resolves against "Rigol_MSO2000A_DS2000A_ProgrammingGuide.pdf".
        string[] tokens = Tokens(guide.FileName);
        if (tokens.Length == 0) return null;

        return pdfs
            .Select(p => (Path: p, Score: Score(Path.GetFileNameWithoutExtension(p), tokens)))
            .Where(x => x.Score >= 2)          // one shared word is a coincidence; two is not
            .OrderByDescending(x => x.Score)
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    private static string? ByName(string[] pdfs, string name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : pdfs.FirstOrDefault(p => string.Equals(
                Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The parts of a name worth matching on: model numbers and words of real length.
    /// "Programming", "Guide", "Manual" and the vendor are dropped — they appear in every
    /// guide ever written and would match everything in the folder.
    /// </summary>
    private static string[] Tokens(string fileName)
    {
        string[] noise =
        {
            "programming", "programmers", "programmer", "guide", "manual", "reference",
            "user", "users", "series", "pdf", "en", "the", "and",
            "rigol", "siglent", "tektronix", "keithley", "keysight", "fluke",
            "rohdeschwarz", "rohde", "schwarz", "gwinstek", "chroma", "bkprecision",
        };

        return Path.GetFileNameWithoutExtension(fileName)
            .Split(new[] { '_', '-', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length >= 3 && !noise.Contains(t))
            .Distinct()
            .ToArray();
    }

    private static int Score(string candidate, string[] tokens)
    {
        string flat = candidate.ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");
        return tokens.Count(t => flat.Contains(t));
    }
}
