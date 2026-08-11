using System;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// Give an operation a deadline, and keep the two ways it can end apart.
///
/// The usual shape — a linked token with <c>CancelAfter</c> — collapses them: the clock
/// running out and the user pressing Cancel both cancel that token and both arrive as the
/// same <see cref="OperationCanceledException"/>. That is how a connect to a switched-off
/// instrument came to be reported as "Connection to 192.168.1.7 cancelled", a sentence that
/// says the user stopped something they never started.
///
/// The caller's own token is what tells them apart. If it is still uncancelled when the
/// attempt is cut short, the deadline is what fired — a failure to report, not a
/// cancellation to accept quietly.
/// </summary>
public static class Deadline
{
    /// <summary>
    /// Run <paramref name="attempt"/> with <paramref name="timeoutMs"/> to finish in.
    /// A timeout raises <see cref="TimeoutException"/>; a cancellation by
    /// <paramref name="ct"/> comes through untouched, as does any other failure.
    ///
    /// If both happen at once — the clock runs out in the same instant the user presses
    /// Cancel — it counts as a cancellation. Both are true, and telling someone the thing
    /// they just stopped was stopped reads better than telling them it timed out.
    /// </summary>
    /// <param name="late">
    /// What went quiet, phrased to be read before " within N ms." — e.g.
    /// "192.168.1.7:5025 did not answer".
    /// </param>
    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> attempt, int timeoutMs,
                                            string late, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeoutMs);

        try
        {
            return await attempt(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{late} within {timeoutMs} ms.");
        }
    }

    /// <summary>As above, for an attempt with nothing to return.</summary>
    public static Task RunAsync(Func<CancellationToken, Task> attempt, int timeoutMs,
                                string late, CancellationToken ct)
        => RunAsync<object?>(async t =>
           {
               await attempt(t).ConfigureAwait(false);
               return null;
           }, timeoutMs, late, ct);
}
