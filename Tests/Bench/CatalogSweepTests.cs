using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;
using Xunit.Abstractions;

namespace LabEquipmentController.Tests.Bench;

/// <summary>
/// Sends every safely-sendable query in a catalog to the real instrument and records what
/// came back.
///
/// This is what turns "transcribed from the guide" into "answered on the bench". It cannot
/// fail the build on a rejected command — a catalog covers a whole model line, and a DS2202
/// legitimately does not implement everything the MSO2000A guide documents. What it produces
/// is a report, written next to the test output, listing exactly which entries answered.
///
/// The sweep changes nothing: queries only, and only those needing no argument (see
/// <see cref="CatalogSweep"/>).
/// </summary>
[Collection(BenchCollection.Name)]
public class CatalogSweepTests
{
    private readonly ITestOutputHelper _out;
    public CatalogSweepTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// Per-command timeout. Most queries answer in milliseconds, but a measurement genuinely
    /// takes its time — an autoranging DC current reading and a 4-wire resistance on an open
    /// input both sat right on a 2.5 s limit and flipped between answered and timed out
    /// across runs. A working command recorded as unsupported is the one error this sweep
    /// must not make, so it waits.
    /// </summary>
    private const int SweepTimeoutMs = 8000;

    /// <summary>
    /// How many link-kills to absorb before giving up on the instrument.
    ///
    /// On a Rigol an unsupported header produces no reply, no error, and no usable session
    /// afterwards — the link has to be rebuilt. A catalog covers a model line, so a base
    /// model can have hundreds of those, and grinding through hundreds of reconnects is what
    /// wedged a DS2202 hard enough to drop it off the network entirely. Past this many, the
    /// sweep stops and says the remainder is untested, which is true and harmless; carrying
    /// on is neither.
    ///
    /// Raised from 40 once <see cref="AlivenessProbe"/> existed to do this job properly. An
    /// SDG2042X wants a fresh link for every unknown header, so 40 stopped its sweep at 65
    /// of 104 commands — a cap standing in for a diagnosis. The probe is the real guard now;
    /// this is a backstop against a catalog that would otherwise reconnect a thousand times.
    /// </summary>
    private const int MaxReconnects = 120;

    /// <summary>
    /// After rebuilding the link, ask something the instrument cannot refuse. If even this
    /// goes unanswered the instrument has stopped listening, and everything the sweep would
    /// record from here is one fact about the instrument written out once per command.
    ///
    /// Counting unanswered queries instead does not work, and was tried: a DS2202 has long
    /// runs of commands the MSO2000A guide documents and it does not implement, recovers
    /// from every one, and answers 401 of 437 across them — a five-in-a-row rule threw away
    /// 396 of those. An SDG2042X produces the same long runs for the same reason, silence
    /// being how it refuses an unknown header. The count says nothing; whether the
    /// instrument comes back says everything.
    ///
    /// In a healthy run this never fires, and both of those are healthy runs. It is here
    /// for the state an SDG2042X reaches when a long burst of unknown headers arrives with
    /// no pause between them: its whole SCPI service stops, it disappears from a subnet
    /// scan, and it needs a power cycle. Reached by hand, not by this sweep, whose
    /// widening-gap reconnect seems to be what keeps it out of that state.
    /// </summary>
    private const string AlivenessProbe = "*IDN?";

    /// <summary>Where reports land. Overridable, because the default is inside bin/.</summary>
    private static string ReportDir =>
        Environment.GetEnvironmentVariable("LEC_BENCH_REPORTS")
        ?? Path.Combine(AppContext.BaseDirectory, "bench-reports");

    public static TheoryData<string, InstrumentFamily, string> Instruments() => new()
    {
        { "scope",       InstrumentFamily.Oscilloscope,     ":SYSTem:ERRor:NEXT?" },
        { "generator",   InstrumentFamily.SiglentGenerator, null! },
        { "multimeter",  InstrumentFamily.Multimeter,       null! },
    };

