using System;
using System.IO;
using System.Text.Json;

namespace LabEquipmentController;

/// <summary>User-facing preferences persisted between runs (see <see cref="SettingsStore"/>).</summary>
public sealed class UserSettings
{
    /// <summary>Local NIC address of the last-used interface, so it can be reselected.</summary>
    public string? InterfaceAddress { get; set; }

    /// <summary>Last SCPI port list as typed in the Port(s) box, e.g. "5025, 5555, 111".</summary>
    public string? Ports { get; set; }

    /// <summary>
    /// Last IP range as typed, e.g. "192.168.1.20-60". Empty or null means the whole subnet.
    ///
    /// Stored as text rather than as parsed addresses so it reads back exactly as written —
    /// "20-60" against a different interface is a different range, and rewriting it into
    /// absolute addresses would silently pin it to the subnet it was first typed on.
    /// </summary>
    public string? ScanRange { get; set; }

    /// <summary>Last instrument-communication timeout, in milliseconds.</summary>
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>Restore bounds of the main window; 0 means "not saved yet".</summary>
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }

    /// <summary>
    /// Which generation of the default window size this file was written under.
    ///
    /// Without it, changing the default achieves nothing for anyone who has already run the
    /// app: the saved size is restored and the new default is never seen. Raising this
    /// makes existing installs adopt the new size once, after which their own resizing is
    /// remembered again as before.
    /// </summary>
    public int LayoutVersion { get; set; }

    /// <summary>
    /// The current generation. Raise it when the default window size changes.
    ///
    /// 2: the main window's default width went to 1500, the width at which the console's
    /// tool row stops wrapping to a second line.
    /// </summary>
    public const int CurrentLayoutVersion = 2;

    /// <summary>
    /// What a window should open at, before it is clamped to the screen: the saved size, or
    /// the designed default where nothing was saved.
    ///
    /// Where the file predates the current generation, each dimension is *raised* to the
    /// default rather than replaced by it. The version is only ever bumped because a default
    /// grew, so a window already dragged past the new one is bigger than the migration would
    /// hand it — taking the default there would shrink a window in the name of enlarging it,
    /// which is what this method exists to prevent.
    /// </summary>
    /// <param name="savedWidth">Saved width; 0 or less means nothing was saved.</param>
    /// <param name="savedHeight">Saved height; 0 or less means nothing was saved.</param>
    /// <param name="savedVersion">The <see cref="LayoutVersion"/> read from the file.</param>
    /// <param name="designWidth">The current default width.</param>
    /// <param name="designHeight">The current default height.</param>
    public static (int Width, int Height) StartingSize(
        int savedWidth, int savedHeight, int savedVersion, int designWidth, int designHeight)
    {
        if (savedWidth <= 0 || savedHeight <= 0) return (designWidth, designHeight);
        if (savedVersion >= CurrentLayoutVersion) return (savedWidth, savedHeight);

        return (Math.Max(savedWidth, designWidth), Math.Max(savedHeight, designHeight));
    }

    /// <summary>Whether the main window was maximized at exit.</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>
    /// The user's AI connection for datasheet extraction. Null until they set one up.
    /// </summary>
    public AiConnection? Ai { get; set; }

    /// <summary>
    /// The AI API key, encrypted for this Windows user, base64-encoded.
    ///
    /// Deliberately not the key itself. This file is plain JSON in a roaming profile — it
    /// gets backed up, synced and copied around — and an API key sitting in it is a key
    /// leaked to everywhere the profile goes. The app encrypts with DPAPI before it lands
    /// here and decrypts on the way out; Core never sees either operation, which is what
    /// keeps it portable and free of Windows crypto.
    /// </summary>
    public string? AiApiKeyProtected { get; set; }

    /// <summary>
    /// Where the user keeps their downloaded programming guides. The app does not ship them —
    /// they are the vendors' copyright — so the command library looks here for a local copy
    /// and offers the vendor's own page when it does not find one. Null until set.
    /// </summary>
    public string? DatasheetFolder { get; set; }

    /// <summary>
    /// The folder to look in: what the user chose, or the repository's own
    /// <c>datasheets/</c> when nothing is set and the app is running from a build inside it.
    ///
    /// That fallback exists for development. The repository carries the folder — its README
    /// and the archived-pages index are committed — but never the guides themselves, so on a
    /// fresh clone this finds an empty folder and the library behaves as it does for any
    /// user who has not pointed it anywhere. It is found by walking up from the executable
    /// rather than written down, because a path from one machine helps nobody on another.
    /// </summary>
    public string? EffectiveDatasheetFolder()
    {
        if (!string.IsNullOrWhiteSpace(DatasheetFolder)) return DatasheetFolder;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int up = 0; up < 5 && dir != null; up++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "datasheets");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>Loads and saves <see cref="UserSettings"/> as JSON under the per-user AppData folder.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Default settings file: %AppData%\LabEquipmentController\settings.json.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LabEquipmentController", "settings.json");

    /// <summary>
    /// Read settings from <paramref name="path"/>. A missing, empty, or corrupt file yields
    /// defaults rather than throwing — persisted preferences must never block startup.
    /// </summary>
    public static UserSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new UserSettings();
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    /// <summary>Write settings to <paramref name="path"/>, creating the directory if needed.</summary>
    public static void Save(string path, UserSettings settings)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
    }

    /// <summary>Read settings from <see cref="DefaultPath"/>.</summary>
    public static UserSettings Load() => Load(DefaultPath);

    /// <summary>Write settings to <see cref="DefaultPath"/>.</summary>
    public static void Save(UserSettings settings) => Save(DefaultPath, settings);
}
