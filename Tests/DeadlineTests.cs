using LabEquipmentController;

namespace LabEquipmentController.Tests;

/// <summary>
/// The one thing these pin down: a deadline that fires and a user who cancels must not
/// come out of the same door. They used to, which is how a connect to a switched-off
/// instrument was reported as "cancelled".
/// </summary>
public class DeadlineTests
{
    private static Task Forever(CancellationToken t) => Task.Delay(Timeout.Infinite, t);

    [Fact]
    public async Task A_deadline_that_fires_is_a_timeout()
    {
        await Assert.ThrowsAsync<TimeoutException>(
            () => Deadline.RunAsync(Forever, 40, "the instrument did not answer",
                                    CancellationToken.None));
    }

    [Fact]
    public async Task The_timeout_says_what_went_quiet_and_how_long_it_waited()
    {
        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(
            () => Deadline.RunAsync(Forever, 40, "192.168.1.7:5025 did not answer",
                                    CancellationToken.None));

        Assert.Equal("192.168.1.7:5025 did not answer within 40 ms.", ex.Message);
    }

    [Fact]
    public async Task A_cancel_stays_a_cancel()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(30);

        // Deliberately a long deadline: the only thing that can end this is the caller.
        OperationCanceledException ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Deadline.RunAsync(Forever, 30_000, "the instrument did not answer", cts.Token));

        Assert.IsNotType<TimeoutException>(ex);   // ... and TimeoutException isn't one anyway
    }

    [Fact]
    public async Task A_cancel_landing_in_the_same_instant_as_the_deadline_wins_the_tie()
    {
        // Both ended it, so either answer is true. "Cancelled" is the one to say: the user
        // did press Cancel, and being told the thing you just stopped was stopped beats
        // being told it timed out. Pinned here because it is a choice, not an accident.
        using var cts = new CancellationTokenSource();

        Task run = Deadline.RunAsync(async t =>
        {
            try { await Task.Delay(Timeout.Infinite, t); }
            catch (OperationCanceledException) { cts.Cancel(); throw; }
        }, 40, "the instrument did not answer", cts.Token);

        OperationCanceledException ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.IsNotType<TimeoutException>(ex);
    }

    [Fact]
    public async Task An_attempt_that_finishes_in_time_is_left_alone()
    {
        bool ran = false;
        await Deadline.RunAsync(_ => { ran = true; return Task.CompletedTask; }, 30_000,
                                "the instrument did not answer", CancellationToken.None);

        Assert.True(ran);
    }

    [Fact]
    public async Task An_ordinary_failure_passes_through_unchanged()
    {
        // Connection refused, host unreachable, a malformed reply — none of these are the
        // deadline's business, and dressing them up as a timeout would misdirect the reader.
        await Assert.ThrowsAsync<IOException>(
            () => Deadline.RunAsync(_ => throw new IOException("connection refused"), 30_000,
                                    "the instrument did not answer", CancellationToken.None));
    }

    [Fact]
    public async Task The_attempt_is_handed_a_token_that_cancels_at_the_deadline()
    {
        CancellationToken seen = default;

        await Assert.ThrowsAsync<TimeoutException>(
            () => Deadline.RunAsync(t => { seen = t; return Forever(t); }, 40,
                                    "the instrument did not answer", CancellationToken.None));

        Assert.True(seen.IsCancellationRequested);
    }

    [Fact]
    public async Task The_callers_token_is_not_disturbed()
    {
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<TimeoutException>(
            () => Deadline.RunAsync(Forever, 40, "the instrument did not answer", cts.Token));

        Assert.False(cts.IsCancellationRequested);
    }
}
