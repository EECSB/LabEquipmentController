using System.Collections.Generic;

namespace LabEquipmentController;

/// <summary>One ready-made script offered by the script editor's Examples dropdown.</summary>
public sealed record ScriptExample(string Name, string Script);

/// <summary>
/// Ready-made scripts, chosen to match the instrument the editor was opened for.
///
/// A single shared list was worse than useless once several instruments could be connected
/// at once: most of it was syntactically valid but meaningless on whatever you had in front
/// of you, and a Siglent generator would sit there answering `C1:BSWV` examples with errors
/// on a multimeter's editor.
///
/// Every command here is transcribed from the same vendor programming guides as the command
/// catalogs in <c>Core/CommandData</c> — nothing is invented. Where a family's guide does not
/// document something (the Siglent SDG and SDM guides carry no error-queue query, and the
/// Rigol spells it <c>:SYSTem:ERRor:NEXT?</c> rather than <c>:SYSTem:ERRor?</c>), the example
/// leaves it out rather than guessing.
///
/// Scripts that change an instrument's state — enabling a generator output above all — say so
/// in a comment on the line above.
/// </summary>
public static class ScriptExamples
{
    /// <summary>The examples that suit this instrument family.</summary>
    public static IReadOnlyList<ScriptExample> ForFamily(InstrumentFamily family) => family switch
    {
        InstrumentFamily.Oscilloscope     => Oscilloscope,
        InstrumentFamily.SiglentGenerator => SiglentGenerator,
        InstrumentFamily.ScpiGenerator    => ScpiGenerator,
        InstrumentFamily.Multimeter       => Multimeter,
        InstrumentFamily.PowerSupply      => PowerSupply,
        InstrumentFamily.ElectronicLoad   => ElectronicLoad,
        InstrumentFamily.SpectrumAnalyzer => SpectrumAnalyzer,
        InstrumentFamily.TektronixScope   => TektronixScope,
        InstrumentFamily.KeysightScope    => KeysightScope,
        InstrumentFamily.KeysightPowerSupply => KeysightPowerSupply,
        InstrumentFamily.KeithleySmu      => KeithleySmu,
        InstrumentFamily.KeithleyDmm      => KeithleyDmm,
        InstrumentFamily.RohdeScope       => RohdeScope,
        InstrumentFamily.RohdePowerSupply => RohdePowerSupply,
        InstrumentFamily.SiglentScope     => SiglentScope,
        InstrumentFamily.FlukeMultimeter  => FlukeMultimeter,
        InstrumentFamily.GwInstekScope    => GwInstekScope,
        InstrumentFamily.ChromaPowerSupply => ChromaPowerSupply,
        InstrumentFamily.BkElectronicLoad => BkElectronicLoad,
        _                                 => Generic,
    };

    /// <summary>The examples that suit the instrument behind this *IDN? string.</summary>
    public static IReadOnlyList<ScriptExample> ForIdentity(string? identity)
        => ForFamily(InstrumentProfile.FamilyForIdentity(identity));

    // ------------------------------------------------------------------ oscilloscope

    private static readonly ScriptExample[] Oscilloscope =
    {
        new("Instrument info",
            "# Oscilloscope — identity and status (read-only)\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*STB?\r\n" +
            ":SYSTem:ERRor:NEXT?\r\n"),

        new("Read scope settings",
            "# Rigol DS2000 — current timebase, channel and trigger setup (read-only)\r\n" +
            "PRINT Scope settings:\r\n" +
            ":CHANnel1:SCALe?\r\n" +
            ":CHANnel1:OFFSet?\r\n" +
            ":TIMebase:MAIN:SCALe?\r\n" +
            ":TRIGger:SWEep?\r\n"),

        new("Measure CH1",
            "# Rigol DS2000 — the standard CH1 measurements (read-only)\r\n" +
            "PRINT CH1 measurements:\r\n" +
            ":MEASure:VPP? CHANnel1\r\n" +
            ":MEASure:VAVG? CHANnel1\r\n" +
            ":MEASure:FREQuency? CHANnel1\r\n" +
            ":MEASure:PERiod? CHANnel1\r\n"),

        new("Poll CH1 amplitude",
            "# Read CH1 peak-to-peak ten times, a second apart.\r\n" +
            "REPEAT 10\r\n" +
            "    :MEASure:VPP? CHANnel1\r\n" +
            "    DELAY 1000\r\n" +
            "END\r\n" +
            "PRINT Polling complete.\r\n"),

        new("Single-shot capture",
            "# Arm a single acquisition, then read what it caught.\r\n" +
            "# NOTE: :SINGle changes the scope's run state.\r\n" +
            ":SINGle\r\n" +
            "DELAY 500\r\n" +
            ":MEASure:VPP? CHANnel1\r\n" +
            ":MEASure:FREQuency? CHANnel1\r\n"),

        new("Autoscale then measure",
            "# NOTE: :AUToscale reconfigures the scope's whole front-panel setup.\r\n" +
            ":AUToscale\r\n" +
            "DELAY 2000\r\n" +
            "PRINT Measurements after autoscale:\r\n" +
            ":MEASure:VPP? CHANnel1\r\n" +
            ":MEASure:VAVG? CHANnel1\r\n" +
            ":MEASure:FREQuency? CHANnel1\r\n" +
            ":CHANnel1:SCALe?\r\n"),
    };

    // -------------------------------------------------------------- Siglent generator

