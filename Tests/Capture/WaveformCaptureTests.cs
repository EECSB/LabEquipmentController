using System;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

public class WaveformCaptureTests
{
    // The example preamble from the Rigol DS2000A programming guide.
    private const string Preamble = "0,0,1400,1,1.000000e-08,-7.000000e-06,0,4.000000e-02,0,127";

    [Fact]
    public void FromRigol_scales_bytes_to_volts_using_the_preamble()
    {
        // yref=127, yorig=0, yinc=0.04 -> 127 is 0 V, 255 is +5.12 V, 0 is -5.08 V.
        var w = WaveformCapture.FromRigol(Preamble, new byte[] { 127, 255, 0 });

        Assert.Equal(3, w.Samples.Count);
        Assert.Equal(0.0, w.Samples[0].Voltage, 6);
        Assert.Equal((255 - 127) * 0.04, w.Samples[1].Voltage, 6);
        Assert.Equal((0 - 127) * 0.04, w.Samples[2].Voltage, 6);
    }

    [Fact]
    public void FromRigol_spaces_samples_in_time_by_xincrement()
    {
        var w = WaveformCapture.FromRigol(Preamble, new byte[] { 10, 20, 30 });

        Assert.Equal(-7.0e-6, w.Samples[0].Time, 12);          // xorigin
        Assert.Equal(-7.0e-6 + 1.0e-8, w.Samples[1].Time, 12); // + xincrement
        Assert.Equal(1.0e-8, w.XIncrement, 12);
    }

    [Fact]
    public void ToCsv_has_a_header_and_one_row_per_sample()
    {
        string csv = WaveformCapture.FromRigol(Preamble, new byte[] { 127, 128 }).ToCsv();
        string[] lines = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        Assert.Equal("Time (s),Voltage (V)", lines[0]);
        Assert.Equal(3, lines.Length);           // header + 2 samples
        Assert.StartsWith("-7", lines[1]);       // first sample's time is the x-origin (-7e-6)
    }

    [Fact]
    public void FromRigol_rejects_a_short_preamble()
    {
        Assert.Throws<FormatException>(() => WaveformCapture.FromRigol("0,0,1400", new byte[] { 1, 2 }));
    }
}
