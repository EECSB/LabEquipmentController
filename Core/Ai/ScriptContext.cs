using System;
using System.Collections.Generic;
using System.Linq;

namespace LabEquipmentController;

/// <summary>
/// What the script writer is told about the instruments a script will drive: for each, the
/// alias the script addresses it by, what its DEVICE line says, its identity and its catalog.
/// Worked out here, once, for both builds.
/// </summary>
/// <remarks>
/// It was worked out twice, and the two disagreed. The desktop named each instrument after its
/// kind — gen, dmm, scope — and the web after the first eight letters of its profile, so two
/// meters on the web were both "multimet" and the check of the reply threw on the duplicate
/// after the model had been paid for. The web also handed over the identity where the model
/// goes and the address where the identity goes, and gave a single-instrument script an alias
/// its lines must not carry.
///
/// What a DEVICE line has to say is <see cref="SequenceBinding"/>'s business: a model binds
/// only when exactly one connected instrument answers to it. So an instrument whose model
/// another on the bench shares is declared by its serial number, and what the writer is told to
/// put in the script is then what binds.
/// </remarks>
public static class ScriptContext
{
    /// <summary>
    /// The one instrument a single-instrument script drives. No alias: its lines carry no
    /// prefix.
    /// </summary>
    /// <param name="identity">Its *IDN? reply.</param>
    /// <param name="name">What to call it if the reply names no model — its address, say.</param>
    public static ScriptContextInstrument ForScript(string identity, string name)
        => new(
            Alias: "",
            Model: SequenceBinding.ModelOf(identity) is { Length: > 0 } model ? model : name,
            Identity: identity,
            Reference: CommandReference.ForIdentity(identity));

    /// <summary>
    /// The instruments a multi-instrument script may address, each with the alias to use and
    /// the text its DEVICE line binds by.
    /// </summary>
    /// <param name="bench">Every connected instrument, in the order to list them.</param>
    /// <param name="identity">An instrument's *IDN? reply.</param>
    /// <param name="host">An instrument's address, as a DEVICE line would write it.</param>
    /// <param name="script">
    /// The script being revised, if there is one. What it already calls an instrument is kept,
    /// so asking for a change to a working script does not rename its devices, and a DEVICE line
    /// of it that binds is kept as it is written.
    /// </param>
    /// <param name="only">
    /// The instruments to describe, when not all of them are wanted — the web's ticks. The rest
    /// still count: a meter left out of the prompt is still a second meter of that model on the
    /// bench, and naming the first by model would bind neither.
    /// </param>
    public static IReadOnlyList<ScriptContextInstrument> ForSequence<T>(
        IReadOnlyList<T> bench,
        Func<T, string> identity,
        Func<T, string> host,
        string? script = null,
        IReadOnlyCollection<T>? only = null) where T : class
    {
        var alias = new Dictionary<T, string>(ReferenceEqualityComparer.Instance);
        var written = new Dictionary<T, string>(ReferenceEqualityComparer.Instance);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // What the script already calls each instrument, by the rule the strip binds it by. A
        // line that binds keeps its alias and its text.
        IReadOnlyList<DeviceBinding<T>> bound =
            SequenceBinding.Bind(SequenceRunner.Requirements(script ?? ""), bench, identity, host);
        foreach (DeviceBinding<T> b in bound)
        {
            if (b.Instrument is not T t || alias.ContainsKey(t) || !used.Add(b.Alias)) continue;
            alias[t] = b.Alias;
            written[t] = b.Model;
        }

        // One that does not — two meters asked for by model — still lends its alias to an
        // instrument it answers to, and that instrument is declared by what makes it bind.
        // Which meter got which name is then in the draft as a serial number, to be checked.
        foreach (DeviceBinding<T> b in bound)
        {
            if (b.Instrument != null || used.Contains(b.Alias)) continue;
            T? t = SequenceBinding.Answering(b.Model, bench, identity, host).Instruments
                                  .FirstOrDefault(x => !alias.ContainsKey(x));
            if (t == null) continue;
            alias[t] = b.Alias;
            used.Add(b.Alias);
        }

        var described = new List<ScriptContextInstrument>();
        foreach (T t in bench)
        {
            if (only != null && !only.Any(o => ReferenceEquals(o, t))) continue;

            string id = identity(t);
            if (!alias.TryGetValue(t, out string? name))
            {
                // Two scopes on the bench would otherwise both be "scope", and the second
                // DEVICE line would quietly overwrite the first.
                string kind = AliasFor(InstrumentProfile.FamilyForIdentity(id));
                name = kind;
                for (int n = 2; !used.Add(name); n++) name = kind + n;
            }

            described.Add(new ScriptContextInstrument(
                name,
                written.TryGetValue(t, out string? text) ? text : DeviceText(t, bench, identity, host),
                id,
                CommandReference.ForIdentity(id)));
        }
        return described;
    }

