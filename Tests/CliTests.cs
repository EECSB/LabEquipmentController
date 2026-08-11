using LabEquipmentController.Cli;

namespace LabEquipmentController.Tests;

/// <summary>
/// The CLI's two failure surfaces that need no instrument: reading an argument list, and
/// writing the result out. Both are worth pinning because both fail quietly — an option
/// silently dropped looks like a default, and a CSV that forgets to quote a comma splits
/// one column into two in whatever tool opens it next.
/// </summary>
public class CliTests
{
    // ------------------------------------------------------------- argument parsing

    [Fact]
    public void No_arguments_asks_for_help()
        => Assert.Equal("help", CommandLine.Parse([]).Verb);

    [Fact]
    public void Verb_and_operands_keep_their_order()
    {
        var c = CommandLine.Parse(["send", "192.168.1.20", "*IDN?", ":MEAS:VPP? CHAN1"]);
        Assert.Equal("send", c.Verb);
        Assert.Equal(["192.168.1.20", "*IDN?", ":MEAS:VPP? CHAN1"], c.Operands);
    }

    [Fact]
    public void The_verb_is_case_insensitive_but_operands_are_not()
    {
        var c = CommandLine.Parse(["SCAN", "KeepMyCase"]);
        Assert.Equal("scan", c.Verb);
        Assert.Equal("KeepMyCase", c.Operands[0]);
    }

    [Theory]
    [InlineData("--range", "20-60")]
    [InlineData("--range=20-60", null)]
    public void An_option_takes_its_value_either_way(string first, string? second)
    {
        string[] args = second is null ? ["scan", first] : ["scan", first, second];
        Assert.Equal("20-60", CommandLine.Parse(args).Value("range"));
    }

    [Fact]
    public void A_flag_is_present_without_a_value()
    {
        var c = CommandLine.Parse(["scan", "--json", "--quiet"]);
        Assert.True(c.Has("json"));
        Assert.True(c.Has("quiet"));
        Assert.False(c.Has("csv"));
    }

    [Fact]
    public void A_flag_given_a_value_is_a_mistake_worth_reporting()
        => Assert.NotNull(CommandLine.Parse(["scan", "--json=yes"]).Error);

    [Fact]
    public void An_option_whose_value_is_missing_does_not_eat_the_next_option()
    {
        // "--range --json" is a forgotten range, not a range of "--json". Swallowing the
        // next option would scan the whole subnet while looking like it had been narrowed.
        var c = CommandLine.Parse(["scan", "--range", "--json"]);
        Assert.NotNull(c.Error);
    }

    [Fact]
    public void Device_bindings_accumulate_rather_than_overwrite()
    {
        var c = CommandLine.Parse(["seq", "f.seq", "--device", "gen=1.1.1.1", "--device", "dmm=1.1.1.2"]);
        Assert.Equal("gen=1.1.1.1;dmm=1.1.1.2", c.Value("device"));
    }

    [Fact]
    public void A_negative_number_is_an_operand_not_an_option()
        => Assert.Contains("-40", CommandLine.Parse(["catalog", "-40"]).Operands);

    [Theory]
    [InlineData("5000", true, 5000)]
    [InlineData("0", false, 0)]
    [InlineData("-1", false, 0)]
    [InlineData("soon", false, 0)]
    public void A_numeric_option_refuses_what_is_not_a_positive_number(string given, bool ok, int expected)
    {
        var c = CommandLine.Parse(["scan", "--timeout", given]);
        // "-1" parses as an operand, not an option, so the option is simply absent and the
        // fallback stands; every other bad value is rejected outright.
        bool parsed = c.TryInt("timeout", 999, out int value);
        if (ok) { Assert.True(parsed); Assert.Equal(expected, value); }
        else Assert.True(!parsed || value == 999);
    }

    // ------------------------------------------------------------------- addresses

    [Fact]
    public void A_bare_host_is_a_raw_socket_on_5025()
    {
        Assert.True(Endpoint.TryParse("192.168.1.20", out var ep, out _));
        Assert.Equal("192.168.1.20", ep.Host);
        Assert.Equal(InstrumentTransport.RawSocket, ep.Transport);
        Assert.Equal(5025, ep.Port);
    }

    [Fact]
    public void A_host_and_port_are_split_at_the_colon()
    {
        Assert.True(Endpoint.TryParse("192.168.1.20:5555", out var ep, out _));
        Assert.Equal("192.168.1.20", ep.Host);
        Assert.Equal(5555, ep.Port);
    }

    [Fact]
    public void Port_111_means_VXI_11_not_a_raw_socket()
    {
        // The portmapper answers on 111 but never speaks SCPI: read as a raw socket, the
        // connection succeeds and then waits for a reply that is never coming.
        Assert.True(Endpoint.TryParse("192.168.1.20:111", out var ep, out _));
        Assert.Equal(InstrumentTransport.Vxi11, ep.Transport);
    }

