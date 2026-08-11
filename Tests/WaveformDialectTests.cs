using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// Every scope hands a trace back differently, and getting the arithmetic wrong does not
/// throw — it draws. A plot at the wrong offset or the wrong timebase looks like a
/// measurement, which is why each decoder is pinned here against the numbers its own vendor
/// publishes, and why the two families that share a preamble layout are pinned against each
/// other to prove they are not sharing a formula.
/// </summary>
public class WaveformDialectTests
{
    // ------------------------------------------------------------------------- decoding

    /// <summary>
    /// Rigol: volts = (raw - yreference - yorigin) * yincrement, time = xorigin + (i - xreference) * xincrement.
    /// </summary>
    [Fact]
    public void Rigol_decodes_with_its_own_formula()
    {
        // format,type,points,count, xinc,  xorig, xref, yinc, yorig, yref
        const string pre = "0,0,3,1,1e-6,-1e-6,0,0.04,0,128";
        var w = WaveformCapture.FromRigol(pre, new byte[] { 128, 153, 103 });

        Assert.Equal(3, w.Samples.Count);
        Assert.Equal(1e-6, w.XIncrement, 12);

        Assert.Equal(-1e-6, w.Samples[0].Time, 12);
        Assert.Equal(0.0, w.Samples[0].Voltage, 9);          // (128-128-0)*0.04
        Assert.Equal(1.0, w.Samples[1].Voltage, 9);          // (153-128-0)*0.04
        Assert.Equal(-1.0, w.Samples[2].Voltage, 9);         // (103-128-0)*0.04
    }

    /// <summary>
    /// Keysight: volts = ((raw - yreference) * yincrement) + yorigin — the InfiniiVision
    /// guide states it in prose and again in its own Python example.
    /// </summary>
    [Fact]
    public void Keysight_decodes_with_its_own_formula()
    {
        const string pre = "0,0,3,1,1e-6,-1e-6,0,0.04,2.5,128";
        var w = WaveformCapture.FromKeysight(pre, new byte[] { 128, 153, 103 });

        Assert.Equal(-1e-6, w.Samples[0].Time, 12);
        Assert.Equal(0.0 + 2.5, w.Samples[0].Voltage, 9);    // ((128-128)*0.04)+2.5
        Assert.Equal(1.0 + 2.5, w.Samples[1].Voltage, 9);
        Assert.Equal(-1.0 + 2.5, w.Samples[2].Voltage, 9);
    }

    /// <summary>
    /// The two share a preamble layout and not an arithmetic. Rigol folds yorigin in before
    /// scaling, Keysight adds it afterwards in volts, so a non-zero yorigin has to make them
    /// disagree. If this ever passes, one of the decoders has been "simplified" into the
    /// other and every Keysight trace is silently at the wrong offset.
    /// </summary>
    [Fact]
    public void Rigol_and_Keysight_disagree_when_yorigin_is_not_zero()
    {
        const string pre = "0,0,1,1,1e-6,0,0,0.04,2.5,128";
        var rigol = WaveformCapture.FromRigol(pre, new byte[] { 200 });
        var keysight = WaveformCapture.FromKeysight(pre, new byte[] { 200 });

        Assert.NotEqual(rigol.Samples[0].Voltage, keysight.Samples[0].Voltage, 6);
        Assert.Equal((200 - 128 - 2.5) * 0.04, rigol.Samples[0].Voltage, 9);
        Assert.Equal(((200 - 128) * 0.04) + 2.5, keysight.Samples[0].Voltage, 9);
    }

    /// <summary>
    /// Tektronix, against the worked example in the programmer manual: XZEro -500 ms,
    /// XINcr 1 ms, YMUlt 4 mV, YZEro 0, giving X1 = -500 ms and X1000 = 499 ms.
    /// </summary>
    [Fact]
    public void Tektronix_matches_the_manuals_worked_example()
    {
        var data = new byte[1000];
        var w = WaveformCapture.FromTektronix(
            data, width: 1, signed: true, msbFirst: true,
            xincr: 1e-3, xzero: -0.5, ptOff: 0, ymult: 4e-3, yoff: 0, yzero: 0);

        Assert.Equal(1000, w.Samples.Count);
        Assert.Equal(-0.5, w.Samples[0].Time, 9);
        Assert.Equal(0.499, w.Samples[999].Time, 9);
    }

