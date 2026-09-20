using System;
using System.Collections.Generic;
using System.Linq;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// What the script writer is told about the bench (SPEC §11c): an alias per instrument, and a
/// DEVICE line that binds as written under the rule in §9a. Both builds take it from here.
/// </summary>
public class ScriptContextTests
{
    private sealed record Box(string Host, string Idn);

    private static readonly Box Gen = new("192.168.1.5", "Siglent Technologies,SDG2042X,SDG2XCAD1R0001,2.01.01.35R3B2");
    private static readonly Box MeterA = new("192.168.1.7", "Siglent Technologies,SDM3065X,SDM36HCD801207,3.02.01.13");
    private static readonly Box MeterB = new("192.168.1.8", "Siglent Technologies,SDM3065X,SDM36HCD801208,3.02.01.13");
    private static readonly Box Scope = new("192.168.1.20", "RIGOL TECHNOLOGIES,DS2202A,DS2A000000,00.03");

    private static IReadOnlyList<ScriptContextInstrument> Describe(
        IReadOnlyList<Box> bench, string? script = null, IReadOnlyCollection<Box>? only = null,
        IReadOnlyDictionary<string, Box>? picked = null)
        => ScriptContext.ForSequence(bench, b => b.Idn, b => b.Host, script, only, picked);

