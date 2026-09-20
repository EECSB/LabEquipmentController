using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LabEquipmentController.Web.Bench;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabEquipmentController.Tests;

/// <summary>
/// Where the server reads its programming guides from.
///
/// The desktop asks the person at the machine, with a folder picker. A server cannot: the disk
/// is somewhere else and the browser has no view of it, so the choice belongs to whoever starts
/// the server — <c>LEC_DATASHEETS</c>, or a folder mounted over the default one.
/// </summary>
public class DatasheetServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lec-sheets-" + Guid.NewGuid().ToString("N"));

    private DatasheetService Service(string? named)
    {
        var settings = new Dictionary<string, string?> { ["LEC_DATA"] = Path.Combine(_root, "data") };
        if (named is not null) settings["LEC_DATASHEETS"] = named;

        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new DatasheetService(
            new AiSettingsStore(new AiOptions(), config, NullLogger<AiSettingsStore>.Instance),
            config,
            NullLogger<DatasheetService>.Instance);
    }

    /// <summary>A PDF that is a PDF as far as anything here is concerned.</summary>
    private string Guide(string folder, string name)
    {
        string dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, "%PDF-1.4 not really"u8.ToArray());
        return path;
    }

    [Fact]
    public void Without_a_folder_named_the_guides_live_under_the_data_directory()
    {
        DatasheetService sheets = Service(named: null);

        Assert.Equal(Path.Combine(_root, "data", "datasheets"), sheets.Directory);
        Assert.Empty(sheets.List());      // and nothing is created merely by asking
        Assert.False(Directory.Exists(sheets.Directory));
    }

    [Fact]
    public void A_folder_named_by_whoever_started_the_server_is_read_instead()
    {
        Guide("library/Rigol", "Rigol_MSO2000A_DS2000A_ProgrammingGuide.pdf");
        DatasheetService sheets = Service(named: Path.Combine(_root, "library"));

        var held = sheets.List();

        Assert.Single(held);
        Assert.Equal("Rigol_MSO2000A_DS2000A_ProgrammingGuide.pdf", held[0].Name);
        // Under the maker's folder, as the desktop files them and as DatasheetLocator expects.
        Assert.Equal("Rigol", Path.GetDirectoryName(held[0].Path)?.Replace('\\', '/'));
    }

    /// <summary>
    /// The matching is Core's, so a guide the desktop would find for a catalog is the guide a
    /// server pointed at the same folder finds.
    /// </summary>
    [Fact]
    public void A_catalog_finds_its_guide_in_the_named_folder()
    {
        Guide("library/Rigol", "Rigol_MSO2000A_DS2000A_ProgrammingGuide.pdf");
        DatasheetService sheets = Service(named: Path.Combine(_root, "library"));

        Assert.NotNull(sheets.For(InstrumentFamily.Oscilloscope));
        Assert.Null(sheets.For(InstrumentFamily.KeysightMultimeter));
    }

    /// <summary>
    /// A path from a browser is not a path the server chose, and naming the folder does not
    /// change that: it is resolved and then has to still be inside the collection.
    /// </summary>
    [Theory]
    [InlineData("../outside.pdf")]
    [InlineData("Rigol/../../outside.pdf")]
    [InlineData("Rigol/Rigol_MSO2000A_DS2000A_ProgrammingGuide.txt")]
    public void A_path_that_leaves_the_collection_is_not_one_of_ours(string asked)
    {
        Guide("library/Rigol", "Rigol_MSO2000A_DS2000A_ProgrammingGuide.pdf");
        Guide("", "outside.pdf");
        DatasheetService sheets = Service(named: Path.Combine(_root, "library"));

        Assert.Null(sheets.Locate(asked));
    }

    [Fact]
    public void A_guide_inside_it_is_found_by_the_path_the_listing_gave()
    {
        Guide("library/Rigol", "Rigol_MSO2000A_DS2000A_ProgrammingGuide.pdf");
        DatasheetService sheets = Service(named: Path.Combine(_root, "library"));

        string asked = sheets.List().Single().Path;

        Assert.NotNull(sheets.Locate(asked));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* a temp folder that outlives the run is not worth failing a test over */ }
    }
}
