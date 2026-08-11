using System.Linq;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

public class InstrumentProfileTests
{
    private static string[] Commands(string? idn) =>
        InstrumentProfile.ForIdentity(idn).Commands.Select(c => c.Command).ToArray();

    [Fact]
    public void Rigol_scope_gets_oscilloscope_profile()
    {
        var p = InstrumentProfile.ForIdentity("RIGOL TECHNOLOGIES,DS2202,DS2A152001051,00.01.00");
        Assert.Equal("Oscilloscope (DS2202)", p.Name);
        Assert.Contains(":RUN", Commands("RIGOL TECHNOLOGIES,DS2202,X,Y"));
    }

    [Fact]
    public void Scope_profile_carries_a_screen_capture_command_generator_does_not()
    {
        Assert.Equal(":DISPlay:DATA?",
            InstrumentProfile.ForIdentity("RIGOL TECHNOLOGIES,DS2202,X,Y").ScreenCaptureCommand);
        Assert.Null(
            InstrumentProfile.ForIdentity("Siglent Technologies,SDG2042X,SN,2.0").ScreenCaptureCommand);
    }

    /// <summary>
    /// The two R&amp;S analyzers reach a screenshot by different routes, and each takes the one
    /// its own manual documents rather than the one the R&amp;S scope uses.
    ///
    /// The FSL was briefly given the scope's "HCOPy:DATA?" on the strength of the shared
    /// maker. Its manual has no such command: a hardcopy there is written to a file and read
    /// back, which is why this profile alone needs a setup sequence.
    /// </summary>
    [Fact]
    public void Rohde_analyzers_capture_the_screen_the_way_their_own_manual_documents()
    {
        var fpc = InstrumentProfile.ForIdentity("Rohde&Schwarz,FPC1500,1328.6660k03/000,1.0");
        Assert.Equal("DISPlay:WINDow:FETCh?", fpc.ScreenCaptureCommand);
        Assert.Empty(fpc.ScreenCaptureSetup);

        var fsl = InstrumentProfile.ForIdentity("Rohde&Schwarz,FSL6,1300.2502K06,2.30");
        Assert.Equal(@"MMEMory:DATA? 'C:\R_S\instr\user\Print.png'", fsl.ScreenCaptureCommand);

        // Format, destination and file name have to be set before the hardcopy is taken,
        // and the hardcopy has to finish before the file is read back.
        Assert.Equal(new[]
        {
            "HCOPy:DEVice:LANGuage1 PNG",
            "HCOPy:DESTination1 'MMEM'",
            @"MMEMory:NAME 'C:\R_S\instr\user\Print.png'",
            "HCOPy:IMMediate1",
            "*WAI",
        }, fsl.ScreenCaptureSetup);

        // The file the hardcopy writes and the file read back must be the same one.
        Assert.Contains(fsl.ScreenCaptureSetup,
            s => s.StartsWith("MMEMory:NAME") && s.EndsWith(@"'C:\R_S\instr\user\Print.png'"));

        // Neither takes the R&S scope's one-shot query, which is an RTB2000 command.
        Assert.Equal("HCOPy:DATA?",
            InstrumentProfile.ForIdentity("Rohde&Schwarz,RTB2004,1333.1005k04/000,1.0")
                             .ScreenCaptureCommand);
    }

    [Fact]
    public void Siglent_multimeter_gets_standard_scpi_not_the_generator_dialect()
    {
        const string idn = "Siglent Technologies,SDM3065X,SDM36HCD801207,3.02.01.13";
        var p = InstrumentProfile.ForIdentity(idn);

        Assert.Equal("Multimeter (SDM3065X)", p.Name);
        Assert.Contains("MEASure:VOLTage:DC?", Commands(idn));
        // Same maker as the SDG generators, but a completely different command language.
        Assert.DoesNotContain(Commands(idn), c => c.Contains("BSWV"));
        // A meter has no screen-dump or waveform-trace support in this app.
        Assert.Null(p.ScreenCaptureCommand);
        Assert.False(p.SupportsWaveformCapture);
    }

