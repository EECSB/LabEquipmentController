using System;
using System.IO;

namespace LabEquipmentController.Tests;

public class DatasheetLocatorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lec-guides-" + Guid.NewGuid().ToString("N"));

    public DatasheetLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>An empty file is enough: nothing here reads a PDF, it only matches names.</summary>
    /// <remarks>
    /// The cases below are written with Windows separators because that is the machine this
    /// folder is usually browsed on, but a backslash is an ordinary filename character on
    /// Linux and macOS. Left alone, `Siglent\Siglent SDM3065X.pdf` became one oddly-named
    /// file in the root instead of a file inside a manufacturer folder, and the test that
    /// depends on the folder layout failed — on those platforms only, which is exactly the
    /// kind of thing nobody notices until CI runs somewhere other than a desktop.
    /// </remarks>
    private string Put(string relativePath)
    {
        string full = Path.Combine(_root, relativePath.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "");
        return full;
    }

    private static CommandGuide Guide(string fileName) =>
        new("A guide", FileName: fileName);

    // ------------------------------------------------------------ a flat folder

    /// <summary>
    /// How every collection starts, and how this folder was laid out before it grew a folder
    /// per manufacturer. It has to keep working — nobody is obliged to reorganise.
    /// </summary>
    [Fact]
    public void A_guide_sitting_flat_in_the_folder_is_found()
    {
        string want = Put("Rigol_DP800_PowerSupply_ProgrammingGuide.pdf");

        Assert.Equal(want, DatasheetLocator.Find(
            _root, Guide("Rigol_DP800_PowerSupply_ProgrammingGuide.pdf"), "Rigol DP800"));
    }

    // ------------------------------------------------- a folder per manufacturer

    [Fact]
    public void A_guide_filed_under_its_manufacturer_is_found()
    {
        string want = Put(@"Rigol\Rigol_DP800_PowerSupply_ProgrammingGuide.pdf");

        Assert.Equal(want, DatasheetLocator.Find(
            _root, Guide("Rigol_DP800_PowerSupply_ProgrammingGuide.pdf"), "Rigol DP800", "Rigol"));
    }

    /// <summary>The subfolder is searched whether or not the caller names the maker.</summary>
    [Fact]
    public void A_filed_guide_is_found_without_being_told_the_manufacturer()
    {
        string want = Put(@"Rigol\Rigol_DP800_PowerSupply_ProgrammingGuide.pdf");

        Assert.Equal(want, DatasheetLocator.Find(
            _root, Guide("Rigol_DP800_PowerSupply_ProgrammingGuide.pdf"), "Rigol DP800"));
    }

    [Fact]
    public void A_manufacturer_folder_is_matched_whatever_its_case()
    {
        string want = Put(@"RIGOL\Rigol_DP800_PowerSupply_ProgrammingGuide.pdf");

        Assert.Equal(want, DatasheetLocator.Find(
            _root, Guide("Rigol_DP800_PowerSupply_ProgrammingGuide.pdf"), "Rigol DP800", "rigol"));
    }

    /// <summary>Folder names come from the tree, which has "B&amp;K Precision" in it.</summary>
    [Fact]
    public void A_manufacturer_folder_may_carry_spaces_and_punctuation()
    {
        string want = Put(@"B&K Precision\BKPrecision_9130B_PowerSupply_ProgrammingManual.pdf");

        Assert.Equal(want, DatasheetLocator.Find(
            _root, Guide("BKPrecision_9130B_PowerSupply_ProgrammingManual.pdf"),
            "B&K 9130B", "B&K Precision"));
    }

    /// <summary>
    /// The reason the maker is passed at all. Two vendors ship a "3000 series" guide; the
    /// loose word-matching that rescues a vendor's own filename would happily return the
    /// wrong one, and the folder is what settles it.
    /// </summary>
    [Fact]
    public void The_manufacturers_own_folder_wins_over_a_similar_name_elsewhere()
    {
        Put(@"Keysight\InfiniiVision_3000_series_ProgrammersGuide.pdf");
        string want = Put(@"Rohde & Schwarz\RTM_3000_series_UserManual.pdf");

        Assert.Equal(want, DatasheetLocator.Find(
            _root, Guide("RohdeSchwarz_RTM3000_Oscilloscope_UserManual.pdf"),
            "R&S RTM3000", "Rohde & Schwarz"));
    }

    // ------------------------------------------------------- forgiving matching

    /// <summary>The vendor's own download name, which shares only its model number.</summary>
    [Fact]
    public void A_vendors_own_filename_still_resolves_by_its_distinctive_words()
    {
        string want = Put(@"Rigol\MSODS2000AProgrammingGuideEN_tcm17-2899.pdf");

        Assert.Equal(want, DatasheetLocator.Find(
            _root, Guide("Rigol_MSO2000A_DS2000A_ProgrammingGuide.pdf"),
            "Rigol MSO2000A", "Rigol"));
    }

    [Fact]
    public void A_file_named_for_the_catalog_is_found()
    {
        string want = Put(@"Siglent\Siglent SDM3065X.pdf");

        Assert.Equal(want, DatasheetLocator.Find(
            _root, Guide("something-else-entirely.pdf"), "Siglent SDM3065X", "Siglent"));
    }

    /// <summary>One word in common is a coincidence, and a coincidence is not a match.</summary>
    [Fact]
    public void An_unrelated_pdf_is_not_offered()
    {
        Put(@"Fluke\Fluke_87V_Multimeter_UserManual.pdf");

        Assert.Null(DatasheetLocator.Find(
            _root, Guide("Rigol_DP800_PowerSupply_ProgrammingGuide.pdf"), "Rigol DP800", "Rigol"));
    }

    // ------------------------------------------------------------ nothing to find

    [Fact]
    public void No_guide_recorded_means_nothing_to_look_for()
        => Assert.Null(DatasheetLocator.Find(_root, null, "Rigol DP800", "Rigol"));

    [Fact]
    public void An_empty_folder_yields_nothing()
        => Assert.Null(DatasheetLocator.Find(
            _root, Guide("Rigol_DP800_PowerSupply_ProgrammingGuide.pdf"), "Rigol DP800", "Rigol"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_folder_set_is_not_an_error(string? folder)
        => Assert.Null(DatasheetLocator.Find(
            folder, Guide("Rigol_DP800.pdf"), "Rigol DP800", "Rigol"));

    /// <summary>A folder that was moved or unplugged reads as "no local copy", not a crash.</summary>
    [Fact]
    public void A_missing_folder_is_not_an_error()
        => Assert.Null(DatasheetLocator.Find(
            Path.Combine(_root, "gone"), Guide("Rigol_DP800.pdf"), "Rigol DP800", "Rigol"));

    /// <summary>A maker folder that does not exist just means "search everything".</summary>
    [Fact]
    public void An_unknown_manufacturer_falls_back_to_the_whole_folder()
    {
        string want = Put(@"Rigol\Rigol_DP800_PowerSupply_ProgrammingGuide.pdf");

        Assert.Equal(want, DatasheetLocator.Find(
            _root, Guide("Rigol_DP800_PowerSupply_ProgrammingGuide.pdf"),
            "Rigol DP800", "Nobody Ltd"));
    }
}