    private static readonly ScriptExample[] SiglentGenerator =
    {
        new("Instrument info",
            "# Siglent SDG — identity (read-only)\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*OPC?\r\n"),

        new("Read both channels",
            "# Siglent SDG — current waveform and output state, both channels (read-only)\r\n" +
            "PRINT CH1:\r\n" +
            "C1:BSWV?\r\n" +
            "C1:OUTP?\r\n" +
            "PRINT CH2:\r\n" +
            "C2:BSWV?\r\n" +
            "C2:OUTP?\r\n"),

        new("1 kHz sine on CH1",
            "# Siglent SDG — 1 kHz 2 Vpp sine on CH1.\r\n" +
            "# NOTE: the last line ENABLES the CH1 output. Check what is connected first.\r\n" +
            "C1:BSWV WVTP,SINE\r\n" +
            "C1:BSWV FRQ,1000HZ\r\n" +
            "C1:BSWV AMP,2V\r\n" +
            "C1:BSWV?\r\n" +
            "C1:OUTP ON\r\n" +
            "C1:OUTP?\r\n"),

        new("Square wave on CH1",
            "# Siglent SDG — 10 kHz 1 Vpp square on CH1, output left as it is.\r\n" +
            "C1:BSWV WVTP,SQUARE\r\n" +
            "C1:BSWV FRQ,10000HZ\r\n" +
            "C1:BSWV AMP,1V\r\n" +
            "C1:BSWV?\r\n"),

        new("Frequency sweep",
            "# Siglent SDG — step CH1 through three decades.\r\n" +
            "# NOTE: this ENABLES the CH1 output.\r\n" +
            "C1:BSWV WVTP,SINE\r\n" +
            "C1:BSWV AMP,2V\r\n" +
            "C1:OUTP ON\r\n" +
            "C1:BSWV FRQ,100HZ\r\n" +
            "DELAY 1000\r\n" +
            "C1:BSWV FRQ,1000HZ\r\n" +
            "DELAY 1000\r\n" +
            "C1:BSWV FRQ,10000HZ\r\n" +
            "DELAY 1000\r\n" +
            "C1:BSWV?\r\n" +
            "PRINT Sweep complete.\r\n"),

        new("All outputs off",
            "# Siglent SDG — quickly make the generator safe.\r\n" +
            "C1:OUTP OFF\r\n" +
            "C2:OUTP OFF\r\n" +
            "C1:OUTP?\r\n" +
            "C2:OUTP?\r\n" +
            "PRINT Both outputs off.\r\n"),
    };

    // ----------------------------------------------------------------- SCPI generator

    private static readonly ScriptExample[] ScpiGenerator =
    {
        new("Instrument info",
            "# Waveform generator — identity (read-only)\r\n" +
            "*IDN?\r\n" +
            "*OPC?\r\n"),

        new("Read CH1 setup",
            "# Standard-SCPI generator — current function and frequency (read-only)\r\n" +
            ":SOURce1:FUNCtion?\r\n" +
            ":SOURce1:FREQuency?\r\n"),

        new("1 kHz sine on CH1",
            "# NOTE: the last line ENABLES the CH1 output. Check what is connected first.\r\n" +
            ":SOURce1:FUNCtion SIN\r\n" +
            ":SOURce1:FREQuency 1000\r\n" +
            ":SOURce1:FUNCtion?\r\n" +
            ":OUTPut1 ON\r\n"),

        new("All outputs off",
            "# Make the generator safe.\r\n" +
            ":OUTPut1 OFF\r\n" +
            ":OUTPut2 OFF\r\n" +
            "PRINT Both outputs off.\r\n"),
    };

    // --------------------------------------------------------------------- multimeter

    private static readonly ScriptExample[] Multimeter =
    {
        new("Instrument info",
            "# Multimeter — identity (read-only)\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*OPC?\r\n"),

        new("Voltage — DC and AC",
            "# Each MEASure? sets the meter's function and then takes one reading.\r\n" +
            "PRINT DC then AC volts:\r\n" +
            "MEASure:VOLTage:DC?\r\n" +
            "MEASure:VOLTage:AC?\r\n"),

        new("Current — DC and AC",
            "# NOTE: current ranges need the leads in the current terminals.\r\n" +
            "MEASure:CURRent:DC?\r\n" +
            "MEASure:CURRent:AC?\r\n"),

        new("Resistance, diode, continuity",
            "# Two- and four-wire resistance, then the diode and continuity functions.\r\n" +
            "MEASure:RESistance?\r\n" +
            "MEASure:FRESistance?\r\n" +
            "MEASure:DIODe?\r\n" +
            "MEASure:CONTinuity?\r\n"),

        new("Log 30 DC readings",
            "# One reading a second for half a minute.\r\n" +
            "# The Readout window plots this kind of run live — this is the scripted form.\r\n" +
            "PRINT Logging DC volts...\r\n" +
            "REPEAT 30\r\n" +
            "    MEASure:VOLTage:DC?\r\n" +
            "    DELAY 1000\r\n" +
            "END\r\n" +
            "PRINT Log complete.\r\n"),

        new("Fast repeat read",
            "# READ? re-triggers the function the meter is already set to,\r\n" +
            "# so it is quicker than a MEASure? for repeated sampling.\r\n" +
            "MEASure:VOLTage:DC?\r\n" +
            "REPEAT 20\r\n" +
            "    READ?\r\n" +
            "    DELAY 250\r\n" +
            "END\r\n"),
    };

    // ---------------------------------------------------------- Chroma power supply

    private static readonly ScriptExample[] ChromaPowerSupply =
    {
        new("Instrument info",
            "# Chroma 62000 — identity and error queue (read-only).\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*OPC?\r\n" +
            "SYSTem:ERRor?\r\n"),

        new("Read the output",
            "# Chroma 62000 — setpoints and readings. These supplies are single-output,\r\n" +
            "# so no channel is named anywhere.\r\n" +
            "PRINT Setpoints:\r\n" +
            "VOLTage?\r\n" +
            "CURRent?\r\n" +
            "# The 62000L guide documents no power measurement, so this reads the\r\n" +
            "# two it does have.\r\n" +
            "PRINT Measured:\r\n" +
            "MEASure:VOLTage?\r\n" +
            "MEASure:CURRent?\r\n" +
            "OUTPut?\r\n"),

        new("Set 5 V / 1 A",
            "# Chroma 62000 — set the CV limit to 5 V and the CC limit to 1 A.\r\n" +
            "# NOTE: the last line ENABLES the output. Check what is wired first.\r\n" +
            "VOLTage 5\r\n" +
            "CURRent 1\r\n" +
            "VOLTage?\r\n" +
            "OUTPut ON\r\n" +
            "DELAY 500\r\n" +
            "MEASure:VOLTage?\r\n" +
            "MEASure:CURRent?\r\n"),

        new("Arm over-voltage protection",
            "# Chroma 62000 — set and enable OVP, then read it back.\r\n" +
            "# Does not enable the output.\r\n" +
            "VOLTage:PROTection 6\r\n" +
            "VOLTage:PROTection:STATe ON\r\n" +
            "VOLTage:PROTection?\r\n" +
            "VOLTage:PROTection:STATe?\r\n"),

        new("Log the output for 30 s",
            "# One voltage and current reading a second for half a minute.\r\n" +
            "PRINT Logging output...\r\n" +
            "REPEAT 30\r\n" +
            "    MEASure:VOLTage?\r\n" +
            "    MEASure:CURRent?\r\n" +
            "    DELAY 1000\r\n" +
            "END\r\n" +
            "PRINT Log complete.\r\n"),

        new("Output off",
            "# Make the supply safe.\r\n" +
            "OUTPut OFF\r\n" +
            "OUTPut?\r\n" +
            "PRINT Output off.\r\n"),
    };

