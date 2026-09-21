using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The two things that keep a bench safe when a run does not end the way it meant to: a
/// deadline for the whole run, and a block that runs at the end however the end came.
///
/// Both exist for the same failure. A generator set to 10 V by a run that died on the next
/// line stays at 10 V, and nothing in the language could say otherwise — the script simply
/// stopped, with the output on and whatever is wired up still being driven.
/// </summary>
public class SequenceSafeStateTests
{
    private sealed class Bench
    {
        public readonly Dictionary<string, FakeInstrumentClient> Instruments =
            new(StringComparer.OrdinalIgnoreCase);

        public readonly List<string> Output = new();
        public readonly List<string> Errors = new();
        public readonly List<SequenceRow> Rows = new();

        public FakeInstrumentClient Add(string model)
            => Instruments[model] = new FakeInstrumentClient { Host = model };

        public Task RunAsync(string script, CancellationToken ct = default, TimeSpan? limit = null)
            => SequenceRunner.RunAsync(
                script,
                model => Instruments.TryGetValue(model, out var c) ? c : null,
                (text, kind) =>
                {
                    Output.Add(text);
                    if (kind == ScriptOutputKind.Error) Errors.Add(text);
                },
                Rows.Add,
                ct,
                null,
                limit);

        public IReadOnlyList<string> Sent(string model) => Instruments[model].Log;
    }

    // ------------------------------------------------------------------- FINALLY

