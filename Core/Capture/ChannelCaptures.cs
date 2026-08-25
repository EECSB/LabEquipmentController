using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// What came back for one channel of a capture: its samples, or why there are none.
///
/// <see cref="WaveformCapture"/> knows about samples and scaling and nothing about channels — a
/// capture is a capture, whichever socket on the front panel it came off. Which channel it came
/// from is the caller's business: it decides the colour a trace is drawn in, the column heading
/// in a CSV, and the name in a legend. So it is carried beside the capture rather than added
/// to it.
/// </summary>
/// <param name="Channel">The channel this was read from, as the instrument numbers them.</param>
/// <param name="Capture">The samples and their scaling, or null if this channel gave none.</param>
/// <param name="Error">Why there are no samples, in words. Null when there are.</param>
public sealed record ChannelRead(int Channel, WaveformCapture? Capture, string? Error)
{
    /// <summary>Whether this channel gave a usable trace.</summary>
    public bool Ok => Capture is not null;
}

/// <summary>
/// Reading several channels of one capture.
///
/// A scope with two probes on it is the ordinary case, and reading one channel against another
/// is what two probes are for. The channels of one capture share a time base and a trigger,
/// which is what makes them comparable — and what lets a caller keep one time axis for all of
/// them rather than a copy per channel.
/// </summary>
public static class ChannelCaptures
{
    /// <summary>
    /// Read each channel in turn, against one time base.
    ///
    /// One at a time because the connection carries one conversation at a time — that is the
    /// instrument's rule rather than a choice made here. A channel that cannot be read fails on
    /// its own and the others are still returned: asking a two-channel scope for four should
    /// give back the two it has rather than failing the capture.
    ///
    /// A scope that does not have a channel does not always say so. Asked for channel 3, a
    /// two-channel Rigol answers — with two samples, against fourteen hundred on the time base.
    /// Every channel of one capture is sampled on the same time base, so a length that disagrees
    /// with the first is not a short trace: it is not a trace, and drawing it would put a flat
    /// line across a channel that is not there. That is reported as a reason, not thrown.
    /// </summary>
    /// <param name="client">The connection to read over.</param>
    /// <param name="dialect">Which family of waveform-transfer commands the instrument speaks.</param>
    /// <param name="channels">Which channels to read, in the order they should be returned.</param>
    /// <param name="ct">Cancels the whole capture, not one channel of it.</param>
    public static async Task<IReadOnlyList<ChannelRead>> ReadAsync(
        IInstrumentClient client, WaveformDialect dialect,
        IReadOnlyList<int> channels, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(channels);

        var reads = new List<ChannelRead>(channels.Count);
        int expected = 0;

        foreach (int channel in channels)
        {
            WaveformCapture capture;
            try
            {
                capture = await WaveformReader.ReadAsync(client, dialect, channel, ct)
                                              .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller went away. Not this channel's failure, and not something to report
                // as one against the channels after it.
                throw;
            }
            catch (Exception ex)
            {
                reads.Add(new ChannelRead(channel, null, ex.Message));
                continue;
            }

            if (expected == 0 && capture.Samples.Count > 0) expected = capture.Samples.Count;

            if (expected > 0 && capture.Samples.Count != expected)
            {
                reads.Add(new ChannelRead(channel, null, string.Format(
                    CultureInfo.InvariantCulture,
                    "answered with {0:N0} samples against {1:N0} on the time base — the instrument "
                  + "may have no such channel, or it is off.",
                    capture.Samples.Count, expected)));
                continue;
            }

            reads.Add(new ChannelRead(channel, capture, null));
        }

        return reads;
    }
}
