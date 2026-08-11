using System;
using System.Collections.Generic;

namespace LabEquipmentController;

/// <summary>One quick-command button: what it says, and what it sends.</summary>
public sealed record QuickCommand(string Label, string Command);

/// <summary>
/// A measurement the instrument can be polled for repeatedly, for the live readout window:
/// what to call it, the query that takes one reading, and the unit its answer is in.
/// </summary>
public sealed record ReadoutFunction(string Label, string Query, string Unit);

/// <summary>Broad instrument class inferred from *IDN?; picks both quick commands and the command reference.</summary>
public enum InstrumentFamily
{
    Generic,
    Oscilloscope,
    SiglentGenerator,
    ScpiGenerator,
    Multimeter,
    PowerSupply,
    ElectronicLoad,
    SpectrumAnalyzer,
    TektronixScope,
    KeysightScope,
    KeysightPowerSupply,
    KeithleySmu,
    KeithleyDmm,
    RohdeScope,
    RohdePowerSupply,
    SiglentScope,
    FlukeMultimeter,
    GwInstekScope,
    ChromaPowerSupply,
    BkElectronicLoad,
    RohdeSpectrumAnalyzer,
    ChromaElectronicLoad,
    RohdeFslAnalyzer,
    RigolMultimeter,
    RigolElectronicLoad,
    RigolSpectrumAnalyzer,
    RohdeFsvAnalyzer,
    KeysightMultimeter,
    GwInstekScopeB,
    ChromaModularLoad,
    BkPowerSupply,
    BkPowerSupply9130,
    RohdeFswAnalyzer,
    RohdeFsuAnalyzer,
    RohdeFspAnalyzer,
    RohdeFsqAnalyzer,
}

/// <summary>
/// Maps an instrument's *IDN? string to a sensible set of quick-command buttons.
///
/// This matters because SCPI is only loosely standard: a Rigol scope wants
/// ":MEASure:VPP? CHANnel1", while a Siglent generator wants "C1:BSWV?" —
/// sending one to the other just produces errors. So we look at the model and
/// offer commands that actually work on it.
/// </summary>
public sealed class InstrumentProfile
{
    /// <summary>Friendly description, e.g. "Oscilloscope (DS2202)".</summary>
    public required string Name { get; init; }

    public required IReadOnlyList<QuickCommand> Commands { get; init; }

    /// <summary>
    /// SCPI query that returns a screenshot as an IEEE 488.2 binary block, or null if the
    /// instrument has no known screen-dump command. Drives the "Capture Screen" button.
    /// </summary>
    public string? ScreenCaptureCommand { get; init; }

    /// <summary>
    /// Commands to send before the screen dump, for instruments that take the image format
    /// as separate state rather than as a parameter. Most vendors put it in the query —
    /// ":PRINt? BMP" — but Tektronix's HARDCopy STARt sends whatever SAVe:IMAGe:FILEFormat
    /// was last set to, so it has to be set.
    /// </summary>
    public IReadOnlyList<string> ScreenCaptureSetup { get; init; } = Array.Empty<string>();

    /// <summary>
    /// How this instrument hands a trace back, or <see cref="WaveformDialect.None"/> when its
    /// guide documents no way to read samples over the wire.
    /// </summary>
    public WaveformDialect WaveformDialect { get; init; } = WaveformDialect.None;

    /// <summary>Whether the Capture Waveform button is worth offering.</summary>
    public bool SupportsWaveformCapture => WaveformDialect != WaveformDialect.None;

    /// <summary>
    /// Measurements that can be polled on a timer to build a trend, or empty when repeated
    /// polling makes no sense for the instrument. A meter reads one value at a time and is
    /// the natural fit; a scope already plots against time itself.
    /// </summary>
    public IReadOnlyList<ReadoutFunction> ReadoutFunctions { get; init; } = Array.Empty<ReadoutFunction>();

    /// <summary>Whether the live readout window is worth offering for this instrument.</summary>
    public bool SupportsLiveReadout => ReadoutFunctions.Count > 0;

    /// <summary>
    /// Pick a profile from an *IDN? response of the usual
    /// "Manufacturer,Model,Serial,Firmware" shape.
    /// </summary>
    public static InstrumentProfile ForIdentity(string? identity)
    {
        (_, string model) = ParseIdentity(identity);
        return FamilyForIdentity(identity) switch
        {
            InstrumentFamily.SiglentGenerator => SiglentGenerator(model),
            InstrumentFamily.ScpiGenerator    => ScpiGenerator(model),
            InstrumentFamily.Oscilloscope     => Oscilloscope(model),
            InstrumentFamily.Multimeter       => Multimeter(model),
            InstrumentFamily.PowerSupply      => PowerSupply(model),
            InstrumentFamily.ElectronicLoad   => ElectronicLoad(model),
            InstrumentFamily.SpectrumAnalyzer => SpectrumAnalyzer(model),
            InstrumentFamily.TektronixScope   => TektronixScope(model),
            InstrumentFamily.KeysightScope    => KeysightScope(model),
            InstrumentFamily.KeysightPowerSupply => KeysightPowerSupply(model),
            InstrumentFamily.KeithleySmu      => KeithleySmu(model),
            InstrumentFamily.KeithleyDmm      => KeithleyDmm(model),
            InstrumentFamily.RohdeScope       => RohdeScope(model),
            InstrumentFamily.RohdePowerSupply => RohdePowerSupply(model),
            InstrumentFamily.SiglentScope     => SiglentScope(model),
            InstrumentFamily.FlukeMultimeter  => FlukeMultimeter(model),
            InstrumentFamily.GwInstekScope    => GwInstekScope(model),
            InstrumentFamily.ChromaPowerSupply => ChromaPowerSupply(model),
            InstrumentFamily.BkElectronicLoad => BkElectronicLoad(model),
            InstrumentFamily.RohdeSpectrumAnalyzer => RohdeSpectrumAnalyzer(model),
            InstrumentFamily.ChromaElectronicLoad  => ChromaElectronicLoad(model),
            InstrumentFamily.RohdeFslAnalyzer      => RohdeFslAnalyzer(model),
            InstrumentFamily.RigolMultimeter       => RigolMultimeter(model),
            InstrumentFamily.RigolElectronicLoad   => RigolElectronicLoad(model),
            InstrumentFamily.RigolSpectrumAnalyzer => RigolSpectrumAnalyzer(model),
            InstrumentFamily.RohdeFsvAnalyzer      => RohdeFsvAnalyzer(model),
            InstrumentFamily.RohdeFswAnalyzer      => RohdeFswAnalyzer(model),
            InstrumentFamily.RohdeFsuAnalyzer      => RohdeFsuAnalyzer(model),
            InstrumentFamily.RohdeFspAnalyzer      => RohdeFspAnalyzer(model),
            InstrumentFamily.RohdeFsqAnalyzer      => RohdeFsqAnalyzer(model),
            InstrumentFamily.KeysightMultimeter    => KeysightMultimeter(model),
            InstrumentFamily.GwInstekScopeB        => GwInstekScopeB(model),
            InstrumentFamily.ChromaModularLoad     => ChromaModularLoad(model),
            InstrumentFamily.BkPowerSupply         => BkPowerSupply(model),
            InstrumentFamily.BkPowerSupply9130     => BkPowerSupply9130(model),
            _                                 => Generic(model),
        };
    }

