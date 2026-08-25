using System.Linq;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

public class CommandReferenceTests
{
    [Theory]
    [InlineData("RIGOL TECHNOLOGIES,DS2202,DS2A000000,00.03", InstrumentFamily.Oscilloscope)]
    [InlineData("Siglent Technologies,SDG2042X,SDG000000,2.01", InstrumentFamily.SiglentGenerator)]
    [InlineData("Keysight Technologies,33500B,MY00000,1.0", InstrumentFamily.ScpiGenerator)]
    [InlineData("Rigol Technologies,DG1022Z,DG000,1.0", InstrumentFamily.ScpiGenerator)]
    [InlineData("Siglent Technologies,SDM3065X,SDM36HCD801207,3.02.01.13", InstrumentFamily.Multimeter)]
    [InlineData("Keysight Technologies,34461A,MY000,1.0", InstrumentFamily.KeysightMultimeter)]
    // Rigol's meters, loads and analyzers each get their own catalog. The three "generic"
    // families they used to take are Siglent catalogs — SDM, SDL1000X and SSA3000X — and a
    // Rigol takes none of those command sets, however standard they all look.
    [InlineData("Rigol Technologies,DM3058E,DM000,1.0", InstrumentFamily.RigolMultimeter)]
    [InlineData("RIGOL TECHNOLOGIES,DM3068,DM000,1.0", InstrumentFamily.RigolMultimeter)]
    [InlineData("RIGOL TECHNOLOGIES,DL3021,DL3000,1.0", InstrumentFamily.RigolElectronicLoad)]
    [InlineData("RIGOL TECHNOLOGIES,DSA815,DSA000,1.0", InstrumentFamily.RigolSpectrumAnalyzer)]
    [InlineData("RIGOL TECHNOLOGIES,DSA875,DSA000,1.0", InstrumentFamily.RigolSpectrumAnalyzer)]
    // ...and the Siglent instruments keep those catalogs, which are theirs.
    [InlineData("Siglent Technologies,SDL1020X,SDL000,1.0", InstrumentFamily.ElectronicLoad)]
    [InlineData("Siglent Technologies,SSA3021X,SSA000,1.0", InstrumentFamily.SpectrumAnalyzer)]
    // Power supplies, loads and analyzers.
    [InlineData("RIGOL TECHNOLOGIES,DP832,DP8A000,1.0", InstrumentFamily.PowerSupply)]
    [InlineData("Siglent Technologies,SPD3303X-E,SPD000,1.0", InstrumentFamily.PowerSupply)]
    [InlineData("Keysight Technologies,E36313A,MY000,1.0", InstrumentFamily.KeysightPowerSupply)]
    // Keysight and Keithley take their own dialects.
    [InlineData("KEYSIGHT TECHNOLOGIES,MSO-X 3054T,MY000,1.0", InstrumentFamily.KeysightScope)]
    [InlineData("AGILENT TECHNOLOGIES,DSO-X 2002A,MY000,1.0", InstrumentFamily.KeysightScope)]
    [InlineData("KEITHLEY INSTRUMENTS,MODEL 2450,04000,1.0", InstrumentFamily.KeithleySmu)]
    [InlineData("KEITHLEY INSTRUMENTS,MODEL 2400,04000,1.0", InstrumentFamily.KeithleySmu)]
    [InlineData("KEITHLEY INSTRUMENTS,MODEL 2635B,04000,1.0", InstrumentFamily.KeithleySmu)]
    [InlineData("KEITHLEY INSTRUMENTS,DMM6500,04000,1.0", InstrumentFamily.KeithleyDmm)]
    [InlineData("KEITHLEY INSTRUMENTS,MODEL 2000,04000,1.0", InstrumentFamily.KeithleyDmm)]
    // Keithley's "MODEL " prefix and Keysight's hyphenated models must not defeat
    // the prefix tests — a real 2450 answers "KEITHLEY INSTRUMENTS,MODEL 2450,...".
    [InlineData("KEITHLEY INSTRUMENTS,MODEL 2200-30-5,04000,1.0", InstrumentFamily.PowerSupply)]
    // Siglent's scopes take their own dialect, distinct from both the Rigol scope
    // catalog they used to share and the SDG generator's "C1:BSWV".
    [InlineData("Siglent Technologies,SDS1104X-E,SDS000,1.0", InstrumentFamily.SiglentScope)]
    [InlineData("Siglent Technologies,SDS2354X Plus,SDS000,1.0", InstrumentFamily.SiglentScope)]
    [InlineData("Siglent Technologies,SDS814X HD,SDS000,1.0", InstrumentFamily.SiglentScope)]
    // ...and only its scopes: the SDG, SDM and SDL keep their own families — the SDL case
    // is pinned above, alongside the Rigol load that no longer shares it.
    // Fluke bench meters and GW Instek scopes.
    [InlineData("FLUKE,8846A,1234567,1.0", InstrumentFamily.FlukeMultimeter)]
    [InlineData("FLUKE,8845A,1234567,1.0", InstrumentFamily.FlukeMultimeter)]
    [InlineData("GW INSTEK,GDS-2204E,GEQ000,1.0", InstrumentFamily.GwInstekScope)]
    [InlineData("GWINSTEK,GDS-1104B,GEQ000,1.0", InstrumentFamily.GwInstekScopeB)]
    // Both are matched on the maker: "884x" and "GDS" are meaningless alone, and a
    // Rigol DS-anything must not be pulled into the GW Instek family.
    [InlineData("RIGOL TECHNOLOGIES,DS1104Z,DS1Z000,1.0", InstrumentFamily.Oscilloscope)]
    // Chroma supplies and B&K loads.
    [InlineData("Chroma ATE,62012P-80-60,000000,1.0", InstrumentFamily.ChromaPowerSupply)]
    [InlineData("CHROMA,62006P-100-25,000000,1.0", InstrumentFamily.ChromaPowerSupply)]
    [InlineData("B&K PRECISION,8600,123456,1.0", InstrumentFamily.BkElectronicLoad)]
    [InlineData("BK PRECISION,8602,123456,1.0", InstrumentFamily.BkElectronicLoad)]
    // Chroma's own loads are 63xxx. They used to keep the generic load catalog, which is
    // the Siglent SDL1000X set and not a command set a Chroma takes. The 63200A guide is
    // transcribed, so those get their own; the 636xx and 638xx lines are documented
    // elsewhere and still fall through rather than take a set that nearly fits.
    [InlineData("Chroma ATE,63206A,000000,1.0", InstrumentFamily.ChromaElectronicLoad)]
    [InlineData("Chroma ATE,63640-80-80,000000,1.0", InstrumentFamily.ChromaModularLoad)]
    // "62" and "86" are meaningless without the maker — pinned by the Keithley
    // 2200-30-5 case further down, which stays a plain power supply.
    // A Siglent SDM keeps the generic multimeter catalog, not Keithley's.
    [InlineData("Siglent Technologies,SDM3055,SDM000,1.0", InstrumentFamily.Multimeter)]
    [InlineData("Rohde&Schwarz,HMP4040,0000,1.0", InstrumentFamily.RohdePowerSupply)]
    [InlineData("Rohde&Schwarz,NGL202,3638.3376k03,1.0", InstrumentFamily.RohdePowerSupply)]
    [InlineData("Rohde&Schwarz,RTB2004,1333.1005k04,1.0", InstrumentFamily.RohdeScope)]
    [InlineData("ROHDE&SCHWARZ,RTM3004,0000,1.0", InstrumentFamily.RohdeScope)]
    // HAMEG is the older brand R&S still ships the HMO/HMP lines under.
    [InlineData("HAMEG,HMO1024,0000,1.0", InstrumentFamily.RohdeScope)]
    // An R&S analyzer used to be given the Siglent SSA3000X catalog. The frequency and
    // bandwidth subsystems overlap enough that it looked as though it worked, which is
    // what made it worth fixing. The FPC and FSL manuals are each transcribed into their own
    // catalog — they are different command sets, not two spellings of one — and the FSV and
    // FSW lines each have their own now as well, from their own manuals. Four R&S analyzer
    // sets, none interchangeable.
    [InlineData("Rohde&Schwarz,FPC1500,0000,1.0", InstrumentFamily.RohdeSpectrumAnalyzer)]
    [InlineData("Rohde&Schwarz,FSL6,0000,1.0", InstrumentFamily.RohdeFslAnalyzer)]
    [InlineData("Rohde&Schwarz,FSW26,0000,1.0", InstrumentFamily.RohdeFswAnalyzer)]
    // Tektronix scopes take their own dialect, so they get their own family...
    [InlineData("TEKTRONIX,MDO4104C,C000,1.0", InstrumentFamily.TektronixScope)]
    [InlineData("TEKTRONIX,DPO4104,C000,1.0", InstrumentFamily.TektronixScope)]
    [InlineData("TEKTRONIX,MSO54,C000,1.0", InstrumentFamily.TektronixScope)]
    [InlineData("TEKTRONIX,TDS2024C,C000,1.0", InstrumentFamily.TektronixScope)]
    // ...but only its scopes: an AFG still takes standard SCPI.
    [InlineData("TEKTRONIX,AFG3252,C000,1.0", InstrumentFamily.ScpiGenerator)]
    //
    // Prefix collisions that the classification order exists to resolve.
    // "MSO" belongs to both Rigol and Tektronix; only the maker separates them.
    [InlineData("RIGOL TECHNOLOGIES,MSO5074,MS5A000,1.0", InstrumentFamily.Oscilloscope)]
    // "DSA" is a Tektronix scope but a Rigol spectrum analyzer — the Rigol case is
    // covered above, so only the Tektronix side needs stating here.
    [InlineData("TEKTRONIX,DSA70804,C000,1.0", InstrumentFamily.TektronixScope)]
    // A DS2202 must not be read as a spectrum analyzer by the "DSA" test above.
    [InlineData("RIGOL TECHNOLOGIES,DS2202,DS2A000,1.0", InstrumentFamily.Oscilloscope)]
    [InlineData("", InstrumentFamily.Generic)]
    [InlineData(null, InstrumentFamily.Generic)]
    public void FamilyForIdentity_classifies_by_maker_and_model(string? idn, InstrumentFamily expected)
    {
        Assert.Equal(expected, InstrumentProfile.FamilyForIdentity(idn));
    }

