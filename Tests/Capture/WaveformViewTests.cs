using System;

namespace LabEquipmentController.Tests;

public class WaveformViewTests
{
    private static WaveformView Zoomed(double at, double factor)
    {
        var v = new WaveformView();
        v.ZoomAt(at, factor);
        return v;
    }

    [Fact]
    public void A_new_view_shows_the_whole_record()
    {
        var v = new WaveformView();
        Assert.Equal(0, v.Start);
        Assert.Equal(1, v.Span);
        Assert.True(v.IsWholeRecord);
    }

    /// <summary>
    /// The property the whole thing rests on: the moment under the pointer stays under it.
    /// Checked at three different points so a formula that only happens to work at the
    /// centre — or only at an edge — does not pass.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Zooming_keeps_what_is_under_the_pointer_under_it(double at)
    {
        var v = new WaveformView();
        double before = v.Start + at * v.Span;

        v.ZoomAt(at, 2.0);

        double after = v.Start + at * v.Span;
        Assert.Equal(before, after, 10);
    }

    [Fact]
    public void Zooming_in_halves_the_span()
        => Assert.Equal(0.5, Zoomed(0.5, 2.0).Span, 10);

    [Fact]
    public void Zooming_back_out_returns_to_the_whole_record()
    {
        var v = Zoomed(0.5, 4.0);
        v.ZoomAt(0.5, 0.25);

        Assert.Equal(1.0, v.Span, 10);
        Assert.Equal(0.0, v.Start, 10);
        Assert.True(v.IsWholeRecord);
    }

    /// <summary>Zooming out past the record shows the record, not a window hanging off it.</summary>
    [Fact]
    public void The_view_never_runs_past_either_end()
    {
        var v = Zoomed(1.0, 8.0);         // hard against the right-hand end
        Assert.True(v.Start + v.Span <= 1.0 + 1e-12);

        v.PanBy(50);                       // shove it far past the end
        Assert.Equal(1.0 - v.Span, v.Start, 10);

        v.PanBy(-50);                      // and far past the start
        Assert.Equal(0.0, v.Start, 10);
    }

    [Fact]
    public void Panning_moves_by_a_fraction_of_the_visible_width()
    {
        var v = Zoomed(0.5, 4.0);          // span 0.25, centred
        double start = v.Start;

        v.PanBy(0.5);                      // half of 0.25

        Assert.Equal(start + 0.125, v.Start, 10);
        Assert.Equal(0.25, v.Span, 10);    // panning does not rescale
    }

    [Fact]
    public void Reset_shows_everything_again()
    {
        var v = Zoomed(0.3, 16.0);
        v.Reset();

        Assert.Equal(0, v.Start);
        Assert.Equal(1, v.Span);
    }

    /// <summary>Nonsense from a device or a divide-by-zero must not move the view.</summary>
    [Theory]
    [InlineData(double.NaN, 2.0)]
    [InlineData(0.5, double.NaN)]
    [InlineData(0.5, 0.0)]
    [InlineData(0.5, -2.0)]
    public void A_meaningless_zoom_is_ignored(double at, double factor)
    {
        var v = Zoomed(0.5, 4.0);
        double start = v.Start, span = v.Span;

        v.ZoomAt(at, factor);

        Assert.Equal(start, v.Start);
        Assert.Equal(span, v.Span);
    }

    [Fact]
    public void The_whole_record_maps_to_every_sample()
        => Assert.Equal((0, 1000), new WaveformView().Range(1000));

    [Fact]
    public void A_zoomed_view_maps_to_the_samples_under_it()
    {
        var v = Zoomed(0.5, 4.0);          // middle quarter
        (int first, int count) = v.Range(1000);

        Assert.Equal(375, first);
        Assert.Equal(250, count);
    }

    /// <summary>
    /// Zoomed to the limit the plot still has a line to draw. One point is not a line, and an
    /// empty plot reads as broken rather than as fully zoomed.
    /// </summary>
    [Fact]
    public void Zooming_all_the_way_in_still_leaves_two_samples()
    {
        var v = new WaveformView();
        for (int i = 0; i < 40; i++) v.ZoomAt(0.5, 2.0);

        (int first, int count) = v.Range(1000);
        Assert.Equal(2, count);
        Assert.InRange(first, 0, 998);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_record_with_no_samples_maps_to_nothing(int sampleCount)
        => Assert.Equal((0, 0), new WaveformView().Range(sampleCount));

    /// <summary>A single sample is all there is, so it is what the range reports.</summary>
    [Fact]
    public void A_single_sample_record_maps_to_that_sample()
        => Assert.Equal((0, 1), Zoomed(0.5, 8.0).Range(1));

    /// <summary>The range must stay inside the array however far right the view has gone.</summary>
    [Fact]
    public void The_range_never_runs_off_the_end_of_the_record()
    {
        var v = Zoomed(1.0, 3.0);
        v.PanBy(10);

        (int first, int count) = v.Range(777);
        Assert.True(first >= 0);
        Assert.True(first + count <= 777);
    }
}
