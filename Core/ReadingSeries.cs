using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LabEquipmentController;

/// <summary>One polled measurement: seconds since the run started, and the value.</summary>
public readonly record struct Reading(double Seconds, double Value);

/// <summary>
/// A rolling window of polled measurements, for the live readout plot.
///
/// Bounded on purpose: a meter polled once a second all afternoon would otherwise grow
/// without limit behind a window nobody is watching. Once full, the oldest reading is
/// dropped — the plot shows the recent past, which is what a trend is for.
/// </summary>
public sealed class ReadingSeries
{
    private readonly Queue<Reading> _items;

    public ReadingSeries(int capacity = 3600)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _items = new Queue<Reading>(Math.Min(capacity, 1024));
    }

    /// <summary>Most readings kept before the oldest starts being discarded.</summary>
    public int Capacity { get; }

    public int Count => _items.Count;

    /// <summary>Total readings taken, including any already dropped off the front.</summary>
    public long TotalTaken { get; private set; }

    public IEnumerable<Reading> Items => _items;

    public void Add(double seconds, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return;   // a bad parse is not a reading

        if (_items.Count >= Capacity) _items.Dequeue();
        _items.Enqueue(new Reading(seconds, value));
        TotalTaken++;
    }

    public void Clear()
    {
        _items.Clear();
        TotalTaken = 0;
    }

    public bool IsEmpty => _items.Count == 0;

    /// <summary>The most recent reading, or null while empty.</summary>
    public double? Latest
    {
        get
        {
            double? last = null;
            foreach (Reading r in _items) last = r.Value;
            return last;
        }
    }

    /// <summary>Smallest, largest and mean value in the window. All zero while empty.</summary>
    public (double Min, double Max, double Mean) Statistics()
    {
        if (_items.Count == 0) return (0, 0, 0);

        double min = double.MaxValue, max = double.MinValue, sum = 0;
        foreach (Reading r in _items)
        {
            if (r.Value < min) min = r.Value;
            if (r.Value > max) max = r.Value;
            sum += r.Value;
        }
        return (min, max, sum / _items.Count);
    }

    /// <summary>Two-column CSV (Time (s), Value) with a header row.</summary>
    public string ToCsv(string valueHeader = "Value")
    {
        var sb = new StringBuilder();
        sb.Append("Time (s),").Append(valueHeader).Append("\r\n");
        foreach (Reading r in _items)
        {
            sb.Append(r.Seconds.ToString("g9", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Value.ToString("g9", CultureInfo.InvariantCulture)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Read a number out of an instrument's reply.
    ///
    /// Meters answer a MEASure? with a bare number in engineering notation
    /// ("+1.234560E-01"), but some append a unit or return several comma-separated fields,
    /// and there is always a line terminator. Take the first field and parse it invariantly
    /// — never with the current culture, or a machine using ',' as its decimal separator
    /// would silently misread every value.
    /// </summary>
    public static bool TryParseReading(string? reply, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(reply)) return false;

        string first = reply.Split(',')[0].Trim().Trim('\0');
        if (first.Length == 0) return false;

        // Trim any trailing unit letters the instrument tacked on ("1.23 VDC").
        int end = first.Length;
        while (end > 0 && !(char.IsDigit(first[end - 1]) || first[end - 1] == '.')) end--;
        string number = first[..end].Trim();

        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