    // ------------------------------------------------------- B&K electronic load

    private static readonly ScriptExample[] BkElectronicLoad =
    {
        new("Instrument info",
            "# B&K Precision 8600 — identity and error queue (read-only).\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*OPC?\r\n" +
            "SYSTem:ERRor?\r\n"),

        new("Read input",
            "# B&K 8600 — what the load is set to and drawing (read-only).\r\n" +
            "FUNCtion?\r\n" +
            "INPut:STATe?\r\n" +
            ":MEASure:VOLTage:DC?\r\n" +
            ":MEASure:CURRent:DC?\r\n"),

        new("Constant current, 500 mA",
            "# B&K 8600 — draw a steady 500 mA.\r\n" +
            "# NOTE: the last line SINKS CURRENT from whatever is connected.\r\n" +
            "FUNCtion CURRent\r\n" +
            "CURRent:LEVel:IMMediate 0.5\r\n" +
            "FUNCtion?\r\n" +
            "INPut:STATe ON\r\n" +
            "DELAY 500\r\n" +
            ":MEASure:VOLTage:DC?\r\n" +
            ":MEASure:CURRent:DC?\r\n"),

        new("Load step",
            "# B&K 8600 — step the load and watch the rail sag.\r\n" +
            "# NOTE: this SINKS CURRENT. Check the supply can deliver it.\r\n" +
            "FUNCtion CURRent\r\n" +
            "CURRent:LEVel:IMMediate 0.1\r\n" +
            "INPut:STATe ON\r\n" +
            "DELAY 1000\r\n" +
            ":MEASure:VOLTage:DC?\r\n" +
            "CURRent:LEVel:IMMediate 1.0\r\n" +
            "DELAY 1000\r\n" +
            ":MEASure:VOLTage:DC?\r\n" +
            "INPut:STATe OFF\r\n" +
            "PRINT Load step complete, input off.\r\n"),

        new("Input off",
            "# Stop drawing current.\r\n" +
            "INPut:STATe OFF\r\n" +
            "INPut:STATe?\r\n" +
            "PRINT Input off.\r\n"),
    };

    // ------------------------------------------------------------- Fluke multimeter

    private static readonly ScriptExample[] FlukeMultimeter =
    {
        new("Instrument info",
            "# Fluke 8845A/8846A — identity and error queue (read-only).\r\n" +
            "# The meter listens on a raw socket, default port 3490 — not one of the\r\n" +
            "# app's default scan ports, so add it before scanning for one.\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*OPC?\r\n" +
            "SYSTem:ERRor?\r\n"),

        new("Voltage — DC and AC",
            "# Each MEASure? presets the meter's function and then takes one reading.\r\n" +
            "PRINT DC then AC volts:\r\n" +
            "MEASure:VOLTage:DC?\r\n" +
            "MEASure:VOLTage:AC?\r\n"),

        new("Resistance, diode, continuity",
            "# Two- and four-wire resistance, then the diode and continuity functions.\r\n" +
            "MEASure:RESistance?\r\n" +
            "MEASure:FRESistance?\r\n" +
            "MEASure:DIODe?\r\n" +
            "MEASure:CONTinuity?\r\n"),

        new("Slow, quiet DC reading",
            "# Ten power-line cycles of integration and the analog filter in: slower,\r\n" +
            "# but the quietest the meter gets.\r\n" +
            "CONFigure:VOLTage:DC\r\n" +
            "VOLTage:DC:NPLC 10\r\n" +
            "VOLTage:DC:FILTer:STATe ON\r\n" +
            "VOLTage:DC:NPLC?\r\n" +
            "READ?\r\n"),

        new("Log 30 DC readings",
            "# One reading a second for half a minute.\r\n" +
            "# The Readout window plots this kind of run live — this is the scripted form.\r\n" +
            "PRINT Logging DC volts...\r\n" +
            "REPEAT 30\r\n" +
            "    MEASure:VOLTage:DC?\r\n" +
            "    DELAY 1000\r\n" +
            "END\r\n" +
            "PRINT Log complete.\r\n"),

        new("Min/max/average over a run",
            "# The meter's own statistics, rather than working them out afterwards.\r\n" +
            "CONFigure:VOLTage:DC\r\n" +
            "CALCulate:FUNCtion AVERage\r\n" +
            "CALCulate:STATe ON\r\n" +
            "REPEAT 20\r\n" +
            "    READ?\r\n" +
            "    DELAY 250\r\n" +
            "END\r\n" +
            "CALCulate:AVERage:MINimum?\r\n" +
            "CALCulate:AVERage:MAXimum?\r\n" +
            "CALCulate:AVERage:AVERage?\r\n" +
            "CALCulate:AVERage:COUNt?\r\n"),
    };

    // ---------------------------------------------------------- GW Instek scope

