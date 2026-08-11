using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController.Tests;

public class SerializedInstrumentClientTests
{
    /// <summary>
    /// Records whether two exchanges were ever in flight at once, and can be told to fail or
    /// to block until released.
    /// </summary>
    private sealed class OverlapSpy : IInstrumentClient
    {
        private int _inFlight;

        public int MaxConcurrent;
        public int Connects;
        public readonly List<string> Log = new();
        public string? ThrowOn;
        public TaskCompletionSource? Hold;

        public string Host => "spy";
        public string Description => "spy";
        public bool IsConnected => true;
        public int TimeoutMs { get; set; }

        public Task ConnectAsync(CancellationToken ct = default)
        {
            Connects++;
            return Task.CompletedTask;
        }

        public async Task SendAsync(string command, CancellationToken ct = default)
            => await Enter(command, ct).ConfigureAwait(false);

        public async Task<string> QueryAsync(string command, CancellationToken ct = default)
        {
            await Enter(command, ct).ConfigureAwait(false);
            return "reply:" + command;
        }

        public async Task<byte[]> QueryBinaryAsync(string command, CancellationToken ct = default)
        {
            await Enter(command, ct).ConfigureAwait(false);
            return Array.Empty<byte>();
        }

        public Task ReturnToLocalAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() { }
        public void Dispose() { }

        private async Task Enter(string command, CancellationToken ct)
        {
            int now = Interlocked.Increment(ref _inFlight);
            lock (Log)
            {
                Log.Add(command);
                MaxConcurrent = Math.Max(MaxConcurrent, now);
            }
            try
            {
                if (Hold != null) await Hold.Task.ConfigureAwait(false);
                else await Task.Yield();
                if (command == ThrowOn) throw new InvalidOperationException("transport failed");
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    [Fact]
    public async Task Overlapping_calls_never_share_the_connection()
    {
        var spy = new OverlapSpy();
        using var client = new SerializedInstrumentClient(spy);

        // Twenty queries fired without awaiting — what holding Enter on a query looks like.
        var calls = new List<Task>();
        for (int i = 0; i < 20; i++) calls.Add(client.QueryAsync("Q" + i));
        await Task.WhenAll(calls);

        Assert.Equal(1, spy.MaxConcurrent);
        Assert.Equal(20, spy.Log.Count);
    }

    [Fact]
    public async Task Queued_callers_are_counted_while_they_wait()
    {
        var spy = new OverlapSpy { Hold = new TaskCompletionSource() };
        using var client = new SerializedInstrumentClient(spy);

        Task first = client.QueryAsync("A");
        Task second = client.QueryAsync("B");
        Task third = client.QueryAsync("C");

        Assert.Equal(3, client.Pending);      // one on the wire, two waiting their turn

        spy.Hold.SetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal(0, client.Pending);
    }

    [Fact]
    public async Task A_failed_exchange_forces_a_reconnect_before_the_next_one()
    {
        var spy = new OverlapSpy { ThrowOn = "BAD" };
        using var client = new SerializedInstrumentClient(spy);

        await client.QueryAsync("GOOD");
        Assert.Equal(0, spy.Connects);        // nothing wrong yet, so nothing to put right

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAsync("BAD"));

        // The reply to BAD may still be in the pipe; the next caller must not read it as its
        // own answer, so the connection is remade first.
        await client.QueryAsync("NEXT");
        Assert.Equal(1, spy.Connects);
    }

    [Fact]
    public async Task The_reconnect_happens_once_not_before_every_later_call()
    {
        var spy = new OverlapSpy { ThrowOn = "BAD" };
        using var client = new SerializedInstrumentClient(spy);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAsync("BAD"));
        await client.QueryAsync("ONE");
        await client.QueryAsync("TWO");

        Assert.Equal(1, spy.Connects);
    }

    [Fact]
    public async Task Cancelling_while_queued_gives_the_count_back()
    {
        var spy = new OverlapSpy { Hold = new TaskCompletionSource() };
        using var client = new SerializedInstrumentClient(spy);
        using var cts = new CancellationTokenSource();

        Task first = client.QueryAsync("A");
        Task waiting = client.QueryAsync("B", cts.Token);
        Assert.Equal(2, client.Pending);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, client.Pending);       // only the one still on the wire

        spy.Hold.SetResult();
        await first;
        Assert.Equal(0, client.Pending);
        Assert.DoesNotContain("B", spy.Log);   // it never reached the instrument
    }
}