    /// <summary>Classify an *IDN? string into a broad instrument family.</summary>
    public static InstrumentFamily FamilyForIdentity(string? identity)
    {
        (string maker, string model) = ParseIdentity(identity);
        string m = model.ToUpperInvariant();

        // Keithley reports its model as "MODEL 2450", not "2450", so every prefix
        // test below would miss without this. Keysight spells some scopes "MSO-X
        // 3054T" — drop the separators so "MSOX3054T" matches the "MSO" prefix.
        if (m.StartsWith("MODEL ")) m = m["MODEL ".Length..].TrimStart();
        m = m.Replace("-", "").Replace(" ", "");
        string mk = maker.ToUpperInvariant();
        bool siglent = mk.Contains("SIGLENT");
        bool tektronix = mk.Contains("TEKTRONIX");
        bool keithley = mk.Contains("KEITHLEY");
        // Agilent and Hewlett-Packard are the same instruments under earlier names;
        // a 34401A answers "HEWLETT-PACKARD" and an MSO-X 3054A "AGILENT TECHNOLOGIES".
        bool keysight = mk.Contains("KEYSIGHT") || mk.Contains("AGILENT") || mk.Contains("HEWLETT");
        // Reported variously as "Rohde&Schwarz", "ROHDE&SCHWARZ" and "Rohde & Schwarz";
        // HAMEG is the older brand whose HMP/HMO lines R&S still sells.
        bool rohde = mk.Contains("ROHDE") || mk.Contains("HAMEG");
        bool fluke = mk.Contains("FLUKE");
        // GW Instek reports itself as "GW INSTEK" or by its parent, Good Will.
        bool gwinstek = mk.Contains("GW INSTEK") || mk.Contains("GWINSTEK") || mk.Contains("GOOD WILL");
        bool chroma = mk.Contains("CHROMA");
        // B&K Precision reports itself variously; "BK PRECISION" is the common form.
        bool bkprecision = mk.Contains("B&K") || mk.Contains("BK PRECISION") || mk.Contains("BKPRECISION");
        bool rigol = mk.Contains("RIGOL");

        // Order is load-bearing throughout: the prefixes overlap, and the first test
        // that matches wins.
        //
        // Tektronix scopes go first because "DSA" belongs to two different instruments
        // — a Tektronix DSA70000 is an oscilloscope, a Rigol DSA815 a spectrum analyzer
        // — and only the maker separates them. Tek's RSA is deliberately not listed
        // here: unlike the DSA it really is a spectrum analyzer.
        if (tektronix && IsTektronixScope(m)) return InstrumentFamily.TektronixScope;

        // Keysight scopes, likewise on the maker: "MSO" and "DSO" are shared with
        // Rigol, and while the two dialects look alike (Rigol modelled its command
        // set on Agilent's) the Keysight command set is far larger.
        if (keysight && IsOscilloscope(m)) return InstrumentFamily.KeysightScope;

        // R&S scopes need their own test: RTB/RTM/RTA/RTO share no prefix with any
        // other maker's scopes, so IsOscilloscope above never sees them.
        if (rohde && IsRohdeScope(m)) return InstrumentFamily.RohdeScope;

        // Siglent's scopes have a dialect of their own — ":TRIGger:RUN" rather than
        // ":RUN", ":CHANnel1:SWITch" rather than ":CHANnel1:DISPlay" — so they must
        // not inherit the Rigol catalog from the "SDS" prefix further down.
        //
        // But only the modern ones. The first generation — SDS1052DL, SDS1102CML and
        // their CNL/CFL siblings — speaks a LeCroy-derived dialect instead: "C1:VDIV",
        // "TDIV", "TRMD", none of which appears in the shipped catalog. Handing them the
        // modern set gives a full strip of buttons where every one is wrong, which is
        // worse than none. Every model taking the modern set carries an X in its name
        // (SDS1202X-E, SDS2104X Plus, SDS800X HD), so the absence of one is the test.
        if (siglent && m.StartsWith("SDS"))
            return m.Contains('X') ? InstrumentFamily.SiglentScope : InstrumentFamily.Generic;

        // GW Instek's GDS scopes: "GDS" clashes with nothing else, but the maker test
        // keeps it consistent with the other vendor families.
        //
        // The newer lines have their own programming manual and a larger set, and they are
        // named for it: GDS-1000B, GDS-2000E, MSO-2000E, MDO-2000E all end in a letter that
        // the GDS-2000 series does not carry.
        if (gwinstek && (m.StartsWith("GDS-1") || m.StartsWith("GDS1")
                         || m.EndsWith("2000E") || m.Contains("-2000E")
                         || m.StartsWith("MSO-2") || m.StartsWith("MDO-2")))
            return InstrumentFamily.GwInstekScopeB;
        if (gwinstek && m.StartsWith("GDS")) return InstrumentFamily.GwInstekScope;

        // Fluke's bench meters. "884" would be far too generic without the maker.
        if (fluke && IsFlukeMultimeter(m)) return InstrumentFamily.FlukeMultimeter;

        // Keithley before the generic multimeter test: a DMM6500 would otherwise be
        // claimed by the "DM" prefix and given the Siglent SDM's much smaller catalog.
        if (keithley && IsKeithleySmu(m)) return InstrumentFamily.KeithleySmu;
        if (keithley && IsKeithleyDmm(m)) return InstrumentFamily.KeithleyDmm;

        // Then spectrum analyzers, because a Rigol DSA800 would otherwise be taken
        // for a scope by the "DS" prefix further down.
        //
        // R&S analyzers are their own family: the SpectrumAnalyzer family carries the
        // Siglent SSA3000X catalog, and an FPC handed those gets a set of commands that
        // look right — the frequency and bandwidth subsystems really do overlap — while
        // the Siglent-specific ones fail. Each R&S analyzer line now has its own catalog.
        if (rohde && m.StartsWith("FPC")) return InstrumentFamily.RohdeSpectrumAnalyzer;
        if (rohde && m.StartsWith("FSL")) return InstrumentFamily.RohdeFslAnalyzer;
        // FSW before FSV: both start "FS", and neither prefix is a prefix of the other, so
        // the order is not load-bearing — but keeping the longest-lived rule last makes the
        // fall-through to Generic below read in the same direction as the list.
        if (rohde && m.StartsWith("FSW")) return InstrumentFamily.RohdeFswAnalyzer;
        // The FSU generation, each line from its own Operating Manual — the fifth, sixth
        // and seventh R&S analyzer command sets. FSPN is excluded from the FSP test on
        // purpose: it is a modern spectrum monitor that happens to share the prefix, and
        // handing it a 2003 catalog is the Siglent-SSA mistake with a different badge.
        if (rohde && m.StartsWith("FSU")) return InstrumentFamily.RohdeFsuAnalyzer;
        if (rohde && m.StartsWith("FSP") && !m.StartsWith("FSPN"))
            return InstrumentFamily.RohdeFspAnalyzer;
        if (rohde && m.StartsWith("FSQ")) return InstrumentFamily.RohdeFsqAnalyzer;
        // FSV and FSVA share the modern R&S command set, which is a third one again — not
        // the FPC's and not the FSL's.
        if (rohde && (m.StartsWith("FSV") || m.StartsWith("FSVA")))
            return InstrumentFamily.RohdeFsvAnalyzer;
        if (rohde && IsSpectrumAnalyzer(m)) return InstrumentFamily.Generic;
        // Rigol's DSA800 is not the Siglent SSA3000X either. The two overlap in the
        // frequency and bandwidth subsystems the way the R&S ones did, which is exactly
        // what makes the mismatch hard to notice from the bench.
        if (rigol && IsSpectrumAnalyzer(m)) return InstrumentFamily.RigolSpectrumAnalyzer;
        if (IsSpectrumAnalyzer(m)) return InstrumentFamily.SpectrumAnalyzer;

        // A Siglent SDM multimeter takes standard SCPI, unlike the SDG generators
        // from the same maker, so it must not fall into the Siglent branch.
        //
        // The Multimeter family is the Siglent SDM catalog, though, so a Rigol DM3058
        // needs its own: the two share :MEASure but not :FUNCtion, :RATE or :CALCulate,
        // and the SDM's own scanner subsystem exists on no Rigol at all.
        if (rigol && IsMultimeter(m)) return InstrumentFamily.RigolMultimeter;
        // Keysight's Truevolt bench meters were the last family still taking another
        // vendor's catalog. Their SCPI reference turned out to be inside the Operating and
        // Service Guide rather than online-help-only, which is what had blocked this.
        if (keysight && IsMultimeter(m)) return InstrumentFamily.KeysightMultimeter;
        if (IsMultimeter(m)) return InstrumentFamily.Multimeter;

        // B&K's loads before the generic test: "86" is in the generic list precisely
        // because of them, and their own catalog is the better match.
        if (bkprecision && IsElectronicLoad(m)) return InstrumentFamily.BkElectronicLoad;

        // Chroma's 63xxx loads are excluded for the same reason as the R&S analyzers:
        // the ElectronicLoad family is the Siglent SDL1000X catalog, and a Chroma load
        // does not take those commands. The 63200A guide is now transcribed, so the
        // 632xx models get their own catalog, and the modular 636xx line has one of its
        // own below; only the AC 638xx loads are still documented nowhere reachable, and
        // they fall through to Generic rather than take a set that is close enough to
        // look right and wrong where it counts.
        if (chroma && m.StartsWith("632")) return InstrumentFamily.ChromaElectronicLoad;
        // The 636xx modular loads have their own manual, and only about half their commands
        // appear in the 63200A set — so they take neither that catalog nor the Siglent one.
        // The 638xx AC loads are documented separately again and still fall to Generic.
        if (chroma && m.StartsWith("636")) return InstrumentFamily.ChromaModularLoad;
        if (chroma && IsElectronicLoad(m)) return InstrumentFamily.Generic;
        // ...and a Rigol DL3000 is not an SDL1000X. It writes its commands under an
        // optional [SOURce] root the Siglent guide never uses, and its transient and
        // battery subsystems have no Siglent counterpart.
        if (rigol && IsElectronicLoad(m)) return InstrumentFamily.RigolElectronicLoad;
        if (IsElectronicLoad(m)) return InstrumentFamily.ElectronicLoad;

        // Chroma's supplies. "62" and "620xx" are far too generic without the maker,
        // and its loads (63xxx) are caught by the electronic-load test above.
        if (chroma && m.StartsWith("62")) return InstrumentFamily.ChromaPowerSupply;

        // A Keysight supply addresses its outputs with a channel list — "VOLTage 5,
        // (@1)" — where a Rigol DP800 uses "CH1". Same subsystem, different calls.
        if (keysight && IsPowerSupply(m)) return InstrumentFamily.KeysightPowerSupply;
        // An R&S supply selects a channel with "INSTrument:NSELect 1" and then applies
        // unqualified commands to it, where a Rigol names the channel in each command.
        if (rohde && IsPowerSupply(m)) return InstrumentFamily.RohdePowerSupply;
        // B&K's two supply lines are split because their guides are. The 9200B's reference is
        // a chapter of its user manual; the 9130B's is a programming manual issued on its own,
        // and describes a triple-output instrument with a channel-selection model the 9200B
        // has no equivalent of. Same vendor, different command sets.
        if (bkprecision && m.StartsWith("920")) return InstrumentFamily.BkPowerSupply;
        if (bkprecision && m.StartsWith("913")) return InstrumentFamily.BkPowerSupply9130;
        if (IsPowerSupply(m)) return InstrumentFamily.PowerSupply;

        // Generator syntax is vendor-specific: Siglent uses its own "C1:BSWV" form,
        // while Rigol DG / Keysight 33xxx / Tek AFG take standard SCPI. Sending one
        // vendor's commands to the other just produces errors, so split them here.
        if (IsGenerator(m)) return siglent ? InstrumentFamily.SiglentGenerator : InstrumentFamily.ScpiGenerator;

        if (IsOscilloscope(m)) return InstrumentFamily.Oscilloscope;
        return InstrumentFamily.Generic;
    }

    /// <summary>Split an *IDN? "Manufacturer,Model,Serial,Firmware" string into maker and model.</summary>
    public static (string Maker, string Model) ParseIdentity(string? identity)
    {
        string maker = "", model = "";
        if (!string.IsNullOrWhiteSpace(identity))
        {
            string[] parts = identity.Split(',');
            if (parts.Length > 0) maker = parts[0].Trim();
            if (parts.Length > 1) model = parts[1].Trim();
        }
        return (maker, model);
    }

    // Siglent SDMxxxx, Rigol DMxxxx, Keysight/Agilent 344xx + 34401A, Keithley 2000/2110.
    private static bool IsMultimeter(string m)
        => m.StartsWith("SDM") || m.StartsWith("DM")
        || m.StartsWith("344") || m.StartsWith("34401")
        || m.StartsWith("2000") || m.StartsWith("2110");

    // Siglent SDGxxxx, Rigol DGxxxx, Tektronix AFGxxxx, Keysight 33xxx.
    private static bool IsGenerator(string m)
        => m.StartsWith("SDG") || m.StartsWith("DG") || m.StartsWith("AFG") || m.StartsWith("33");

    // Rigol DSxxxx/MSOxxxx, Siglent SDSxxxx, Tektronix TDS/MDO/DPO, Keysight DSO.
    private static bool IsOscilloscope(string m)
        => m.StartsWith("DS") || m.StartsWith("MSO") || m.StartsWith("SDS")
        || m.StartsWith("TDS") || m.StartsWith("MDO") || m.StartsWith("DPO")
        || m.StartsWith("DSO");

    // Tektronix scope model lines: TDS, DPO, MSO, MDO, DSA, and the LPD low-profile
    // digitizers. Only consulted when the maker is Tektronix — every one of these
    // prefixes is used by somebody else too.
    private static bool IsTektronixScope(string m)
        => m.StartsWith("TDS") || m.StartsWith("DPO") || m.StartsWith("MSO")
        || m.StartsWith("MDO") || m.StartsWith("DSA") || m.StartsWith("LPD");

    // R&S scope lines: RTB/RTM/RTA bench scopes, RTO/RTE/RTP lab scopes, RTH/Scope
    // Rider handhelds, and the older HAMEG HMO series.
    private static bool IsRohdeScope(string m)
        => m.StartsWith("RTB") || m.StartsWith("RTM") || m.StartsWith("RTA")
        || m.StartsWith("RTO") || m.StartsWith("RTE") || m.StartsWith("RTP")
        || m.StartsWith("RTH") || m.StartsWith("HMO");

    // Fluke bench multimeters: the 8845A/8846A pair and the 8808A. Only consulted
    // when the maker is Fluke — "45" and "88" mean nothing on their own.
    private static bool IsFlukeMultimeter(string m)
        => m.StartsWith("8845") || m.StartsWith("8846") || m.StartsWith("884")
        || m.StartsWith("8808") || m.StartsWith("8808A") || m == "45";

    // Keithley source-measure units: 24xx (2400/2410/2450/2460/2470), 26xx
    // (2601–2657), and the electrometer/source lines 6221, 6430, 6514, 6517.
    // Only consulted when the maker is Keithley — "24xx" is far too generic alone.
    private static bool IsKeithleySmu(string m)
        => m.StartsWith("24") || m.StartsWith("26")
        || m.StartsWith("6221") || m.StartsWith("6430")
        || m.StartsWith("6514") || m.StartsWith("6517");

    // Keithley meters: DMM6500/DMM7510, and the 2000/2001/2010/2100/2110 and
    // 2700/2701/2750 scanning-DMM lines.
    private static bool IsKeithleyDmm(string m)
        => m.StartsWith("DMM") || m.StartsWith("20") || m.StartsWith("21")
        || m.StartsWith("27");