    private static readonly ScriptExample[] GwInstekScope =
    {
        new("Instrument info",
            "# GW Instek GDS — identity (read-only).\r\n" +
            "# The guide documents only *IDN?, *LRN?, *RCL, *RST and *SAV, so this\r\n" +
            "# leaves out the usual *OPC? and error-queue reads.\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n"),

        new("Read scope settings",
            "# GW Instek GDS-2000 — current vertical and horizontal setup (read-only).\r\n" +
            "PRINT Scope settings:\r\n" +
            ":CHANnel1:DISPlay?\r\n" +
            ":CHANnel1:SCALe?\r\n" +
            ":CHANnel1:OFFSet?\r\n" +
            ":CHANnel1:COUPling?\r\n" +
            ":TIMebase:SCALe?\r\n"),

        new("Measure CH1",
            "# GW Instek measures against one selected source, so point it at the\r\n" +
            "# channel first and then read each parameter.\r\n" +
            ":MEASure:SOURce 1\r\n" +
            ":MEASure:VPP?\r\n" +
            ":MEASure:VAVerage?\r\n" +
            ":MEASure:FREQuency?\r\n" +
            ":MEASure:PERiod?\r\n"),

        new("Poll CH1 amplitude",
            "# Read CH1 peak-to-peak ten times, a second apart.\r\n" +
            ":MEASure:SOURce 1\r\n" +
            "REPEAT 10\r\n" +
            "    :MEASure:VPP?\r\n" +
            "    DELAY 1000\r\n" +
            "END\r\n" +
            "PRINT Polling complete.\r\n"),

        new("Autoset then measure",
            "# NOTE: :AUToset reconfigures the scope's whole front-panel setup.\r\n" +
            ":AUToset\r\n" +
            "DELAY 2000\r\n" +
            ":RUN\r\n" +
            ":CHANnel1:SCALe?\r\n" +
            ":TIMebase:SCALe?\r\n" +
            ":MEASure:SOURce 1\r\n" +
            ":MEASure:VPP?\r\n"),
    };

    // ----------------------------------------------------------------- Siglent scope

    private static readonly ScriptExample[] SiglentScope =
    {
        new("Instrument info",
            "# Siglent SDS — identity (read-only).\r\n" +
            "# The SDS guide documents no error-queue query, so this leaves it out.\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*OPC?\r\n"),

        new("Read scope settings",
            "# Siglent SDS — current vertical, horizontal and trigger setup (read-only).\r\n" +
            "PRINT Scope settings:\r\n" +
            ":CHANnel1:SWITch?\r\n" +
            ":CHANnel1:SCALe?\r\n" +
            ":CHANnel1:OFFSet?\r\n" +
            ":CHANnel1:COUPling?\r\n" +
            ":TIMebase:SCALe?\r\n" +
            ":TIMebase:DELay?\r\n" +
            ":TRIGger:MODE?\r\n"),

        new("Measure CH1",
            "# Siglent SDS — point the simple-measurement engine at a channel, then ask\r\n" +
            "# for each parameter by name.\r\n" +
            ":MEASure:SIMPle:SOURce C1\r\n" +
            ":MEASure:SIMPle:VALue? PKPK\r\n" +
            ":MEASure:SIMPle:VALue? FREQ\r\n" +
            ":MEASure:SIMPle:VALue? MEAN\r\n" +
            ":MEASure:SIMPle:VALue? PER\r\n"),

        new("Poll CH1 amplitude",
            "# Read CH1 peak-to-peak ten times, a second apart.\r\n" +
            ":MEASure:SIMPle:SOURce C1\r\n" +
            "REPEAT 10\r\n" +
            "    :MEASure:SIMPle:VALue? PKPK\r\n" +
            "    DELAY 1000\r\n" +
            "END\r\n" +
            "PRINT Polling complete.\r\n"),

        new("Run, stop, autoset",
            "# Siglent runs the acquisition through the trigger subsystem, not a bare :RUN.\r\n" +
            "# NOTE: :AUToset reconfigures the scope's whole front-panel setup.\r\n" +
            ":TRIGger:STOP\r\n" +
            ":AUToset\r\n" +
            "DELAY 2000\r\n" +
            ":TRIGger:RUN\r\n" +
            ":CHANnel1:SCALe?\r\n" +
            ":TIMebase:SCALe?\r\n"),

        new("Set up a waveform transfer",
            "# Siglent reads traces with :WAVeform:DATA?, described by :WAVeform:PREamble?.\r\n" +
            "# The Waveform button is not offered here — that preamble is a packed binary\r\n" +
            "# descriptor, not the Rigol ten-field comma list the built-in decoder reads.\r\n" +
            ":WAVeform:SOURce C1\r\n" +
            ":WAVeform:WIDTh BYTE\r\n" +
            ":WAVeform:POINt 0\r\n" +
            ":WAVeform:PREamble?\r\n" +
            "PRINT Send :WAVeform:DATA? to read the samples themselves.\r\n"),
    };

    // --------------------------------------------------------------------- R&S scope

    private static readonly ScriptExample[] RohdeScope =
    {
        new("Instrument info",
            "# R&S RTB2000 — identity and error queue (read-only).\r\n" +
            "# Note the dialect: no leading colon, and a bare RUN rather than :RUN.\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*OPC?\r\n" +
            "SYSTem:ERRor:NEXT?\r\n"),

        new("Read scope settings",
            "# R&S RTB2000 — current vertical, horizontal and trigger setup (read-only).\r\n" +
            "PRINT Scope settings:\r\n" +
            "CHANnel1:STATe?\r\n" +
            "CHANnel1:SCALe?\r\n" +
            "CHANnel1:OFFSet?\r\n" +
            "CHANnel1:COUPling?\r\n" +
            "TIMebase:SCALe?\r\n" +
            "TRIGger:A:SOURce?\r\n"),

        new("Measure CH1",
            "# R&S measures through numbered measurement places: point one at a source,\r\n" +
            "# choose what it measures, enable it, then read the result.\r\n" +
            "MEASurement1:SOURce CH1\r\n" +
            "MEASurement1:MAIN PEAK\r\n" +
            "MEASurement1:ENABle ON\r\n" +
            "DELAY 500\r\n" +
            "MEASurement1:RESult:ACTual?\r\n" +
            "MEASurement1:MAIN FREQuency\r\n" +
            "DELAY 500\r\n" +
            "MEASurement1:RESult:ACTual?\r\n"),

        new("Poll CH1 amplitude",
            "# Read the CH1 peak-to-peak ten times, a second apart.\r\n" +
            "MEASurement1:SOURce CH1\r\n" +
            "MEASurement1:MAIN PEAK\r\n" +
            "MEASurement1:ENABle ON\r\n" +
            "REPEAT 10\r\n" +
            "    MEASurement1:RESult:ACTual?\r\n" +
            "    DELAY 1000\r\n" +
            "END\r\n" +
            "PRINT Polling complete.\r\n"),

        new("Single-shot capture",
            "# Arm one acquisition, then read what it caught.\r\n" +
            "# NOTE: this changes the scope's run state.\r\n" +
            "SINGle\r\n" +
            "DELAY 500\r\n" +
            "MEASurement1:SOURce CH1\r\n" +
            "MEASurement1:MAIN PEAK\r\n" +
            "MEASurement1:ENABle ON\r\n" +
            "MEASurement1:RESult:ACTual?\r\n"),

        new("Set up a waveform transfer",
            "# R&S reads traces with CHANnel<m>:DATA?, described by CHANnel<m>:DATA:HEADer?.\r\n" +
            "# The Waveform button is not offered here — that header has a different shape\r\n" +
            "# from the Rigol ten-field preamble the built-in decoder implements.\r\n" +
            "FORMat:DATA ASCii\r\n" +
            "CHANnel1:DATA:HEADer?\r\n" +
            "CHANnel1:DATA:POINts?\r\n" +
            "PRINT Send CHANnel1:DATA? to read the samples themselves.\r\n"),
    };

