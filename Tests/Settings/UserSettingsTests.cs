using System;
using System.IO;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

public class UserSettingsTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "lec-settings-" + Guid.NewGuid().ToString("N") + ".json");

    /// <summary>
    /// Every field survives a write by a window that owns only some of them.
    ///
    /// Settings are edited in three places — the main window keeps the address, ports,
    /// timeout and geometry; the AI dialog keeps the connection and its protected key; the
    /// command library keeps the datasheet folder. The main window writes last, on
    /// FormClosed, and it used to write a brand-new UserSettings built from its own
    /// controls. Everything it had no control for went back to its default. The AI key was
    /// gone by the next launch, and nothing reported it, because a settings write that
    /// succeeds is silent by design.
    ///
    /// This is the load-modify-save shape the fix uses, one caller at a time.
    /// </summary>
    [Fact]
    public void A_partial_write_leaves_other_windows_settings_alone()
    {
        string path = TempFile();
        try
        {
            SettingsStore.Save(path, new UserSettings
            {
                Ai = new AiConnection { Provider = AiProvider.Anthropic, Model = "claude-opus-5" },
                AiApiKeyProtected = "protected-blob",
                DatasheetFolder = @"C:\guides",
            });

            // What MainForm.SaveSettings does: load, change only its own, save.
            UserSettings mine = SettingsStore.Load(path);
            mine.InterfaceAddress = "192.168.1.28";
            mine.WindowWidth = 1500;
            SettingsStore.Save(path, mine);

            UserSettings after = SettingsStore.Load(path);
            Assert.Equal("192.168.1.28", after.InterfaceAddress);
            Assert.Equal(1500, after.WindowWidth);

            Assert.NotNull(after.Ai);
            Assert.Equal("claude-opus-5", after.Ai!.Model);
            Assert.Equal("protected-blob", after.AiApiKeyProtected);
            Assert.Equal(@"C:\guides", after.DatasheetFolder);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_then_Load_round_trips_all_fields()
    {
        string path = TempFile();
        try
        {
            var original = new UserSettings
            {
                InterfaceAddress = "192.168.1.3",
                Ports = "5025, 5555, 111",
                TimeoutMs = 4500,
                WindowWidth = 1024,
                WindowHeight = 768,
                WindowMaximized = true,
            };

            SettingsStore.Save(path, original);
            var loaded = SettingsStore.Load(path);

            Assert.Equal(original.InterfaceAddress, loaded.InterfaceAddress);
            Assert.Equal(original.Ports, loaded.Ports);
            Assert.Equal(original.TimeoutMs, loaded.TimeoutMs);
            Assert.Equal(original.WindowWidth, loaded.WindowWidth);
            Assert.Equal(original.WindowHeight, loaded.WindowHeight);
            Assert.True(loaded.WindowMaximized);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_missing_file_returns_defaults()
    {
        var s = SettingsStore.Load(TempFile());   // never created
        Assert.Null(s.InterfaceAddress);
        Assert.Equal(3000, s.TimeoutMs);
        Assert.False(s.WindowMaximized);
    }

    [Fact]
    public void Load_corrupt_file_returns_defaults_without_throwing()
    {
        string path = TempFile();
        try
        {
            File.WriteAllText(path, "{ this is not valid json ");
            var s = SettingsStore.Load(path);
            Assert.Equal(3000, s.TimeoutMs);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_creates_missing_directory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lec-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "nested", "settings.json");
        try
        {
            SettingsStore.Save(path, new UserSettings { Ports = "5025" });
            Assert.True(File.Exists(path));
            Assert.Equal("5025", SettingsStore.Load(path).Ports);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    // ------------------------------------------------------ StartingSize

    private const int Old = UserSettings.CurrentLayoutVersion - 1;

    [Fact]
    public void A_profile_with_no_saved_size_gets_the_default()
        => Assert.Equal((1500, 980), UserSettings.StartingSize(0, 0, Old, 1500, 980));

    /// <summary>A half-written file is no more trustworthy than an empty one.</summary>
    [Fact]
    public void A_saved_size_missing_one_dimension_gets_the_default()
    {
        Assert.Equal((1500, 980), UserSettings.StartingSize(1800, 0, Old, 1500, 980));
        Assert.Equal((1500, 980), UserSettings.StartingSize(0, 1200, Old, 1500, 980));
    }

    /// <summary>Once the file is current, what was saved is what opens — including smaller.</summary>
    [Fact]
    public void A_current_profile_keeps_exactly_what_it_saved()
        => Assert.Equal((900, 700),
            UserSettings.StartingSize(900, 700, UserSettings.CurrentLayoutVersion, 1500, 980));

    [Fact]
    public void An_older_profile_below_the_new_default_is_raised_to_it()
        => Assert.Equal((1500, 980), UserSettings.StartingSize(1180, 900, Old, 1500, 980));

    /// <summary>
    /// The case the whole method exists for. The version is only bumped because a default
    /// grew, so a window already dragged past it must not be pulled back in the name of
    /// enlarging it — 2452 wide became 1500 before this, which is a shrink of nearly a
    /// thousand pixels applied as a "widening".
    /// </summary>
    [Fact]
    public void An_older_profile_already_larger_is_left_alone()
        => Assert.Equal((2452, 1768), UserSettings.StartingSize(2452, 1768, Old, 1500, 980));

    /// <summary>Each dimension is decided on its own; one being larger says nothing about the other.</summary>
    [Fact]
    public void The_two_dimensions_are_migrated_independently()
        => Assert.Equal((2452, 980), UserSettings.StartingSize(2452, 700, Old, 1500, 980));
}
