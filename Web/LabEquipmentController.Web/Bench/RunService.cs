using System.Collections.Concurrent;
using LabEquipmentController.Web.Client.Contracts;
using LabEquipmentController.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace LabEquipmentController.Web.Bench;

/// <summary>
/// Runs scripts and multi-instrument sequences, pushing each line and each recorded row to
/// the browser as it happens.
/// </summary>
/// <remarks>
/// A sweep can take twenty minutes, so the HTTP request that starts one returns
/// immediately with a run id and the output arrives over SignalR. Holding a request open
/// for the length of a measurement would break on every proxy between here and the browser,
/// and would give the user nothing to watch in the meantime.
///
/// Runs are keyed and cancellable, so Stop means stop — the same guarantee the desktop
/// app's Stop button gives, and for the same reason: a script that cannot be stopped is a
/// script driving equipment nobody can interrupt.
/// </remarks>
public sealed class RunService
{
    private readonly BenchService _bench;
    private readonly IHubContext<BenchHub> _hub;
    private readonly ILogger<RunService> _log;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runs = new();

    /// <summary>
    /// Runs that have been started and are waiting for someone to listen.
    ///
    /// The output goes to a SignalR group named after the run, and the browser can only join
    /// that group once it knows the run's id — which it learns from the reply to the request
    /// that started it. So there is a gap between the run beginning and anyone being in the
    /// group, and a script that finishes inside that gap says everything it has to say to an
    /// empty room. "*IDN?" against a meter takes about eight milliseconds; the round trip is
    /// longer, and the whole run — every line and the finish — was being lost.
    /// </summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _listeners = new();

    /// <summary>Someone has joined this run's group. Called by the hub, from Watch.</summary>
    internal void Listening(string runId)
    {
        if (_listeners.TryGetValue(runId, out var waiting)) waiting.TrySetResult();
    }

    public RunService(BenchService bench, IHubContext<BenchHub> hub, ILogger<RunService> log)
        => (_bench, _hub, _log) = (bench, hub, log);

    public bool Stop(string runId)
    {
        if (!_runs.TryGetValue(runId, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    public IReadOnlyList<string> Active => _runs.Keys.ToList();

    public RunSummary StartScript(ScriptRunRequest req)
    {
        var session = _bench.Raw(req.SessionId);
        if (session is null)
            return new RunSummary("", [], true, "No such session.");

        var columns = ScriptRunner.Columns(req.Script);
        string runId = Guid.NewGuid().ToString("N");
        Launch(runId, columns, async (output, record, ct) =>
            await ScriptRunner.RunAsync(req.Script, session.Client, output, record, ct),
            req.SessionId);
        return new RunSummary(runId, columns, false, null);
    }

    public RunSummary StartSequence(SequenceRunRequest req)
    {
        var required = SequenceRunner.Requirements(req.Script);
        var clients = new Dictionary<string, IInstrumentClient>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, sessionId) in req.Bindings)
        {
            var s = _bench.Raw(sessionId);
            if (s is null) return new RunSummary("", [], true, $"'{alias}' is bound to a session that is not open.");
            clients[alias] = s.Client;
        }

        // Report every missing alias at once. Failing on the first means the user fixes
        // one, runs again, and is told about the next.
        var missing = required.Where(r => !clients.ContainsKey(r.Alias)).ToList();
        if (missing.Count > 0)
            return new RunSummary("", [], true,
                "This script needs an instrument for: " +
                string.Join(", ", missing.Select(m => $"{m.Alias} ({m.Model})")) + ".");

        var columns = SequenceRunner.Columns(req.Script);
        string runId = Guid.NewGuid().ToString("N");
        Launch(runId, columns, async (output, record, ct) =>
            await SequenceRunner.RunAsync(req.Script,
                alias => clients.TryGetValue(alias, out var c) ? c : null,
                output, record, ct),
            [.. req.Bindings.Values]);
        return new RunSummary(runId, columns, false, null);
    }

    /// <param name="drives">
    /// The sessions this run holds for its duration. Their consoles lock themselves out while
    /// it runs, which is what the desktop does with <c>Session.IsBusy</c> — and a sequence
    /// takes every instrument it binds, as SequenceForm does, because a command typed into any
    /// one of them lands between two steps of the same script.
    /// </param>
    private void Launch(
        string runId,
        IReadOnlyList<string> columns,
        Func<Action<string, ScriptOutputKind>, Action<SequenceRow>, CancellationToken, Task> run,
        params string[] drives)
    {
        var cts = new CancellationTokenSource();
        _runs[runId] = cts;

        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _listeners[runId] = listening;

        // Taken here rather than inside the task, so an instrument is held from the moment the
        // caller is told the run started. Between those two points the console would otherwise
        // still be typeable, which is a race with a small window and a confusing failure.
        var hold = _bench.Drive(drives);

        _ = Task.Run(async () =>
        {
            bool failed = false;
            try
            {
                // Wait for the watcher before sending anything. The pause is the length of one
                // round trip for a browser — it already has the id and is joining the group as
                // this runs. The two seconds are for a caller that never listens at all, which
                // is anything driving the API directly: it waits, once, and gets on with it.
                try { await Task.WhenAny(listening.Task, Task.Delay(2000, cts.Token)); }
                finally { _listeners.TryRemove(runId, out _); }

                await _hub.Clients.Group(runId).SendAsync("RunStarted", runId, columns, cts.Token);

                void Output(string line, ScriptOutputKind kind)
                {
                    if (kind == ScriptOutputKind.Error) failed = true;
                    // Fire-and-forget: a script must not stall because a browser is slow to
                    // read. The run is the thing that matters; the log is a view of it.
                    _ = _hub.Clients.Group(runId).SendAsync("RunOutput", runId,
                        new ScriptOutputLine(line, kind.ToString()), CancellationToken.None);
                }

                void Record(SequenceRow row)
                    => _ = _hub.Clients.Group(runId).SendAsync("RunRow", runId,
                        new RecordedRow(row.Values), CancellationToken.None);

                await run(Output, Record, cts.Token);
                await _hub.Clients.Group(runId).SendAsync("RunFinished", runId, failed, (string?)null, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                await _hub.Clients.Group(runId).SendAsync("RunFinished", runId, true, "Stopped.", CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Run {RunId} failed", runId);
                await _hub.Clients.Group(runId).SendAsync("RunFinished", runId, true, ex.Message, CancellationToken.None);
            }
            finally
            {
                hold.Dispose();
                _runs.TryRemove(runId, out _);
                cts.Dispose();
            }
        });
    }
}