    // -------------------------------------------------------------- R&S power supply

    private static readonly ScriptExample[] RohdePowerSupply =
    {
        new("Instrument info",
            "# R&S NGL/NGM/NGE — identity (read-only).\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*OPC?\r\n"),

        new("Read a channel",
            "# R&S supplies select a channel first, then act on it unqualified —\r\n" +
            "# unlike a Rigol, which names the channel in every command.\r\n" +
            "INSTrument:NSELect 1\r\n" +
            "INSTrument:NSELect?\r\n" +
            "PRINT Setpoints:\r\n" +
            "VOLTage?\r\n" +
            "CURRent?\r\n" +
            "PRINT Measured:\r\n" +
            "MEASure:SCALar:VOLTage:DC?\r\n" +
            "MEASure:SCALar:CURRent:DC?\r\n" +
            "MEASure:SCALar:POWer?\r\n"),

        new("Set 5 V / 1 A on CH1",
            "# R&S NGL/NGM — set channel 1 to 5 V with a 1 A limit.\r\n" +
            "# NOTE: the last two lines ENABLE the output. Check what is wired first.\r\n" +
            "# Both are needed: OUTPut:SELect arms the channel, OUTPut:GENeral is the\r\n" +
            "# master switch that actually connects the terminals.\r\n" +
            "INSTrument:NSELect 1\r\n" +
            "VOLTage 5\r\n" +
            "CURRent 1\r\n" +
            "VOLTage?\r\n" +
            "OUTPut:SELect ON\r\n" +
            "OUTPut:GENeral ON\r\n" +
            "DELAY 500\r\n" +
            "MEASure:SCALar:VOLTage:DC?\r\n" +
            "MEASure:SCALar:CURRent:DC?\r\n"),

        new("Log a rail for 30 s",
            "# One voltage, current and power reading a second for half a minute.\r\n" +
            "INSTrument:NSELect 1\r\n" +
            "PRINT Logging channel 1...\r\n" +
            "REPEAT 30\r\n" +
            "    MEASure:SCALar:VOLTage:DC?\r\n" +
            "    MEASure:SCALar:CURRent:DC?\r\n" +
            "    DELAY 1000\r\n" +
            "END\r\n" +
            "PRINT Log complete.\r\n"),

        new("All outputs off",
            "# The master switch takes every channel down at once.\r\n" +
            "OUTPut:GENeral OFF\r\n" +
            "OUTPut:GENeral?\r\n" +
            "PRINT All outputs off.\r\n"),
    };

    // ---------------------------------------------------------------- Keysight scope

    private static readonly ScriptExample[] KeysightScope =
    {
        new("Instrument info",
            "# Keysight InfiniiVision — identity and error queue (read-only)\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*OPC?\r\n" +
            ":SYSTem:ERRor?\r\n"),

        new("Read scope settings",
            "# Keysight InfiniiVision — current channel, timebase and trigger setup.\r\n" +
            "PRINT Scope settings:\r\n" +
            ":CHANnel1:SCALe?\r\n" +
            ":CHANnel1:OFFSet?\r\n" +
            ":CHANnel1:COUPling?\r\n" +
            ":TIMebase:SCALe?\r\n" +
            ":TRIGger:MODE?\r\n"),

        new("Measure CH1",
            "# Keysight InfiniiVision — the standard CH1 measurements (read-only)\r\n" +
            "PRINT CH1 measurements:\r\n" +
            ":MEASure:VPP? CHANnel1\r\n" +
            ":MEASure:VAVerage? CHANnel1\r\n" +
            ":MEASure:FREQuency? CHANnel1\r\n" +
            ":MEASure:PERiod? CHANnel1\r\n"),

        new("Single-shot capture",
            "# Arm a single acquisition, then read what it caught.\r\n" +
            "# NOTE: :SINGle changes the scope's run state.\r\n" +
            ":SINGle\r\n" +
            "DELAY 500\r\n" +
            ":MEASure:VPP? CHANnel1\r\n" +
            ":MEASure:FREQuency? CHANnel1\r\n"),

        new("Set up a waveform transfer",
            "# Keysight reads traces with :WAVeform:DATA?, scaled by :WAVeform:PREamble?.\r\n" +
            "# The Waveform button is not offered here: the preamble has the same ten\r\n" +
            "# fields as the Rigol one but a different conversion —\r\n" +
            "#   voltage = (data - yreference) * yincrement + yorigin\r\n" +
            "# so the built-in decoder would give the right shape at the wrong offset.\r\n" +
            ":WAVeform:SOURce CHANnel1\r\n" +
            ":WAVeform:FORMat BYTE\r\n" +
            ":WAVeform:POINts:MODE RAW\r\n" +
            ":WAVeform:PREamble?\r\n" +
            "PRINT Send :WAVeform:DATA? to read the samples themselves.\r\n"),

        new("Screen capture notes",
            "# The Capture Screen button sends :DISPlay:DATA? PNG,COLor.\r\n" +
            "# Ink-saver is on by default, which returns the image on a white\r\n" +
            "# background. Turn it off first for the on-screen colours.\r\n" +
            ":HARDcopy:INKSaver OFF\r\n" +
            ":HARDcopy:INKSaver?\r\n"),
    };

    // --------------------------------------------------------- Keysight power supply

