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
    /// <summary>How long a finished run stays readable, and how many runs are kept at most.</summary>
    internal static readonly TimeSpan Kept = TimeSpan.FromHours(1);
    internal const int MostKept = 200;

    private readonly BenchService _bench;
    private readonly IHubContext<BenchHub> _hub;
    private readonly ILogger<RunService> _log;
    private readonly ServiceMode? _service;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runs = new();

    /// <summary>
    /// Every run this server has started and not yet forgotten — the running ones and the last
    /// hour's finished ones — so a run can be read after the fact (<see cref="Record"/>).
    /// </summary>
    private readonly ConcurrentDictionary<string, RunRecord> _records = new();

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

    public RunService(BenchService bench, IHubContext<BenchHub> hub, ILogger<RunService> log, ServiceMode? service = null)
        => (_bench, _hub, _log, _service) = (bench, hub, log, service);

    public bool Stop(string runId)
    {
        if (!_runs.TryGetValue(runId, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    public IReadOnlyList<string> Active => _runs.Keys.ToList();

    /// <summary>One run as it stands, running or ended, or null once it has been forgotten.</summary>
    public RunRecordDto? Record(string runId)
        => _records.TryGetValue(runId, out var record) ? record.Snapshot() : null;

    /// <summary>Every run still remembered, newest first.</summary>
    public IReadOnlyList<RunRecordDto> Records()
        => _records.Values.Select(r => r.Snapshot()).OrderByDescending(r => r.StartedAt).ToList();

    public RunSummary StartScript(ScriptRunRequest req)
    {
        var session = _bench.Raw(req.SessionId);
        if (session is null)
            return new RunSummary("", [], true, "No such session.");

        // In service mode a held instrument is held against other runs too, not only against its
        // console: two runs interleaving on one link is the thing a hold exists to prevent, and a
        // host's second request is as able to cause it as a second person.
        if (_service?.Enabled == true && _bench.IsDriven(req.SessionId))
            return new RunSummary("", [], true, ServiceMode.HeldMessage);

        var columns = ScriptRunner.Columns(req.Script);
        string runId = Guid.NewGuid().ToString("N");
        Launch(runId, "script", columns, async (output, record, ct) =>
            await ScriptRunner.RunAsync(req.Script, session.Client, output, record, ct),
            req.SessionId);
        return new RunSummary(runId, columns, false, null);
    }

    public RunSummary StartSequence(SequenceRunRequest req)
    {
        // Before an instrument is looked at, let alone held: a value that is not what its own
        // INPUT line declared is the caller's mistake, and nothing on the bench has to be
        // touched to say so. The runner checks it again, so a caller that reaches past this
        // does not reach past the check.
        if (!SequenceRunner.TryBindInputs(req.Script, req.Inputs, out _, out string? inputError))
            return new RunSummary("", [], true, inputError);

        var required = SequenceRunner.Requirements(req.Script);
        var clients = new Dictionary<string, IInstrumentClient>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, sessionId) in req.Bindings)
        {
            var s = _bench.Raw(sessionId);
            if (s is null) return new RunSummary("", [], true, $"'{alias}' is bound to a session that is not open.");
            if (_service?.Enabled == true && _bench.IsDriven(sessionId))
                return new RunSummary("", [], true,
                    $"'{alias}' is bound to an instrument a run holds, and it takes nothing else until that run ends.");
            clients[alias] = s.Client;
        }

        // Report every missing alias at once. Failing on the first means the user fixes
        // one, runs again, and is told about the next.
        var missing = required.Where(r => !clients.ContainsKey(r.Alias)).ToList();
        if (missing.Count > 0)
            return new RunSummary("", [], true,
                "This script needs an instrument for: " +
                string.Join(", ", missing.Select(m => $"{m.Alias} ({m.Model})")) + ".");

        // Resolved by alias, because that is what the page bound: each row of its table is an
        // alias and the session playing it. Looked up by model instead, an alias not spelled
        // like its model finds nothing, and two meters of one model are one meter.
        var columns = SequenceRunner.Columns(req.Script);
        string runId = Guid.NewGuid().ToString("N");
        Launch(runId, "sequence", columns, async (output, record, ct) =>
            await SequenceRunner.RunAsync(req.Script,
                (alias, _) => clients.TryGetValue(alias, out var c) ? c : null,
                output, record, ct, req.Inputs,
                req.TimeoutSeconds is > 0 ? TimeSpan.FromSeconds(req.TimeoutSeconds.Value) : null),
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
        string kind,
        IReadOnlyList<string> columns,
        Func<Action<string, ScriptOutputKind>, Action<SequenceRow>, CancellationToken, Task> run,
        params string[] drives)
    {
        var cts = new CancellationTokenSource();
        _runs[runId] = cts;

        // Remembered from the start, so a caller that asks a moment after starting it finds it
        // running rather than unknown; and the old ones let go of at the same time.
        var record = new RunRecord(runId, kind, columns);
        _records[runId] = record;
        Forget();

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

                void Output(string line, ScriptOutputKind outputKind)
                {
                    if (outputKind == ScriptOutputKind.Error) failed = true;
                    var said = new ScriptOutputLine(line, outputKind.ToString());
                    record.Add(said);
                    // Fire-and-forget: a script must not stall because a browser is slow to
                    // read. The run is the thing that matters; the log is a view of it.
                    _ = _hub.Clients.Group(runId).SendAsync("RunOutput", runId, said, CancellationToken.None);
                }

                void Record(SequenceRow row)
                {
                    var recorded = new RecordedRow(row.Values);
                    record.Add(recorded);
                    _ = _hub.Clients.Group(runId).SendAsync("RunRow", runId, recorded, CancellationToken.None);
                }

                await run(Output, Record, cts.Token);
                record.End(failed ? "failed" : "finished", null);
                await _hub.Clients.Group(runId).SendAsync("RunFinished", runId, failed, (string?)null, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                record.End("stopped", "Stopped.");
                await _hub.Clients.Group(runId).SendAsync("RunFinished", runId, true, "Stopped.", CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Run {RunId} failed", runId);
                record.End("failed", ex.Message);
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

    /// <summary>
    /// Let go of the runs nobody will ask about again: ended more than <see cref="Kept"/> ago, and
    /// past that the oldest ended ones once more than <see cref="MostKept"/> are held. A running
    /// run is never forgotten, however long it has run.
    /// </summary>
    private void Forget()
    {
        var now = DateTimeOffset.UtcNow;
        var ended = _records.Values
            .Where(r => r.EndedAt is not null)
            .OrderBy(r => r.EndedAt)
            .ToList();

        foreach (var old in ended.Where(r => now - r.EndedAt!.Value > Kept))
            _records.TryRemove(old.RunId, out _);

        int over = _records.Count - MostKept;
        foreach (var old in ended.Where(r => _records.ContainsKey(r.RunId)).Take(Math.Max(0, over)))
            _records.TryRemove(old.RunId, out _);
    }

    /// <summary>
    /// One run's story, kept as it is told. Bounded, because a REPEAT with no end says a great deal,
    /// and a record that grew without limit would be the one thing on this server that did.
    /// </summary>
    private sealed class RunRecord(string runId, string kind, IReadOnlyList<string> columns)
    {
        private const int Most = 20_000;
        private readonly Lock _lock = new();
        private readonly List<ScriptOutputLine> _output = [];
        private readonly List<RecordedRow> _rows = [];
        private string _status = "running";
        private string? _error;

        public string RunId { get; } = runId;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? EndedAt { get; private set; }

        public void Add(ScriptOutputLine line)
        {
            lock (_lock) if (_output.Count < Most) _output.Add(line);
        }

        public void Add(RecordedRow row)
        {
            lock (_lock) if (_rows.Count < Most) _rows.Add(row);
        }

        public void End(string status, string? error)
        {
            lock (_lock)
            {
                if (EndedAt is not null) return;
                _status = status;
                _error = error;
                EndedAt = DateTimeOffset.UtcNow;
            }
        }

        public RunRecordDto Snapshot()
        {
            lock (_lock)
                return new RunRecordDto(RunId, kind, _status, columns, StartedAt, EndedAt, _error,
                                        [.. _output], [.. _rows]);
        }
    }
}