    [Fact]
    public void The_vxi_scheme_is_the_short_way_to_ask_for_it()
    {
        Assert.True(Endpoint.TryParse("vxi://192.168.1.20", out var ep, out _));
        Assert.Equal(InstrumentTransport.Vxi11, ep.Transport);
        Assert.Equal("192.168.1.20", ep.Host);
    }

    [Fact]
    public void A_VISA_resource_string_is_understood_the_way_the_app_understands_it()
    {
        Assert.True(Endpoint.TryParse("TCPIP0::192.168.1.20::inst0::INSTR", out var ep, out _));
        Assert.Equal(InstrumentTransport.Vxi11, ep.Transport);
        Assert.Equal("inst0", ep.DeviceName);
    }

    [Fact]
    public void An_IPv6_literal_keeps_its_colons()
    {
        Assert.True(Endpoint.TryParse("[fe80::1]:5025", out var ep, out _));
        Assert.Equal("fe80::1", ep.Host);
        Assert.Equal(5025, ep.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[fe80::1]:99999")]
    public void An_unusable_address_is_refused_with_a_reason(string text)
    {
        Assert.False(Endpoint.TryParse(text, out _, out string error));
        Assert.NotEmpty(error);
    }

    // ---------------------------------------------------------------------- output

    [Fact]
    public void A_table_aligns_its_columns_and_leaves_no_trailing_space()
    {
        string text = Output.Table(["A", "Bee"], [["x", "y"], ["longer", "z"]]);
        var lines = text.TrimEnd('\n').Split('\n');
        Assert.Equal(4, lines.Length);                                   // header, rule, 2 rows
        Assert.All(lines, l => Assert.Equal(l.TrimEnd(), l));
        Assert.StartsWith("A     ", lines[0]);                           // padded to "longer"
    }

    [Fact]
    public void Csv_quotes_a_value_that_would_otherwise_split_the_row()
    {
        string csv = Output.Csv(["Syntax", "Description"],
                                [["MEAS:VOLT?", "Reads volts, then stops"], ["A", "He said \"no\""]]);
        Assert.Contains("\"Reads volts, then stops\"", csv);
        Assert.Contains("\"He said \"\"no\"\"\"", csv);
    }

    [Fact]
    public void Json_output_names_its_fields_after_the_headers()
    {
        var cmd = CommandLine.Parse(["scan", "--json"]);
        string json = Output.Render(cmd, ["Address", "Identity"], [["1.2.3.4", "ACME,X1"]]);
        Assert.Contains("\"Address\"", json);
        Assert.Contains("\"ACME,X1\"", json);
    }

    // ------------------------------------------------------------- catalog search

    [Fact]
    public void A_command_typed_the_way_it_is_sent_finds_the_entry_the_guide_prints()
    {
        // The guide prints "[SENSe:]VOLTage[:DC]:NPLC"; nobody types the brackets. A plain
        // substring search misses this, which is how the search first shipped.
        var entry = new CommandRef("Sense", "[SENSe:]VOLTage[:DC]:NPLC {<PLC>|MIN|MAX}", "Integration time.");
        Assert.True(Commands.SearchHit(entry, "VOLTage:DC:NPLC"));
        Assert.True(Commands.SearchHit(entry, "NPLC"));            // still a substring hit
        Assert.True(Commands.SearchHit(entry, "integration"));     // and a description hit
        Assert.False(Commands.SearchHit(entry, "FREQuency:STOP"));
    }

    // ------------------------------------------------------- screenshot file format

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ".png")]
    [InlineData(new byte[] { 0x42, 0x4D, 0x36, 0x00 }, ".bmp")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ".jpg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, ".gif")]
    [InlineData(new byte[] { 0x00, 0x01, 0x02, 0x03 }, ".bin")]
    public void The_image_format_is_read_from_the_bytes_not_the_file_name(byte[] data, string expected)
        => Assert.Equal(expected, Capture.ExtensionFor(data));

    [Fact]
    public void A_scope_that_sends_BMP_does_not_get_a_file_called_png()
    {
        // The Rigol's :DISPlay:DATA? returns a BMP however the output file was named.
        // Writing those bytes into "shot.png" produces a file that some tools refuse and
        // others open while reporting the wrong format.
        byte[] bmp = [0x42, 0x4D, 0x36, 0x00];
        Assert.Equal("shot.bmp", Capture.PathFor("shot.png", bmp));
        Assert.Equal("shot.bmp", Capture.PathFor("shot.bmp", bmp));

        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.Equal("shot.png", Capture.PathFor("shot.png", png));
        Assert.Equal("shot.jpeg", Capture.PathFor("shot.jpeg", [0xFF, 0xD8, 0xFF, 0xE0]));  // .jpeg is .jpg
    }

    // ------------------------------------------------------------------- intervals

    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("2s", 2000)]
    [InlineData("1m", 60000)]
    [InlineData("250", 250)]
    [InlineData("0.5s", 500)]
    public void A_poll_interval_reads_its_unit(string text, int expected)
    {
        Assert.True(Capture.TryParseInterval(text, out int ms));
        Assert.Equal(expected, ms);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("0")]
    [InlineData("-5s")]
    public void An_unusable_interval_is_refused(string text)
        => Assert.False(Capture.TryParseInterval(text, out _));

