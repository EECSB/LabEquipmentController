using System;
using System.Collections.Generic;
using System.Linq;
using LabEquipmentController;
using Xunit;
using Xunit.Abstractions;

namespace LabEquipmentController.Tests.Bench;

/// <summary>
/// Which families can be verified against hardware and which cannot, stated once so that
/// "not bench-verified" is a recorded fact rather than an open question.
///
/// Three instruments are on this bench. The other eighteen catalogs are not outstanding work
/// waiting to be done — there is no such instrument here to do it with, and saying so
/// explicitly is what stops the gap being re-investigated every few months.
///
/// This runs offline. It asserts nothing about the instruments; it asserts that the list
/// below still describes the app, so that adding a family without deciding which side it
/// falls on fails here.
/// </summary>
[Collection(BenchCollection.Name)]
public class BenchInventoryTests
{
    private readonly ITestOutputHelper _out;
    public BenchInventoryTests(ITestOutputHelper output) => _out = output;

    /// <summary>The instruments physically available, and what verifies them.</summary>
    public static readonly IReadOnlyDictionary<InstrumentFamily, string> Available =
        new Dictionary<InstrumentFamily, string>
        {
            [InstrumentFamily.Oscilloscope]     = "Rigol DS2202",
            [InstrumentFamily.SiglentGenerator] = "Siglent SDG2042X",
            [InstrumentFamily.Multimeter]       = "Siglent SDM3065X",
        };

    /// <summary>
    /// Catalogued families with no instrument here. Each is transcribed from a vendor guide
    /// and cross-checked where a driver existed, and that is as far as it can go.
    /// </summary>
    public static readonly IReadOnlyList<InstrumentFamily> NoHardware = new[]
    {
        InstrumentFamily.ScpiGenerator,
        InstrumentFamily.PowerSupply,
        InstrumentFamily.ElectronicLoad,
        InstrumentFamily.SpectrumAnalyzer,
        InstrumentFamily.TektronixScope,
        InstrumentFamily.KeysightScope,
        InstrumentFamily.KeysightPowerSupply,
        InstrumentFamily.KeithleySmu,
        InstrumentFamily.KeithleyDmm,
        InstrumentFamily.RohdeScope,
        InstrumentFamily.RohdePowerSupply,
        InstrumentFamily.SiglentScope,
        InstrumentFamily.FlukeMultimeter,
        InstrumentFamily.GwInstekScope,
        InstrumentFamily.ChromaPowerSupply,
        InstrumentFamily.BkElectronicLoad,
        InstrumentFamily.RohdeSpectrumAnalyzer,
        InstrumentFamily.ChromaElectronicLoad,
        InstrumentFamily.RohdeFslAnalyzer,
        InstrumentFamily.RigolMultimeter,
        InstrumentFamily.RigolElectronicLoad,
        InstrumentFamily.RigolSpectrumAnalyzer,
        InstrumentFamily.RohdeFsvAnalyzer,
        InstrumentFamily.KeysightMultimeter,
        InstrumentFamily.GwInstekScopeB,
        InstrumentFamily.ChromaModularLoad,
        InstrumentFamily.BkPowerSupply,
        InstrumentFamily.BkPowerSupply9130,
        InstrumentFamily.RohdeFswAnalyzer,
        InstrumentFamily.RohdeFsuAnalyzer,
        InstrumentFamily.RohdeFspAnalyzer,
        InstrumentFamily.RohdeFsqAnalyzer,
    };

    /// <summary>
    /// Every catalogued family is on exactly one of the two lists. A new catalog that lands
    /// without a decision about how it gets verified fails here rather than quietly joining
    /// the eighteen.
    /// </summary>
    [Fact]
    public void Every_catalogued_family_is_accounted_for()
    {
        var catalogued = Enum.GetValues<InstrumentFamily>()
            .Where(f => CommandReference.ForFamily(f)?.Commands.Count > 0)
            .ToList();

        var listed = Available.Keys.Concat(NoHardware).ToHashSet();

        var unaccounted = catalogued.Where(f => !listed.Contains(f)).ToList();
        Assert.True(unaccounted.Count == 0,
            $"catalogued but not on either bench list: {string.Join(", ", unaccounted)}");

        var phantom = listed.Where(f => !catalogued.Contains(f)).ToList();
        Assert.True(phantom.Count == 0,
            $"on a bench list but has no catalog: {string.Join(", ", phantom)}");

        Assert.Empty(Available.Keys.Intersect(NoHardware));

        _out.WriteLine($"{catalogued.Count} catalogued families: " +
                       $"{Available.Count} testable here, {NoHardware.Count} without hardware");
        foreach (var (family, instrument) in Available)
            _out.WriteLine($"  testable   {family,-24} {instrument}");
    }

    /// <summary>
    /// The three available families are the three carrying bench ticks. If a tick appears
    /// on a catalog no instrument here can reach, it did not come from this bench.
    /// </summary>
    [Fact]
    public void Only_families_with_hardware_carry_bench_ticks()
    {
        var ticked = Enum.GetValues<InstrumentFamily>()
            .Where(f => CommandReference.ForFamily(f)?.Commands.Any(c => c.BenchVerified) == true)
            .ToList();

        foreach (InstrumentFamily f in ticked)
            _out.WriteLine($"  {f,-24} {CommandReference.ForFamily(f)!.Commands.Count(c => c.BenchVerified)} ticked");

        var unexplained = ticked.Where(f => !Available.ContainsKey(f)).ToList();
        Assert.True(unexplained.Count == 0,
            $"bench-ticked with no instrument to have done it: {string.Join(", ", unexplained)}");
    }

    /// <summary>
    /// A sweep exists for each available family. Adding an instrument to the bench without
    /// pointing a sweep at it would leave it as unverified as the eighteen.
    /// </summary>
    [Fact]
    public void Each_available_instrument_has_a_sweep_that_can_reach_it()
    {
        foreach (InstrumentFamily f in Available.Keys)
        {
            var plan = CatalogSweep.Plan(CommandReference.ForFamily(f)!);
            _out.WriteLine($"  {f,-24} {plan.Count} sendable");
            Assert.True(plan.Count > 0, $"{f}: nothing can be swept");
        }
    }
}