    /// <summary>
    /// YOFf is the vertical position in digitizing levels, and the manual's general form is
    /// ((curve_in_dl - YOFf) * YMUlt) + YZEro. The worked example above omits it only
    /// because it is zero there; dropping it in general shifts every trace.
    /// </summary>
    [Fact]
    public void Tektronix_applies_the_vertical_offset()
    {
        var w = WaveformCapture.FromTektronix(
            new byte[] { 100 }, width: 1, signed: true, msbFirst: true,
            xincr: 1e-3, xzero: 0, ptOff: 0, ymult: 4e-3, yoff: -50, yzero: 0.1);

        Assert.Equal(((100 - -50) * 4e-3) + 0.1, w.Samples[0].Voltage, 9);
    }

    [Theory]
    [InlineData(true, new byte[] { 0xFF, 0x00 }, -256)]   // MSB first, signed
    [InlineData(false, new byte[] { 0x00, 0xFF }, -256)]  // LSB first, same value
    public void Tektronix_honours_byte_order(bool msbFirst, byte[] data, double expected)
    {
        var w = WaveformCapture.FromTektronix(
            data, width: 2, signed: true, msbFirst: msbFirst,
            xincr: 1, xzero: 0, ptOff: 0, ymult: 1, yoff: 0, yzero: 0);

        Assert.Single(w.Samples);
        Assert.Equal(expected, w.Samples[0].Voltage, 6);
    }

    /// <summary>
    /// R&amp;S returns volts already, so the only arithmetic is the timebase, and the header
    /// gives the interval's endpoints rather than its step.
    /// </summary>
    [Fact]
    public void Rohde_reads_volts_directly_and_derives_the_step()
    {
        var w = WaveformCapture.FromRohdeAscii(
            "-1E-6,1E-6,5,1", "-0.125,-0.123,0,0.123,0.125");

        Assert.Equal(5, w.Samples.Count);
        Assert.Equal(-0.125, w.Samples[0].Voltage, 9);
        Assert.Equal(0.125, w.Samples[4].Voltage, 9);

        Assert.Equal(-1e-6, w.Samples[0].Time, 12);
        Assert.Equal(1e-6, w.Samples[4].Time, 12);
        Assert.Equal(0.5e-6, w.XIncrement, 12);           // (1e-6 - -1e-6) / (5 - 1)
    }

    /// <summary>
    /// Siglent, against the worked example in the SDS guide: a first code of -11 with
    /// vdiv 10, code_per_div 30 and voffset 14.5 gives -18.167 V, and with timebase 2E-8,
    /// grid 10 and delay 1.72E-8 the first point sits at -117.2 ns and the second 0.2 ns later.
    /// </summary>
    [Fact]
    public void Siglent_matches_the_guides_worked_example()
    {
        byte[] pre = new byte[346];
        BitConverter.GetBytes(10f).CopyTo(pre, 156);        // vdiv
        BitConverter.GetBytes(14.5f).CopyTo(pre, 160);      // voffset
        BitConverter.GetBytes(30f).CopyTo(pre, 164);        // code_per_div
        BitConverter.GetBytes(2e-10f).CopyTo(pre, 176);     // interval
        BitConverter.GetBytes(1.72e-8).CopyTo(pre, 180);    // delay

        var w = WaveformCapture.FromSiglent(pre, new byte[] { unchecked((byte)-11), 0 },
                                            secondsPerDiv: 2e-8);

        Assert.Equal(-18.167, w.Samples[0].Voltage, 3);
        Assert.Equal(-117.2e-9, w.Samples[0].Time, 12);
        Assert.Equal(-117.0e-9, w.Samples[1].Time, 12);
    }