    // Rigol DSA/RSA, Siglent SSA/SVA, R&S FPC/FSL/FSV/FSW, Keysight N90xx
    // (CXA/EXA/MXA X-series), Anritsu MS2xxx handheld analyzers.
    private static bool IsSpectrumAnalyzer(string m)
        => m.StartsWith("DSA") || m.StartsWith("RSA") || m.StartsWith("SSA")
        || m.StartsWith("SVA") || m.StartsWith("FPC") || m.StartsWith("FSL")
        || m.StartsWith("FSV") || m.StartsWith("FSW") || m.StartsWith("N90")
        || m.StartsWith("MS2")
        // The FSE and FSIQ generations, and the modern FSPN spectrum monitor: recognised
        // as analyzers so the R&S decline above catches them — no guide for any of the
        // three is here, and an R&S analyzer with no catalog of its own must go Generic
        // rather than fall through to the Siglent set.
        || m.StartsWith("FSE") || m.StartsWith("FSIQ") || m.StartsWith("FSPN");

    // Rigol DP/DP900, Siglent SPD, Keysight E36xx, Keithley 22xx,
    // R&S HMP/HMC804/NGE/NGL/NGM/NGP, Tektronix PWS.
    //
    // "DPO" is excluded deliberately: a Tektronix DPO4104 is an oscilloscope, and
    // it would otherwise be claimed here by the Rigol DP800's "DP" prefix.
    private static bool IsPowerSupply(string m)
        => (m.StartsWith("DP") && !m.StartsWith("DPO"))
        || m.StartsWith("SPD") || m.StartsWith("E36") || m.StartsWith("PWS")
        || m.StartsWith("HMP") || m.StartsWith("HMC8") || m.StartsWith("NGE")
        || m.StartsWith("NGL") || m.StartsWith("NGM") || m.StartsWith("NGP")
        || m.StartsWith("2200") || m.StartsWith("2220") || m.StartsWith("2230")
        || m.StartsWith("2231") || m.StartsWith("2260") || m.StartsWith("2280")
        || m.StartsWith("2281");

    // Rigol DL3000, Siglent SDL1000X, Keysight EL3000/N33xx, ITECH IT85xx,
    // Chroma 63xxx, B&K Precision 86xx.
    private static bool IsElectronicLoad(string m)
        => m.StartsWith("DL") || m.StartsWith("SDL") || m.StartsWith("EL3")
        || m.StartsWith("N33") || m.StartsWith("IT8") || m.StartsWith("63")
        || m.StartsWith("86");

    /// <summary>Standard SCPI scope commands (verified against a Rigol DS2202).</summary>
    private static InstrumentProfile Oscilloscope(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Oscilloscope" : $"Oscilloscope ({model})",
        // Rigol DS/MSO scopes return the screen as a BMP block from :DISPlay:DATA?.
        ScreenCaptureCommand = ":DISPlay:DATA?",
        WaveformDialect = WaveformDialect.Rigol,
        Commands = new[]
        {
            new QuickCommand("Run",       ":RUN"),
            new QuickCommand("Stop",      ":STOP"),
            new QuickCommand("Single",    ":SINGle"),
            new QuickCommand("Autoscale", ":AUToscale"),
            new QuickCommand("CH1 On",    ":CHANnel1:DISPlay ON"),
            new QuickCommand("CH1 Off",   ":CHANnel1:DISPlay OFF"),
            new QuickCommand("Vpp CH1",   ":MEASure:VPP? CHANnel1"),
            new QuickCommand("Freq CH1",  ":MEASure:FREQuency? CHANnel1"),
            new QuickCommand("V/div?",    ":CHANnel1:SCALe?"),
            new QuickCommand("*IDN?",     "*IDN?"),
            new QuickCommand("*CLS",      "*CLS"),
        },
    };

