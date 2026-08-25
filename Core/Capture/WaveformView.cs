using System;

namespace LabEquipmentController;

/// <summary>
/// Which slice of a captured trace is on screen, held as fractions of the whole record
/// rather than as sample indices.
///
/// Fractions because the record underneath changes: a running capture replaces the samples
/// several times a second, and a scope that has been retriggered at a different memory depth
/// returns a different number of them. Indices would move the view every time that happened —
/// zoomed into the last microsecond of a 1400-point record, you would find yourself a third
/// of the way through the next 7000-point one. A fraction means the same again.
/// </summary>
public sealed class WaveformView
{
    /// <summary>
    /// The narrowest slice the view will show, as a fraction of the record. A floor is needed
    /// at all because zooming is multiplicative and would otherwise converge on zero width;
    /// this one is well past the point where <see cref="Range"/>'s own two-sample minimum
    /// takes over on any real capture.
    /// </summary>
    public const double MinSpan = 1e-5;

    /// <summary>Left edge of the visible slice, 0 at the start of the record.</summary>
    public double Start { get; private set; }

    /// <summary>Width of the visible slice; 1 is the whole record.</summary>
    public double Span { get; private set; } = 1.0;

    /// <summary>Whether the whole record is on screen, so there is nothing to reset to.</summary>
    public bool IsWholeRecord => Span >= 1.0;

    /// <summary>Show everything again.</summary>
    public void Reset()
    {
        Start = 0;
        Span = 1.0;
    }

    /// <summary>
    /// Zoom about a point, leaving whatever is under it under it. That is the property that
    /// makes a wheel zoom feel attached to the pointer rather than to the window: zooming
    /// about the centre instead walks the feature you were aiming at off the edge.
    /// </summary>
    /// <param name="at">Where the pointer is across the visible slice: 0 left edge, 1 right.</param>
    /// <param name="factor">Greater than 1 zooms in, less than 1 out.</param>
    public void ZoomAt(double at, double factor)
    {
        if (double.IsNaN(at) || double.IsNaN(factor) || factor <= 0) return;

        at = Math.Clamp(at, 0, 1);
        double anchor = Start + at * Span;               // the moment under the pointer
        double span = Math.Clamp(Span / factor, MinSpan, 1.0);

        Place(anchor - at * span, span);
    }

    /// <summary>
    /// Slide the view by a fraction of its own width — negative goes earlier. Of its own
    /// width, not of the record, so a drag moves the trace by the same amount on screen
    /// however far in you are zoomed.
    /// </summary>
    public void PanBy(double fractionOfSpan)
    {
        if (double.IsNaN(fractionOfSpan)) return;
        Place(Start + fractionOfSpan * Span, Span);
    }

    /// <summary>
    /// The samples on screen, as a start index and a count.
    ///
    /// Never fewer than two of them where the record has two: one point is not a line, and a
    /// plot that empties itself when you zoom in far enough looks broken rather than fully
    /// zoomed. This is also what keeps <see cref="MinSpan"/> from mattering in practice.
    /// </summary>
    public (int First, int Count) Range(int sampleCount)
    {
        if (sampleCount <= 0) return (0, 0);
        if (sampleCount == 1) return (0, 1);

        int count = Math.Clamp((int)Math.Round(Span * sampleCount), 2, sampleCount);
        int first = Math.Clamp((int)Math.Round(Start * sampleCount), 0, sampleCount - count);
        return (first, count);
    }

    private void Place(double start, double span)
    {
        Span = Math.Clamp(span, MinSpan, 1.0);
        Start = Math.Clamp(start, 0, 1.0 - Span);
    }
}
