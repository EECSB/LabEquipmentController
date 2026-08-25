using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

public class ScpiSyntaxTests
{
    [Theory]
    // Optional trailing node.
    [InlineData(":OUTPut1 ON", ":OUTPut[<n>][:STATe] {ON|1|OFF|0}")]
    [InlineData(":OUTPut1:STATe ON", ":OUTPut[<n>][:STATe] {ON|1|OFF|0}")]
    [InlineData(":OUTPut ON", ":OUTPut[<n>][:STATe] {ON|1|OFF|0}")]
    // Optional bracketed root, both spellings vendors use.
    [InlineData(":SOURce1:FUNCtion SIN", "[:SOURce[<n>]]:FUNCtion <function>")]
    [InlineData(":FUNCtion SIN", "[:SOURce[<n>]]:FUNCtion <function>")]
    [InlineData("VOLTage:DC:RANGe 10", "[SENSe:]VOLTage:{AC|DC}:RANGe {<range>|MIN|MAX|DEF}")]
    // Short form and long form of a mnemonic, and nothing between them.
    [InlineData(":MEAS:VOLT:DC? CH1", ":MEASure[:VOLTage][:DC]? [CH1|CH2|CH3]")]
    [InlineData(":MEASure:VOLTage:DC? CH1", ":MEASure[:VOLTage][:DC]? [CH1|CH2|CH3]")]
    [InlineData(":MEASure? CH1", ":MEASure[:VOLTage][:DC]? [CH1|CH2|CH3]")]
    // IEEE 488.2 common commands.
    [InlineData("*IDN?", "*IDN?")]
    [InlineData("*RST", "*RST")]
    // Keithley's "[1]" optional channel suffix, the same idea as "<n>".
    [InlineData(":OUTPut:STATe ON", ":OUTPut[1][:STATe] <state>")]
    [InlineData(":OUTPut1:STATe ON", ":OUTPut[1][:STATe] <state>")]
    [InlineData(":OUTPut ON", ":OUTPut[1][:STATe] <state>")]
    [InlineData(":SOURce:FUNCtion VOLTage", ":SOURce[1]:FUNCtion[:MODE] <function>")]
    // A bare "<placeholder>" node stands for a whole sub-path: Keithley documents
    // one entry for every measurement function.
    [InlineData(":MEASure:VOLTage:DC?", ":MEASure:<function>?")]
    [InlineData(":MEASure:RESistance?", ":MEASure:<function>?")]
    [InlineData(":SENSe:CURRent:AC:RANGe 1", "[:SENSe[1]]:<function>:RANGe <n>")]
    // Underscores belong to the mnemonic.
    [InlineData("WFMOutpre:NR_Pt?", "WFMOutpre:NR_Pt?")]
    public void Matches_documented_templates(string command, string template)
    {
        Assert.True(ScpiSyntax.Matches(command, template));
    }

    [Theory]
    // A mnemonic abbreviated to something that is neither the short nor the long form.
    [InlineData(":MEASU:VOLT?", ":MEASure[:VOLTage][:DC]? [CH1|CH2|CH3]")]
    // A node the template does not document at all.
    [InlineData(":OUTPut1:BOGUS ON", ":OUTPut[<n>][:STATe] {ON|1|OFF|0}")]
    // A required node left out.
    [InlineData(":SOURce1 SIN", "[:SOURce[<n>]]:FUNCtion <function>")]
    // Query and set forms must not satisfy each other.
    [InlineData(":OUTPut1?", ":OUTPut[<n>][:STATe] {ON|1|OFF|0}")]
    [InlineData(":OUTPut1 ON", ":OUTPut[<n>][:STATe]?")]
    [InlineData("*IDN?", "*RST")]
    // A wildcard stands for at least one node, so the bare parent is not an instance.
    [InlineData(":MEASure?", ":MEASure:<function>?")]
    // ...and it must still leave the required nodes after it satisfied.
    [InlineData(":SENSe:CURRent 1", "[:SENSe[1]]:<function>:RANGe <n>")]
    public void Rejects_commands_the_template_does_not_cover(string command, string template)
    {
        Assert.False(ScpiSyntax.Matches(command, template));
    }

    [Fact]
    public void HeaderOf_strips_parameters_and_the_query_mark()
    {
        Assert.Equal(":MEASure:VOLTage:DC", ScpiSyntax.HeaderOf(":MEASure:VOLTage:DC? CH1"));
        Assert.Equal(":OUTPut", ScpiSyntax.HeaderOf(":OUTPut CH1,ON"));
        Assert.Equal("*IDN", ScpiSyntax.HeaderOf("*IDN?"));
    }

    [Fact]
    public void MatchesAny_finds_the_covering_template()
    {
        string[] catalog = { ":RUN", ":STOP", ":OUTPut[<n>][:STATe] {ON|1|OFF|0}" };
        Assert.True(ScpiSyntax.MatchesAny(":OUTPut2 OFF", catalog));
        Assert.False(ScpiSyntax.MatchesAny(":AUToscale", catalog));
    }
}