    [BenchTheory]
    [MemberData(nameof(Instruments))]
    public async Task Sweep(string which, InstrumentFamily family, string? errorQuery)
    {
        string host = which switch
        {
            "scope" => Bench.Scope,
            "generator" => Bench.Generator,
            _ => Bench.Multimeter,
        };

        CommandReference reference = CommandReference.ForFamily(family)!;
        var plan = CatalogSweep.Plan(reference);
        _out.WriteLine($"{which} at {host}: {plan.Count} of {reference.Commands.Count} sendable");

        // A short timeout: a query that is going to answer does so in milliseconds, and the
        // sweep spends the rest of its life waiting on ones that never will.
        IInstrumentClient client = await Bench.ConnectAsync(host, timeoutMs: SweepTimeoutMs);
        string idn = await client.QueryAsync("*IDN?");
        _out.WriteLine($"*IDN? -> {idn.Trim()}");

        var results = new List<SweepResult>();
        int reconnects = 0;
        string? stoppedBecause = null;

        foreach ((CommandRef command, string send) in plan)
        {
            SweepResult r = await TryAsync(client, command, send, errorQuery);
            results.Add(r);

            // One unanswered query kills the session, and every command after it then reports
            // a failure it had nothing to do with — the first sweep of this scope recorded 39
            // answers, one genuine miss, and 397 casualties. So an unanswered query costs a
            // reconnect, and the next command starts from a clean link.
            //
            // Both shapes of "unanswered" count. A Rigol lets the read time out; a Siglent
            // returns nothing and keeps the link nominally open, which is worse, because the
            // sweep then sails on collecting empties. When Empty became its own outcome this
            // condition was left testing only TimedOut, so no Siglent empty ever cost a
            // reconnect and the SDG2042X reported 80 of them in a run of 104.
            //
            // Reconnecting on those recovers the early ones — answers resume on the fresh
            // link. It does not recover the later ones, and that is not a reason to skip it:
            // it is the reason <see cref="GiveUpAfterConsecutive"/> exists, because an
            // unanswered query a new link cannot fix is a different fact from one it can.
            if (r.Outcome is not (SweepOutcome.TimedOut or SweepOutcome.Empty)) continue;

            if (++reconnects > MaxReconnects)
            {
                stoppedBecause = $"{MaxReconnects} link resets, which is as much as this "
                               + "sweep will ask of an instrument in one run";
                break;
            }

            client.Dispose();
            if (!await Reconnect(host, r => client = r))
            {
                stoppedBecause = $"the link could not be rebuilt after {send}";
                break;
            }

            // A rebuilt link is not the same as a working instrument. Ask it something it
            // cannot refuse; if that goes unanswered too, stop rather than write out one
            // dead instrument once per remaining command.
            if (!await IsAnswering(client))
            {
                stoppedBecause = $"the instrument stopped answering {AlivenessProbe} after "
                               + $"{send}; nothing is claimed about the commands after it";
                break;
            }

            // The command that killed the link left an error behind it. Clear it now, or the
            // next command to read the queue inherits the blame.
            if (errorQuery != null)
                try { await DrainErrors(client, errorQuery); } catch { /* not fatal */ }
        }

        _out.WriteLine($"reconnected {reconnects} time(s)");
        if (stoppedBecause != null)
            _out.WriteLine($"stopped: {stoppedBecause} — " +
                           $"{plan.Count - results.Count} of {plan.Count} left untested");
        await Bench.ReleaseAsync(client);
        client.Dispose();

        int answered = results.Count(r => r.Outcome == SweepOutcome.Answered);
        int rejected = results.Count(r => r.Outcome == SweepOutcome.Rejected);
        int timedOut = results.Count(r => r.Outcome == SweepOutcome.TimedOut);
        int empty = results.Count(r => r.Outcome == SweepOutcome.Empty);

        _out.WriteLine($"answered {answered}, rejected {rejected}, timed out {timedOut}, empty {empty}");
        string path = WriteReport(which, host, idn, reference, results, plan.Count, stoppedBecause);
        _out.WriteLine($"report: {path}");

        // The instrument answered something, and the link survived the whole sweep. Anything
        // stronger would be asserting that a DS2202 implements the entire MSO2000A guide.
        Assert.False(string.IsNullOrWhiteSpace(idn));
        Assert.True(answered > 0, "nothing answered — check the address and that it is idle");
    }

    /// <summary>Is the instrument still answering at all?</summary>
    private static async Task<bool> IsAnswering(IInstrumentClient client)
    {
        try { return !string.IsNullOrWhiteSpace(await client.QueryAsync(AlivenessProbe)); }
        catch { return false; }
    }