    private static readonly ScriptExample[] KeysightPowerSupply =
    {
        new("Instrument info",
            "# Keysight E36300 — identity and error queue (read-only)\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*OPC?\r\n" +
            "SYSTem:ERRor?\r\n"),

        new("Read all channels",
            "# Keysight E36300 — settings and readings for all three outputs.\r\n" +
            "# Outputs are addressed by channel list, (@1), not by name.\r\n" +
            "PRINT Setpoints:\r\n" +
            "VOLTage? (@1)\r\n" +
            "CURRent? (@1)\r\n" +
            "PRINT Measured:\r\n" +
            "MEASure:VOLTage:DC? (@1)\r\n" +
            "MEASure:CURRent:DC? (@1)\r\n" +
            "OUTPut? (@1)\r\n"),

        new("Set 5 V / 1 A on CH1",
            "# Keysight E36300 — set output 1 to 5 V with a 1 A limit.\r\n" +
            "# NOTE: the last line ENABLES the output. Check what is wired first.\r\n" +
            "VOLTage 5,(@1)\r\n" +
            "CURRent 1,(@1)\r\n" +
            "VOLTage? (@1)\r\n" +
            "OUTPut ON,(@1)\r\n" +
            "DELAY 500\r\n" +
            "MEASure:VOLTage:DC? (@1)\r\n" +
            "MEASure:CURRent:DC? (@1)\r\n"),

        new("Log a rail for 30 s",
            "# One voltage and current reading a second for half a minute.\r\n" +
            "PRINT Logging output 1...\r\n" +
            "REPEAT 30\r\n" +
            "    MEASure:VOLTage:DC? (@1)\r\n" +
            "    MEASure:CURRent:DC? (@1)\r\n" +
            "    DELAY 1000\r\n" +
            "END\r\n" +
            "PRINT Log complete.\r\n"),

        new("All outputs off",
            "# Make the supply safe. A channel list can name several at once.\r\n" +
            "OUTPut OFF,(@1,2,3)\r\n" +
            "OUTPut? (@1)\r\n" +
            "OUTPut? (@2)\r\n" +
            "PRINT All outputs off.\r\n"),
    };

    // ------------------------------------------------------------- Keithley SourceMeter

    private static readonly ScriptExample[] KeithleySmu =
    {
        new("Instrument info",
            "# Keithley SourceMeter — identity, language and error queue (read-only).\r\n" +
            "# A 2450 set to TSP answers none of the SCPI below. *LANG? tells you which\r\n" +
            "# it is in; switching needs '*LANG SCPI' and a power cycle.\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*LANG?\r\n" +
            ":SYSTem:ERRor?\r\n"),

        new("Read source and measure setup",
            "# Keithley 2450 — what it is sourcing and what it is measuring (read-only).\r\n" +
            ":SOURce:FUNCtion?\r\n" +
            ":OUTPut:STATe?\r\n" +
            ":MEASure:VOLTage:DC?\r\n" +
            ":MEASure:CURRent:DC?\r\n"),

        new("Source 1 V, measure current",
            "# Keithley 2450 — the classic IV point: force a voltage, read the current.\r\n" +
            "# NOTE: the output line ENERGISES whatever is connected. Check the limit.\r\n" +
            ":SOURce:FUNCtion VOLTage\r\n" +
            ":SOURce:VOLTage 1\r\n" +
            ":SOURce:VOLTage:ILIMit:LEVel 0.01\r\n" +
            ":OUTPut:STATe ON\r\n" +
            "DELAY 200\r\n" +
            ":MEASure:CURRent:DC?\r\n" +
            ":OUTPut:STATe OFF\r\n" +
            "PRINT Output off.\r\n"),

        new("IV sweep by hand",
            "# Keithley 2450 — step a voltage and read the current at each point.\r\n" +
            "# NOTE: this ENERGISES the device under test.\r\n" +
            ":SOURce:FUNCtion VOLTage\r\n" +
            ":SOURce:VOLTage:ILIMit:LEVel 0.01\r\n" +
            ":OUTPut:STATe ON\r\n" +
            ":SOURce:VOLTage 0.2\r\n" +
            "DELAY 200\r\n" +
            ":MEASure:CURRent:DC?\r\n" +
            ":SOURce:VOLTage 0.4\r\n" +
            "DELAY 200\r\n" +
            ":MEASure:CURRent:DC?\r\n" +
            ":SOURce:VOLTage 0.6\r\n" +
            "DELAY 200\r\n" +
            ":MEASure:CURRent:DC?\r\n" +
            ":OUTPut:STATe OFF\r\n" +
            "PRINT Sweep complete, output off.\r\n"),

        new("Output off",
            "# Make the SourceMeter safe.\r\n" +
            ":OUTPut:STATe OFF\r\n" +
            ":OUTPut:STATe?\r\n" +
            "PRINT Output off.\r\n"),
    };

    // --------------------------------------------------------------- Keithley DMM

    private static readonly ScriptExample[] KeithleyDmm =
    {
        new("Instrument info",
            "# Keithley DMM6500 — identity and language (read-only).\r\n" +
            "# Like the SourceMeter it can be in TSP mode, where none of this answers.\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*LANG?\r\n" +
            ":SYSTem:ERRor?\r\n"),

        new("Voltage — DC and AC",
            "# Each :MEASure? sets the meter's function and then takes one reading.\r\n" +
            "PRINT DC then AC volts:\r\n" +
            ":MEASure:VOLTage:DC?\r\n" +
            ":MEASure:VOLTage:AC?\r\n"),

        new("Resistance and diode",
            "# Two- and four-wire resistance, then the diode function.\r\n" +
            ":MEASure:RESistance?\r\n" +
            ":MEASure:FRESistance?\r\n" +
            ":MEASure:DIODe?\r\n"),

        new("Log 30 DC readings",
            "# One reading a second for half a minute.\r\n" +
            "# The Readout window plots this kind of run live — this is the scripted form.\r\n" +
            "PRINT Logging DC volts...\r\n" +
            "REPEAT 30\r\n" +
            "    :MEASure:VOLTage:DC?\r\n" +
            "    DELAY 1000\r\n" +
            "END\r\n" +
            "PRINT Log complete.\r\n"),

        new("Fast repeat read",
            "# :READ? re-triggers the function the meter is already set to, so it is\r\n" +
            "# quicker than a :MEASure? for repeated sampling.\r\n" +
            ":MEASure:VOLTage:DC?\r\n" +
            "REPEAT 20\r\n" +
            "    :READ?\r\n" +
            "    DELAY 250\r\n" +
            "END\r\n"),
    };