    /// <summary>Picks as the web's table makes them: by alias, whatever its case.</summary>
    private static Dictionary<string, Box> Picks(params (string Alias, Box Box)[] picks)
        => picks.ToDictionary(p => p.Alias, p => p.Box, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The property all of this is for: declare every instrument the way the writer is told to,
    /// and every line binds — to the instrument it was written for.
    /// </summary>
    private static void BindsAsWritten(IReadOnlyList<Box> bench, IReadOnlyList<ScriptContextInstrument> described)
    {
        string script = string.Join("\n", described.Select(i => $"DEVICE {i.Alias} : {i.Model}"));
        var bound = SequenceBinding.Bind(SequenceRunner.Requirements(script), bench, b => b.Idn, b => b.Host);

        for (int i = 0; i < described.Count; i++)
        {
            Assert.Equal(DeviceBindingState.Bound, bound[i].State);
            Assert.Equal(described[i].Identity, bound[i].Instrument!.Idn);
        }
    }

    [Fact]
    public void Each_instrument_is_named_after_its_kind_and_declared_by_its_model()
    {
        var described = Describe([Gen, MeterA, Scope]);

        Assert.Equal(["gen", "dmm", "scope"], described.Select(i => i.Alias));
        Assert.Equal(["SDG2042X", "SDM3065X", "DS2202A"], described.Select(i => i.Model));
        Assert.Equal(Gen.Idn, described[0].Identity);   // the identity, not the address
        Assert.NotNull(described[0].Reference);
        BindsAsWritten([Gen, MeterA, Scope], described);
    }

    /// <summary>
    /// Two of one model: two names, and each declared by its serial number. The model is one
    /// question with two answers, and a DEVICE line asking it binds neither meter.
    /// </summary>
    [Fact]
    public void Two_of_one_model_are_two_aliases_declared_by_serial_number()
    {
        var described = Describe([Gen, MeterA, MeterB]);

        Assert.Equal(["gen", "dmm", "dmm2"], described.Select(i => i.Alias));
        Assert.Equal(["SDG2042X", "SDM36HCD801207", "SDM36HCD801208"], described.Select(i => i.Model));
        BindsAsWritten([Gen, MeterA, MeterB], described);
    }

    /// <summary>
    /// A meter left out of the prompt is still on the bench. Ticking one of two identical meters
    /// declares it by serial number all the same, or the line written for it would bind neither.
    /// </summary>
    [Fact]
    public void An_instrument_left_out_still_counts_as_a_second_of_its_model()
    {
        var only = Assert.Single(Describe([MeterA, MeterB], only: [MeterB]));
        Assert.Equal("SDM36HCD801208", only.Model);
    }

    /// <summary>With no serial number to tell them apart, the address: it binds, if DHCP lets it.</summary>
    [Fact]
    public void Two_of_a_kind_with_no_serial_number_are_declared_by_address()
    {
        Box a = new("192.168.1.7", "Siglent Technologies,SDM3065X,,3.02");
        Box b = new("192.168.1.8", "Siglent Technologies,SDM3065X,,3.02");

        var described = Describe([a, b]);

        Assert.Equal(["192.168.1.7", "192.168.1.8"], described.Select(i => i.Model));
        BindsAsWritten([a, b], described);
    }

    /// <summary>
    /// What the script being revised already calls an instrument is kept, and so is a DEVICE line
    /// of it that binds: asking for a change to a working sequence does not rename its devices or
    /// declare them differently. The old lookup matched a declaration only as the prefix of a
    /// model, so a serial number in the editor matched nothing — the meter it named came back
    /// "right2", and "left" was lost.
    /// </summary>
    [Fact]
    public void A_script_being_revised_keeps_its_names_and_its_working_declarations()
    {
        var described = Describe([Gen, MeterA, MeterB],
            "DEVICE source : 192.168.1.5\nDEVICE left : SDM36HCD801208\nDEVICE right : SDM3065X");

        var byIdentity = described.ToDictionary(i => i.Identity);
        Assert.Equal(("source", "192.168.1.5"), (byIdentity[Gen.Idn].Alias, byIdentity[Gen.Idn].Model));
        Assert.Equal(("left", "SDM36HCD801208"), (byIdentity[MeterB.Idn].Alias, byIdentity[MeterB.Idn].Model));
        Assert.Equal(("right", "SDM3065X"), (byIdentity[MeterA.Idn].Alias, byIdentity[MeterA.Idn].Model));
        BindsAsWritten([Gen, MeterA, MeterB], described);
    }

    /// <summary>
    /// A script asking for two meters by model binds neither, but its names are still the ones
    /// someone chose. They are kept, and each meter is declared so that it binds — which meter got
    /// which name is then in the draft as a serial number, for the user to check.
    /// </summary>
    [Fact]
    public void Names_from_lines_that_do_not_bind_are_kept_and_declared_so_they_do()
    {
        var described = Describe([MeterA, MeterB], "DEVICE left : SDM3065X\nDEVICE right : SDM3065X");

        Assert.Equal(["left", "right"], described.Select(i => i.Alias));
        Assert.Equal(["SDM36HCD801207", "SDM36HCD801208"], described.Select(i => i.Model));
        BindsAsWritten([MeterA, MeterB], described);
    }

    /// <summary>
    /// Two meters asked for by model, and the table told which is which. The writer is told what
    /// the table shows: each name on the meter picked for it, where the rule on its own would have
    /// handed them out the other way round. Declared by serial number, because the model binds
    /// neither, so the draft binds as it is written with nothing picked at all.
    /// </summary>
    [Fact]
    public void A_part_picked_by_hand_is_described_as_the_instrument_picked()
    {
        var described = Describe([MeterA, MeterB], "DEVICE left : SDM3065X\nDEVICE right : SDM3065X",
                                 picked: Picks(("LEFT", MeterB), ("right", MeterA)));

        var byIdentity = described.ToDictionary(i => i.Identity);
        Assert.Equal(("left", "SDM36HCD801208"), (byIdentity[MeterB.Idn].Alias, byIdentity[MeterB.Idn].Model));
        Assert.Equal(("right", "SDM36HCD801207"), (byIdentity[MeterA.Idn].Alias, byIdentity[MeterA.Idn].Model));
        BindsAsWritten([MeterA, MeterB], described);
    }

    /// <summary>A pick of what the line binds anyway changes nothing, and the line is kept as written.</summary>
    [Fact]
    public void A_picked_line_that_binds_by_itself_keeps_its_text()
    {
        var described = Describe([Gen, MeterA], "DEVICE meter : SDM3065X", picked: Picks(("meter", MeterA)));

        var meter = described.Single(i => i.Identity == MeterA.Idn);
        Assert.Equal(("meter", "SDM3065X"), (meter.Alias, meter.Model));
    }

    /// <summary>A name the script already uses is not handed out again to another instrument.</summary>
    [Fact]
    public void A_name_the_script_uses_is_not_given_to_a_second_instrument()
        => Assert.Equal(["dmm", "dmm2"], Describe([Gen, MeterA], "DEVICE dmm : SDG2042X").Select(i => i.Alias));

    [Fact]
    public void A_single_instrument_script_has_no_alias_and_is_named_by_model()
    {
        ScriptContextInstrument one = ScriptContext.ForScript(MeterA.Idn, "192.168.1.7");

        Assert.Equal("", one.Alias);            // its lines carry no prefix
        Assert.Equal("SDM3065X", one.Model);
        Assert.Equal(MeterA.Idn, one.Identity);
        Assert.NotNull(one.Reference);

        // ...and one that does not say what it is goes by the name it was given.
        Assert.Equal("192.168.1.9", ScriptContext.ForScript("", "192.168.1.9").Model);
    }
}
