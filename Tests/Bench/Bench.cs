using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests.Bench;

/// <summary>
/// Which instruments are on the bench, and how to reach them.
///
/// These tests talk to real hardware, so they are **off unless asked for**: set `LEC_BENCH=1`
/// and they run, otherwise every one of them skips. A normal `dotnet test` on a machine with
/// no bench must stay green, or the suite stops being worth running at all.
///
/// Addresses are **discovered**, not written down. All three instruments are on DHCP and they
/// move: the README's two recorded addresses were both wrong within a day, and worse, the one
/// that still resolved had been reassigned — `192.168.1.19` was the scope and is now the
/// generator. A stale default is worse than none, because a test that reaches the wrong
/// instrument fails somewhere deep and confusing instead of at the connection.
///
/// So the suite scans once per run and keys on what each instrument says it is:
///
///     set LEC_BENCH=1
///     dotnet test --filter "FullyQualifiedName~Bench"
///
/// Set `LEC_SCOPE`, `LEC_GENERATOR` or `LEC_MULTIMETER` to skip discovery for one of them,
/// and `LEC_SUBNET` (default `192.168.1`) if the bench is on a different /24.
/// </summary>
public static class Bench
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("LEC_BENCH") is "1" or "true" or "TRUE";

    /// <summary>Rigol DS2202. VXI-11: its raw-socket port lags replies by one query.</summary>
    public static string Scope => Find(InstrumentFamily.Oscilloscope, "LEC_SCOPE");

    /// <summary>Siglent SDG2042X. VXI-11 only — it exposes no raw SCPI socket.</summary>
    public static string Generator => Find(InstrumentFamily.SiglentGenerator, "LEC_GENERATOR");

    /// <summary>Siglent SDM3065X.</summary>
    public static string Multimeter => Find(InstrumentFamily.Multimeter, "LEC_MULTIMETER");

    /// <summary>One scan per test run, shared by every test that needs an address.</summary>
    private static readonly Lazy<IReadOnlyList<ScpiDevice>> Discovered = new(() =>
    {
        string subnet = Environment.GetEnvironmentVariable("LEC_SUBNET")?.Trim() ?? "192.168.1";
        var hosts = Enumerable.Range(1, 254)
            .Select(i => IPAddress.Parse($"{subnet}.{i}"))
            .ToList();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        return NetworkScanner.ScanAsync(
            hosts, NetworkScanner.CommonScpiPorts,
            connectTimeoutMs: 400, idnTimeoutMs: 2500,
            progress: null, ct: cts.Token).GetAwaiter().GetResult();
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The address of the one instrument that classifies as this family, or an override.
    ///
    /// Ambiguity throws rather than guesses. Two scopes on the subnet and a silent
    /// first-match would send the sweep to whichever answered fastest, which is exactly the
    /// sort of thing that produces a confident report about the wrong instrument.
    /// </summary>
    private static string Find(InstrumentFamily family, string overrideVar)
    {
        string? forced = Environment.GetEnvironmentVariable(overrideVar);
        if (!string.IsNullOrWhiteSpace(forced)) return forced.Trim();

        var matches = Discovered.Value
            .Where(d => InstrumentProfile.FamilyForIdentity(d.Identity) == family)
            .ToList();

        if (matches.Count == 1) return matches[0].Address.ToString();

        string seen = Discovered.Value.Count == 0
            ? "nothing answered on the subnet"
            : "found " + string.Join("; ", Discovered.Value.Select(
                d => $"{d.Address} = {d.Identity.Trim()}"));

        throw new InvalidOperationException(
            matches.Count == 0
                ? $"No {family} on the bench — set {overrideVar} to override ({seen})."
                : $"{matches.Count} instruments classify as {family}; set {overrideVar} "
                  + $"to say which ({seen}).");
    }

    /// <summary>
    /// Connect over VXI-11, which is what all three instruments here want. Callers dispose it.
    /// </summary>
    public static async Task<IInstrumentClient> ConnectAsync(string host, int timeoutMs = 5000)
    {
        var client = new Vxi11Client(host) { TimeoutMs = timeoutMs };
        try
        {
            await client.ConnectAsync();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Hand the front panel back. Every bench test does this, because leaving an instrument
    /// locked out after a test run is the kind of thing that gets a test suite switched off.
    /// </summary>
    public static async Task ReleaseAsync(IInstrumentClient client)
    {
        try { await client.ReturnToLocalAsync(); } catch { /* best effort */ }
    }
}

/// <summary>
/// One collection for everything that talks to the bench, so none of it runs at the same time
/// as the rest of it.
///
/// xUnit runs test *classes* in parallel by default, and these classes share three
/// instruments. The first full run of this suite failed seven tests — the whole of the scope's
/// and one of the meter's — while the sweep was sending four hundred queries to the same
/// scope a feature test was trying to capture a waveform from. Re-running the same tests on
/// their own passed every one. Nothing was wrong with the app or the instruments; the suite
/// was competing with itself, and a suite that fails at random is one nobody runs twice.
///
/// <c>DisableParallelization</c> as well as a shared collection: sharing the collection stops
/// these classes overlapping each other, and this stops them overlapping the 900-odd tests
/// that do not touch hardware but do compete for the CPU while a 5-second instrument timeout
/// is counting down.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BenchCollection
{
    public const string Name = "bench hardware";
}

/// <summary>A fact that runs only when <see cref="Bench.Enabled"/>.</summary>
public sealed class BenchFactAttribute : FactAttribute
{
    public BenchFactAttribute()
    {
        if (!Bench.Enabled) Skip = "Bench tests are off. Set LEC_BENCH=1 to run them.";
    }
}

/// <summary>A theory that runs only when <see cref="Bench.Enabled"/>.</summary>
public sealed class BenchTheoryAttribute : TheoryAttribute
{
    public BenchTheoryAttribute()
    {
        if (!Bench.Enabled) Skip = "Bench tests are off. Set LEC_BENCH=1 to run them.";
    }
}

/// <summary>
/// A fact that runs only when `LEC_AI=1`. Separate from the bench switch because an
/// extraction costs money per run, which is a different thing to agree to than talking to an
/// instrument on the desk.
///
/// An attribute rather than an early `return`: a test that quietly passes when it did nothing
/// reports the same green as one that ran, and the whole point of this suite is to be honest
/// about what has actually been exercised.
/// </summary>
public sealed class AiFactAttribute : FactAttribute
{
    public AiFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LEC_AI") is not ("1" or "true" or "TRUE"))
            Skip = "AI extraction tests are off (they cost money). Set LEC_AI=1 to run them.";
    }
}
