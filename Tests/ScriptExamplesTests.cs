using System.Linq;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

public class ScriptExamplesTests
{
    [Theory]
    [InlineData(InstrumentFamily.Oscilloscope)]
    [InlineData(InstrumentFamily.SiglentGenerator)]
    [InlineData(InstrumentFamily.ScpiGenerator)]
    [InlineData(InstrumentFamily.Multimeter)]
    [InlineData(InstrumentFamily.Generic)]
    public void Every_family_gets_usable_examples(InstrumentFamily family)
    {
        var examples = ScriptExamples.ForFamily(family);

        Assert.NotEmpty(examples);
        Assert.All(examples, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Name));
            Assert.False(string.IsNullOrWhiteSpace(e.Script));
        });
        // The dropdown shows names; duplicates would be indistinguishable.
        Assert.Equal(examples.Count, examples.Select(e => e.Name).Distinct().Count());
    }

    [Fact]
    public void A_multimeter_is_not_offered_the_generator_dialect()
    {
        // The whole point of splitting these: an SDM3065X and an SDG2042X share a maker,
        // and "C1:BSWV" on the multimeter's editor would be nothing but errors.
        string all = string.Concat(ScriptExamples.ForFamily(InstrumentFamily.Multimeter)
                                                 .Select(e => e.Script));

        Assert.Contains("MEASure:VOLTage:DC?", all);
        Assert.DoesNotContain("BSWV", all);
        Assert.DoesNotContain(":CHANnel1", all);
    }

    [Fact]
    public void A_siglent_generator_gets_its_own_dialect_not_standard_scpi()
    {
        string all = string.Concat(ScriptExamples.ForFamily(InstrumentFamily.SiglentGenerator)
                                                 .Select(e => e.Script));

        Assert.Contains("C1:BSWV", all);
        Assert.Contains("C1:OUTP", all);
        Assert.DoesNotContain(":SOURce1", all);
    }

    [Fact]
    public void A_scope_gets_scope_commands()
    {
        string all = string.Concat(ScriptExamples.ForFamily(InstrumentFamily.Oscilloscope)
                                                 .Select(e => e.Script));

        Assert.Contains(":MEASure:VPP? CHANnel1", all);
        Assert.Contains(":TIMebase:MAIN:SCALe?", all);
        Assert.DoesNotContain("BSWV", all);
    }

    [Fact]
    public void Only_the_error_query_the_vendor_guide_documents_is_used()
    {
        // The Rigol guide spells it :SYSTem:ERRor:NEXT?. The Siglent SDG and SDM guides
        // document no error query at all, so their examples must not invent one.
        string scope = string.Concat(ScriptExamples.ForFamily(InstrumentFamily.Oscilloscope)
                                                   .Select(e => e.Script));
        Assert.Contains(":SYSTem:ERRor:NEXT?", scope);

        foreach (InstrumentFamily family in new[] { InstrumentFamily.SiglentGenerator, InstrumentFamily.Multimeter })
        {
            string all = string.Concat(ScriptExamples.ForFamily(family).Select(e => e.Script));
            Assert.DoesNotContain("ERRor", all);
        }
    }

    [Fact]
    public void Anything_that_enables_a_generator_output_says_so_first()
    {
        // Turning on an output can damage whatever is wired up. Every example that does it
        // must carry a NOTE line, so the warning is visible before the script is run.
        foreach (InstrumentFamily family in new[] { InstrumentFamily.SiglentGenerator, InstrumentFamily.ScpiGenerator })
        {
            foreach (ScriptExample example in ScriptExamples.ForFamily(family))
            {
                bool enablesOutput = example.Script.Contains("OUTP ON")
                                  || example.Script.Contains(":OUTPut1 ON")
                                  || example.Script.Contains(":OUTPut2 ON");
                if (enablesOutput)
                    Assert.Contains("NOTE:", example.Script);
            }
        }
    }

    [Fact]
    public void Examples_follow_the_identity_the_way_profiles_do()
    {
        Assert.Same(ScriptExamples.ForFamily(InstrumentFamily.Multimeter),
                    ScriptExamples.ForIdentity("Siglent Technologies,SDM3065X,SDM1234,3.02"));
        Assert.Same(ScriptExamples.ForFamily(InstrumentFamily.SiglentGenerator),
                    ScriptExamples.ForIdentity("Siglent Technologies,SDG2042X,SDG1234,2.01"));
        Assert.Same(ScriptExamples.ForFamily(InstrumentFamily.Oscilloscope),
                    ScriptExamples.ForIdentity("RIGOL TECHNOLOGIES,DS2202,DS2A15,00.01"));
        Assert.Same(ScriptExamples.ForFamily(InstrumentFamily.Generic),
                    ScriptExamples.ForIdentity(null));
    }
}
