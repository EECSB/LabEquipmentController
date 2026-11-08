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

    /// <summary>
    /// How much one read of a run answers at most, of its lines and of its rows each: twenty thousand,
    /// which is what a whole record held until 2026-09-23, and no more than 8 MiB, so a table of wide
    /// rows is read a piece at a time rather than all at once. <c>OutputKept</c> and <c>RowsKept</c> in
    /// the answer say how many there are, so a reader knows when it has them all (<see cref="RunRecordDto"/>).
    /// </summary>
    /// <remarks>
    /// What a record keeps is bounded by the disk now rather than by this, and a run that goes past
    /// <see cref="RunFiles.MostBytes"/> goes on with the rest counted rather than kept.
    /// </remarks>
    internal const int MostPerRead = 20_000;
    internal const int MostBytesPerRead = 8 * 1024 * 1024;

    private readonly BenchService _bench;
    private readonly IHubContext<BenchHub> _hub;
    private readonly ILogger<RunService> _log;
    private readonly ServiceMode? _service;
    private readonly RunFiles _files;
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

    /// <summary>
    /// Ids a caller asked to stop when no run had them, and when it asked. A start under one of
    /// them is refused rather than begun (<see cref="StartSequence"/>), for as long as a finished
    /// run is kept and no more than as many.
    /// </summary>
    /// <remarks>
    /// This is how a host closes a start it never heard back from. It chose the run's id, so it
    /// asks for the run by it; while nothing has the id, the start may still be on its way here —
    /// queued behind a slow answer on a dial-out connection, or in a request this server has not
    /// got round to — and would begin a run nobody is waiting for. Stopping the id first means it
    /// never will.
    /// </remarks>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _stoppedBefore = new();

    /// <summary>
    /// Held while a start under a caller's id looks the id up and takes it, and while a stop marks
    /// an id nothing has: a start and a stop that arrive together for one id come out as one or the
    /// other, never as a run the stop missed.
    /// </summary>
    private readonly Lock _ids = new();

    public const string StoppedBeforeMessage =
        "A stop for this run arrived before its start did, so it was not begun.";

    public const string RunIdMessage =
        "A run's id is 32 lowercase hexadecimal digits, a GUID written without its dashes, and not the empty one.";

    /// <summary>Someone has joined this run's group. Called by the hub, from Watch.</summary>
    internal void Listening(string runId)
    {
        if (_listeners.TryGetValue(runId, out var waiting)) waiting.TrySetResult();
    }

    /// <param name="files">
    /// Where the records keep what the runs say and record. The server's own is under its data
    /// folder; one made without it, which is a test, shares one under the temporary folder.
    /// </param>
    public RunService(BenchService bench, IHubContext<BenchHub> hub, ILogger<RunService> log, ServiceMode? service = null, RunFiles? files = null)
        => (_bench, _hub, _log, _service, _files) = (bench, hub, log, service, files ?? RunFiles.Temporary());

    /// <summary>
    /// Stop a run that is going; false for one that is not. An id no run has, running or kept, is
    /// remembered as stopped when it is one a caller could have chosen, so a start under it that
    /// arrives later is refused (<see cref="_stoppedBefore"/>).
    /// </summary>
    public bool Stop(string runId)
    {
        CancellationTokenSource? cts;
        lock (_ids)
        {
            if (!_runs.TryGetValue(runId, out cts))
            {
                if (IsCallersId(runId) && !_records.ContainsKey(runId))
                {
                    _stoppedBefore[runId] = DateTimeOffset.UtcNow;
                    ForgetStops();
                }
                return false;
            }
        }

        // Outside the lock: a cancellation runs whatever was waiting on it, and none of that is
        // the ids' business.
        cts.Cancel();
        return true;
    }

    /// <summary>
    /// Whether an id is one a caller may choose for a run: a GUID written as this server writes its
    /// own, 32 lowercase hexadecimal digits, and not the empty one. Nothing else is taken, since an
    /// id names the run's files on this server's disk and its group on the hub.
    /// </summary>
    public static bool IsCallersId(string? runId)
        => Guid.TryParseExact(runId, "N", out var id) && id != Guid.Empty && id.ToString("N") == runId;

    public IReadOnlyList<string> Active => _runs.Keys.ToList();

    /// <summary>
    /// One run as it stands, running or ended, or null once it has been forgotten — from line
    /// <paramref name="outputFrom"/> and row <paramref name="rowsFrom"/> on, which is what a reader
    /// following the run has not read yet, as much of it as one read answers (<see cref="RunRecordDto"/>).
    /// </summary>
    public RunRecordDto? Record(string runId, int outputFrom = 0, int rowsFrom = 0)
        => _records.TryGetValue(runId, out var record) ? record.Read(outputFrom, rowsFrom) : null;

    /// <summary>
    /// Every run still remembered, newest first: how each stands and how much it has said and
    /// recorded, without the lines and rows themselves. Those are a read of the run, one at a time;
    /// in one answer for every run at once they could be two hundred runs' worth of disk.
    /// </summary>
    public IReadOnlyList<RunRecordDto> Records()
        => _records.Values.Select(r => r.Summary()).OfType<RunRecordDto>().OrderByDescending(r => r.StartedAt).ToList();

    public RunSummary StartScript(ScriptRunRequest req)
    {
        var session = _bench.Raw(req.SessionId);
        if (session is null)
            return new RunSummary("", [], true, "No such session.");

        // In service mode a held instrument is held against other runs too, not only against its
        // console: two runs interleaving on one link is the thing a hold exists to prevent, and a
        // host's second request is as able to cause it as a second person.
        if (_service?.Driven == true && _bench.IsDriven(req.SessionId))
            return new RunSummary("", [], true, ServiceMode.HeldMessage);

        var columns = ScriptRunner.Columns(req.Script);
        string runId = Guid.NewGuid().ToString("N");
        Launch(runId, "script", columns, async (output, record, ct) =>
            await ScriptRunner.RunAsync(req.Script, session.Client, output, record, ct),
            req.SessionId);
        return new RunSummary(runId, columns, false, null);
    }

    /// <summary>
    /// Start a multi-instrument run, under an id this server makes, or under <paramref name="runId"/>
    /// when the caller chose one (<c>PUT /api/runs/{id}</c>, since 2026-09-29).
    /// </summary>
    /// <remarks>
    /// <strong>A caller's id makes a start safe to lose.</strong> An id this server makes goes back
    /// in the answer to the start and nowhere else, so a host that never hears that answer — the
    /// connection went with it in flight, or it came too late — cannot read the run or stop it,
    /// while the run holds every instrument it binds until its deadline. A host that chose the id
    /// knows it all the same.
    ///
    /// So a start under an id this server already has begins nothing and answers the run it
    /// began: the same start sent twice is one run. And a start under an id a stop reached first
    /// is refused (<see cref="Stop"/>), which is how a host closes a start it stopped waiting for.
    /// </remarks>
    public RunSummary StartSequence(SequenceRunRequest req, string? runId = null)
    {
        if (runId is null) return BeginSequence(req, Guid.NewGuid().ToString("N"));

        if (!IsCallersId(runId)) return new RunSummary("", [], true, RunIdMessage);

        lock (_ids)
        {
            if (_records.TryGetValue(runId, out var known)) return known.Started();
            if (_stoppedBefore.ContainsKey(runId)) return new RunSummary("", [], true, StoppedBeforeMessage);
            return BeginSequence(req, runId);
        }
    }

    private RunSummary BeginSequence(SequenceRunRequest req, string runId)
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
            if (_service?.Driven == true && _bench.IsDriven(sessionId))
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
        var record = new RunRecord(runId, kind, columns, _files);
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
    /// run is never forgotten, however long it has run. What a forgotten run kept goes with it.
    /// </summary>
    private void Forget()
    {
        var now = DateTimeOffset.UtcNow;
        var ended = _records.Values
            .Where(r => r.EndedAt is not null)
            .OrderBy(r => r.EndedAt)
            .ToList();

        foreach (var old in ended.Where(r => now - r.EndedAt!.Value > Kept))
            if (_records.TryRemove(old.RunId, out var gone)) gone.Dispose();

        int over = _records.Count - MostKept;
        foreach (var old in ended.Where(r => _records.ContainsKey(r.RunId)).Take(Math.Max(0, over)))
            if (_records.TryRemove(old.RunId, out var gone)) gone.Dispose();

        ForgetStops();
    }

    /// <summary>
    /// Let go of the ids stopped before any run had them, on the same terms as the runs: older than
    /// <see cref="Kept"/>, and past that the oldest once more than <see cref="MostKept"/> are held. A
    /// start still on its way after an hour is not one anybody is waiting for, and the host that
    /// stopped it has long since said the run did not begin.
    /// </summary>
    private void ForgetStops()
    {
        var now = DateTimeOffset.UtcNow;
        var stopped = _stoppedBefore.OrderBy(s => s.Value).ToList();

        foreach (var old in stopped.Where(s => now - s.Value > Kept))
            _stoppedBefore.TryRemove(old.Key, out _);

        int over = _stoppedBefore.Count - MostKept;
        foreach (var old in stopped.Where(s => _stoppedBefore.ContainsKey(s.Key)).Take(Math.Max(0, over)))
            _stoppedBefore.TryRemove(old.Key, out _);
    }

    /// <summary>
    /// One run's story, kept as it is told, in two files (<see cref="RunFiles"/>). Bounded, because a
    /// REPEAT with no end says a great deal, and a record that grew without limit would be the one
    /// thing on this server that did — and what goes past the bound is counted, because a table cut
    /// short without a word reads as the whole measurement. Until 2026-09-23 it was not counted: a run
    /// that recorded thirty thousand rows was read by its host as twenty thousand, and nothing said so.
    /// And until the same day the bound was those twenty thousand, because the record was in memory.
    /// </summary>
    private sealed class RunRecord(string runId, string kind, IReadOnlyList<string> columns, RunFiles files) : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly KeptList<ScriptOutputLine> _output = files.Open<ScriptOutputLine>(runId + ".lines");
        private readonly KeptList<RecordedRow> _rows = files.Open<RecordedRow>(runId + ".rows");
        private string _status = "running";
        private string? _error;
        private bool _forgotten;

        public string RunId { get; } = runId;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? EndedAt { get; private set; }

        public void Add(ScriptOutputLine line)
        {
            lock (_lock)
            {
                if (!_forgotten) _output.Add(line);
            }
        }

        public void Add(RecordedRow row)
        {
            lock (_lock)
            {
                if (!_forgotten) _rows.Add(row);
            }
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

        /// <summary>
        /// From line <paramref name="outputFrom"/> and row <paramref name="rowsFrom"/> on, as much as
        /// one read answers; null once forgotten, which a reader that found the record just before may meet.
        /// </summary>
        public RunRecordDto? Read(int outputFrom, int rowsFrom)
        {
            lock (_lock)
            {
                if (_forgotten) return null;

                // Past the end is the end: a reader that has everything is answered with nothing new.
                var (lines, linesFrom) = _output.Read(outputFrom, MostPerRead, MostBytesPerRead);
                var (rows, fromRow) = _rows.Read(rowsFrom, MostPerRead, MostBytesPerRead);

                return Dto(lines, rows, linesFrom, fromRow);
            }
        }

        /// <summary>
        /// What its start answered: the run under its id, begun. A start sent again under the same
        /// id is answered this, whatever has become of the run since, which a read of it says.
        /// </summary>
        public RunSummary Started() => new(RunId, columns, false, null);

        /// <summary>How it stands, with none of its lines or rows.</summary>
        public RunRecordDto? Summary()
        {
            lock (_lock)
            {
                return _forgotten ? null : Dto([], [], 0, 0);
            }
        }

        private RunRecordDto Dto(List<ScriptOutputLine> lines, List<RecordedRow> rows, int linesFrom, int fromRow)
            => new(RunId, kind, _status, columns, StartedAt, EndedAt, _error, lines, rows,
                   linesFrom, fromRow, _output.Dropped, _rows.Dropped, _output.Count, _rows.Count);

        public void Dispose()
        {
            lock (_lock)
            {
                _forgotten = true;
                _output.Dispose();
                _rows.Dispose();
            }
        }
    }
}