    [Fact]
    public void Siglent_generator_gets_siglent_syntax()
    {
        var p = InstrumentProfile.ForIdentity("Siglent Technologies,SDG2042X,SN,2.0");
        Assert.Contains("Siglent generator", p.Name);
        Assert.Contains("C1:BSWV?", Commands("Siglent Technologies,SDG2042X,SN,2.0"));
    }

    [Fact]
    public void Siglent_scope_is_a_scope_not_a_generator()
    {
        var p = InstrumentProfile.ForIdentity("Siglent Technologies,SDS1104X-E,SN,1.0");
        Assert.Equal("Siglent oscilloscope (SDS1104X-E)", p.Name);
        // Siglent's scope dialect, not its generator's: ":TRIGger:RUN" rather than
        // ":RUN", and nothing resembling the SDG's "C1:BSWV".
        string[] commands = Commands("Siglent Technologies,SDS1104X-E,SN,1.0");
        Assert.Contains(":TRIGger:RUN", commands);
        Assert.DoesNotContain(commands, c => c.Contains("BSWV"));
    }

    [Theory]
    [InlineData("RIGOL TECHNOLOGIES,DG1032Z,SN,03.01")]
    [InlineData("Keysight Technologies,33500B,MY,2.0")]
    public void Non_siglent_generator_gets_standard_scpi_not_siglent_syntax(string idn)
    {
        var cmds = Commands(idn);
        Assert.Contains(cmds, c => c.StartsWith(":SOURce1"));   // standard SCPI
        Assert.DoesNotContain(cmds, c => c.Contains("BSWV"));   // NOT Siglent's dialect
    }