    [Fact]
    public void Siglent_refuses_a_descriptor_too_short_to_hold_the_scaling()
        => Assert.Throws<FormatException>(
            () => WaveformCapture.FromSiglent(new byte[16], new byte[] { 0 }, 1e-6));

    // ------------------------------------------------------------------------- sequences

    /// <summary>
    /// Records what was sent, and answers queries from a script. Enough to pin the command
    /// sequence each dialect uses without an instrument on the bench.
    /// </summary>
    private sealed class FakeScope : IInstrumentClient
    {
        private readonly Dictionary<string, string> _text;
        private readonly Dictionary<string, byte[]> _binary;

        public FakeScope(Dictionary<string, string> text, Dictionary<string, byte[]> binary)
            => (_text, _binary) = (text, binary);

        public List<string> Sent { get; } = new();

        public string Host => "fake";
        public string Description => "fake";
        public bool IsConnected => true;
        public int TimeoutMs { get; set; }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SendAsync(string command, CancellationToken ct = default)
        {
            Sent.Add(command);
            return Task.CompletedTask;
        }

        public Task<string> QueryAsync(string command, CancellationToken ct = default)
        {
            Sent.Add(command);
            return Task.FromResult(_text.TryGetValue(command, out string? v) ? v : "0");
        }

        public Task<byte[]> QueryBinaryAsync(string command, CancellationToken ct = default)
        {
            Sent.Add(command);
            return Task.FromResult(_binary.TryGetValue(command, out byte[]? v) ? v : Array.Empty<byte>());
        }

        public Task ReturnToLocalAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() { }
        public void Dispose() { }
    }

    public static TheoryData<WaveformDialect, string> DataQueries() => new()
    {
        { WaveformDialect.Rigol,      ":WAVeform:DATA?" },
        { WaveformDialect.Keysight,   ":WAVeform:DATA?" },
        { WaveformDialect.Tektronix,  "CURVe?" },
        { WaveformDialect.RohdeAscii, "CHANnel1:DATA?" },
        { WaveformDialect.Siglent,    ":WAVeform:DATA?" },
    };

    /// <summary>A descriptor with a usable scale, since an all-zero one is rightly refused.</summary>
    private static byte[] SiglentDescriptor()
    {
        byte[] pre = new byte[346];
        BitConverter.GetBytes(10f).CopyTo(pre, 156);
        BitConverter.GetBytes(0f).CopyTo(pre, 160);
        BitConverter.GetBytes(30f).CopyTo(pre, 164);
        BitConverter.GetBytes(2e-10f).CopyTo(pre, 176);
        BitConverter.GetBytes(0.0).CopyTo(pre, 180);
        return pre;
    }

    [Theory]
    [MemberData(nameof(DataQueries))]
    public async Task Each_dialect_asks_for_its_own_data(WaveformDialect dialect, string query)
    {
        var scope = new FakeScope(
            new Dictionary<string, string>
            {
                [":WAVeform:PREamble?"] = "0,0,1,1,1e-6,0,0,0.04,0,128",
                ["CHANnel1:DATA:HEADer?"] = "-1E-6,1E-6,2,1",
                ["CHANnel1:DATA?"] = "0.1,0.2",
                ["WFMOutpre:BYT_Nr?"] = "1",
                ["WFMOutpre:BN_Fmt?"] = "RI",
                ["WFMOutpre:BYT_Or?"] = "MSB",
                [":TIMebase:SCALe?"] = "2e-8",
            },
            new Dictionary<string, byte[]>
            {
                [":WAVeform:DATA?"] = new byte[] { 128 },
                ["CURVe?"] = new byte[] { 0 },
                [":WAVeform:PREamble?"] = SiglentDescriptor(),
            });

        await WaveformReader.ReadAsync(scope, dialect);

        Assert.Contains(query, scope.Sent);
    }

