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
            await ScriptRunner.RunAsync(req.Script, session.Client, output, record, ct));
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
                output, record, ct));
        return new RunSummary(runId, columns, false, null);
    }

    private void Launch(
        string runId,
        IReadOnlyList<string> columns,
        Func<Action<string, ScriptOutputKind>, Action<SequenceRow>, CancellationToken, Task> run)
    {
        var cts = new CancellationTokenSource();
        _runs[runId] = cts;

        _ = Task.Run(async () =>
        {
            bool failed = false;
            try
            {
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
                _runs.TryRemove(runId, out _);
                cts.Dispose();
            }
        });
    }
}