    // ------------------------------------------------------------------------ csv

    [Fact]
    public void A_recorded_csv_reads_back_with_its_quoted_fields_intact()
    {
        var (headers, rows) = Capture.ReadCsv("A,\"B, with comma\"\r\n1,\"say \"\"hi\"\"\"\r\n2,x\r\n");
        Assert.Equal(["A", "B, with comma"], headers);
        Assert.Equal(2, rows.Count);
        Assert.Equal("say \"hi\"", rows[0][1]);
        Assert.Equal("2", rows[1][0]);
    }

    [Fact]
    public void A_csv_without_a_trailing_newline_still_yields_its_last_row()
    {
        var (_, rows) = Capture.ReadCsv("A,B\n1,2");
        Assert.Single(rows);
        Assert.Equal("2", rows[0][1]);
    }

    // ----------------------------------------------------------------------- plot

    [Fact]
    public void A_plot_is_well_formed_xml_with_one_path_per_series()
    {
        string svg = Plot.Svg("t", "x", "y",
        [
            new Plot.Series("A", [(0, 0), (1, 1)]),
            new Plot.Series("B", [(0, 1), (1, 0)]),
        ]);
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(svg);                                    // throws if malformed
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(svg, "<path ").Count);
    }

    [Fact]
    public void A_title_containing_markup_cannot_break_out_of_the_svg()
    {
        // An *IDN? reply becomes a plot title, and it is whatever the instrument says.
        string svg = Plot.Svg("ACME <b>\"&\" x</b>", "x", "y", [new Plot.Series("s", [(0, 0), (1, 1)])]);
        new System.Xml.XmlDocument().LoadXml(svg);
        Assert.Contains("&lt;b&gt;", svg);
        Assert.DoesNotContain("<b>", svg);
    }

    [Fact]
    public void A_flat_trace_still_draws_instead_of_dividing_by_zero()
    {
        string svg = Plot.Svg("flat", "x", "y", [new Plot.Series("s", [(1, 5), (2, 5), (3, 5)])]);
        new System.Xml.XmlDocument().LoadXml(svg);
        Assert.DoesNotContain("NaN", svg);
        Assert.DoesNotContain("Infinity", svg);
    }

    [Fact]
    public void A_table_becomes_one_series_per_numeric_column_skipping_blanks()
    {
        var series = Plot.FromTable(
            ["Hz", "Vout", "Note"],
            [["1000", "1.9", "ok"], ["2000", "", "dropped"], ["3000", "1.5", "ok"]]);

        // "Note" holds no numbers, so it is not a series; the blank Vout reading is a gap,
        // not a zero — plotting a missing measurement at zero invents a data point.
        var vout = Assert.Single(series);
        Assert.Equal("Vout", vout.Name);
        Assert.Equal(2, vout.Points.Count);
    }

    // --------------------------------------------------------------------- stream

    [Fact]
    public void Without_the_stream_flag_nothing_is_written_as_it_goes()
    {
        var sw = new StringWriter();
        using var stream = new RowStream(CommandLine.Parse(["run", "a", "b"]), ["X"], sw);
        stream.Write(["1"]);
        Assert.False(stream.Streaming);
        Assert.Equal("", sw.ToString());
    }

    [Fact]
    public void Streaming_writes_the_header_once_then_a_row_at_a_time()
    {
        var sw = new StringWriter();
        using var stream = new RowStream(CommandLine.Parse(["run", "a", "b", "--stream"]), ["Hz", "V"], sw);
        Assert.True(stream.Streaming);

        stream.Write(["1000", "1.9"]);
        string afterFirst = sw.ToString();
        stream.Write(["2000", "1.8"]);

        // The row must be there before the second one arrives — that is what makes the
        // output tailable rather than a table that appears when the sweep ends.
        Assert.Equal("Hz,V\r\n1000,1.9\r\n", afterFirst);
        // Exactly, not Contains: the first version of this passed a Contains check while
        // emitting a blank line before every row, because the CSV writer wrote a header
        // line even when handed no headers. Every reader of that file sees an empty record
        // between each real one.
        Assert.Equal("Hz,V\r\n1000,1.9\r\n2000,1.8\r\n", sw.ToString());
    }

    [Fact]
    public void A_streamed_script_with_no_columns_line_names_its_columns_by_position()
    {
        var sw = new StringWriter();
        using var stream = new RowStream(CommandLine.Parse(["run", "a", "b", "--stream"]), [], sw);
        stream.Write(["x", "y"]);
        Assert.Contains("Column 1,Column 2", sw.ToString());
    }
}