    /// <summary>
    /// Rebuild the link, giving the instrument time to notice the old one is gone.
    ///
    /// Redialling the instant a session dies simply fails: the DS2202's firmware wedges under
    /// rapid reconnection — the README has said so since long before this suite existed — and
    /// a Siglent SDM refuses the next connect for a moment too. Three tries with a widening
    /// gap recovers both; failing all three means the instrument has stopped listening, which
    /// is worth reporting rather than retrying into the ground.
    /// </summary>
    private static async Task<bool> Reconnect(string host, Action<IInstrumentClient> assign)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            await Task.Delay(200 * attempt);
            try
            {
                assign(await Bench.ConnectAsync(host, timeoutMs: SweepTimeoutMs));
                return true;
            }
            catch { /* try again, more patiently */ }
        }
        return false;
    }

    private static async Task<SweepResult> TryAsync(
        IInstrumentClient client, CommandRef command, string send, string? errorQuery)
    {
        try
        {
            string reply = await client.QueryAsync(send);
            bool empty = string.IsNullOrWhiteSpace(reply);

            if (errorQuery == null)
                return new SweepResult(command.Syntax, send,
                    empty ? SweepOutcome.Empty : SweepOutcome.Answered, reply.Trim());

            // Drain, don't peek. :SYSTem:ERRor:NEXT? pops one error, and the queue outlives
            // the command that filled it — so a single read after each command attributes
            // whatever is at the head to whoever asked. That marked :MEASure:VPP? as an
            // undefined header on a scope that answers it perfectly well, because two
            // commands earlier had timed out and left their errors behind.
            var errors = await DrainErrors(client, errorQuery);

            if (errors.Any(CatalogSweep.IsUndefinedHeader))
                return new SweepResult(command.Syntax, send, SweepOutcome.Rejected,
                    string.Join(" ", errors));

            return new SweepResult(command.Syntax, send,
                empty ? SweepOutcome.Empty : SweepOutcome.Answered,
                errors.Count == 0 ? reply.Trim() : $"{reply.Trim()}  [{string.Join(" ", errors)}]");
        }
        catch (Exception ex)
        {
            return new SweepResult(command.Syntax, send, SweepOutcome.TimedOut, ex.Message);
        }
    }

    /// <summary>
    /// Empty the error queue, returning everything that was in it. Bounded, because an
    /// instrument that answers every error query with an error would otherwise spin.
    /// </summary>
    private static async Task<List<string>> DrainErrors(IInstrumentClient client, string errorQuery)
    {
        var errors = new List<string>();
        for (int i = 0; i < 12; i++)
        {
            string err = (await client.QueryAsync(errorQuery)).Trim();
            if (CatalogSweep.IsClean(err)) break;
            errors.Add(err);
        }
        return errors;
    }

    /// <summary>
    /// A Markdown report, and a plain list of the syntaxes that answered. The second is the
    /// one that matters: it is what a later pass would use to stamp benchVerified, and
    /// keeping it separate means that stamping is a deliberate act rather than a side effect
    /// of running tests.
    /// </summary>
    private static string WriteReport(
        string which, string host, string idn,
        CommandReference reference, IReadOnlyList<SweepResult> results,
        int planned, string? stoppedBecause)
    {
        Directory.CreateDirectory(ReportDir);
        string path = Path.Combine(ReportDir, $"sweep-{which}.md");

        var sb = new StringBuilder();
        sb.AppendLine($"# Catalog sweep — {which}");
        sb.AppendLine();
        sb.AppendLine($"- instrument: `{idn.Trim()}`");
        sb.AppendLine($"- address: `{host}`");
        sb.AppendLine($"- catalog: {reference.Instrument} ({reference.Commands.Count} entries)");
        sb.AppendLine($"- sent: {results.Count}");
        sb.AppendLine($"- answered: {results.Count(r => r.Outcome == SweepOutcome.Answered)}");
        sb.AppendLine($"- rejected: {results.Count(r => r.Outcome == SweepOutcome.Rejected)}");
        sb.AppendLine($"- timed out: {results.Count(r => r.Outcome == SweepOutcome.TimedOut)}");
        sb.AppendLine($"- answered with nothing: {results.Count(r => r.Outcome == SweepOutcome.Empty)}");
        if (stoppedBecause != null)
            sb.AppendLine($"- **not tested: {planned - results.Count} of {planned}** — {stoppedBecause}");
        sb.AppendLine();
        sb.AppendLine("| Outcome | Sent | Catalog syntax | Reply |");
        sb.AppendLine("|---|---|---|---|");
        foreach (SweepResult r in results)
            sb.AppendLine($"| {r.Outcome} | `{r.Sent}` | `{r.Syntax}` | {Escape(r.Detail)} |");

        File.WriteAllText(path, sb.ToString());

        File.WriteAllLines(
            Path.Combine(ReportDir, $"answered-{which}.txt"),
            results.Where(r => r.Outcome == SweepOutcome.Answered).Select(r => r.Syntax));

        return path;
    }

    private static string Escape(string s)
    {
        string t = s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length > 90 ? t[..90] + "…" : t;
    }

    // ------------------------------------------------------------------- offline checks

    /// <summary>
    /// The planner itself runs without a bench, because a sweep that silently plans nothing
    /// would look exactly like an instrument that answered nothing.
    /// </summary>
    [Theory]
    [InlineData(InstrumentFamily.Oscilloscope, 300)]
    [InlineData(InstrumentFamily.SiglentGenerator, 60)]
    [InlineData(InstrumentFamily.Multimeter, 50)]
    public void The_sweep_plans_a_useful_number_of_commands(InstrumentFamily family, int atLeast)
    {
        var plan = CatalogSweep.Plan(CommandReference.ForFamily(family)!);
        Assert.True(plan.Count >= atLeast,
            $"{family}: only {plan.Count} sendable, expected at least {atLeast}");
    }

    [Theory]
    // Bare queries go as-is.
    [InlineData("*IDN?", "*IDN?")]
    [InlineData(":SYSTem:ERRor:NEXT?", ":SYSTem:ERRor:NEXT?")]
    // Optional arguments are dropped; suffixes are an index the instrument already has.
    [InlineData("SAMPle:COUNt? [{MIN|MAX|DEF}]", "SAMPle:COUNt?")]
    [InlineData("R? [<max_readings>]", "R?")]
    [InlineData(":CHANnel<n>:SCALe?", ":CHANnel1:SCALe?")]
    [InlineData("C<n>:BSWV?", "C1:BSWV?")]
    // An optional *node* is kept. Dropping it yields "MEASure:DC?", a short form the guide
    // never prints and a Siglent SDM answers by hanging — which then reads as a command the
    // instrument does not support.
    [InlineData("MEASure[:VOLTage]:DC?", "MEASure:VOLTage:DC?")]
    [InlineData("INITiate[:IMMediate]?", "INITiate:IMMediate?")]
    // Settings change the instrument. Queries still wanting an argument would need one invented.
    [InlineData(":CHANnel<n>:SCALe <scale>", null)]
    [InlineData("C<n>:OUTP <ON|OFF>", null)]
    [InlineData("MEASure:VOLTage:DC? <range>", null)]
    [InlineData(":WAVeform:DATA?", ":WAVeform:DATA?")]
    public void Only_what_can_be_sent_without_changing_anything_is_planned(string syntax, string? expected)
        => Assert.Equal(expected, CatalogSweep.Sendable(syntax));

    [Theory]
    [InlineData("-113,\"Undefined header\"", true)]
    [InlineData("-100,\"Command error\"", true)]
    [InlineData("0,\"No error\"", false)]
    [InlineData("-222,\"Data out of range\"", false)]   // understood, just not liked
    public void An_undefined_header_is_told_apart_from_other_errors(string reply, bool expected)
        => Assert.Equal(expected, CatalogSweep.IsUndefinedHeader(reply));

    /// <summary>
    /// An empty reply is not an answer, and the distinction is the whole worth of the report:
    /// what it lists as answered is what gets stamped benchVerified.
    ///
    /// A DS2202 has no logic analyser, and returned nothing at all to :LA:ACTive?,
    /// :LA:DIGital&lt;n&gt;:POSition? and :LA:POD&lt;n&gt;:DISPlay?. Counted as answers, those
    /// three were about to be stamped as confirmed on hardware that cannot do them.
    /// </summary>
    [Theory]
    [InlineData("CHAN1", false)]
    [InlineData("0", false)]          // a real reading, and falsy in every other sense
    [InlineData("OFF", false)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("\r\n", true)]
    public void A_reply_of_nothing_is_not_an_answer(string reply, bool expectedEmpty)
        => Assert.Equal(expectedEmpty, string.IsNullOrWhiteSpace(reply));
}