    [Fact]
    public void Unknown_model_falls_back_to_safe_ieee4882_set()
    {
        var p = InstrumentProfile.ForIdentity("Some Vendor,XYZ-999,SN,1.0");
        Assert.Contains("XYZ-999", p.Name);
        Assert.Contains("*IDN?", p.Commands.Select(c => c.Command));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_identity_is_handled(string? idn)
    {
        var p = InstrumentProfile.ForIdentity(idn);
        Assert.NotEmpty(p.Commands);
        Assert.Contains("*IDN?", p.Commands.Select(c => c.Command));
    }

    /// <summary>
    /// One identity per row of SPEC §8's classification table, in its order.
    ///
    /// That table had drifted: it listed Chroma power supplies eighth when they are actually
    /// matched after the load tests, and it predated five rules — the Siglent X-suffix split
    /// and the two vendor-specific pairs that match and then decline to Generic. A routing
    /// table is exactly the kind of documentation that looks maintained while being wrong,
    /// because nothing reads it. This does.
    /// </summary>
    [Theory]
    [InlineData("TEKTRONIX,MDO4104C,C,1", InstrumentFamily.TektronixScope)]                 // 1
    [InlineData("KEYSIGHT TECHNOLOGIES,MSO-X 3054T,M,1", InstrumentFamily.KeysightScope)]   // 2
    [InlineData("Rohde&Schwarz,RTB2004,1,1", InstrumentFamily.RohdeScope)]                  // 3
    [InlineData("Siglent Technologies,SDS1202X-E,S,1", InstrumentFamily.SiglentScope)]      // 4
    [InlineData("Siglent Technologies,SDS1052DL,S,1", InstrumentFamily.Generic)]            // 4, no X
    [InlineData("GW INSTEK,GDS-2204E,G,1", InstrumentFamily.GwInstekScope)]                 // 5
    [InlineData("FLUKE,8846A,1,1", InstrumentFamily.FlukeMultimeter)]                       // 6
    [InlineData("KEITHLEY INSTRUMENTS,MODEL 2450,0,1", InstrumentFamily.KeithleySmu)]       // 7
    [InlineData("KEITHLEY INSTRUMENTS,DMM6500,0,1", InstrumentFamily.KeithleyDmm)]          // 8
    [InlineData("Rohde&Schwarz,FPC1500,1,1", InstrumentFamily.RohdeSpectrumAnalyzer)]       // 9
    [InlineData("Rohde&Schwarz,FSL6,1,1", InstrumentFamily.RohdeFslAnalyzer)]               // 9
    [InlineData("Rohde&Schwarz,FSW26,1,1", InstrumentFamily.RohdeFswAnalyzer)]              // 9
    [InlineData("Rohde&Schwarz,FSV13,1,1", InstrumentFamily.RohdeFsvAnalyzer)]              // 9
    [InlineData("Rohde&Schwarz,FSU26,1,1", InstrumentFamily.RohdeFsuAnalyzer)]              // 9
    [InlineData("Rohde&Schwarz,FSP13,1,1", InstrumentFamily.RohdeFspAnalyzer)]              // 9
    [InlineData("Rohde&Schwarz,FSQ26,1,1", InstrumentFamily.RohdeFsqAnalyzer)]              // 9
    [InlineData("ROHDE&SCHWARZ,FSEB30,1,1", InstrumentFamily.Generic)]                      // 10, no guide
    [InlineData("Siglent Technologies,SSA3021X,S,1", InstrumentFamily.SpectrumAnalyzer)]    // 11
    [InlineData("Siglent Technologies,SDM3065X,S,1", InstrumentFamily.Multimeter)]          // 12
    [InlineData("B&K PRECISION,8600,1,1", InstrumentFamily.BkElectronicLoad)]               // 13
    [InlineData("Chroma ATE,63206A,0,1", InstrumentFamily.ChromaElectronicLoad)]            // 14
    [InlineData("Chroma ATE,63640-80-80,0,1", InstrumentFamily.ChromaModularLoad)]          // 15
    [InlineData("Chroma ATE,63803-350-105,0,1", InstrumentFamily.Generic)]                  // 16, declines
    [InlineData("Siglent Technologies,SDL1020X,S,1", InstrumentFamily.ElectronicLoad)]      // 17
    [InlineData("Chroma ATE,62012P-80-60,0,1", InstrumentFamily.ChromaPowerSupply)]         // 18
    [InlineData("Keysight Technologies,E36313A,M,1", InstrumentFamily.KeysightPowerSupply)] // 19
    [InlineData("Rohde&Schwarz,NGL202,3,1", InstrumentFamily.RohdePowerSupply)]             // 20
    [InlineData("B&K PRECISION,9202,0,1", InstrumentFamily.BkPowerSupply)]                  // 21
    [InlineData("B&K PRECISION,9130B,0,1", InstrumentFamily.BkPowerSupply9130)]             // 22
    [InlineData("RIGOL TECHNOLOGIES,DP832,X,1", InstrumentFamily.PowerSupply)]              // 23
    [InlineData("Siglent Technologies,SDG2042X,X,1", InstrumentFamily.SiglentGenerator)]    // 24
    [InlineData("Rigol Technologies,DG1022Z,X,1", InstrumentFamily.ScpiGenerator)]          // 24
    [InlineData("RIGOL TECHNOLOGIES,DS2202,X,1", InstrumentFamily.Oscilloscope)]            // 25
    [InlineData("ACME,WIDGET,0,1", InstrumentFamily.Generic)]                               // 26
    public void The_classification_table_in_SPEC_8_matches_the_code(string idn, InstrumentFamily expected)
        => Assert.Equal(expected, InstrumentProfile.FamilyForIdentity(idn));

    // ----------------------------------------------------- confidently-wrong catalogs
    //
    // Three instruments used to be handed another vendor's command set. That is worse
    // than no catalog: the buttons appear, some even work, and the ones that fail look
    // like the instrument's fault. Each falls through to Generic until a real guide for
    // it is transcribed — no buttons, but nothing false either.
    //
    // All three now have one. What has to keep holding is the shape of the rule: a
    // transcribed guide earns a catalog, and everything else still gets none.
    //
    // The FSU, FSP and FSQ all joined the transcribed side in August 2026 — their
    // Operating Manuals turned out to be on R&S's own CDNs and a mirror — so the case
    // here fell back a generation, to the FSE series and the FSIQ: no guide in this
    // repo, not one of the seven the classifier names, and they must still come out
    // Generic. If someone ever widens a prefix test far enough to swallow one of these,
    // this is what says so.

    [Theory]
    [InlineData("ROHDE&SCHWARZ,FSEB30,847121/004,3.30")]
    [InlineData("ROHDE&SCHWARZ,FSIQ26,847121/004,4.31")]
    public void Rohde_analyzers_without_a_guide_get_no_catalog(string idn)
    {
        Assert.Equal(InstrumentFamily.Generic, InstrumentProfile.FamilyForIdentity(idn));
        Assert.Null(CommandReference.ForIdentity(idn));
    }

    /// <summary>
    /// The FSP and FSQ, from their own Operating Manuals — the FSU generation complete.
    /// Neither takes another sibling's catalog: seven R&amp;S analyzer sets, none
    /// interchangeable, and an FSPN — a modern spectrum monitor that shares the FSP's
    /// prefix — takes none of them.
    /// </summary>
    [Theory]
    [InlineData("Rohde&Schwarz,FSP13,1164.4391K13,4.70", InstrumentFamily.RohdeFspAnalyzer, "FSP")]
    [InlineData("Rohde&Schwarz,FSP30,1164.4391K30,4.70", InstrumentFamily.RohdeFspAnalyzer, "FSP")]
    [InlineData("Rohde&Schwarz,FSQ8,1313.9100K08,4.75", InstrumentFamily.RohdeFsqAnalyzer, "FSQ")]
    [InlineData("ROHDE&SCHWARZ,FSQ26,1313.9100K26,4.75", InstrumentFamily.RohdeFsqAnalyzer, "FSQ")]
    public void Rohde_FSP_and_FSQ_get_their_own_transcribed_catalogs(
        string idn, InstrumentFamily family, string marker)
    {
        Assert.Equal(family, InstrumentProfile.FamilyForIdentity(idn));

        CommandReference? r = CommandReference.ForIdentity(idn);
        Assert.NotNull(r);
        Assert.Contains(marker, r!.Instrument);
        Assert.DoesNotContain("FSU", r.Instrument);
    }

    [Fact]
    public void An_FSPN_spectrum_monitor_is_not_an_FSP()
        => Assert.Equal(InstrumentFamily.Generic,
            InstrumentProfile.FamilyForIdentity("Rohde&Schwarz,FSPN26,1322.8003K26,2.10"));

    /// <summary>
    /// The FSU, from its own Operating Manual (1313.9646.12-02) — the fifth R&amp;S analyzer
    /// set, and the FSL's ancestor: the same numeric-suffix dialect, its own manual, its
    /// own catalog. Not the FSL's catalog and not the FSW's, which is the confusion the
    /// per-line families exist to prevent.
    /// </summary>
    [Theory]
    [InlineData("Rohde&Schwarz,FSU8,1166.1660K08,4.71")]
    [InlineData("ROHDE&SCHWARZ,FSU26,1313.9000K26,4.71")]
    public void Rohde_FSU_analyzers_get_the_transcribed_FSU_catalog(string idn)
    {
        Assert.Equal(InstrumentFamily.RohdeFsuAnalyzer, InstrumentProfile.FamilyForIdentity(idn));

        CommandReference? r = CommandReference.ForIdentity(idn);
        Assert.NotNull(r);
        Assert.Contains("FSU", r!.Instrument);
        Assert.DoesNotContain("FSL", r.Instrument);
    }

    /// <summary>
    /// The FSV and FSVA now have their own catalog too, from the FSVA/FSV Operating Manual.
    /// That makes three R&amp;S analyzer command sets in the app, none of them interchangeable:
    /// the FSV takes the modern [SENSe:] root, the FSL its own numeric suffixes, and the FPC
    /// something smaller than either.
    ///
    /// The FSW has one now as well, which makes four.
    /// </summary>
    [Theory]
    [InlineData("Rohde&Schwarz,FSV13,1307.9002K13,3.20")]
    [InlineData("Rohde&Schwarz,FSV30,1321.3008K30,3.20")]
    [InlineData("ROHDE&SCHWARZ,FSVA40,1321.3008K40,3.20")]
    public void Rohde_FSV_analyzers_get_the_transcribed_FSV_catalog(string idn)
    {
        Assert.Equal(InstrumentFamily.RohdeFsvAnalyzer, InstrumentProfile.FamilyForIdentity(idn));
        Assert.NotNull(CommandReference.ForIdentity(idn));
    }

    /// <summary>
    /// The FSW, from its own User Manual (1173.9411.02 v56) — the fourth R&amp;S analyzer set,
    /// and the one that was open longest. The manual was never unavailable: the download
    /// page archived alongside it carries a direct link to R&amp;S's own CDN, which is where it
    /// came from. What had been recorded was that every third-party mirror 403s, which is
    /// true and was not the question.
    ///
    /// FSW and FSV must not collide. Both start "FS", both are R&amp;S, and handing an FSW the
    /// FSV catalog is the same mistake as handing an FSL the Siglent one — close enough to
    /// look like it works, so the failures read as the instrument misbehaving.
    /// </summary>
    [Theory]
    [InlineData("Rohde&Schwarz,FSW8,1331.5003K08,5.20")]
    [InlineData("Rohde&Schwarz,FSW26,1312.8000K26,4.70")]
    [InlineData("ROHDE&SCHWARZ,FSW85,1331.5003K85,5.20")]
    public void Rohde_FSW_analyzers_get_the_transcribed_FSW_catalog(string idn)
    {
        Assert.Equal(InstrumentFamily.RohdeFswAnalyzer, InstrumentProfile.FamilyForIdentity(idn));

        CommandReference? r = CommandReference.ForIdentity(idn);
        Assert.NotNull(r);
        Assert.Contains("FSW", r!.Instrument);
        // Not the FSV's catalog, which is the confusion this guards against.
        Assert.DoesNotContain("FSVA", r.Instrument);
    }

    /// <summary>
    /// The FSL gets its own catalog rather than the FPC's. They are both R&amp;S analyzers and
    /// share almost nothing: the FSL takes SENSe and CALCulate with numeric suffixes the FPC
    /// has no notion of, and its command set is nearly three times the size.
    /// </summary>
    [Theory]
    [InlineData("Rohde&Schwarz,FSL3,1300.2502K03,2.30")]
    [InlineData("Rohde&Schwarz,FSL6,1300.2502K06,2.30")]
    [InlineData("Rohde&Schwarz,FSL18,1300.2502K18,2.30")]
    public void Rohde_FSL_analyzers_get_the_transcribed_FSL_catalog(string idn)
    {
        Assert.Equal(InstrumentFamily.RohdeFslAnalyzer, InstrumentProfile.FamilyForIdentity(idn));

        CommandReference? r = CommandReference.ForIdentity(idn);
        Assert.NotNull(r);
        Assert.Equal("Rohde & Schwarz", r!.Manufacturer);

        // Not the FPC catalog under another name: the FSL's suffixed CALCulate tree is the
        // clearest thing the two do not share.
        Assert.Contains(r.Commands, c => c.Syntax.StartsWith("CALCulate<1|2>:"));
    }

    [Theory]
    [InlineData("Rohde&Schwarz,FPC1500,1304.1004K02,3.20")]
    [InlineData("Rohde&Schwarz,FPC1000,1328.6660K02,1.50")]
    public void Rohde_FPC_analyzers_get_the_transcribed_FPC_catalog(string idn)
    {
        Assert.Equal(InstrumentFamily.RohdeSpectrumAnalyzer,
            InstrumentProfile.FamilyForIdentity(idn));

        CommandReference? r = CommandReference.ForIdentity(idn);
        Assert.NotNull(r);
        Assert.Equal("Rohde & Schwarz", r!.Manufacturer);
        // Not the Siglent catalog wearing a different name.
        Assert.DoesNotContain(r.Commands, c => c.Syntax.Contains(":TRACe:DATA?"));
    }

    [Fact]
    public void Siglent_analyzers_still_get_their_own_catalog()
        => Assert.Equal(InstrumentFamily.SpectrumAnalyzer,
            InstrumentProfile.FamilyForIdentity("Siglent Technologies,SSA3021X,SSA0000,1.2"));

    [Theory]
    // The 63800 AC loads and the older 631xx line are documented in manuals of their own,
    // neither transcribed, so they keep getting nothing rather than the 63200A set.
    [InlineData("CHROMA,63103A,C123,2.10")]
    [InlineData("Chroma ATE,63804,C125,1.00")]
    public void Chroma_loads_without_a_guide_get_no_catalog(string idn)
        => Assert.Equal(InstrumentFamily.Generic, InstrumentProfile.FamilyForIdentity(idn));

    /// <summary>
    /// The 63600 modular loads have their own catalog now. Only about half their command
    /// headers appear in the 63200A set, which is why they were never given that one.
    /// </summary>
    [Theory]
    [InlineData("Chroma ATE,63640-80-80,C124,1.20")]
    [InlineData("CHROMA,63630-80-60,C127,1.20")]
    public void Chroma_63600_loads_get_the_transcribed_modular_catalog(string idn)
    {
        Assert.Equal(InstrumentFamily.ChromaModularLoad, InstrumentProfile.FamilyForIdentity(idn));
        Assert.NotNull(CommandReference.ForIdentity(idn));
    }

    [Theory]
    [InlineData("Chroma ATE,63206A-150-200,123456,1.0")]
    [InlineData("CHROMA,63202A-150-200,C126,1.0")]
    public void Chroma_63200A_loads_get_the_transcribed_catalog(string idn)
    {
        Assert.Equal(InstrumentFamily.ChromaElectronicLoad,
            InstrumentProfile.FamilyForIdentity(idn));

        CommandReference? r = CommandReference.ForIdentity(idn);
        Assert.NotNull(r);
        Assert.Equal("Chroma", r!.Manufacturer);
        // MODE is the command that selects CC/CR/CV/CP, and its block in the manual is so
        // badly typeset that it has to be written out by hand. Pin that it survived.
        Assert.Contains(r.Commands, c => c.Syntax == "MODE <NRf>");
    }

    [Fact]
    public void Chroma_supplies_still_get_their_own_catalog()
        => Assert.Equal(InstrumentFamily.ChromaPowerSupply,
            InstrumentProfile.FamilyForIdentity("Chroma ATE,62012P-80-60,C1,1.0"));

    [Theory]
    // First generation, LeCroy-derived dialect: C1:VDIV, TDIV, TRMD. No X in the name.
    [InlineData("Siglent Technologies,SDS1052DL,SDS00001,1.0")]
    [InlineData("Siglent Technologies,SDS1102CML,SDS00002,1.0")]
    [InlineData("Siglent Technologies,SDS1202CNL,SDS00003,1.0")]
    public void First_generation_Siglent_scopes_do_not_get_the_modern_catalog(string idn)
        => Assert.Equal(InstrumentFamily.Generic, InstrumentProfile.FamilyForIdentity(idn));

    [Theory]
    // Everything taking the modern set carries an X.
    [InlineData("Siglent Technologies,SDS1202X-E,SDS00004,1.0")]
    [InlineData("Siglent Technologies,SDS2104X Plus,SDS00005,1.0")]
    [InlineData("Siglent Technologies,SDS814X HD,SDS00006,1.0")]
    public void Modern_Siglent_scopes_still_get_the_Siglent_catalog(string idn)
        => Assert.Equal(InstrumentFamily.SiglentScope, InstrumentProfile.FamilyForIdentity(idn));

    // ------------------------------------------------------------------------- ports

    [Fact]
    public void A_Fluke_bench_meter_is_reachable_by_a_default_scan()
    {
        // The 8845A/8846A answer on 3490 and nothing else. Without it in the default
        // list the meter is invisible and its 125-command catalog is unreachable.
        Assert.Contains(3490, NetworkScanner.CommonScpiPorts);
    }

    [Fact]
    public void The_default_scan_still_covers_the_conventional_ports()
    {
        Assert.Contains(5025, NetworkScanner.CommonScpiPorts);   // LXI/VISA
        Assert.Contains(5555, NetworkScanner.CommonScpiPorts);   // Rigol
        Assert.Contains(111, NetworkScanner.CommonScpiPorts);    // VXI-11 portmapper
    }
}