    /// <summary>
    /// A Tektronix with HEADer ON echoes the command back before the value. Parsing that as
    /// a bare number throws, and the failure would land on the user as an unhelpful
    /// FormatException in the middle of a capture.
    /// </summary>
    [Fact]
    public async Task Tektronix_tolerates_an_echoed_header_in_the_reply()
    {
        var scope = new FakeScope(
            new Dictionary<string, string>
            {
                ["WFMOutpre:BYT_Nr?"] = ":WFMOUTPRE:BYT_NR 1",
                ["WFMOutpre:BN_Fmt?"] = ":WFMOUTPRE:BN_FMT RI",
                ["WFMOutpre:BYT_Or?"] = ":WFMOUTPRE:BYT_OR MSB",
                ["WFMOutpre:YMUlt?"] = ":WFMOUTPRE:YMULT 4.0000E-3",
                ["WFMOutpre:XINcr?"] = ":WFMOUTPRE:XINCR 1.0000E-3",
                ["WFMOutpre:XZEro?"] = ":WFMOUTPRE:XZERO -500.0000E-3",
                ["WFMOutpre:PT_Off?"] = ":WFMOUTPRE:PT_OFF 0",
                ["WFMOutpre:YOFf?"] = ":WFMOUTPRE:YOFF 0",
                ["WFMOutpre:YZEro?"] = ":WFMOUTPRE:YZERO 0",
            },
            new Dictionary<string, byte[]> { ["CURVe?"] = new byte[] { 25 } });

        var w = await WaveformReader.ReadAsync(scope, WaveformDialect.Tektronix);

        Assert.Equal(-0.5, w.Samples[0].Time, 9);
        Assert.Equal(0.1, w.Samples[0].Voltage, 9);          // 25 * 4 mV
    }

