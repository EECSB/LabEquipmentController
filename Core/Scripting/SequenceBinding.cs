using System;
using System.Collections.Generic;
using System.Linq;

namespace LabEquipmentController;

/// <summary>Where one DEVICE line of a sequence stands on a bench.</summary>
public enum DeviceBindingState
{
    /// <summary>It has the one instrument it names.</summary>
    Bound,

    /// <summary>Nothing connected answers to what it names.</summary>
    NotConnected,

    /// <summary>More than one connected instrument answers to it, and nothing says which.</summary>
    Ambiguous,

    /// <summary>What answers to it is already playing another alias's part.</summary>
    Taken,
}

/// <summary>What one DEVICE line of a sequence is bound to on a bench, or why it is bound to nothing.</summary>
/// <typeparam name="T">Whatever the front end keeps a connection in.</typeparam>
/// <param name="Alias">The alias the line declares — "gen".</param>
/// <param name="Model">What the line names its instrument by: a model, a serial number or an address.</param>
/// <param name="Instrument">The instrument it is bound to, or null.</param>
/// <param name="State">Bound, or why not.</param>
/// <param name="Answering">How many connected instruments answer to what the line names.</param>
/// <param name="TakenBy">The aliases that have them, when the line is <see cref="DeviceBindingState.Taken"/>.</param>
public sealed record DeviceBinding<T>(
    string Alias,
    string Model,
    T? Instrument,
    DeviceBindingState State,
    int Answering,
    IReadOnlyList<string> TakenBy) where T : class
{
    /// <summary>
    /// Why nothing is bound, in the words both builds use — "not connected", "2 connected",
    /// "taken by dmm" — or null when something is. What to do about it is each build's own: a
    /// picker on the web, a serial number or an address on the desktop.
    /// </summary>
    public string? Reason => State switch
    {
        DeviceBindingState.NotConnected => "not connected",
        DeviceBindingState.Ambiguous => $"{Answering} connected",
        DeviceBindingState.Taken => "taken by " + string.Join(", ", TakenBy),
        _ => null,
    };
}

/// <summary>
/// Which connected instrument each DEVICE line of a sequence names: the one rule the desktop's
/// device strip and the web's binding table are both filled by.
/// </summary>
/// <remarks>
/// A line names an instrument by model — <c>DEVICE left : SDM3065X</c> — or one instrument in
/// particular by the serial number its *IDN? reports, or by its address. A model is matched
/// exactly and then as a prefix, because a label is shorter than what some instruments answer:
/// an SDS2354X says <c>SDS2354X Plus</c>.
///
/// A model binds only when exactly one connected instrument answers to it that no other alias
/// already has. Two meters of one model are the same answer to "which SDM3065X?", and handing
/// out whichever connected first is a guess about which one is wired to what — so they resolve
/// to nothing, as a prefix two models share always has. So does a second alias asking for the
/// one meter a first alias has: bound to it, the script reads one meter twice and reports two.
///
/// Serial numbers, addresses and a front end's own picks go first and are taken at their word;
/// the model lines follow in script order. That is what makes naming one of two identical
/// meters enough: the other line finds the one that is left. Which instrument connected first
/// never enters into it.
/// </remarks>
public static class SequenceBinding
{
    /// <summary>Bind every DEVICE line of a script, in the order given.</summary>
    /// <param name="needs">The script's DEVICE lines — <see cref="SequenceRunner.Requirements"/>.</param>
    /// <param name="bench">The connected instruments.</param>
    /// <param name="identity">An instrument's *IDN? reply.</param>
    /// <param name="host">An instrument's address, as a DEVICE line would write it.</param>
    /// <param name="picked">
    /// Instruments chosen by hand, by alias — the web's binding table. Taken as given, the same
    /// instrument for two aliases included: that is a decision, not a guess. Looked up with the
    /// dictionary's own comparer, so give it a case-insensitive one, as aliases are.
    /// </param>
    public static IReadOnlyList<DeviceBinding<T>> Bind<T>(
        IReadOnlyList<(string Alias, string Model)> needs,
        IReadOnlyList<T> bench,
        Func<T, string> identity,
        Func<T, string> host,
        IReadOnlyDictionary<string, T>? picked = null) where T : class
    {
        int n = needs.Count;
        var answering = new List<T>[n];
        var byModel = new bool[n];
        var bound = new T?[n];

        for (int i = 0; i < n; i++)
            (answering[i], byModel[i]) = Answering(needs[i].Model, bench, identity, host);

        // First, and at their word: what was picked, then what was named one by one.
        for (int i = 0; i < n; i++)
        {
            if (picked != null && picked.TryGetValue(needs[i].Alias, out T? pick)) bound[i] = pick;
            else if (!byModel[i] && answering[i].Count == 1) bound[i] = answering[i][0];
        }

        // Then the models, in script order.
        for (int i = 0; i < n; i++)
        {
            if (bound[i] != null || !byModel[i]) continue;
            var free = answering[i].Where(t => Holders([t], needs[i].Alias, needs, bound).Count == 0).ToList();
            if (free.Count == 1) bound[i] = free[0];
        }

        var result = new List<DeviceBinding<T>>(n);
        for (int i = 0; i < n; i++)
        {
            (string alias, string model) = needs[i];
            int count = answering[i].Count;

            if (bound[i] is T instrument)
                result.Add(new(alias, model, instrument, DeviceBindingState.Bound, count, []));
            else if (count == 0)
                result.Add(new(alias, model, null, DeviceBindingState.NotConnected, 0, []));
            else if (byModel[i] && answering[i].All(t => Holders([t], alias, needs, bound).Count > 0))
                result.Add(new(alias, model, null, DeviceBindingState.Taken, count,
                               Holders(answering[i], alias, needs, bound)));
            else
                result.Add(new(alias, model, null, DeviceBindingState.Ambiguous, count, []));
        }
        return result;
    }