    // --------------------------------------------------------------- Tektronix scope

    private static readonly ScriptExample[] TektronixScope =
    {
        new("Instrument info",
            "# Tektronix scope — identity and event queue (read-only)\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*OPC?\r\n" +
            "ALLEv?\r\n"),

        new("Read scope settings",
            "# Tektronix MDO/MSO/DPO — current vertical, horizontal and trigger setup.\r\n" +
            "# Read-only. Note the dialect: CH1:SCAle, not Rigol's :CHANnel1:SCALe.\r\n" +
            "PRINT Scope settings:\r\n" +
            "CH1:SCAle?\r\n" +
            "CH1:POSition?\r\n" +
            "CH1:COUPling?\r\n" +
            "HORizontal:SCAle?\r\n" +
            "TRIGger:A:EDGE:SOUrce?\r\n"),

        new("Measure CH1",
            "# Tektronix takes one immediate measurement at a time: point it at a\r\n" +
            "# source, choose a type, then read the value.\r\n" +
            "MEASUrement:IMMed:SOUrce1 CH1\r\n" +
            "MEASUrement:IMMed:TYPe PK2pk\r\n" +
            "MEASUrement:IMMed:VALue?\r\n" +
            "MEASUrement:IMMed:TYPe FREQuency\r\n" +
            "MEASUrement:IMMed:VALue?\r\n" +
            "MEASUrement:IMMed:TYPe MEAN\r\n" +
            "MEASUrement:IMMed:VALue?\r\n"),

        new("Poll CH1 amplitude",
            "# Read CH1 peak-to-peak ten times, a second apart.\r\n" +
            "MEASUrement:IMMed:SOUrce1 CH1\r\n" +
            "MEASUrement:IMMed:TYPe PK2pk\r\n" +
            "REPEAT 10\r\n" +
            "    MEASUrement:IMMed:VALue?\r\n" +
            "    DELAY 1000\r\n" +
            "END\r\n" +
            "PRINT Polling complete.\r\n"),

        new("Single-shot capture",
            "# Arm one acquisition, then read what it caught.\r\n" +
            "# NOTE: this changes the scope's run state.\r\n" +
            "ACQuire:STOPAfter SEQuence\r\n" +
            "ACQuire:STATE RUN\r\n" +
            "DELAY 500\r\n" +
            "BUSY?\r\n" +
            "MEASUrement:IMMed:SOUrce1 CH1\r\n" +
            "MEASUrement:IMMed:TYPe PK2pk\r\n" +
            "MEASUrement:IMMed:VALue?\r\n"),

        new("Autoset then measure",
            "# NOTE: AUTOSet reconfigures the scope's whole front-panel setup.\r\n" +
            "AUTOSet EXECute\r\n" +
            "DELAY 2000\r\n" +
            "PRINT Measurements after autoset:\r\n" +
            "CH1:SCAle?\r\n" +
            "HORizontal:SCAle?\r\n" +
            "MEASUrement:IMMed:SOUrce1 CH1\r\n" +
            "MEASUrement:IMMed:TYPe PK2pk\r\n" +
            "MEASUrement:IMMed:VALue?\r\n"),

        new("Set up a waveform transfer",
            "# Tektronix reads traces with CURVe?, described by a WFMOutpre preamble —\r\n" +
            "# a different scheme from the Rigol :WAVeform tree the Waveform button\r\n" +
            "# uses, which is why that button is not offered for this instrument.\r\n" +
            "# This prepares the transfer and reads the scaling factors.\r\n" +
            "DATa:SOUrce CH1\r\n" +
            "DATa:ENCdg RIBinary\r\n" +
            "DATa:WIDth 1\r\n" +
            "WFMOutpre:NR_Pt?\r\n" +
            "WFMOutpre:XINcr?\r\n" +
            "WFMOutpre:XZEro?\r\n" +
            "WFMOutpre:YMUlt?\r\n" +
            "WFMOutpre:YOFf?\r\n" +
            "WFMOutpre:YZEro?\r\n" +
            "PRINT Send CURVe? to read the samples themselves.\r\n"),

        new("Screen capture to the PC",
            "# The two-step Tektronix screenshot. The Capture Screen button expects a\r\n" +
            "# single query, so it is not offered here — run this instead, then read\r\n" +
            "# the image block that HARDCopy STARt returns.\r\n" +
            "SAVe:IMAGe:FILEFormat PNG\r\n" +
            "SAVe:IMAGe:INKSaver OFF\r\n" +
            "SAVe:IMAGe:FILEFormat?\r\n" +
            "PRINT Now send: HARDCopy STARt\r\n"),
    };

    // ------------------------------------------------------------------- power supply

    private static readonly ScriptExample[] PowerSupply =
    {
        new("Instrument info",
            "# DC power supply — identity and error queue (read-only)\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*OPC?\r\n" +
            ":SYSTem:ERRor?\r\n"),

        new("Read all channels",
            "# Rigol DP800 — what every channel is set to and doing (read-only)\r\n" +
            "PRINT CH1:\r\n" +
            ":APPLy? CH1\r\n" +
            ":MEASure:ALL:DC? CH1\r\n" +
            ":OUTPut? CH1\r\n" +
            "PRINT CH2:\r\n" +
            ":APPLy? CH2\r\n" +
            ":MEASure:ALL:DC? CH2\r\n" +
            ":OUTPut? CH2\r\n"),

        new("Set 5 V / 1 A on CH1",
            "# Rigol DP800 — set CH1 to 5 V with a 1 A limit.\r\n" +
            "# NOTE: the last line ENABLES the CH1 output. Check what is wired first.\r\n" +
            ":APPLy CH1,5,1\r\n" +
            ":APPLy? CH1\r\n" +
            ":OUTPut CH1,ON\r\n" +
            ":OUTPut? CH1\r\n" +
            ":MEASure:ALL:DC? CH1\r\n"),

        new("Arm over-voltage protection",
            "# Rigol DP800 — set and enable OVP/OCP on CH1, then read them back.\r\n" +
            "# Does not enable the output.\r\n" +
            ":OUTPut:OVP:VALue CH1,6\r\n" +
            ":OUTPut:OVP CH1,ON\r\n" +
            ":OUTPut:OCP:VALue CH1,1.5\r\n" +
            ":OUTPut:OCP CH1,ON\r\n" +
            ":OUTPut:OVP:VALue? CH1\r\n" +
            ":OUTPut:OCP:VALue? CH1\r\n"),

        new("Log a rail for 30 s",
            "# One voltage/current/power reading a second for half a minute.\r\n" +
            "# The Readout window plots this live — this is the scripted form.\r\n" +
            "PRINT Logging CH1...\r\n" +
            "REPEAT 30\r\n" +
            "    :MEASure:ALL:DC? CH1\r\n" +
            "    DELAY 1000\r\n" +
            "END\r\n" +
            "PRINT Log complete.\r\n"),

        new("All outputs off",
            "# Make the supply safe.\r\n" +
            ":OUTPut CH1,OFF\r\n" +
            ":OUTPut CH2,OFF\r\n" +
            ":OUTPut CH3,OFF\r\n" +
            ":OUTPut? CH1\r\n" +
            ":OUTPut? CH2\r\n" +
            "PRINT All outputs off.\r\n"),
    };

