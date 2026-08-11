using System;
using System.Collections.Generic;

namespace LabEquipmentController.Tests;

public class MeasurementUnitTests
{
    [Theory]
    [InlineData("MEASure:VOLTage:DC?", "V")]
    [InlineData("MEAS:VOLT:AC?", "V")]
    [InlineData(":measure:voltage:dc?", "V")]      // vendors vary the case as well as the length
    [InlineData("MEASure:CURRent:DC?", "A")]
    [InlineData("MEASure:RESistance?", "Ω")]
    [InlineData("MEASure:FRESistance?", "Ω")]
    [InlineData("MEASure:FREQuency?", "Hz")]
    [InlineData("MEASure:CAPacitance?", "F")]
    [InlineData("MEASure:TEMPerature?", "°C")]
    [InlineData("MEASure:PERiod?", "s")]
    [InlineData("MEASure:DIODe?", "V")]
    [InlineData(":MEASure:VPP? CHANnel1", "V")]    // a scope's measurement, not a meter's
    [InlineData(":MEASure:RISetime? CHAN1", "s")]
    public void A_measurement_command_offers_its_unit(string command, string expected)
        => Assert.Equal(expected, MeasurementUnit.ForCommand(command));

    [Theory]
    [InlineData("*IDN?")]
    [InlineData(":SYSTem:ERRor?")]
    [InlineData("")]
    [InlineData(null)]
    public void A_command_that_measures_nothing_offers_nothing(string? command)
        => Assert.Null(MeasurementUnit.ForCommand(command));

    /// <summary>
    /// A voltage query that also mentions its aperture is still a voltage. The mnemonics are
    /// checked most-specific first for exactly this reason.
    /// </summary>
    [Fact]
    public void A_qualifier_does_not_displace_the_measurement()
        => Assert.Equal("V", MeasurementUnit.ForCommand("MEASure:VOLTage:DC:APERture?"));

    [Theory]
    [InlineData("Vout (Vrms)", "Vrms")]
    [InlineData("Frequency (Hz)", "Hz")]
    [InlineData("Gain (dB)", "dB")]
    public void A_column_heading_can_carry_its_own_unit(string heading, string expected)
        => Assert.Equal(expected, MeasurementUnit.ForColumn(heading));

    [Theory]
    [InlineData("Value")]
    [InlineData("Vout ()")]
    [InlineData("Reading (a rather long parenthetical)")]   // prose, not a unit
    [InlineData(null)]
    public void A_heading_without_a_unit_offers_nothing(string? heading)
        => Assert.Null(MeasurementUnit.ForColumn(heading));

    /// <summary>What the author wrote in the heading beats anything inferred from a command.</summary>
    [Fact]
    public void The_heading_wins_over_the_commands()
        => Assert.Equal("dB", MeasurementUnit.Guess("Gain (dB)", new[] { "MEASure:VOLTage:DC?" }));

    [Fact]
    public void Without_a_heading_unit_the_commands_are_used()
        => Assert.Equal("Ω", MeasurementUnit.Guess("Value", new[] { "MEASure:RESistance?" }));

    /// <summary>
    /// A console records *IDN? alongside its readings; the first row that means something is
    /// what the axis is labelled from, rather than giving up at the first unrecognised one.
    /// </summary>
    [Fact]
    public void An_unrecognised_command_does_not_stop_the_search()
        => Assert.Equal("V", MeasurementUnit.Guess(
            "Value", new[] { "*IDN?", ":SYSTem:ERRor?", "MEASure:VOLTage:DC?" }));

    [Fact]
    public void Nothing_to_go_on_means_no_unit()
    {
        Assert.Null(MeasurementUnit.Guess("Value", new[] { "*IDN?" }));
        Assert.Null(MeasurementUnit.Guess("Value", null));
        Assert.Null(MeasurementUnit.Guess(null, Array.Empty<string>()));
    }
}
