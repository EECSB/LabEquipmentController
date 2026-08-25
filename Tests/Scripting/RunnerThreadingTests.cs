using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The runners' callbacks leave the thread the run was started on, and this pins that so the
/// script windows keep marshalling them back.
///
/// A regression test for a bug that shipped. Both runners await with
/// <c>ConfigureAwait(false)</c> — correct for UI-free Core — so once a round-trip actually
/// suspends, their callbacks resume on a thread-pool thread. The script windows were passing
/// their <c>Append</c> straight in, and a WinForms control touched off the UI thread throws:
/// "Cross-thread operation not valid".
///
/// It survived the whole suite and a run against a loopback stand-in because neither ever
/// suspends: a completed task resumes inline, so callbacks stayed where they started and code
/// that is only correct on one thread looked correct. Only a real instrument on real Ethernet
/// yields — and then it fails on the second line of the script, in front of the user.
///
/// The test needs a real UI-like thread, not just a thread-id comparison: the pool cheerfully
/// hands a continuation back to the very thread that was waiting, so "ran somewhere else" is
/// not observable that way. <see cref="UiLikeThread"/> is a dedicated thread with a pumping
/// <see cref="SynchronizationContext"/> — the shape WinForms has — and a pool thread can
/// never be mistaken for it.
/// </summary>
public class RunnerThreadingTests
{
    [Fact]
    public async Task A_script_run_leaves_the_thread_it_was_started_on()
    {
        using var ui = new UiLikeThread();
        var threads = new HashSet<int>();

        await ui.RunAsync(() => ScriptRunner.RunAsync(
            "*IDN?\r\n*IDN?\r\n*IDN?",
            new FakeInstrumentClient { Yields = true },
            (_, _) => { lock (threads) threads.Add(Environment.CurrentManagedThreadId); },
            _ => { },
            CancellationToken.None));

        Assert.NotEmpty(threads);
        Assert.Contains(threads, t => t != ui.ManagedThreadId);
    }

    /// <summary>
    /// The other half of the story, kept as a test so the reason the bug hid is written down
    /// rather than remembered: an instrument that answers without suspending — a stand-in on
    /// loopback — calls back on the UI thread throughout, and the broken code passes.
    /// </summary>
    [Fact]
    public async Task An_instrument_that_never_suspends_stays_on_the_ui_thread()
    {
        using var ui = new UiLikeThread();
        var threads = new HashSet<int>();

        await ui.RunAsync(() => ScriptRunner.RunAsync(
            "*IDN?\r\n*IDN?\r\n*IDN?",
            new FakeInstrumentClient { Yields = false },
            (_, _) => { lock (threads) threads.Add(Environment.CurrentManagedThreadId); },
            _ => { },
            CancellationToken.None));

        Assert.Equal(new HashSet<int> { ui.ManagedThreadId }, threads);
    }

    [Fact]
    public async Task A_multi_instrument_run_leaves_the_thread_it_was_started_on()
    {
        using var ui = new UiLikeThread();
        var gen = new FakeInstrumentClient { Yields = true };
        var dmm = new FakeInstrumentClient { Yields = true };

        var output = new HashSet<int>();
        var records = new HashSet<int>();

        const string script = """
            DEVICE gen : SDG2042X
            DEVICE dmm : SDM3065X
            COLUMNS Frequency, Volts
            FOR f = 100 TO 300 STEP 100
                gen: C1:BSWV FRQ,$f
                dmm: MEAS:VOLT:AC? -> v
                RECORD $f, $v
            END
            """;

        await ui.RunAsync(() => SequenceRunner.RunAsync(
            script,
            model => model.StartsWith("SDG", StringComparison.OrdinalIgnoreCase) ? gen : dmm,
            (_, _) => { lock (output) output.Add(Environment.CurrentManagedThreadId); },
            _ => { lock (records) records.Add(Environment.CurrentManagedThreadId); },
            CancellationToken.None));

        Assert.NotEmpty(output);
        Assert.NotEmpty(records);

        // Both callbacks matter: output goes to a RichTextBox and RECORD to a ListView, and
        // each throws just as readily from the wrong thread.
        Assert.Contains(output, t => t != ui.ManagedThreadId);
        Assert.Contains(records, t => t != ui.ManagedThreadId);
    }

    /// <summary>
    /// A run that suspends sends exactly what a run that does not sends. The fix is about
    /// which thread reports the commands, and must not change the commands.
    /// </summary>
    [Fact]
    public async Task Suspending_between_commands_changes_nothing_about_what_is_sent()
    {
        var inline = new FakeInstrumentClient { Yields = false };
        var yielding = new FakeInstrumentClient { Yields = true };

        const string script = "*IDN?\r\nC1:OUTP ON\r\nREPEAT 2\r\n*IDN?\r\nEND";

        await ScriptRunner.RunAsync(script, inline, (_, _) => { }, _ => { }, CancellationToken.None);
        await ScriptRunner.RunAsync(script, yielding, (_, _) => { }, _ => { }, CancellationToken.None);

        Assert.Equal(inline.Log, yielding.Log);
    }

    /// <summary>
    /// One dedicated thread with a posting <see cref="SynchronizationContext"/> — the shape a
    /// WinForms UI thread has. Work started here resumes here, unless the awaited code opted
    /// out with <c>ConfigureAwait(false)</c>, which is exactly the case under test.
    /// </summary>
    private sealed class UiLikeThread : IDisposable
    {
        private readonly BlockingCollection<Action> _work = new();
        private readonly Thread _thread;

        public int ManagedThreadId { get; }

        public UiLikeThread()
        {
            using var ready = new ManualResetEventSlim();

            _thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new PumpContext(_work));
                ready.Set();
                foreach (Action a in _work.GetConsumingEnumerable()) a();
            })
            {
                IsBackground = true,
                Name = "ui-like",
            };

            _thread.Start();
            ready.Wait();
            ManagedThreadId = _thread.ManagedThreadId;
        }

        /// <summary>Start <paramref name="work"/> on this thread and wait for it to finish.</summary>
        public Task RunAsync(Func<Task> work)
        {
            var done = new TaskCompletionSource();

            _work.Add(async () =>
            {
                try { await work(); done.SetResult(); }
                catch (Exception ex) { done.SetException(ex); }
            });

            return done.Task;
        }

        public void Dispose()
        {
            _work.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _work.Dispose();
        }

        private sealed class PumpContext : SynchronizationContext
        {
            private readonly BlockingCollection<Action> _work;
            public PumpContext(BlockingCollection<Action> work) => _work = work;

            public override void Post(SendOrPostCallback d, object? state) => _work.Add(() => d(state));

            public override void Send(SendOrPostCallback d, object? state)
            {
                using var done = new ManualResetEventSlim();
                _work.Add(() => { d(state); done.Set(); });
                done.Wait();
            }
        }
    }
}
