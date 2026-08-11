using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LabEquipmentController;

/// <summary>One documented SCPI command in an instrument's curated reference.</summary>
/// <param name="BenchVerified">Confirmed against the real instrument on this bench.</param>
/// <param name="CrossChecked">
/// The same command was found in at least one independent open-source instrument
/// driver as well as in the vendor guide. Weaker evidence than a bench check, but
/// it catches a command transcribed from a guide that the hardware never accepted.
/// </param>
public sealed record CommandRef(
    string Category,
    string Syntax,
    string Description,
    string? Example = null,
    bool IsQuery = false,
    bool BenchVerified = false,
    bool CrossChecked = false,

    /// <summary>
    /// Read out of a datasheet by a language model rather than transcribed from a guide by
    /// hand. Never true for an embedded catalog — SPEC section 10 — and always shown with its
    /// own mark, because a plausible-looking command that no instrument implements is exactly
    /// what this flag exists to keep visible.
    /// </summary>
    bool AiExtracted = false,

    /// <summary>
    /// What the vendor guide prints here that looks wrong, and what it probably meant.
    /// Null for the overwhelming majority of entries.
    ///
    /// Vendor guides contain typos, and section 10 leaves no good way to deal with one:
    /// transcribe a dropped letter faithfully and the catalog offers a command the
    /// instrument will reject; correct it and the catalog carries SCPI nobody documented.
    /// So <see cref="Syntax"/> stays exactly as printed and this says so out loud — the
    /// user gets the guide's word, the doubt, and the likely correction, and decides at
    /// the bench. Shown with its own mark for the same reason
    /// <see cref="AiExtracted"/> is: an entry nobody should trust silently.
    /// </summary>
    string? GuideMisprint = null);

/// <summary>
/// The vendor document a catalog was transcribed from.
///
/// Deliberately not a link to a PDF this project hosts. Programming guides are the vendors'
/// copyright — free to download, which is not the same as free to redistribute — so the app
/// ships the *reference* and the user brings the file. <see cref="Url"/> points at the
/// vendor's own documentation page, which also means the user always lands on the current
/// revision; <see cref="FileName"/> is the name a local copy is expected to have, so the
/// app can find one in whatever folder the user downloaded it to.
/// </summary>
public sealed record CommandGuide(
    string Title,
    string Edition = "",
    string Vendor = "",
    string Url = "",
    string FileName = "");

/// <summary>
/// A curated, per-instrument-family catalog of SCPI commands, used when the live
/// <see cref="CommandDiscovery"/> query isn't supported by the instrument.
///
/// The catalog for each family is authored as JSON and embedded in this assembly as
/// "commands.&lt;family&gt;.json". Content is transcribed from the vendor programming
/// guides; entries confirmed on the bench carry <see cref="CommandRef.BenchVerified"/>.
/// </summary>
public sealed class CommandReference
{
    /// <summary>Friendly title, e.g. "Siglent generator (SDG2000X series)".</summary>
    public string Instrument { get; init; } = "";

    /// <summary>Manufacturer, for grouping in the command library.</summary>
    public string Manufacturer { get; init; } = "";

    /// <summary>The vendor document behind this catalog, when one is recorded.</summary>
    public CommandGuide? Guide { get; init; }

    /// <summary>Where the entries were transcribed from, shown to the user for provenance.</summary>
    public string Source { get; init; } = "";