    // ---------------------------------------------------------------- electronic load

    private static readonly ScriptExample[] ElectronicLoad =
    {
        new("Instrument info",
            "# Electronic load — identity (read-only)\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*OPC?\r\n"),

        new("Read input",
            "# Siglent SDL1000X — what the load is set to and drawing (read-only)\r\n" +
            ":SOURce:FUNCtion?\r\n" +
            ":SOURce:INPut:STATe?\r\n" +
            "MEASure:VOLTage:DC?\r\n" +
            "MEASure:CURRent:DC?\r\n" +
            "MEASure:POWer:DC?\r\n" +
            "MEASure:RESistance:DC?\r\n"),

        new("Constant current, 500 mA",
            "# Siglent SDL1000X — draw a steady 500 mA.\r\n" +
            "# NOTE: the last line SINKS CURRENT from whatever is connected.\r\n" +
            ":SOURce:FUNCtion CURRent\r\n" +
            ":SOURce:CURRent:LEVel:IMMediate 0.5\r\n" +
            ":SOURce:FUNCtion?\r\n" +
            ":SOURce:INPut:STATe ON\r\n" +
            "DELAY 500\r\n" +
            "MEASure:VOLTage:DC?\r\n" +
            "MEASure:CURRent:DC?\r\n"),

        new("Load step",
            "# Siglent SDL1000X — step the load and watch the rail sag.\r\n" +
            "# NOTE: this SINKS CURRENT. Check the supply can deliver it.\r\n" +
            ":SOURce:FUNCtion CURRent\r\n" +
            ":SOURce:CURRent:LEVel:IMMediate 0.1\r\n" +
            ":SOURce:INPut:STATe ON\r\n" +
            "DELAY 1000\r\n" +
            "MEASure:VOLTage:DC?\r\n" +
            ":SOURce:CURRent:LEVel:IMMediate 1.0\r\n" +
            "DELAY 1000\r\n" +
            "MEASure:VOLTage:DC?\r\n" +
            ":SOURce:INPut:STATe OFF\r\n" +
            "PRINT Load step complete, input off.\r\n"),

        new("Input off",
            "# Stop drawing current.\r\n" +
            ":SOURce:INPut:STATe OFF\r\n" +
            ":SOURce:INPut:STATe?\r\n" +
            "PRINT Input off.\r\n"),
    };

    // --------------------------------------------------------------- spectrum analyzer

    private static readonly ScriptExample[] SpectrumAnalyzer =
    {
        new("Instrument info",
            "# Spectrum analyzer — identity (read-only)\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*OPC?\r\n"),

        new("Read sweep setup",
            "# Siglent SSA3000X — the current sweep configuration (read-only)\r\n" +
            ":SENSe:FREQuency:CENTer?\r\n" +
            ":SENSe:FREQuency:SPAN?\r\n" +
            ":SENSe:FREQuency:STARt?\r\n" +
            ":SENSe:FREQuency:STOP?\r\n" +
            ":SENSe:BWIDth:RESolution?\r\n" +
            ":SENSe:BWIDth:VIDeo?\r\n" +
            ":SENSe:SWEep:TIME?\r\n"),

        new("Look at a 100 MHz carrier",
            "# Siglent SSA3000X — centre on 100 MHz with a 1 MHz span.\r\n" +
            "# Changes the analyzer's sweep setup; harmless to whatever is connected.\r\n" +
            ":SENSe:FREQuency:CENTer 100000000\r\n" +
            ":SENSe:FREQuency:SPAN 1000000\r\n" +
            ":SENSe:BWIDth:RESolution:AUTO ON\r\n" +
            ":SENSe:FREQuency:CENTer?\r\n" +
            ":SENSe:FREQuency:SPAN?\r\n"),

        new("Single sweep",
            "# Take one sweep rather than running continuously.\r\n" +
            ":INITiate:CONTinuous OFF\r\n" +
            ":INITiate:IMMediate\r\n" +
            "DELAY 2000\r\n" +
            ":SENSe:SWEep:TIME?\r\n" +
            "PRINT Sweep complete.\r\n"),

        new("Clear all markers",
            "# Siglent SSA3000X — turn every marker off.\r\n" +
            ":CALCulate:MARKer:AOFF\r\n" +
            "PRINT Markers cleared.\r\n"),
    };

    // ------------------------------------------------------------------------ generic

    private static readonly ScriptExample[] Generic =
    {
        new("Instrument info",
            "# Commands every IEEE 488.2 instrument understands.\r\n" +
            "PRINT Reading instrument info...\r\n" +
            "*IDN?\r\n" +
            "*OPC?\r\n" +
            "*STB?\r\n"),

        new("Clear status",
            "# Clear the status registers, then read them back.\r\n" +
            "*CLS\r\n" +
            "*STB?\r\n"),

        new("Poll identity",
            "# A simple loop, useful for checking a link stays up.\r\n" +
            "REPEAT 5\r\n" +
            "    *IDN?\r\n" +
            "    DELAY 500\r\n" +
            "END\r\n" +
            "PRINT Link still alive.\r\n"),
    };
}