    [Fact]
    public async Task The_block_runs_when_the_script_finishes()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            gen: C1:OUTP ON
            FINALLY
                gen: C1:OUTP OFF
            END
            """);

        Assert.Empty(bench.Errors);
        Assert.Equal(["SEND:C1:OUTP ON", "SEND:C1:OUTP OFF"], bench.Sent("SDG2042X"));
    }

    [Fact]
    public async Task The_block_runs_after_a_line_that_failed()
    {
        var bench = new Bench();
        bench.Instruments["SDG2042X"] = new FakeInstrumentClient { Host = "SDG2042X", ThrowOn = "C1:BSWV FRQ,1000" };

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            gen: C1:OUTP ON
            gen: C1:BSWV FRQ,1000
            gen: C1:BSWV AMP,2
            FINALLY
                gen: C1:OUTP OFF
            END
            """);

        Assert.Single(bench.Errors);
        Assert.Contains("ERROR on line 3", bench.Errors[0]);

        // The line after the failure never ran; the safe state did.
        Assert.DoesNotContain("SEND:C1:BSWV AMP,2", bench.Sent("SDG2042X"));
        Assert.Equal("SEND:C1:OUTP OFF", bench.Sent("SDG2042X").Last());
    }

    /// <summary>
    /// The case it exists for. Stop cancels the run's token, so the safe state has to run on
    /// one of its own — otherwise the very press that means "stop driving my board" would be
    /// the press that stopped the outputs being switched off.
    /// </summary>
    [Fact]
    public async Task The_block_runs_when_the_run_is_stopped()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        using var stop = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bench.RunAsync("""
            DEVICE gen : SDG2042X
            gen: C1:OUTP ON
            DELAY 30000
            gen: C1:BSWV AMP,2
            FINALLY
                gen: C1:OUTP OFF
            END
            """, StopOnceStarted(stop, bench)));

        Assert.Equal(["SEND:C1:OUTP ON", "SEND:C1:OUTP OFF"], bench.Sent("SDG2042X"));
    }

    /// <summary>Cancel as soon as the first command has been sent, as a hand on Stop would.</summary>
    private static CancellationToken StopOnceStarted(CancellationTokenSource stop, Bench bench)
    {
        _ = Task.Run(async () =>
        {
            while (bench.Instruments.Values.All(i => i.Log.Count == 0))
                await Task.Delay(5);

            stop.Cancel();
        });

        return stop.Token;
    }

    [Fact]
    public async Task ALWAYS_is_the_same_word()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            ALWAYS
                gen: C1:OUTP OFF
            END
            """);

        Assert.Equal(["SEND:C1:OUTP OFF"], bench.Sent("SDG2042X"));
    }

    /// <summary>
    /// Written at the top is where a careful author puts the safe state, so that whoever reads
    /// the script sees what it leaves behind before they see what it does.
    /// </summary>
    [Fact]
    public async Task The_block_may_be_written_anywhere_and_still_runs_last()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            FINALLY
                gen: C1:OUTP OFF
            END
            gen: C1:OUTP ON
            """);

        Assert.Equal(["SEND:C1:OUTP ON", "SEND:C1:OUTP OFF"], bench.Sent("SDG2042X"));
    }

    [Fact]
    public async Task Two_blocks_run_in_the_order_they_are_written()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            FINALLY
                gen: C1:OUTP OFF
            END
            FINALLY
                gen: C2:OUTP OFF
            END
            """);

        Assert.Equal(["SEND:C1:OUTP OFF", "SEND:C2:OUTP OFF"], bench.Sent("SDG2042X"));
    }

    /// <summary>
    /// A run that never reached the DEVICE line naming the generator still has to be able to
    /// switch its output off — which means the safe state addresses every instrument the
    /// script declared, not only those the run got as far as.
    /// </summary>
    [Fact]
    public async Task The_block_can_address_an_instrument_the_run_never_reached()
    {
        var bench = new Bench();
        bench.Instruments["SDM3065X"] = new FakeInstrumentClient { Host = "SDM3065X", ThrowOn = "MEASure:VOLTage:DC?" };
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE dmm : SDM3065X
            dmm: MEASure:VOLTage:DC?
            DEVICE gen : SDG2042X
            gen: C1:OUTP ON
            FINALLY
                gen: C1:OUTP OFF
            END
            """);

        Assert.Single(bench.Errors);
        Assert.Equal(["SEND:C1:OUTP OFF"], bench.Sent("SDG2042X"));
    }

    [Fact]
    public async Task What_the_block_sends_is_not_run_twice_by_the_body()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            FINALLY
                gen: C1:OUTP OFF
            END
            """);

        Assert.Single(bench.Sent("SDG2042X"));
    }

    [Fact]
    public async Task A_block_inside_the_safe_state_works_as_it_does_anywhere()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            FINALLY
                WITH gen
                    C1:OUTP OFF
                    C2:OUTP OFF
                END
            END
            """);

        Assert.Equal(["SEND:C1:OUTP OFF", "SEND:C2:OUTP OFF"], bench.Sent("SDG2042X"));
    }

    // ------------------------------------------------------------------- TIMEOUT

    [Theory]
    [InlineData("TIMEOUT 90s", 90)]
    [InlineData("TIMEOUT 5m", 300)]
    [InlineData("TIMEOUT 1h", 3600)]
    [InlineData("TIMEOUT 500ms", 0.5)]
    public void A_script_says_its_own_deadline(string line, double seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), SequenceRunner.TimeLimit(line));
    }

    [Fact]
    public void A_script_with_no_TIMEOUT_line_has_no_deadline()
    {
        Assert.Null(SequenceRunner.TimeLimit("DEVICE gen : SDG2042X"));
    }

    /// <summary>
    /// DELAY's bare number is milliseconds, so a bare number here would read as milliseconds to
    /// whoever had just written one — and TIMEOUT 90 meaning a minute and a half to the runner
    /// and a tenth of a second to its author is not a difference anybody finds in time.
    /// </summary>
    [Fact]
    public void A_deadline_without_a_unit_is_not_read_as_one()
    {
        Assert.Null(SequenceRunner.TimeLimit("TIMEOUT 90"));

        Assert.False(SequenceRunner.TryParseSpan("90", out _, out string? why));
        Assert.Contains("needs a unit", why);
    }

    [Fact]
    public async Task A_run_that_passes_its_deadline_ends_and_says_so()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            TIMEOUT 200ms
            DEVICE gen : SDG2042X
            gen: C1:OUTP ON
            DELAY 30000
            gen: C1:BSWV AMP,2
            """);

        Assert.Single(bench.Errors);
        Assert.Contains("Timed out after", bench.Errors[0]);
        Assert.Equal(["SEND:C1:OUTP ON"], bench.Sent("SDG2042X"));
    }

    [Fact]
    public async Task A_run_cut_off_by_its_deadline_still_leaves_the_bench_safe()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            TIMEOUT 200ms
            DEVICE gen : SDG2042X
            gen: C1:OUTP ON
            DELAY 30000
            FINALLY
                gen: C1:OUTP OFF
            END
            """);

        Assert.Equal(["SEND:C1:OUTP ON", "SEND:C1:OUTP OFF"], bench.Sent("SDG2042X"));
    }

    /// <summary>
    /// A host imposing its own deadline on a script that carries none: a run nobody is watching
    /// must not be able to hold a bench forever.
    /// </summary>
    [Fact]
    public async Task A_deadline_the_caller_imposes_applies_to_a_script_that_names_none()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            DEVICE gen : SDG2042X
            DELAY 30000
            """, default, TimeSpan.FromMilliseconds(200));

        Assert.Contains("Timed out after", Assert.Single(bench.Errors));
    }

    [Fact]
    public async Task The_shorter_of_the_two_deadlines_is_the_one_that_runs()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        // The script says an hour; the caller says a fifth of a second.
        await bench.RunAsync("""
            TIMEOUT 1h
            DEVICE gen : SDG2042X
            DELAY 30000
            """, default, TimeSpan.FromMilliseconds(200));

        Assert.Contains("Timed out after", Assert.Single(bench.Errors));
    }

    [Fact]
    public async Task A_run_inside_its_deadline_is_untouched_by_it()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            TIMEOUT 1h
            DEVICE gen : SDG2042X
            gen: C1:OUTP ON
            """);

        Assert.Empty(bench.Errors);
        Assert.Equal(["SEND:C1:OUTP ON"], bench.Sent("SDG2042X"));
    }

    /// <summary>
    /// Stopping is not timing out. The distinction reaches the caller: a run the Stop button
    /// ended raises the cancellation its caller is waiting for and is recorded as stopped,
    /// while one the deadline ended returns normally and is recorded as failed.
    /// </summary>
    [Fact]
    public async Task A_deadline_does_not_raise_the_cancellation_that_stopping_does()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            TIMEOUT 200ms
            DEVICE gen : SDG2042X
            DELAY 30000
            """);

        Assert.Contains("Timed out after", Assert.Single(bench.Errors));
    }

    [Fact]
    public async Task The_TIMEOUT_line_is_not_sent_to_an_instrument()
    {
        var bench = new Bench();
        bench.Add("SDG2042X");

        await bench.RunAsync("""
            TIMEOUT 1h
            DEVICE gen : SDG2042X
            """);

        Assert.Empty(bench.Sent("SDG2042X"));
    }
}