    [Fact]
    public async Task A_dialect_of_None_refuses_rather_than_sending_something_plausible()
    {
        var scope = new FakeScope(new(), new());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => WaveformReader.ReadAsync(scope, WaveformDialect.None));
        Assert.Empty(scope.Sent);
    }

    // ---------------------------------------------------------------------------- SPEC §10

    /// <summary>
    /// Every command a capture sends has to be in that family's catalog.
    ///
    /// CatalogCoverageTests checks the quick-command buttons, the readout queries and the
    /// bundled scripts, because those were the only places the app spoke on its own account.
    /// Capture is a fourth: WaveformReader sends eight or ten commands per trace that no
    /// button lists, and a typo among them fails on the bench rather than in the build. The
    /// sequences are collected by actually running each dialect against the fake, so what is
    /// checked is what would be sent.
    /// </summary>
    [Theory]
    [InlineData(WaveformDialect.Rigol, "RIGOL TECHNOLOGIES,DS2202,X,1.0")]
    [InlineData(WaveformDialect.Keysight, "KEYSIGHT TECHNOLOGIES,MSO-X 3054T,MY000,1.0")]
    [InlineData(WaveformDialect.Tektronix, "TEKTRONIX,MDO4104C,C000,1.0")]
    [InlineData(WaveformDialect.RohdeAscii, "Rohde&Schwarz,RTB2004,1333.1005k04/000,1.0")]
    [InlineData(WaveformDialect.Siglent, "Siglent Technologies,SDS1104X-E,SDS000,1.0")]
    public async Task Every_command_a_capture_sends_is_in_the_catalog(
        WaveformDialect dialect, string idn)
    {
        var scope = new FakeScope(
            new Dictionary<string, string>
            {
                [":WAVeform:PREamble?"] = "0,0,1,1,1e-6,0,0,0.04,0,128",
                ["CHANnel1:DATA:HEADer?"] = "-1E-6,1E-6,2,1",
                ["CHANnel1:DATA?"] = "0.1,0.2",
                ["WFMOutpre:BYT_Nr?"] = "1",
                ["WFMOutpre:BN_Fmt?"] = "RI",
                ["WFMOutpre:BYT_Or?"] = "MSB",
                [":TIMebase:SCALe?"] = "2e-8",
            },
            new Dictionary<string, byte[]>
            {
                [":WAVeform:DATA?"] = new byte[] { 128 },
                ["CURVe?"] = new byte[] { 0 },
                [":WAVeform:PREamble?"] = SiglentDescriptor(),
            });

        await WaveformReader.ReadAsync(scope, dialect);

        CommandReference reference = CommandReference.ForIdentity(idn)!;
        List<string> templates = reference.Commands.Select(c => c.Syntax).ToList();

        var undocumented = scope.Sent
            .Where(cmd => !ScpiSyntax.MatchesAny(cmd, templates))
            .ToList();

        Assert.True(undocumented.Count == 0,
            $"{dialect}: sent but not in the catalog: {string.Join(", ", undocumented)}");
    }

    /// <summary>
    /// Every family that ships a catalog, so that giving a profile a capture command cannot
    /// quietly escape the check below.
    ///
    /// This used to be five hand-written scopes, and an R&amp;S analyzer was then given the R&amp;S
    /// scope's "HCOPy:DATA?" — a command its own manual never mentions — which the test
    /// could not see because the analyzer was not in the list. Reusing the catalog roster
    /// means the next family is covered by existing.
    /// </summary>
    public static TheoryData<InstrumentFamily> CataloguedFamilies()
        => CatalogCoverageTests.CataloguedFamilies();

    /// <summary>
    /// The same for the screen dump, which is a command plus, where the guide calls for it,
    /// the setup that decides the format and — on the FSL — writes the file being read back.
    ///
    /// Families with no documented capture route are skipped rather than failed: not having
    /// one is a legitimate state, and <see cref="InstrumentProfileTests"/> pins which.
    /// </summary>
    [Theory]
    [MemberData(nameof(CataloguedFamilies))]
    public void Every_command_a_screen_capture_sends_is_in_the_catalog(InstrumentFamily family)
    {
        string idn = CatalogCoverageTests.IdentityFor(family);
        InstrumentProfile p = InstrumentProfile.ForIdentity(idn);
        CommandReference reference = CommandReference.ForIdentity(idn)!;
        List<string> templates = reference.Commands.Select(c => c.Syntax).ToList();

        if (string.IsNullOrEmpty(p.ScreenCaptureCommand)) return;   // no documented route

        var sent = new List<string>(p.ScreenCaptureSetup) { p.ScreenCaptureCommand };
        var undocumented = sent.Where(c => !ScpiSyntax.MatchesAny(c, templates)).ToList();

        Assert.True(undocumented.Count == 0,
            $"{p.Name}: sent but not in the catalog: {string.Join(", ", undocumented)}");
    }

    // --------------------------------------------------------------------------- profiles

    [Theory]
    [InlineData("RIGOL TECHNOLOGIES,DS2202,X,1.0", WaveformDialect.Rigol)]
    [InlineData("KEYSIGHT TECHNOLOGIES,MSO-X 3054T,MY000,1.0", WaveformDialect.Keysight)]
    [InlineData("TEKTRONIX,MDO4104C,C000,1.0", WaveformDialect.Tektronix)]
    [InlineData("Rohde&Schwarz,RTB2004,1333.1005k04/000,1.0", WaveformDialect.RohdeAscii)]
    [InlineData("Siglent Technologies,SDS1104X-E,SDS000,1.0", WaveformDialect.Siglent)]
    // The GDS-2000 manual documents the framing of :ACQuire<X>:MEMory? but never how a
    // stored code becomes a voltage, so this one stays None on purpose.
    [InlineData("GW INSTEK,GDS-2204E,GEQ000,1.0", WaveformDialect.None)]
    public void Scopes_get_the_dialect_their_guide_documents(string idn, WaveformDialect expected)
        => Assert.Equal(expected, InstrumentProfile.ForIdentity(idn).WaveformDialect);

    [Theory]
    [InlineData("Siglent Technologies,SDM3065X,X,1.0")]
    [InlineData("RIGOL TECHNOLOGIES,DP832,X,1.0")]
    [InlineData("Rohde&Schwarz,FPC1500,1304.1004K02,3.20")]
    public void Instruments_without_a_trace_to_read_offer_no_waveform_button(string idn)
    {
        InstrumentProfile p = InstrumentProfile.ForIdentity(idn);
        Assert.Equal(WaveformDialect.None, p.WaveformDialect);
        Assert.False(p.SupportsWaveformCapture);
    }
}