    /// <summary>
    /// What a DEVICE line has to say to bind to this instrument alone: its model, unless another
    /// instrument on the bench answers to that model too — then its serial number, and failing
    /// that its address.
    /// </summary>
    private static string DeviceText<T>(
        T t, IReadOnlyList<T> bench, Func<T, string> identity, Func<T, string> host) where T : class
    {
        string model = SequenceBinding.ModelOf(identity(t));
        if (model.Length == 0) return host(t);

        var others = bench.Where(o => !ReferenceEquals(o, t)).ToList();
        if (!others.Any(o => SequenceBinding.Same(SequenceBinding.ModelOf(identity(o)), model)))
            return model;

        string serial = SequenceBinding.SerialOf(identity(t));
        bool alone = serial.Length > 0
                  && !others.Any(o => SequenceBinding.Same(SequenceBinding.SerialOf(identity(o)), serial));
        return alone ? serial : host(t);
    }

    /// <summary>The short name an engineer would give this kind of instrument.</summary>
    private static string AliasFor(InstrumentFamily family) => family switch
    {
        InstrumentFamily.SiglentGenerator or InstrumentFamily.ScpiGenerator => "gen",

        InstrumentFamily.Multimeter or InstrumentFamily.RigolMultimeter
            or InstrumentFamily.KeysightMultimeter or InstrumentFamily.KeithleyDmm
            or InstrumentFamily.FlukeMultimeter => "dmm",

        InstrumentFamily.PowerSupply or InstrumentFamily.KeysightPowerSupply
            or InstrumentFamily.RohdePowerSupply or InstrumentFamily.ChromaPowerSupply
            or InstrumentFamily.BkPowerSupply or InstrumentFamily.BkPowerSupply9130 => "psu",

        InstrumentFamily.ElectronicLoad or InstrumentFamily.BkElectronicLoad
            or InstrumentFamily.ChromaElectronicLoad or InstrumentFamily.ChromaModularLoad
            or InstrumentFamily.RigolElectronicLoad => "load",

        InstrumentFamily.SpectrumAnalyzer or InstrumentFamily.RigolSpectrumAnalyzer
            or InstrumentFamily.RohdeSpectrumAnalyzer or InstrumentFamily.RohdeFslAnalyzer
            or InstrumentFamily.RohdeFsvAnalyzer or InstrumentFamily.RohdeFswAnalyzer
            or InstrumentFamily.RohdeFsuAnalyzer or InstrumentFamily.RohdeFspAnalyzer
            or InstrumentFamily.RohdeFsqAnalyzer
            or InstrumentFamily.RohdeFsiqAnalyzer => "sa",

        InstrumentFamily.KeithleySmu => "smu",

        InstrumentFamily.Oscilloscope or InstrumentFamily.SiglentScope
            or InstrumentFamily.TektronixScope or InstrumentFamily.KeysightScope
            or InstrumentFamily.RohdeScope or InstrumentFamily.GwInstekScope
            or InstrumentFamily.GwInstekScopeB => "scope",

        _ => "inst",
    };
}