    [Fact]
    public void Oscilloscope_catalog_loads_and_is_populated()
    {
        CommandReference? r = CommandReference.ForFamily(InstrumentFamily.Oscilloscope);
        Assert.NotNull(r);
        Assert.NotEmpty(r!.Commands);
        Assert.False(string.IsNullOrWhiteSpace(r.Instrument));
        Assert.False(string.IsNullOrWhiteSpace(r.Source));
    }

    [Fact]
    public void Siglent_catalog_uses_siglent_syntax()
    {
        CommandReference? r = CommandReference.ForFamily(InstrumentFamily.SiglentGenerator);
        Assert.NotNull(r);
        // Siglent's dialect, not standard SCPI — the catalog must reflect that.
        Assert.Contains(r!.Commands, c => c.Syntax.Contains("BSWV"));
    }

    [Fact]
    public void Multimeter_catalog_uses_standard_scpi_measure_queries()
    {
        CommandReference? r = CommandReference.ForFamily(InstrumentFamily.Multimeter);
        Assert.NotNull(r);
        Assert.Contains(r!.Commands, c => c.Syntax.Contains("MEASure:RESistance?"));
        // The SDM is standard SCPI — it must NOT pick up the SDG generator's dialect.
        Assert.DoesNotContain(r.Commands, c => c.Syntax.Contains("BSWV"));
    }

    [Fact]
    public void Generic_family_has_no_curated_catalog()
    {
        Assert.Null(CommandReference.ForFamily(InstrumentFamily.Generic));
    }

    [Fact]
    public void ForIdentity_resolves_the_matching_catalog()
    {
        CommandReference? r = CommandReference.ForIdentity("RIGOL TECHNOLOGIES,DS2202,X,1.0");
        Assert.NotNull(r);
        Assert.Contains(r!.Commands, c => c.Syntax == ":RUN");
    }

    [Fact]
    public void Every_entry_has_category_syntax_and_description()
    {
        foreach (InstrumentFamily fam in new[] { InstrumentFamily.Oscilloscope,
                                                 InstrumentFamily.SiglentGenerator,
                                                 InstrumentFamily.Multimeter })
        {
            CommandReference r = CommandReference.ForFamily(fam)!;
            Assert.All(r.Commands, c =>
            {
                Assert.False(string.IsNullOrWhiteSpace(c.Category));
                Assert.False(string.IsNullOrWhiteSpace(c.Syntax));
                Assert.False(string.IsNullOrWhiteSpace(c.Description));
            });
        }
    }
}