    /// <summary>
    /// The connected instruments a DEVICE line's text answers to, by the first rule that finds
    /// any: the model exactly, the model as a prefix, the serial number, the address. The flag
    /// says a model found them; the other two name one instrument in particular.
    /// </summary>
    private static (List<T> Instruments, bool ByModel) Answering<T>(
        string text, IReadOnlyList<T> bench, Func<T, string> identity, Func<T, string> host)
    {
        string want = text.Trim();
        if (want.Length == 0) return ([], false);

        var exact = bench.Where(t => Same(ModelOf(identity(t)), want)).ToList();
        if (exact.Count > 0) return (exact, true);

        var prefix = bench.Where(t => ModelOf(identity(t)).StartsWith(want, StringComparison.OrdinalIgnoreCase)).ToList();
        if (prefix.Count > 0) return (prefix, true);

        var serial = bench.Where(t => Same(SerialOf(identity(t)), want)).ToList();
        if (serial.Count > 0) return (serial, false);

        return (bench.Where(t => Same(host(t), want)).ToList(), false);
    }

    /// <summary>
    /// The aliases other than <paramref name="alias"/> that any of these instruments is bound to
    /// so far, in script order. A line repeating its own alias is the same part declared twice,
    /// not a second part.
    /// </summary>
    private static List<string> Holders<T>(
        IReadOnlyList<T> instruments, string alias,
        IReadOnlyList<(string Alias, string Model)> needs, T?[] bound) where T : class
    {
        var holders = new List<string>();
        for (int j = 0; j < needs.Count; j++)
        {
            if (bound[j] is not T held || Same(needs[j].Alias, alias)) continue;
            if (!instruments.Any(t => ReferenceEquals(t, held))) continue;
            if (!holders.Contains(needs[j].Alias, StringComparer.OrdinalIgnoreCase)) holders.Add(needs[j].Alias);
        }
        return holders;
    }

    private static string ModelOf(string? identity) => InstrumentProfile.ParseIdentity(identity).Model;

    /// <summary>The third field of "Manufacturer,Model,Serial,Firmware", or "".</summary>
    private static string SerialOf(string? identity)
    {
        string[] parts = (identity ?? "").Split(',');
        return parts.Length > 2 ? parts[2].Trim() : "";
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
