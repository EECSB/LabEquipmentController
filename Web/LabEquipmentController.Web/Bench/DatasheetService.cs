using LabEquipmentController.Web.Client.Contracts;

namespace LabEquipmentController.Web.Bench;

/// <summary>
/// The programming guides this server holds, and the one place they are read and written.
///
/// The app does not ship these — they are the vendors' copyright — so they are the user's own
/// copies. On the desktop that is a folder on their machine, pointed at by
/// <c>Set Datasheets Folder…</c>. A server has nobody at a keyboard to point anywhere, so the
/// folder is its own, under <c>LEC_DATA</c> beside the extracted catalogs, and the browser puts
/// files into it by uploading them. Same collection, reached the way each build can reach it.
///
/// Filed under a folder per manufacturer, which is how the desktop's collection is filed and
/// what lets <see cref="DatasheetLocator"/> tell two vendors' identical model numbers apart. The
/// matching is Core's, so a guide the desktop finds for a catalog is the guide this finds.
/// </summary>
public sealed class DatasheetService
{
    private readonly ILogger<DatasheetService> _log;

    public DatasheetService(AiSettingsStore store, IConfiguration config, ILogger<DatasheetService> log)
    {
        _log = log;
        Directory = config["LEC_DATASHEETS"] is { Length: > 0 } folder
            ? Path.GetFullPath(folder)
            : Path.Combine(store.DataDirectory, "datasheets");
    }

    /// <summary>
    /// Where the guides live: <c>LEC_DATASHEETS</c> if whoever started this server named a
    /// folder, and otherwise one under <c>LEC_DATA</c>, created on the first upload.
    ///
    /// That variable is this build's answer to the desktop's <c>Set Datasheets Folder…</c>, and
    /// it is deliberately not a button on the page. The desktop's picker is for the person
    /// sitting at the machine the folder is on; here the only person who can see that disk is
    /// the one who started the server, so that is who chooses — by naming the folder, or by
    /// mounting one over <c>/data/datasheets</c>, which is the same choice made in the
    /// compose file. A picker in the browser would be picking a path on somebody else's
    /// machine, out of a list it cannot see.
    ///
    /// A collection pointed at this way may well be read-only, which costs nothing to read
    /// and makes an upload fail — and an upload that fails says so already.
    /// </summary>
    public string Directory { get; }

    /// <summary>
    /// Every PDF held, newest folder first, with the path a browser fetches it by.
    ///
    /// The relative path rather than the absolute one: it is what the fetch URL is built from,
    /// and an absolute path from the server's disk is not something a page has any use for.
    /// </summary>
    public IReadOnlyList<DatasheetDto> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];

        try
        {
            return [.. System.IO.Directory
                .GetFiles(Directory, "*.pdf", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderBy(f => Relative(f.FullName), StringComparer.OrdinalIgnoreCase)
                .Select(f => new DatasheetDto(Relative(f.FullName), f.Name, f.Length))];
        }
        catch (Exception ex)
        {
            // A folder that cannot be read is an empty collection, not a broken page.
            _log.LogWarning(ex, "Could not list the datasheets under {Folder}.", Directory);
            return [];
        }
    }

    /// <summary>
    /// The guide this server holds for one catalog, or null.
    ///
    /// Core's <see cref="DatasheetLocator"/> does the matching — the suggested name first, then
    /// the catalog's own, then any PDF carrying the same distinctive words — so the guide the
    /// desktop finds for a catalog is the guide this finds. Nothing here renames or moves.
    /// </summary>
    public DatasheetDto? For(InstrumentFamily family)
    {
        CommandReference? reference = CommandReference.ForFamily(family);
        if (reference is null) return null;

        string? found = DatasheetLocator.Find(
            Directory, reference.Guide, reference.Instrument, reference.Manufacturer);
        if (found is null) return null;

        var file = new FileInfo(found);
        return new DatasheetDto(Relative(found), file.Name, file.Length);
    }

    /// <summary>
    /// Where one of these lives on disk, given the path a browser asked for. Null if it is not
    /// one of ours.
    ///
    /// The check is the point. A path arriving from a browser is not a path this server chose,
    /// and "datasheets/../../../etc/passwd" is a perfectly ordinary-looking string until it is
    /// resolved — so it is resolved, and then required to still be under the folder it claimed
    /// to be under.
    /// </summary>
    public string? Locate(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;

        try
        {
            string root = Path.GetFullPath(Directory);
            string full = Path.GetFullPath(Path.Combine(root, relative));

            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            if (!full.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return null;

            return File.Exists(full) ? full : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Put a guide into the collection, under its manufacturer's folder, and say where it went.
    ///
    /// The manufacturer's folder because that is how the collection is filed and what lets two
    /// vendors' identical model numbers be told apart. The name is taken to pieces and rebuilt
    /// from what is left: a name arriving from a browser is not a name this server chose, and
    /// only the leaf of it is any of its business.
    /// </summary>
    public async Task<DatasheetDto?> SaveAsync(string? manufacturer, string fileName, byte[] bytes, CancellationToken ct)
    {
        string leaf = Path.GetFileName(fileName ?? "");
        if (leaf.Length == 0 || !leaf.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return null;
        if (bytes.Length == 0) return null;

        string folder = Directory;
        if (Safe(manufacturer) is { Length: > 0 } maker) folder = Path.Combine(folder, maker);

        try
        {
            System.IO.Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, leaf);
            await File.WriteAllBytesAsync(path, bytes, ct);

            _log.LogInformation("Datasheet {Name} saved under {Folder}.", leaf, folder);
            return new DatasheetDto(Relative(path), leaf, bytes.Length);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not save the datasheet {Name}.", leaf);
            return null;
        }
    }

    /// <summary>Forget one. The folder it was in is left alone, empty or not.</summary>
    public bool Delete(string? relative)
    {
        string? full = Locate(relative);
        if (full is null) return false;

        try { File.Delete(full); return true; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not delete the datasheet {Path}.", relative);
            return false;
        }
    }

    /// <summary>A folder name with nothing in it that could mean anything but a folder name.</summary>
    private static string Safe(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? ""
            : string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();

    /// <summary>The path under the collection's root, with forward slashes, for a URL.</summary>
    private string Relative(string full)
        => Path.GetRelativePath(Directory, full).Replace('\\', '/');
}