    /// <summary>
    /// Siglent SDG-style generator commands (verified against an SDG2042X).
    /// These are NOT standard SCPI — Siglent uses its own "C1:BSWV" syntax.
    /// </summary>
    private static InstrumentProfile SiglentGenerator(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Siglent generator" : $"Siglent generator ({model})",
        Commands = new[]
        {
            new QuickCommand("CH1 On",    "C1:OUTP ON"),
            new QuickCommand("CH1 Off",   "C1:OUTP OFF"),
            new QuickCommand("CH2 On",    "C2:OUTP ON"),
            new QuickCommand("CH2 Off",   "C2:OUTP OFF"),
            new QuickCommand("CH1 Wave?", "C1:BSWV?"),
            new QuickCommand("CH1 Out?",  "C1:OUTP?"),
            new QuickCommand("Sine",      "C1:BSWV WVTP,SINE"),
            new QuickCommand("Square",    "C1:BSWV WVTP,SQUARE"),
            new QuickCommand("1 kHz",     "C1:BSWV FRQ,1000HZ"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>
    /// Standard-SCPI generator commands — Rigol DG, Keysight 33xxx, Tektronix AFG.
    /// (Not verified against real hardware here; no such unit on this bench.)
    /// </summary>
    private static InstrumentProfile ScpiGenerator(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Waveform generator" : $"Waveform generator ({model})",
        Commands = new[]
        {
            new QuickCommand("CH1 On",   ":OUTPut1 ON"),
            new QuickCommand("CH1 Off",  ":OUTPut1 OFF"),
            new QuickCommand("CH2 On",   ":OUTPut2 ON"),
            new QuickCommand("CH2 Off",  ":OUTPut2 OFF"),
            new QuickCommand("Func?",    ":SOURce1:FUNCtion?"),
            new QuickCommand("Freq?",    ":SOURce1:FREQuency?"),
            new QuickCommand("Sine",     ":SOURce1:FUNCtion SIN"),
            new QuickCommand("Square",   ":SOURce1:FUNCtion SQU"),
            new QuickCommand("1 kHz",    ":SOURce1:FREQuency 1000"),
            new QuickCommand("*IDN?",    "*IDN?"),
        },
    };

    /// <summary>
    /// Digital-multimeter commands (verified against a Siglent SDM3065X's programming
    /// guide). Standard SCPI MEASure? queries — note this is NOT Siglent's generator
    /// dialect, even though both instruments come from the same maker.
    /// </summary>
    private static InstrumentProfile Multimeter(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Multimeter" : $"Multimeter ({model})",
        // Each of these sets the meter's function and returns one reading, so they can be
        // called on a timer to build a trend. Same queries as the quick commands below.
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("DC volts",            "MEASure:VOLTage:DC?",  "V"),
            new ReadoutFunction("AC volts",            "MEASure:VOLTage:AC?",  "V"),
            new ReadoutFunction("DC current",          "MEASure:CURRent:DC?",  "A"),
            new ReadoutFunction("AC current",          "MEASure:CURRent:AC?",  "A"),
            new ReadoutFunction("Resistance (2-wire)", "MEASure:RESistance?",  "Ω"),
            new ReadoutFunction("Resistance (4-wire)", "MEASure:FRESistance?", "Ω"),
            new ReadoutFunction("Frequency",           "MEASure:FREQuency?",   "Hz"),
            new ReadoutFunction("Capacitance",         "MEASure:CAPacitance?", "F"),
        },
        Commands = new[]
        {
            new QuickCommand("DC V",    "MEASure:VOLTage:DC?"),
            new QuickCommand("AC V",    "MEASure:VOLTage:AC?"),
            new QuickCommand("DC I",    "MEASure:CURRent:DC?"),
            new QuickCommand("AC I",    "MEASure:CURRent:AC?"),
            new QuickCommand("2-wire Ω", "MEASure:RESistance?"),
            new QuickCommand("4-wire Ω", "MEASure:FRESistance?"),
            new QuickCommand("Freq",    "MEASure:FREQuency?"),
            new QuickCommand("Cap",     "MEASure:CAPacitance?"),
            new QuickCommand("Diode",   "MEASure:DIODe?"),
            new QuickCommand("Continuity", "MEASure:CONTinuity?"),
            new QuickCommand("Read",    "READ?"),
            new QuickCommand("*IDN?",   "*IDN?"),
        },
    };

    /// <summary>
    /// DC power supply commands, transcribed from the Rigol DP800 Series Programming
    /// Guide. The channel-suffixed forms (":MEASure:VOLTage? CH1") are what the DP800
    /// documents; a single-output supply accepts the same commands without the suffix.
    /// (Not verified against real hardware — no bench supply here.)
    /// </summary>
    private static InstrumentProfile PowerSupply(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "DC power supply" : $"DC power supply ({model})",
        // A supply's output is exactly the kind of slow-moving quantity the readout
        // window was built for — logging a rail while a board warms up, say.
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("CH1 voltage", ":MEASure:VOLTage:DC? CH1", "V"),
            new ReadoutFunction("CH1 current", ":MEASure:CURRent:DC? CH1", "A"),
            new ReadoutFunction("CH1 power",   ":MEASure:POWEr:DC? CH1",   "W"),
            new ReadoutFunction("CH2 voltage", ":MEASure:VOLTage:DC? CH2", "V"),
            new ReadoutFunction("CH2 current", ":MEASure:CURRent:DC? CH2", "A"),
        },
        Commands = new[]
        {
            new QuickCommand("CH1 On",   ":OUTPut CH1,ON"),
            new QuickCommand("CH1 Off",  ":OUTPut CH1,OFF"),
            new QuickCommand("CH2 On",   ":OUTPut CH2,ON"),
            new QuickCommand("CH2 Off",  ":OUTPut CH2,OFF"),
            new QuickCommand("Out?",     ":OUTPut? CH1"),
            new QuickCommand("V CH1",    ":MEASure:VOLTage:DC? CH1"),
            new QuickCommand("I CH1",    ":MEASure:CURRent:DC? CH1"),
            new QuickCommand("All CH1",  ":MEASure:ALL:DC? CH1"),
            new QuickCommand("Apply?",   ":APPLy? CH1"),
            new QuickCommand("Mode?",    ":OUTPut:MODE? CH1"),
            new QuickCommand("Errors",   ":SYSTem:ERRor?"),
            new QuickCommand("*IDN?",    "*IDN?"),
        },
    };

    /// <summary>
    /// DC electronic load commands, transcribed from the Siglent SDL1000X Programming
    /// Guide. Standard SCPI — the load's own dialect is not a thing, unlike the SDG.
    /// (Not verified against real hardware — no bench load here.)
    /// </summary>
    private static InstrumentProfile ElectronicLoad(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Electronic load" : $"Electronic load ({model})",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("Voltage",    "MEASure:VOLTage:DC?",    "V"),
            new ReadoutFunction("Current",    "MEASure:CURRent:DC?",    "A"),
            new ReadoutFunction("Power",      "MEASure:POWer:DC?",      "W"),
            new ReadoutFunction("Resistance", "MEASure:RESistance:DC?", "Ω"),
        },
        Commands = new[]
        {
            new QuickCommand("Input On",  ":SOURce:INPut:STATe ON"),
            new QuickCommand("Input Off", ":SOURce:INPut:STATe OFF"),
            new QuickCommand("Input?",    ":SOURce:INPut:STATe?"),
            new QuickCommand("Volts",     "MEASure:VOLTage:DC?"),
            new QuickCommand("Amps",      "MEASure:CURRent:DC?"),
            new QuickCommand("Watts",     "MEASure:POWer:DC?"),
            new QuickCommand("Ohms",      "MEASure:RESistance:DC?"),
            new QuickCommand("CC mode",   ":SOURce:FUNCtion CURRent"),
            new QuickCommand("CV mode",   ":SOURce:FUNCtion VOLTage"),
            new QuickCommand("Mode?",     ":SOURce:FUNCtion?"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>
    /// Spectrum analyzer commands, transcribed from the Siglent SSA3000X Series
    /// Programming Guide. The Rigol DSA800 guide documents the same headers for the
    /// frequency, bandwidth and trace subsystems.
    /// (Not verified against real hardware — no bench analyzer here.)
    /// </summary>
    private static InstrumentProfile SpectrumAnalyzer(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Spectrum analyzer" : $"Spectrum analyzer ({model})",
        Commands = new[]
        {
            new QuickCommand("Centre?",   ":SENSe:FREQuency:CENTer?"),
            new QuickCommand("Span?",     ":SENSe:FREQuency:SPAN?"),
            new QuickCommand("Start?",    ":SENSe:FREQuency:STARt?"),
            new QuickCommand("Stop?",     ":SENSe:FREQuency:STOP?"),
            new QuickCommand("Zero span", ":SENSe:FREQuency:SPAN:ZERO"),
            new QuickCommand("RBW?",      ":SENSe:BWIDth:RESolution?"),
            new QuickCommand("VBW?",      ":SENSe:BWIDth:VIDeo?"),
            new QuickCommand("Ref lvl?",  ":DISPlay:WINDow:TRACe:Y:SCALe:RLEVel?"),
            new QuickCommand("Sweep t?",  ":SENSe:SWEep:TIME?"),
            new QuickCommand("Single",    ":INITiate:IMMediate"),
            new QuickCommand("Markers off", ":CALCulate:MARKer:AOFF"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>
    /// Tektronix oscilloscope commands, transcribed from the MDO4000C/MDO4000B/MDO4000/
    /// MSO4000B/DPO4000B/MDO3000 Programmer Manual.
    ///
    /// A different dialect from the Rigol scope profile above, not a variation on it:
    /// Tektronix writes "CH1:SCAle" where Rigol writes ":CHANnel1:SCALe", runs the
    /// acquisition with "ACQuire:STATE RUN" rather than ":RUN", and reads the error
    /// queue with "ALLEv?" rather than ":SYSTem:ERRor:NEXT?".
    ///
    /// Neither capture button is offered, deliberately:
    ///
    /// - Screen capture would need two commands (set "SAVe:IMAGe:FILEFormat", then
    ///   "HARDCopy STARt"), where the profile carries a single query. The script
    ///   examples show the sequence instead, so it stays visible and under control.
    /// - Waveform capture reads the Rigol ":WAVeform" tree and decodes its 10-field
    ///   preamble (SPEC §11). Tektronix uses "CURVe?" with a "WFMOutpre" preamble of
    ///   a different shape, so the existing decoder would return plausible nonsense.
    ///
    /// (Not verified against real hardware — no Tektronix instrument on this bench.)
    /// </summary>
    private static InstrumentProfile TektronixScope(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Tektronix oscilloscope" : $"Tektronix oscilloscope ({model})",
        // "HARDCopy STARt sends a block of data representing the current screen image to the
        // requested port", in whatever SAVe:IMAGe:FILEFormat is set to — so the format is set
        // first, in the console, rather than being a parameter of the dump itself.
        ScreenCaptureCommand = "HARDCopy STARt",
        // PNG because the same manual notes BMP and TIFF go over uncompressed while PNG does
        // not, and this is a screen's worth of flat colour across a 3 000 ms default timeout.
        ScreenCaptureSetup = new[] { "SAVe:IMAGe:FILEFormat PNG" },
        WaveformDialect = WaveformDialect.Tektronix,
        Commands = new[]
        {
            new QuickCommand("Run",       "ACQuire:STATE RUN"),
            new QuickCommand("Stop",      "ACQuire:STATE STOP"),
            new QuickCommand("Single",    "ACQuire:STOPAfter SEQuence"),
            new QuickCommand("Autoset",   "AUTOSet EXECute"),
            new QuickCommand("CH1 On",    "SELect:CH1 ON"),
            new QuickCommand("CH1 Off",   "SELect:CH1 OFF"),
            new QuickCommand("V/div?",    "CH1:SCAle?"),
            new QuickCommand("Time/div?", "HORizontal:SCAle?"),
            new QuickCommand("Meas CH1",  "MEASUrement:IMMed:SOUrce1 CH1"),
            new QuickCommand("Meas Vpp",  "MEASUrement:IMMed:TYPe PK2pk"),
            new QuickCommand("Meas?",     "MEASUrement:IMMed:VALue?"),
            new QuickCommand("Errors",    "ALLEv?"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>
    /// Keysight oscilloscope commands, transcribed from the InfiniiVision 3000T
    /// X-Series Programmer's Guide.
    ///
    /// Close to the Rigol scope profile — Rigol modelled its command set on Agilent's,
    /// so ":CHANnel1:SCALe" and ":MEASure:VPP?" mean the same on both — but the
    /// Keysight set is far larger, which is why it gets its own catalog.
    ///
    /// Waveform capture is not offered. The preamble has the same ten fields as the
    /// Rigol one, but the conversion differs: the guide gives
    /// <c>voltage = (data - yreference) * yincrement + yorigin</c>, where the decoder
    /// this app implements (SPEC §11) subtracts yorigin *before* scaling. Reusing it
    /// would produce a plausible trace with the wrong offset.
    ///
    /// (Not verified against real hardware — no Keysight instrument on this bench.)
    /// </summary>
    private static InstrumentProfile KeysightScope(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Keysight oscilloscope" : $"Keysight oscilloscope ({model})",
        // Documented as ":DISPlay:DATA? [<format>][,<palette>]". Note :HARDcopy:INKSaver
        // defaults to ON, so the image comes back on a white background.
        ScreenCaptureCommand = ":DISPlay:DATA? PNG,COLor",
        WaveformDialect = WaveformDialect.Keysight,
        Commands = new[]
        {
            new QuickCommand("Run",       ":RUN"),
            new QuickCommand("Stop",      ":STOP"),
            new QuickCommand("Single",    ":SINGle"),
            new QuickCommand("Autoscale", ":AUToscale"),
            new QuickCommand("CH1 On",    ":CHANnel1:DISPlay ON"),
            new QuickCommand("CH1 Off",   ":CHANnel1:DISPlay OFF"),
            new QuickCommand("Vpp CH1",   ":MEASure:VPP? CHANnel1"),
            new QuickCommand("Freq CH1",  ":MEASure:FREQuency? CHANnel1"),
            new QuickCommand("V/div?",    ":CHANnel1:SCALe?"),
            new QuickCommand("Time/div?", ":TIMebase:SCALe?"),
            new QuickCommand("Errors",    ":SYSTem:ERRor?"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>
    /// Keysight DC power supply commands, transcribed from the E36300 Series
    /// Programming Guide.
    ///
    /// Addresses outputs with a channel list — "VOLTage 5,(@1)" — where a Rigol DP800
    /// writes "CH1". Same subsystem names, incompatible calls, hence the separate family.
    /// (Not verified against real hardware — no Keysight supply on this bench.)
    /// </summary>
    private static InstrumentProfile KeysightPowerSupply(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Keysight power supply" : $"Keysight power supply ({model})",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("CH1 voltage", "MEASure:VOLTage:DC? (@1)", "V"),
            new ReadoutFunction("CH1 current", "MEASure:CURRent:DC? (@1)", "A"),
            new ReadoutFunction("CH2 voltage", "MEASure:VOLTage:DC? (@2)", "V"),
            new ReadoutFunction("CH2 current", "MEASure:CURRent:DC? (@2)", "A"),
            new ReadoutFunction("CH3 voltage", "MEASure:VOLTage:DC? (@3)", "V"),
            new ReadoutFunction("CH3 current", "MEASure:CURRent:DC? (@3)", "A"),
        },
        Commands = new[]
        {
            new QuickCommand("CH1 On",   "OUTPut ON,(@1)"),
            new QuickCommand("CH1 Off",  "OUTPut OFF,(@1)"),
            new QuickCommand("CH2 On",   "OUTPut ON,(@2)"),
            new QuickCommand("CH2 Off",  "OUTPut OFF,(@2)"),
            new QuickCommand("Out?",     "OUTPut? (@1)"),
            new QuickCommand("V CH1",    "MEASure:VOLTage:DC? (@1)"),
            new QuickCommand("I CH1",    "MEASure:CURRent:DC? (@1)"),
            new QuickCommand("Vset?",    "VOLTage? (@1)"),
            new QuickCommand("Iset?",    "CURRent? (@1)"),
            new QuickCommand("Errors",   "SYSTem:ERRor?"),
            new QuickCommand("*IDN?",    "*IDN?"),
        },
    };

    /// <summary>
    /// Keithley SourceMeter commands, transcribed from the Model 2450 Reference Manual.
    ///
    /// A source-measure unit sources one quantity while measuring another, so its
    /// commands split across ":SOURce" and "[:SENSe[1]]" in a way no other family here
    /// does. The guide writes a measurement function as a placeholder,
    /// ":MEASure:&lt;function&gt;?", which stands for ":MEASure:VOLTage:DC?" and the rest.
    ///
    /// Note a 2450 ships able to speak either SCPI or Keithley's own TSP, and answers
    /// none of this until "*LANG SCPI" has been sent and the instrument rebooted.
    ///
    /// (Not verified against real hardware — no Keithley instrument on this bench.)
    /// </summary>
    private static InstrumentProfile KeithleySmu(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Keithley SourceMeter" : $"Keithley SourceMeter ({model})",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("Voltage",    ":MEASure:VOLTage:DC?", "V"),
            new ReadoutFunction("Current",    ":MEASure:CURRent:DC?", "A"),
            new ReadoutFunction("Resistance", ":MEASure:RESistance?", "Ω"),
        },
        Commands = new[]
        {
            new QuickCommand("Output On",  ":OUTPut:STATe ON"),
            new QuickCommand("Output Off", ":OUTPut:STATe OFF"),
            new QuickCommand("Output?",    ":OUTPut:STATe?"),
            new QuickCommand("Source V",   ":SOURce:FUNCtion VOLTage"),
            new QuickCommand("Source I",   ":SOURce:FUNCtion CURRent"),
            new QuickCommand("Source?",    ":SOURce:FUNCtion?"),
            new QuickCommand("Volts",      ":MEASure:VOLTage:DC?"),
            new QuickCommand("Amps",       ":MEASure:CURRent:DC?"),
            new QuickCommand("Ohms",       ":MEASure:RESistance?"),
            new QuickCommand("Read",       ":READ?"),
            new QuickCommand("Errors",     ":SYSTem:ERRor?"),
            new QuickCommand("*IDN?",      "*IDN?"),
        },
    };

    /// <summary>
    /// Keithley multimeter commands, transcribed from the DMM6500 Reference Manual.
    ///
    /// Standard SCPI, but a much larger set than the Siglent SDM catalog covers, and
    /// with the same TSP caveat as the SourceMeter above — "*LANG SCPI" then a reboot.
    /// (Not verified against real hardware — no Keithley instrument on this bench.)
    /// </summary>
    private static InstrumentProfile KeithleyDmm(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Keithley multimeter" : $"Keithley multimeter ({model})",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("DC volts",            ":MEASure:VOLTage:DC?",  "V"),
            new ReadoutFunction("AC volts",            ":MEASure:VOLTage:AC?",  "V"),
            new ReadoutFunction("DC current",          ":MEASure:CURRent:DC?",  "A"),
            new ReadoutFunction("AC current",          ":MEASure:CURRent:AC?",  "A"),
            new ReadoutFunction("Resistance (2-wire)", ":MEASure:RESistance?",  "Ω"),
            new ReadoutFunction("Resistance (4-wire)", ":MEASure:FRESistance?", "Ω"),
            new ReadoutFunction("Frequency",           ":MEASure:FREQuency?",   "Hz"),
            new ReadoutFunction("Capacitance",         ":MEASure:CAPacitance?", "F"),
        },
        Commands = new[]
        {
            new QuickCommand("DC V",       ":MEASure:VOLTage:DC?"),
            new QuickCommand("AC V",       ":MEASure:VOLTage:AC?"),
            new QuickCommand("DC I",       ":MEASure:CURRent:DC?"),
            new QuickCommand("AC I",       ":MEASure:CURRent:AC?"),
            new QuickCommand("2-wire Ω",   ":MEASure:RESistance?"),
            new QuickCommand("4-wire Ω",   ":MEASure:FRESistance?"),
            new QuickCommand("Freq",       ":MEASure:FREQuency?"),
            new QuickCommand("Cap",        ":MEASure:CAPacitance?"),
            new QuickCommand("Diode",      ":MEASure:DIODe?"),
            new QuickCommand("Read",       ":READ?"),
            new QuickCommand("Errors",     ":SYSTem:ERRor?"),
            new QuickCommand("*IDN?",      "*IDN?"),
        },
    };

    /// <summary>
    /// Rohde &amp; Schwarz oscilloscope commands, transcribed from the RTB2000 User
    /// Manual, whose Remote Control Commands chapter also covers the RTM3000 and RTA4000.
    ///
    /// A third scope dialect, distinct from both the Rigol and Tektronix ones: R&amp;S
    /// writes its headers without a leading colon (<c>CHANnel1:SCALe</c>, not
    /// <c>:CHANnel1:SCALe</c>), runs with a bare <c>RUN</c> rather than <c>:RUN</c>,
    /// and takes measurements through numbered measurement "places" —
    /// <c>MEASurement1:MAIN</c> selects what to measure, <c>MEASurement1:RESult:ACTual?</c>
    /// reads it.
    ///
    /// Waveform capture is not offered: R&amp;S reads traces with <c>CHANnel&lt;m&gt;:DATA?</c>
    /// and a <c>:DATA:HEADer?</c> of a different shape from the Rigol ten-field preamble
    /// the decoder implements (SPEC §11).
    ///
    /// (Not verified against real hardware — no R&amp;S instrument on this bench.)
    /// </summary>
    private static InstrumentProfile RohdeScope(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "R&S oscilloscope" : $"R&S oscilloscope ({model})",
        // Documented as returning the screen in the format set by HCOPy:FORMat.
        ScreenCaptureCommand = "HCOPy:DATA?",
        WaveformDialect = WaveformDialect.RohdeAscii,
        Commands = new[]
        {
            new QuickCommand("Run",       "RUN"),
            new QuickCommand("Stop",      "STOP"),
            new QuickCommand("Single",    "SINGle"),
            new QuickCommand("Autoset",   "AUToscale"),
            new QuickCommand("CH1 On",    "CHANnel1:STATe ON"),
            new QuickCommand("CH1 Off",   "CHANnel1:STATe OFF"),
            new QuickCommand("V/div?",    "CHANnel1:SCALe?"),
            new QuickCommand("Time/div?", "TIMebase:SCALe?"),
            new QuickCommand("Meas CH1",  "MEASurement1:SOURce CH1"),
            new QuickCommand("Meas Vpp",  "MEASurement1:MAIN PEAK"),
            new QuickCommand("Meas?",     "MEASurement1:RESult:ACTual?"),
            new QuickCommand("Errors",    "SYSTem:ERRor:NEXT?"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>
    /// Rohde &amp; Schwarz DC power supply commands, transcribed from the NGL200/NGM200
    /// User Manual, with the NGE100 and HMP guides for the older lines.
    ///
    /// Channel handling differs from every other supply here: rather than naming the
    /// channel in each command, you select one with <c>INSTrument:NSELect 1</c> and the
    /// unqualified commands then apply to it. <c>OUTPut:SELect</c> arms a channel and
    /// <c>OUTPut:GENeral</c> is the master switch.
    ///
    /// (Not verified against real hardware — no R&amp;S instrument on this bench.)
    /// </summary>
    private static InstrumentProfile RohdePowerSupply(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "R&S power supply" : $"R&S power supply ({model})",
        // Readings apply to whichever channel INSTrument:NSELect last chose.
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("Voltage", "MEASure:SCALar:VOLTage:DC?", "V"),
            new ReadoutFunction("Current", "MEASure:SCALar:CURRent:DC?", "A"),
            new ReadoutFunction("Power",   "MEASure:SCALar:POWer?",      "W"),
        },
        Commands = new[]
        {
            new QuickCommand("Select CH1", "INSTrument:NSELect 1"),
            new QuickCommand("Select CH2", "INSTrument:NSELect 2"),
            new QuickCommand("CH?",        "INSTrument:NSELect?"),
            new QuickCommand("Ch On",      "OUTPut:SELect ON"),
            new QuickCommand("Ch Off",     "OUTPut:SELect OFF"),
            new QuickCommand("Master On",  "OUTPut:GENeral ON"),
            new QuickCommand("Master Off", "OUTPut:GENeral OFF"),
            new QuickCommand("Out?",       "OUTPut:STATe?"),
            new QuickCommand("Volts",      "MEASure:SCALar:VOLTage:DC?"),
            new QuickCommand("Amps",       "MEASure:SCALar:CURRent:DC?"),
            new QuickCommand("Watts",      "MEASure:SCALar:POWer?"),
            new QuickCommand("*IDN?",      "*IDN?"),
        },
    };

    /// <summary>
    /// Siglent oscilloscope commands, transcribed from the SDS Series Programming Guide.
    ///
    /// A fifth scope dialect. Siglent runs the acquisition through the trigger subsystem
    /// (<c>:TRIGger:RUN</c>, <c>:TRIGger:STOP</c>), switches a channel on with
    /// <c>:CHANnel1:SWITch</c> rather than <c>:DISPlay</c>, and measures by naming a
    /// source and then asking for a named parameter —
    /// <c>:MEASure:SIMPle:SOURce C1</c> then <c>:MEASure:SIMPle:VALue? PKPK</c>.
    ///
    /// This is the modern standard-SCPI set. First-generation SDS models take an older
    /// LeCroy-derived dialect (<c>C1:VDIV</c>, <c>TDIV</c>, <c>TRMD</c>) that is not
    /// covered here — those instruments will connect and work from the command line,
    /// but the catalog and buttons will not match them.
    ///
    /// Waveform capture is not offered: <c>:WAVeform:PREamble?</c> returns a packed
    /// binary descriptor, not the Rigol ten-field comma list the decoder expects (§11).
    ///
    /// (Not verified against real hardware — the bench Siglents are an SDG and an SDM.)
    /// </summary>
    private static InstrumentProfile SiglentScope(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Siglent oscilloscope" : $"Siglent oscilloscope ({model})",
        // Documented as ":PRINt? <type>" with <type> := {BMP|PNG}.
        ScreenCaptureCommand = ":PRINt? BMP",
        WaveformDialect = WaveformDialect.Siglent,
        Commands = new[]
        {
            new QuickCommand("Run",       ":TRIGger:RUN"),
            new QuickCommand("Stop",      ":TRIGger:STOP"),
            new QuickCommand("Autoset",   ":AUToset"),
            new QuickCommand("CH1 On",    ":CHANnel1:SWITch ON"),
            new QuickCommand("CH1 Off",   ":CHANnel1:SWITch OFF"),
            new QuickCommand("V/div?",    ":CHANnel1:SCALe?"),
            new QuickCommand("Time/div?", ":TIMebase:SCALe?"),
            new QuickCommand("Meas CH1",  ":MEASure:SIMPle:SOURce C1"),
            new QuickCommand("Vpp?",      ":MEASure:SIMPle:VALue? PKPK"),
            new QuickCommand("Freq?",     ":MEASure:SIMPle:VALue? FREQ"),
            new QuickCommand("Trig mode?", ":TRIGger:MODE?"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>
    /// Fluke bench multimeter commands, transcribed from the 8845A/8846A Programmers
    /// Manual. Standard SCPI, closely following the Keysight 34401A set.
    ///
    /// Reachable over LAN: the meter listens on a raw socket, default port 3490, which
    /// is not one of the app's default scan ports — add it to the port list to find one.
    ///
    /// (Not verified against real hardware — no Fluke instrument on this bench.)
    /// </summary>
    private static InstrumentProfile FlukeMultimeter(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Fluke multimeter" : $"Fluke multimeter ({model})",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("DC volts",            "MEASure:VOLTage:DC?",   "V"),
            new ReadoutFunction("AC volts",            "MEASure:VOLTage:AC?",   "V"),
            new ReadoutFunction("DC current",          "MEASure:CURRent:DC?",   "A"),
            new ReadoutFunction("AC current",          "MEASure:CURRent:AC?",   "A"),
            new ReadoutFunction("Resistance (2-wire)", "MEASure:RESistance?",   "Ω"),
            new ReadoutFunction("Resistance (4-wire)", "MEASure:FRESistance?",  "Ω"),
            new ReadoutFunction("Frequency",           "MEASure:FREQuency?",    "Hz"),
            new ReadoutFunction("Capacitance",         "MEASure:CAPacitance?",  "F"),
        },
        Commands = new[]
        {
            new QuickCommand("DC V",     "MEASure:VOLTage:DC?"),
            new QuickCommand("AC V",     "MEASure:VOLTage:AC?"),
            new QuickCommand("DC I",     "MEASure:CURRent:DC?"),
            new QuickCommand("AC I",     "MEASure:CURRent:AC?"),
            new QuickCommand("2-wire Ω", "MEASure:RESistance?"),
            new QuickCommand("4-wire Ω", "MEASure:FRESistance?"),
            new QuickCommand("Freq",     "MEASure:FREQuency?"),
            new QuickCommand("Cap",      "MEASure:CAPacitance?"),
            new QuickCommand("Diode",    "MEASure:DIODe?"),
            new QuickCommand("Read",     "READ?"),
            new QuickCommand("Errors",   "SYSTem:ERRor?"),
            new QuickCommand("*IDN?",    "*IDN?"),
        },
    };

    /// <summary>
    /// GW Instek oscilloscope commands, transcribed from the GDS-2000 Series
    /// Programming Manual.
    ///
    /// Standard-SCPI shaped and close to the Rigol set — <c>:RUN</c>, <c>:STOP</c>,
    /// <c>:AUToset</c>, <c>:CHANnel&lt;X&gt;:SCALe</c> — but the measurements are bare
    /// queries against one selected source (<c>:MEASure:SOURce 1</c>, then
    /// <c>:MEASure:VPP?</c>) rather than a channel argument per query, and the guide
    /// documents only five IEEE 488.2 common commands.
    ///
    /// (Not verified against real hardware — no GW Instek instrument on this bench.)
    /// </summary>
    private static InstrumentProfile GwInstekScope(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "GW Instek oscilloscope" : $"GW Instek oscilloscope ({model})",
        // No screen capture and no waveform capture, and in both cases because the GDS-2000
        // programming manual documents no way to do it rather than because it has not been
        // written yet.
        //
        // Screen: :COPY "sends a copy of the screen display … to the flash disk or printer".
        // There is no query that returns the image over the wire, the way :DISPlay:DATA? and
        // HCOPy:DATA? do elsewhere.
        //
        // Waveform: :ACQuire<X>:MEMory? does return the samples, and the manual is precise
        // about the framing — an eight-byte header carrying the sample interval as a
        // little-endian float, then two bytes per point, MSB first. What it never states is
        // how a stored code becomes a voltage. Without the digitizing levels per division
        // that conversion needs a constant, and a constant nobody has written down is a
        // guess. A trace drawn against a guessed vertical scale looks entirely convincing
        // and is wrong by an unknown factor, which is worse than the button being absent.
        Commands = new[]
        {
            new QuickCommand("Run",       ":RUN"),
            new QuickCommand("Stop",      ":STOP"),
            new QuickCommand("Autoset",   ":AUToset"),
            new QuickCommand("CH1 On",    ":CHANnel1:DISPlay 1"),
            new QuickCommand("CH1 Off",   ":CHANnel1:DISPlay 0"),
            new QuickCommand("V/div?",    ":CHANnel1:SCALe?"),
            new QuickCommand("Time/div?", ":TIMebase:SCALe?"),
            new QuickCommand("Meas CH1",  ":MEASure:SOURce 1"),
            new QuickCommand("Vpp?",      ":MEASure:VPP?"),
            new QuickCommand("Freq?",     ":MEASure:FREQuency?"),
            new QuickCommand("Vavg?",     ":MEASure:VAVerage?"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>
    /// Chroma DC power supply commands, transcribed from the 62000L Series User's
    /// Manual. The 62000P series shares the set.
    ///
    /// A fourth channel-addressing scheme: Chroma's supplies are single-output, so
    /// there is no channel at all — <c>VOLTage 5</c> and <c>MEASure:VOLTage?</c> act on
    /// the one output, where a Rigol names a channel and a Keysight passes a list.
    ///
    /// (Not verified against real hardware — no Chroma instrument on this bench.)
    /// </summary>
    private static InstrumentProfile ChromaPowerSupply(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Chroma power supply" : $"Chroma power supply ({model})",
        ReadoutFunctions = new[]
        {
            // The 62000L guide documents no power measurement, only volts and amps.
            new ReadoutFunction("Voltage", "MEASure:VOLTage?", "V"),
            new ReadoutFunction("Current", "MEASure:CURRent?", "A"),
        },
        Commands = new[]
        {
            new QuickCommand("Output On",  "OUTPut ON"),
            new QuickCommand("Output Off", "OUTPut OFF"),
            new QuickCommand("Output?",    "OUTPut?"),
            new QuickCommand("Volts",      "MEASure:VOLTage?"),
            new QuickCommand("Amps",       "MEASure:CURRent?"),
            new QuickCommand("Vset?",      "VOLTage?"),
            new QuickCommand("Iset?",      "CURRent?"),
            new QuickCommand("OVP?",       "VOLTage:PROTection?"),
            new QuickCommand("Errors",     "SYSTem:ERRor?"),
            new QuickCommand("*IDN?",      "*IDN?"),
        },
    };

    /// <summary>
    /// B&amp;K Precision electronic load commands, transcribed from the 8600 Series
    /// Programming Manual.
    ///
    /// Same shape as the Siglent SDL1000X load — an optional <c>[SOURce:]</c> root over
    /// <c>INPut</c>, <c>FUNCtion</c> and the four regulation levels — but B&amp;K writes
    /// the measurement queries with a leading colon (<c>:MEASure:VOLTage:DC?</c>) and
    /// treats <c>INPut</c> and <c>OUTPut</c> as equivalent spellings.
    ///
    /// (Not verified against real hardware — no B&amp;K instrument on this bench.)
    /// </summary>
    /// <summary>
    /// R&amp;S FPC1000/FPC1500 analyzers. Standard SCPI, but not the Siglent SSA3000X dialect the
    /// SpectrumAnalyzer family carries — the marker and display trees differ, which is why
    /// these have their own catalog rather than sharing that one.
    /// </summary>
    private static InstrumentProfile RohdeSpectrumAnalyzer(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "R&S spectrum analyzer" : $"R&S spectrum analyzer ({model})",
        // Unlike the FSL, the FPC has a one-shot screen query: DISPlay<n>[:WINDow]:FETCh?
        // "takes a screenshot of the current screen content and returns it as a jpg in
        // binary format". No file to name, no hardcopy to wait for.
        ScreenCaptureCommand = "DISPlay:WINDow:FETCh?",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("Marker 1", "CALCulate:MARKer:Y?", "dBm"),
        },
        Commands = new[]
        {
            new QuickCommand("Centre?",   "SENSe:FREQuency:CENTer?"),
            new QuickCommand("Span?",     "SENSe:FREQuency:SPAN?"),
            new QuickCommand("Start?",    "SENSe:FREQuency:STARt?"),
            new QuickCommand("Stop?",     "SENSe:FREQuency:STOP?"),
            new QuickCommand("Full span", "SENSe:FREQuency:SPAN:FULL"),
            new QuickCommand("RBW?",      "SENSe:BANDwidth:RESolution?"),
            new QuickCommand("VBW?",      "SENSe:BANDwidth:VIDeo?"),
            new QuickCommand("Ref lvl?",  "DISPlay:WINDow:TRACe:Y:SCALe:RLEVel?"),
            new QuickCommand("Att?",      "INPut:ATTenuation?"),
            new QuickCommand("Marker Y?", "CALCulate:MARKer:Y?"),
            new QuickCommand("Peak",      "CALCulate:MARKer:MAXimum:PEAK"),
            new QuickCommand("Markers off", "CALCulate:MARKer:AOFF"),
            new QuickCommand("Single",    "INITiate:IMMediate"),
            new QuickCommand("Errors",    "SYSTem:ERRor:ALL?"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>
    /// R&amp;S FSL3/FSL6/FSL18 analyzers. An older and much larger command set than the FPC's,
    /// and a different one: the FSL takes SENSe and CALCulate with numeric suffixes the FPC
    /// does not use, which is why the two do not share a catalog.
    /// </summary>
    private static InstrumentProfile RohdeFslAnalyzer(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "R&S FSL analyzer" : $"R&S FSL analyzer ({model})",
        // The FSL has no one-shot screen query. Its manual documents the hardcopy as a file
        // written to mass memory and read back afterwards, and spells the whole sequence out
        // under HCOPy[:IMMediate<1|2>] — format, destination, file name, then the hardcopy
        // itself. The path is the manual's own example. PNG is one of the formats
        // HCOPy:DEVice:LANGuage<1|2> documents for output to a file.
        //
        // *WAI is what keeps the readback from racing the write: the FSL defines it as
        // "permits servicing of subsequent commands only after all preceding commands have
        // been executed", so MMEMory:DATA? cannot run until the hardcopy has landed.
        ScreenCaptureSetup = new[]
        {
            "HCOPy:DEVice:LANGuage1 PNG",
            "HCOPy:DESTination1 'MMEM'",
            @"MMEMory:NAME 'C:\R_S\instr\user\Print.png'",
            "HCOPy:IMMediate1",
            "*WAI",
        },
        ScreenCaptureCommand = @"MMEMory:DATA? 'C:\R_S\instr\user\Print.png'",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("Marker 1", "CALC:MARK:Y?", "dBm"),
        },
        Commands = new[]
        {
            // Written in the short form the FSL's own examples use, and each one is an
            // instance of a template in its catalog — CatalogCoverageTests checks that.
            new QuickCommand("Centre?",   "FREQ:CENT?"),
            new QuickCommand("Span?",     "FREQ:SPAN?"),
            new QuickCommand("Start?",    "FREQ:STAR?"),
            new QuickCommand("Stop?",     "FREQ:STOP?"),
            new QuickCommand("Full span", "FREQ:SPAN:FULL"),
            new QuickCommand("RBW?",      "BAND?"),
            new QuickCommand("VBW?",      "BAND:VID?"),
            new QuickCommand("Sweep t?",  "SWE:TIME?"),
            new QuickCommand("Att?",      "INP:ATT?"),
            new QuickCommand("Marker Y?", "CALC:MARK:Y?"),
            new QuickCommand("Peak",      "CALC:MARK:MAX"),
            new QuickCommand("Markers off", "CALC:MARK:AOFF"),
            new QuickCommand("Single",    "INIT"),
            new QuickCommand("Errors",    "SYST:ERR?"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>
    /// Rigol DM3058/DM3058E bench meters.
    ///
    /// Standard SCPI, but not the Siglent SDM's standard SCPI, which is what this family
    /// used to be handed. They agree on :MEASure and part ways everywhere else: the Rigol
    /// selects a function with :FUNCtion:VOLTage:DC where the Siglent uses
    /// CONFigure:VOLTage:DC, and its measurement rate lives under :RATE, a subsystem the
    /// SDM has no equivalent for.
    /// </summary>
    private static InstrumentProfile RigolMultimeter(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Rigol multimeter" : $"Rigol multimeter ({model})",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("DC volts", ":MEASure:VOLTage:DC?", "V"),
        },
        Commands = new[]
        {
            new QuickCommand("DCV",      ":FUNCtion:VOLTage:DC"),
            new QuickCommand("ACV",      ":FUNCtion:VOLTage:AC"),
            new QuickCommand("DCI",      ":FUNCtion:CURRent:DC"),
            new QuickCommand("ACI",      ":FUNCtion:CURRent:AC"),
            new QuickCommand("2-wire Ω", ":FUNCtion:RESistance"),
            new QuickCommand("4-wire Ω", ":FUNCtion:FRESistance"),
            new QuickCommand("Cap",      ":FUNCtion:CAPacitance"),
            new QuickCommand("Freq",     ":FUNCtion:FREQuency"),
            new QuickCommand("Diode",    ":FUNCtion:DIODe"),
            new QuickCommand("Cont",     ":FUNCtion:CONTinuity"),
            new QuickCommand("Read DCV", ":MEASure:VOLTage:DC?"),
            new QuickCommand("Read DCI", ":MEASure:CURRent:DC?"),
            new QuickCommand("Read Ω",   ":MEASure:RESistance?"),
            new QuickCommand("Function?", ":FUNCtion?"),
            new QuickCommand("*IDN?",    "*IDN?"),
        },
    };

    /// <summary>
    /// Rigol DL3000 loads. Its commands hang off an optional [SOURce] root the Siglent
    /// SDL1000X guide — the catalog this family used to get — never uses at all.
    /// </summary>
    private static InstrumentProfile RigolElectronicLoad(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Rigol electronic load" : $"Rigol electronic load ({model})",
        ReadoutFunctions = new[]
        {
            // The optional [:DC] spelled out: a readout sends this string as it stands,
            // and an instrument has no idea what to do with a square bracket.
            new ReadoutFunction("Voltage", ":MEASure:VOLTage:DC?", "V"),
            new ReadoutFunction("Current", ":MEASure:CURRent:DC?", "A"),
            new ReadoutFunction("Power",   ":MEASure:POWer:DC?",   "W"),
        },
        Commands = new[]
        {
            new QuickCommand("Input ON",  ":SOURce:INPut:STATe ON"),
            new QuickCommand("Input OFF", ":SOURce:INPut:STATe OFF"),
            new QuickCommand("Input?",    ":SOURce:INPut:STATe?"),
            new QuickCommand("CC mode",   ":SOURce:FUNCtion CURRent"),
            new QuickCommand("CV mode",   ":SOURce:FUNCtion VOLTage"),
            new QuickCommand("CR mode",   ":SOURce:FUNCtion RESistance"),
            new QuickCommand("CP mode",   ":SOURce:FUNCtion POWer"),
            new QuickCommand("Mode?",     ":SOURce:FUNCtion?"),
            new QuickCommand("Volts?",    ":MEASure:VOLTage:DC?"),
            new QuickCommand("Amps?",     ":MEASure:CURRent:DC?"),
            new QuickCommand("Watts?",    ":MEASure:POWer:DC?"),
            new QuickCommand("Ω?",        ":MEASure:RESistance:DC?"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>
    /// Rigol DSA800/DSA800E analyzers. Its frequency and bandwidth trees look enough like
    /// the Siglent SSA3000X's — the catalog this family used to be given — that the swap
    /// went unnoticed; the marker, trace and EMI trees do not match at all.
    /// </summary>
    private static InstrumentProfile RigolSpectrumAnalyzer(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Rigol spectrum analyzer" : $"Rigol spectrum analyzer ({model})",
        // The guide documents the screen dump as a file written to internal or USB storage
        // (:MMEMory:STORe:SCReen), not as a query that hands the image back over the wire,
        // so there is nothing to wire a Capture button to.
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("Marker 1", ":CALCulate:MARKer1:Y?", "dBm"),
        },
        Commands = new[]
        {
            new QuickCommand("Centre?",   ":SENSe:FREQuency:CENTer?"),
            new QuickCommand("Span?",     ":SENSe:FREQuency:SPAN?"),
            new QuickCommand("Start?",    ":SENSe:FREQuency:STARt?"),
            new QuickCommand("Stop?",     ":SENSe:FREQuency:STOP?"),
            new QuickCommand("Full span", ":SENSe:FREQuency:SPAN:FULL"),
            new QuickCommand("RBW?",      ":SENSe:BANDwidth:RESolution?"),
            new QuickCommand("VBW?",      ":SENSe:BANDwidth:VIDeo?"),
            new QuickCommand("Marker Y?", ":CALCulate:MARKer1:Y?"),
            new QuickCommand("Marker X?", ":CALCulate:MARKer1:X?"),
            new QuickCommand("Peak",      ":CALCulate:MARKer1:MAXimum:MAX"),
            new QuickCommand("Next peak", ":CALCulate:MARKer1:MAXimum:NEXT"),
            new QuickCommand("Markers off", ":CALCulate:MARKer:AOFF"),
            new QuickCommand("Single",    ":INITiate:IMMediate"),
            new QuickCommand("Errors",    ":SYSTem:ERRor?"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>
    /// R&amp;S FSV/FSVA analyzers. A third R&amp;S analyzer command set, distinct from both the FPC's
    /// and the FSL's: it takes the modern [SENSe:] root and a marker suffix on CALCulate,
    /// where the FSL writes SENSe and CALCulate with numeric suffixes of its own.
    /// </summary>
    private static InstrumentProfile RohdeFsvAnalyzer(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "R&S FSV analyzer" : $"R&S FSV analyzer ({model})",
        // The same file route the FSL takes, documented the same way in its own manual —
        // format, destination, file name, hardcopy, then read the file back. The FSV has no
        // HCOPy:DATA? either; that is the RTB2000 scope's command, and giving it to an R&S
        // analyzer is the mistake this profile was written to avoid repeating.
        ScreenCaptureSetup = new[]
        {
            "HCOPy:DEVice:LANGuage1 PNG",
            "HCOPy:DESTination1 'MMEM'",
            @"MMEMory:NAME 'C:\R_S\instr\user\Print.png'",
            "HCOPy:IMMediate1",
            "*WAI",
        },
        ScreenCaptureCommand = @"MMEMory:DATA? 'C:\R_S\instr\user\Print.png'",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("Marker 1", "CALCulate:MARKer:Y?", "dBm"),
        },
        Commands = new[]
        {
            new QuickCommand("Centre?",   "SENSe:FREQuency:CENTer?"),
            new QuickCommand("Span?",     "SENSe:FREQuency:SPAN?"),
            new QuickCommand("Start?",    "SENSe:FREQuency:STARt?"),
            new QuickCommand("Stop?",     "SENSe:FREQuency:STOP?"),
            new QuickCommand("Full span", "SENSe:FREQuency:SPAN:FULL"),
            new QuickCommand("RBW?",      "SENSe:BANDwidth:RESolution?"),
            new QuickCommand("VBW?",      "SENSe:BANDwidth:VIDeo?"),
            new QuickCommand("Marker Y?", "CALCulate:MARKer:Y?"),
            new QuickCommand("Markers off", "CALCulate:MARKer:AOFF"),
            new QuickCommand("Single",    "INITiate:IMMediate"),
            new QuickCommand("Errors",    "SYSTem:ERRor:NEXT?"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>
    /// R&amp;S FSU analyzers (FSU3 through FSU67). The FSL's ancestor and the fifth R&amp;S analyzer
    /// set here — the same dialect family as the FSL (SENSe with numeric suffixes, the
    /// BANDwidth|BWIDth spellings) but its own manual and its own catalog.
    ///
    /// The quick commands are the FSL's, in the short form both manuals' examples use, and
    /// every one is an instance of a template in the FSU catalog — the coverage test checks
    /// that. The capture sequence differs in one place that matters: the FSU's
    /// HCOPy:DEVice:LANGuage documents GDI | WMF | EWMF | BMP and no PNG, so the hardcopy
    /// is asked for as BMP — asking for PNG would be sending a value the manual never
    /// prints.
    /// </summary>
    private static InstrumentProfile RohdeFsuAnalyzer(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "R&S FSU analyzer" : $"R&S FSU analyzer ({model})",
        ScreenCaptureSetup = new[]
        {
            "HCOPy:DEVice:LANGuage1 BMP",
            "HCOPy:DESTination1 'MMEM'",
            @"MMEMory:NAME 'C:\R_S\instr\user\Print.bmp'",
            "HCOPy:IMMediate1",
            "*WAI",
        },
        ScreenCaptureCommand = @"MMEMory:DATA? 'C:\R_S\instr\user\Print.bmp'",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("Marker 1", "CALC:MARK:Y?", "dBm"),
        },
        Commands = new[]
        {
            new QuickCommand("Centre?",   "FREQ:CENT?"),
            new QuickCommand("Span?",     "FREQ:SPAN?"),
            new QuickCommand("Start?",    "FREQ:STAR?"),
            new QuickCommand("Stop?",     "FREQ:STOP?"),
            new QuickCommand("Full span", "FREQ:SPAN:FULL"),
            new QuickCommand("RBW?",      "BAND?"),
            new QuickCommand("VBW?",      "BAND:VID?"),
            new QuickCommand("Sweep t?",  "SWE:TIME?"),
            new QuickCommand("Att?",      "INP:ATT?"),
            new QuickCommand("Marker Y?", "CALC:MARK:Y?"),
            new QuickCommand("Markers off", "CALC:MARK:AOFF"),
            new QuickCommand("Single",    "INIT"),
            new QuickCommand("Errors",    "SYST:ERR?"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>
    /// R&amp;S FSP analyzers (FSP3 through FSP40), the FSU's economy sibling — same generation,
    /// same dialect family, its own manual and catalog. The capture asks for BMP because
    /// its HCOPy:DEVice:LANGuage documents GDI | WMF | EWMF | BMP and nothing newer.
    /// </summary>
    private static InstrumentProfile RohdeFspAnalyzer(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "R&S FSP analyzer" : $"R&S FSP analyzer ({model})",
        ScreenCaptureSetup = new[]
        {
            "HCOPy:DEVice:LANGuage1 BMP",
            "HCOPy:DESTination1 'MMEM'",
            @"MMEMory:NAME 'C:\R_S\instr\user\Print.bmp'",
            "HCOPy:IMMediate1",
            "*WAI",
        },
        ScreenCaptureCommand = @"MMEMory:DATA? 'C:\R_S\instr\user\Print.bmp'",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("Marker 1", "CALC:MARK:Y?", "dBm"),
        },
        Commands = new[]
        {
            new QuickCommand("Centre?",   "FREQ:CENT?"),
            new QuickCommand("Span?",     "FREQ:SPAN?"),
            new QuickCommand("Start?",    "FREQ:STAR?"),
            new QuickCommand("Stop?",     "FREQ:STOP?"),
            new QuickCommand("Full span", "FREQ:SPAN:FULL"),
            new QuickCommand("RBW?",      "BAND?"),
            new QuickCommand("VBW?",      "BAND:VID?"),
            new QuickCommand("Sweep t?",  "SWE:TIME?"),
            new QuickCommand("Att?",      "INP:ATT?"),
            new QuickCommand("Marker Y?", "CALC:MARK:Y?"),
            new QuickCommand("Markers off", "CALC:MARK:AOFF"),
            new QuickCommand("Single",    "INIT"),
            new QuickCommand("Errors",    "SYST:ERR?"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>
    /// R&amp;S FSQ signal analyzers (FSQ3 through FSQ40) — the FSU generation's top line, same
    /// dialect family again, its own manual and catalog. BMP for the capture, as its
    /// siblings: HCOPy:DEVice:LANGuage documents GDI | WMF | EWMF | BMP.
    /// </summary>
    private static InstrumentProfile RohdeFsqAnalyzer(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "R&S FSQ analyzer" : $"R&S FSQ analyzer ({model})",
        ScreenCaptureSetup = new[]
        {
            "HCOPy:DEVice:LANGuage1 BMP",
            "HCOPy:DESTination1 'MMEM'",
            @"MMEMory:NAME 'C:\R_S\instr\user\Print.bmp'",
            "HCOPy:IMMediate1",
            "*WAI",
        },
        ScreenCaptureCommand = @"MMEMory:DATA? 'C:\R_S\instr\user\Print.bmp'",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("Marker 1", "CALC:MARK:Y?", "dBm"),
        },
        Commands = new[]
        {
            new QuickCommand("Centre?",   "FREQ:CENT?"),
            new QuickCommand("Span?",     "FREQ:SPAN?"),
            new QuickCommand("Start?",    "FREQ:STAR?"),
            new QuickCommand("Stop?",     "FREQ:STOP?"),
            new QuickCommand("Full span", "FREQ:SPAN:FULL"),
            new QuickCommand("RBW?",      "BAND?"),
            new QuickCommand("VBW?",      "BAND:VID?"),
            new QuickCommand("Sweep t?",  "SWE:TIME?"),
            new QuickCommand("Att?",      "INP:ATT?"),
            new QuickCommand("Marker Y?", "CALC:MARK:Y?"),
            new QuickCommand("Markers off", "CALC:MARK:AOFF"),
            new QuickCommand("Single",    "INIT"),
            new QuickCommand("Errors",    "SYST:ERR?"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>
    /// R&amp;S FSW analyzers (FSW8 through FSW85). The fourth R&amp;S analyzer command set here, and
    /// the largest — its base-unit manual documents 1367 headers against the FSV's 1279.
    ///
    /// It reads like the FSV's, taking the modern <c>[SENSe:]</c> root, but it suffixes far
    /// more of the tree: <c>CALCulate&lt;n&gt;</c> for the window and <c>MARKer&lt;m&gt;</c> for the marker,
    /// where the FSV manual documents most of the same commands unsuffixed. The suffixes are
    /// optional on the instrument, so the unsuffixed forms below are what both catalogs
    /// document and what the coverage test matches.
    /// </summary>
    private static InstrumentProfile RohdeFswAnalyzer(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "R&S FSW analyzer" : $"R&S FSW analyzer ({model})",
        // The same file route as the FSV and FSL, and for the same reason: no R&S analyzer
        // has a one-shot screen query. Its manual documents HCOPy as format, destination,
        // file name, hardcopy, then read the file back.
        //
        // Not the FSV's spellings, though. The FSW manual prints HCOPy:DEVice:LANGuage and
        // HCOPy[:IMMediate] without a numeric suffix, where the FSV documents LANGuage1 and
        // IMMediate1 — only DESTination<di> keeps its suffix here. This profile shipped
        // with the FSV's suffixed forms copied across, which is inventing SCPI; the
        // coverage guard caught it the first time it was allowed to run for this family.
        ScreenCaptureSetup = new[]
        {
            "HCOPy:DEVice:LANGuage PNG",
            "HCOPy:DESTination1 'MMEM'",
            @"MMEMory:NAME 'C:\R_S\instr\user\Print.png'",
            "HCOPy:IMMediate",
            "*WAI",
        },
        ScreenCaptureCommand = @"MMEMory:DATA? 'C:\R_S\instr\user\Print.png'",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("Marker 1", "CALCulate:MARKer:Y?", "dBm"),
        },
        Commands = new[]
        {
            new QuickCommand("Centre?",   "SENSe:FREQuency:CENTer?"),
            new QuickCommand("Span?",     "SENSe:FREQuency:SPAN?"),
            new QuickCommand("Start?",    "SENSe:FREQuency:STARt?"),
            new QuickCommand("Stop?",     "SENSe:FREQuency:STOP?"),
            new QuickCommand("Full span", "SENSe:FREQuency:SPAN:FULL"),
            new QuickCommand("RBW?",      "SENSe:BANDwidth:RESolution?"),
            new QuickCommand("VBW?",      "SENSe:BANDwidth:VIDeo?"),
            new QuickCommand("Marker Y?", "CALCulate:MARKer:Y?"),
            new QuickCommand("Markers off", "CALCulate:MARKer:AOFF"),
            new QuickCommand("Single",    "INITiate:IMMediate"),
            new QuickCommand("Errors",    "SYSTem:ERRor:NEXT?"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>
    /// Keysight Truevolt bench meters (34460A/34461A/34465A/34470A).
    ///
    /// These took the Multimeter family until now, which is the Siglent SDM catalog: the two
    /// share MEASure and nothing else worth relying on. Keysight configures with CONFigure
    /// and reads back with READ?, and its whole CALCulate tree — statistics, histogram,
    /// smoothing — has no Siglent counterpart.
    /// </summary>
    private static InstrumentProfile KeysightMultimeter(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Keysight multimeter" : $"Keysight multimeter ({model})",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("DC volts", "MEASure:VOLTage:DC?", "V"),
        },
        Commands = new[]
        {
            new QuickCommand("DCV",      "MEASure:VOLTage:DC?"),
            new QuickCommand("ACV",      "MEASure:VOLTage:AC?"),
            new QuickCommand("DCI",      "MEASure:CURRent:DC?"),
            new QuickCommand("ACI",      "MEASure:CURRent:AC?"),
            new QuickCommand("2-wire Ω", "MEASure:RESistance?"),
            new QuickCommand("4-wire Ω", "MEASure:FRESistance?"),
            new QuickCommand("Cap",      "MEASure:CAPacitance?"),
            new QuickCommand("Freq",     "MEASure:FREQuency?"),
            new QuickCommand("Diode",    "MEASure:DIODe?"),
            new QuickCommand("Cont",     "MEASure:CONTinuity?"),
            new QuickCommand("Config?",  "CONFigure?"),
            new QuickCommand("Read",     "READ?"),
            new QuickCommand("Errors",   "SYSTem:ERRor?"),
            new QuickCommand("*IDN?",    "*IDN?"),
        },
    };

    /// <summary>
    /// GW Instek's newer scopes — GDS-1000B, GDS-2000E, MSO-2000E, MDO-2000E — which have
    /// their own programming manual and a larger command set than the GDS-2000 series.
    ///
    /// Waveform capture stays off for the same reason it is off on the GDS-2000: the manual
    /// frames the data but never says how a stored code becomes a voltage.
    /// </summary>
    private static InstrumentProfile GwInstekScopeB(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "GW Instek oscilloscope" : $"GW Instek oscilloscope ({model})",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("Frequency", ":MEASure:FREQuency?", "Hz"),
        },
        Commands = new[]
        {
            new QuickCommand("Run",      ":RUN"),
            new QuickCommand("Stop",     ":STOP"),
            new QuickCommand("Single",   ":SINGle"),
            new QuickCommand("Autoset",  ":AUToset"),
            new QuickCommand("Freq?",    ":MEASure:FREQuency?"),
            new QuickCommand("Vpp?",     ":MEASure:PK2PK?"),
            new QuickCommand("Vrms?",    ":MEASure:RMS?"),
            new QuickCommand("CH1 scale?", ":CHANnel1:SCALe?"),
            new QuickCommand("Timebase?", ":TIMebase:SCALe?"),
            new QuickCommand("*IDN?",    "*IDN?"),
        },
    };

    /// <summary>
    /// Chroma 63600 modular loads. A different set from the 63200A's — only about half its
    /// command headers appear there — so these get their own catalog rather than the one
    /// that nearly fits.
    /// </summary>
    private static InstrumentProfile ChromaModularLoad(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Chroma modular load" : $"Chroma modular load ({model})",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("Voltage", "FETCh:VOLTage?", "V"),
            new ReadoutFunction("Current", "FETCh:CURRent?", "A"),
            new ReadoutFunction("Power",   "FETCh:POWer?",   "W"),
        },
        Commands = new[]
        {
            new QuickCommand("Load ON",  "LOAD ON"),
            new QuickCommand("Load OFF", "LOAD OFF"),
            new QuickCommand("Load?",    "LOAD?"),
            new QuickCommand("Volts?",   "FETCh:VOLTage?"),
            new QuickCommand("Amps?",    "FETCh:CURRent?"),
            new QuickCommand("Watts?",   "FETCh:POWer?"),
            new QuickCommand("Channel?", "CHANnel?"),
            new QuickCommand("Protection?", "LOAD:PROTection?"),
            new QuickCommand("Clear prot", "LOAD:PROTection:CLEar"),
            new QuickCommand("Errors",   "SYSTem:ERRor?"),
            new QuickCommand("*IDN?",    "*IDN?"),
        },
    };

    /// <summary>
    /// B&amp;K Precision 9200B multi-range supplies. Its command reference is a chapter of the
    /// user manual; the 9130B's is a separate programming manual describing a different
    /// instrument, and has its own profile and catalog.
    /// </summary>
    private static InstrumentProfile BkPowerSupply(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "B&K power supply" : $"B&K power supply ({model})",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("Voltage", "FETCh:VOLTage?", "V"),
            new ReadoutFunction("Current", "FETCh:CURRent?", "A"),
            new ReadoutFunction("Power",   "FETCh:POWer?",   "W"),
        },
        Commands = new[]
        {
            new QuickCommand("Output ON",  "SOURce:OUTPut:STATe ON"),
            new QuickCommand("Output OFF", "SOURce:OUTPut:STATe OFF"),
            new QuickCommand("Output?",    "SOURce:OUTPut:STATe?"),
            new QuickCommand("Volts?",     "FETCh:VOLTage?"),
            new QuickCommand("Amps?",      "FETCh:CURRent?"),
            new QuickCommand("Watts?",     "FETCh:POWer?"),
            new QuickCommand("Vset?",      "SOURce:VOLTage:LEVel:IMMediate:AMPLitude?"),
            new QuickCommand("Iset?",      "SOURce:CURRent:LEVel:IMMediate:AMPLitude?"),
            new QuickCommand("*IDN?",      "*IDN?"),
        },
    };

    /// <summary>
    /// B&amp;K 9130B / 9131B / 9132B triple-output supplies.
    ///
    /// Nearly every command here acts on "the present channel", so the channel buttons are
    /// not a convenience — they are the state the readouts and the set commands are read
    /// against. They sit first for that reason. MEASure:VOLTage:ALL? and MEASure:CURRent:ALL?
    /// are the two that ignore the selection and report all three at once.
    /// </summary>
    private static InstrumentProfile BkPowerSupply9130(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "B&K triple-output supply" : $"B&K triple-output supply ({model})",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("Voltage", "MEASure:VOLTage?", "V"),
            new ReadoutFunction("Current", "MEASure:CURRent?", "A"),
            new ReadoutFunction("Power",   "MEASure:POWer?",   "W"),
        },
        Commands = new[]
        {
            new QuickCommand("CH1",        "INSTrument CH1"),
            new QuickCommand("CH2",        "INSTrument CH2"),
            new QuickCommand("CH3",        "INSTrument CH3"),
            new QuickCommand("Output ON",  "OUTPut ON"),
            new QuickCommand("Output OFF", "OUTPut OFF"),
            new QuickCommand("Output?",    "OUTPut:STATe?"),
            new QuickCommand("Volts?",     "MEASure:VOLTage?"),
            new QuickCommand("Amps?",      "MEASure:CURRent?"),
            new QuickCommand("Watts?",     "MEASure:POWer?"),
            new QuickCommand("All V?",     "MEASure:VOLTage:ALL?"),
            new QuickCommand("All A?",     "MEASure:CURRent:ALL?"),
            new QuickCommand("Vset?",      "VOLTage?"),
            new QuickCommand("Iset?",      "CURRent?"),
            new QuickCommand("*IDN?",      "*IDN?"),
        },
    };

    /// <summary>
    /// Chroma 63200A high-power loads. MODE carries the mode and the range together — CCL,
    /// CCM, CCH are constant current on low, middle and high range — so there is no separate
    /// range command to set alongside it.
    /// </summary>
    private static InstrumentProfile ChromaElectronicLoad(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Chroma electronic load" : $"Chroma electronic load ({model})",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("Voltage", "MEASure:VOLTage?", "V"),
            new ReadoutFunction("Current", "MEASure:CURRent?", "A"),
            new ReadoutFunction("Power",   "MEASure:POWer?",   "W"),
        },
        Commands = new[]
        {
            new QuickCommand("Load On",  "LOAD ON"),
            new QuickCommand("Load Off", "LOAD OFF"),
            new QuickCommand("Load?",    "LOAD?"),
            new QuickCommand("Volts",    "MEASure:VOLTage?"),
            new QuickCommand("Amps",     "MEASure:CURRent?"),
            new QuickCommand("Watts",    "MEASure:POWer?"),
            new QuickCommand("CC mode",  "MODE CCH"),
            new QuickCommand("CV mode",  "MODE CVH"),
            new QuickCommand("CR mode",  "MODE CRH"),
            new QuickCommand("Mode?",    "MODE?"),
            new QuickCommand("Set I",    "CURRent:STATic:L1 1"),
            new QuickCommand("I set?",   "CURRent:STATic:L1?"),
            new QuickCommand("Prot?",    "LOAD:PROTection?"),
            new QuickCommand("Clr prot", "LOAD:PROTection:CLEar"),
            new QuickCommand("*IDN?",    "*IDN?"),
        },
    };

    private static InstrumentProfile BkElectronicLoad(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "B&K electronic load" : $"B&K electronic load ({model})",
        ReadoutFunctions = new[]
        {
            new ReadoutFunction("Voltage", ":MEASure:VOLTage:DC?", "V"),
            new ReadoutFunction("Current", ":MEASure:CURRent:DC?", "A"),
        },
        Commands = new[]
        {
            new QuickCommand("Input On",  "INPut:STATe ON"),
            new QuickCommand("Input Off", "INPut:STATe OFF"),
            new QuickCommand("Input?",    "INPut:STATe?"),
            new QuickCommand("Volts",     ":MEASure:VOLTage:DC?"),
            new QuickCommand("Amps",      ":MEASure:CURRent:DC?"),
            new QuickCommand("CC mode",   "FUNCtion CURRent"),
            new QuickCommand("CV mode",   "FUNCtion VOLTage"),
            new QuickCommand("Mode?",     "FUNCtion?"),
            new QuickCommand("Set I",     "CURRent:LEVel:IMMediate 0.5"),
            new QuickCommand("I set?",    "CURRent:LEVel:IMMediate?"),
            new QuickCommand("Errors",    "SYSTem:ERRor?"),
            new QuickCommand("*IDN?",     "*IDN?"),
        },
    };

    /// <summary>Fallback: only commands every IEEE-488.2 instrument understands.</summary>
    private static InstrumentProfile Generic(string model) => new()
    {
        Name = string.IsNullOrEmpty(model) ? "Not connected" : $"Instrument ({model})",
        Commands = new[]
        {
            new QuickCommand("*IDN?", "*IDN?"),
            new QuickCommand("*CLS",  "*CLS"),
            new QuickCommand("*OPC?", "*OPC?"),
            new QuickCommand("*STB?", "*STB?"),
        },
    };
}
