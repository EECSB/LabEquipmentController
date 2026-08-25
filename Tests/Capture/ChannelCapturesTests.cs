using System;
using System.Collections.Generic;
using System.Linq;

using System.Threading;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// Reading several channels of one capture.
///
/// The rules worth holding are the ones that came off the bench rather than out of a
/// specification: a channel that cannot be read must not take the others down with it, and a
/// scope asked for a channel it does not have does not always refuse — the DS2202 answers with
/// two samples against fourteen hundred on the time base, which is why a length that disagrees
/// with the first is treated as an absent channel rather than drawn.
/// </summary>
public class ChannelCapturesTests
{
    /// <summary>
    /// A Rigol that hands over a different record per channel.
    ///
    /// Enough of the dialect to be read by <see cref="WaveformReader"/>: the ten-field preamble,
    /// then a definite-length block of one byte per sample. Which channel is being asked about is
    /// taken from the last <c>:WAVeform:SOURce</c> it was sent, which is how the real exchange
    /// works — the source is set, then the preamble and the data are asked for.
    /// </summary>
    private sealed class Scope : IInstrumentClient
    {
        private readonly IReadOnlyDictionary<int, int> _samplesPerChannel;
        private readonly ISet<int> _refuse;
        private int _source = 1;

        public Scope(IReadOnlyDictionary<int, int> samplesPerChannel, ISet<int>? refuse = null)
        {
            _samplesPerChannel = samplesPerChannel;
            _refuse = refuse ?? new HashSet<int>();
        }

        public List<int> Asked { get; } = new();

        public string Host => "fake-scope";
        public string Description => "fake-scope";
        public bool IsConnected => true;
        public int TimeoutMs { get; set; } = 5000;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ReturnToLocalAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() { }
        public void Dispose() { }

        public Task SendAsync(string command, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (command.Contains("SOUR", StringComparison.OrdinalIgnoreCase))
            {
                string digits = new([.. command.Where(char.IsDigit)]);
                if (int.TryParse(digits, out int n)) { _source = n; Asked.Add(n); }
            }
            return Task.CompletedTask;
        }

        public Task<string> QueryAsync(string command, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_refuse.Contains(_source))
                throw new System.IO.IOException($"channel {_source} is not available");

            // format,type,points,count,xincrement,xorigin,xreference,yincrement,yorigin,yreference
            int points = Samples();
            return Task.FromResult($"0,0,{points},1,2e-06,0,0,0.04,0,128");
        }

        /// <summary>
        /// The sample bytes, and only those.
        ///
        /// A real client hands back the payload of the IEEE 488.2 block with the "#N…" header
        /// already taken off — that is ScpiClient.ReadBlockAsync's job, not the caller's. A fake
        /// that returned the header too would be testing the reader against a protocol nothing
        /// speaks, and every trace would come out six samples long.
        /// </summary>
        public Task<byte[]> QueryBinaryAsync(string command, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_refuse.Contains(_source))
                throw new System.IO.IOException($"channel {_source} is not available");

            int points = Samples();
            var data = new byte[points];
            for (int i = 0; i < points; i++) data[i] = (byte)(128 + (i % 7) * _source);
            return Task.FromResult(data);
        }

        private int Samples() => _samplesPerChannel.TryGetValue(_source, out int n) ? n : 0;
    }

    private static Scope TwoChannelRigol() => new(new Dictionary<int, int>
    {
        [1] = 1400,
        [2] = 1400,
        // What a two-channel DS2202 actually does when asked for a third: it answers.
        [3] = 2,
        [4] = 2,
    });

    [Fact]
    public async Task Reads_every_channel_it_is_asked_for()
    {
        var reads = await ChannelCaptures.ReadAsync(TwoChannelRigol(), WaveformDialect.Rigol, [1, 2]);

        Assert.Equal([1, 2], reads.Select(r => r.Channel));
        Assert.All(reads, r => Assert.True(r.Ok));
        Assert.All(reads, r => Assert.Equal(1400, r.Capture!.Samples.Count));
    }

    ///
    ///The order asked for is the order returned, so a legend and a CSV can rely on it.
    ///
    [Fact]
    public async Task Keeps_the_order_it_was_given()
    {
        var reads = await ChannelCaptures.ReadAsync(TwoChannelRigol(), WaveformDialect.Rigol, [2, 1]);

        Assert.Equal([2, 1], reads.Select(r => r.Channel));
    }

    ///
    ///The bench behaviour this exists for: a channel that is not there answers, briefly, and must
    ///not be drawn as a flat line across a channel the instrument does not have.
    ///
    [Fact]
    public async Task A_channel_that_answers_too_briefly_is_not_a_channel()
    {
        var reads = await ChannelCaptures.ReadAsync(TwoChannelRigol(), WaveformDialect.Rigol, [1, 3]);

        Assert.True(reads[0].Ok);
        Assert.False(reads[1].Ok);
        Assert.Contains("2 samples against 1,400", reads[1].Error);
        Assert.Contains("no such channel", reads[1].Error);
    }

    ///
    ///And one that refuses outright fails on its own: the others are still read, because asking a
    ///two-channel scope for four should give back the two it has.
    ///
    [Fact]
    public async Task A_channel_that_refuses_does_not_take_the_others_with_it()
    {
        var scope = new Scope(
            new Dictionary<int, int> { [1] = 1400, [2] = 1400 },
            new HashSet<int> { 2 });

        var reads = await ChannelCaptures.ReadAsync(scope, WaveformDialect.Rigol, [1, 2]);

        Assert.True(reads[0].Ok);
        Assert.False(reads[1].Ok);
        Assert.Contains("not available", reads[1].Error);
    }

    ///
    ///Every channel of one capture shares a time base, which is what lets a caller keep one time
    ///axis rather than a copy per channel. Same times, whichever channel they came off.
    ///
    [Fact]
    public async Task Every_channel_is_sampled_against_the_same_times()
    {
        var reads = await ChannelCaptures.ReadAsync(TwoChannelRigol(), WaveformDialect.Rigol, [1, 2]);

        double[] first = [.. reads[0].Capture!.Samples.Select(s => s.Time)];
        double[] second = [.. reads[1].Capture!.Samples.Select(s => s.Time)];
        Assert.Equal(first, second);
    }

    ///
    ///Cancelling cancels the capture rather than being reported as one channel's failure — which
    ///would leave the channels after it being read after the caller had gone.
    ///
    [Fact]
    public async Task Cancelling_stops_the_capture_rather_than_failing_a_channel()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ChannelCaptures.ReadAsync(TwoChannelRigol(), WaveformDialect.Rigol, [1, 2], cts.Token));
    }
}