    public IReadOnlyList<CommandRef> Commands { get; init; } = Array.Empty<CommandRef>();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Embedded-resource name for a family, or null when nothing is bundled.</summary>
    private static string? ResourceName(InstrumentFamily family) => family switch
    {
        InstrumentFamily.Oscilloscope     => "commands.oscilloscope.json",
        InstrumentFamily.SiglentGenerator => "commands.siglent-generator.json",
        InstrumentFamily.ScpiGenerator    => "commands.scpi-generator.json",
        InstrumentFamily.Multimeter       => "commands.multimeter.json",
        InstrumentFamily.PowerSupply      => "commands.power-supply.json",
        InstrumentFamily.ElectronicLoad   => "commands.electronic-load.json",
        InstrumentFamily.SpectrumAnalyzer => "commands.spectrum-analyzer.json",
        InstrumentFamily.TektronixScope   => "commands.tektronix-scope.json",
        InstrumentFamily.KeysightScope    => "commands.keysight-scope.json",
        InstrumentFamily.KeysightPowerSupply => "commands.keysight-power-supply.json",
        InstrumentFamily.KeithleySmu      => "commands.keithley-smu.json",
        InstrumentFamily.KeithleyDmm      => "commands.keithley-dmm.json",
        InstrumentFamily.RohdeScope       => "commands.rohde-scope.json",
        InstrumentFamily.RohdePowerSupply => "commands.rohde-power-supply.json",
        InstrumentFamily.SiglentScope     => "commands.siglent-scope.json",
        InstrumentFamily.FlukeMultimeter  => "commands.fluke-multimeter.json",
        InstrumentFamily.GwInstekScope    => "commands.gwinstek-scope.json",
        InstrumentFamily.ChromaPowerSupply => "commands.chroma-power-supply.json",
        InstrumentFamily.BkElectronicLoad => "commands.bk-electronic-load.json",
        InstrumentFamily.RohdeSpectrumAnalyzer => "commands.rohde-spectrum-analyzer.json",
        InstrumentFamily.ChromaElectronicLoad  => "commands.chroma-electronic-load.json",
        InstrumentFamily.RohdeFslAnalyzer      => "commands.rohde-fsl-analyzer.json",
        InstrumentFamily.RigolMultimeter       => "commands.rigol-multimeter.json",
        InstrumentFamily.RigolElectronicLoad   => "commands.rigol-electronic-load.json",
        InstrumentFamily.RigolSpectrumAnalyzer => "commands.rigol-spectrum-analyzer.json",
        InstrumentFamily.RohdeFsvAnalyzer      => "commands.rohde-fsv-analyzer.json",
        InstrumentFamily.KeysightMultimeter    => "commands.keysight-multimeter.json",
        InstrumentFamily.GwInstekScopeB        => "commands.gwinstek-gds1000b-scope.json",
        InstrumentFamily.ChromaModularLoad     => "commands.chroma-modular-load.json",
        InstrumentFamily.BkPowerSupply         => "commands.bk-power-supply.json",
        InstrumentFamily.BkPowerSupply9130     => "commands.bk-power-supply-9130b.json",
        InstrumentFamily.RohdeFswAnalyzer      => "commands.rohde-fsw-analyzer.json",
        InstrumentFamily.RohdeFsuAnalyzer      => "commands.rohde-fsu-analyzer.json",
        InstrumentFamily.RohdeFspAnalyzer      => "commands.rohde-fsp-analyzer.json",
        InstrumentFamily.RohdeFsqAnalyzer      => "commands.rohde-fsq-analyzer.json",
        _                                 => null,
    };

    /// <summary>
    /// The catalog file's base name for a family, or null when none is bundled. Used by the
    /// command library to look for a local copy of the guide under that name.
    /// </summary>
    public static string? CatalogNameFor(InstrumentFamily family)
    {
        string? resource = ResourceName(family);
        if (resource == null) return null;
        // "commands.<name>.json" -> "<name>"
        return resource["commands.".Length..^".json".Length];
    }

    /// <summary>Load the curated reference for a family, or null if none is bundled/parsable.</summary>
    public static CommandReference? ForFamily(InstrumentFamily family)
    {
        string? res = ResourceName(family);
        if (res == null) return null;

        using Stream? s = typeof(CommandReference).Assembly.GetManifestResourceStream(res);
        if (s == null) return null;

        try
        {
            using var reader = new StreamReader(s);
            return JsonSerializer.Deserialize<CommandReference>(reader.ReadToEnd(), Options);
        }
        catch (JsonException)
        {
            return null;   // a malformed catalog should degrade to "no reference", not crash
        }
    }

    /// <summary>Load the curated reference matching an *IDN? string.</summary>
    public static CommandReference? ForIdentity(string? identity)
        => ForFamily(InstrumentProfile.FamilyForIdentity(identity));
}
