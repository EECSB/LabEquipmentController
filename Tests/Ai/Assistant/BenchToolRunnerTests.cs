using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The assistant's bench tools, called the way a model calls them — JSON in, words out — and
/// checked by what reached the instrument rather than by what the answer claims. A refusal is
/// checked to have sent nothing at all: on a bench, that is the whole point of one.
/// </summary>
public class BenchToolRunnerTests
{
    private const string Generator = "Siglent Technologies,SDG2042X,SDG2XCAD1R0001,2.01.01.35R3B2";
    private const string Meter = "Siglent Technologies,SDM3065X,SDM36HCD801207,3.02.01.13";
    private const string Scope = "RIGOL TECHNOLOGIES,DS2202,DS2A152001051,00.01.00";

    [Fact]
    public async Task The_list_names_each_instrument_as_a_sequence_would_and_says_what_it_can_do()
    {
        var bench = new FakeBench(new FakeInstrument(Generator, "192.168.1.20"), new FakeInstrument(Meter, "192.168.1.21") { Busy = true });

        var answer = await Run(bench, BenchToolRunner.ListInstruments, "{}");

        Assert.False(answer.IsError);
        Assert.Contains("- gen: Siglent Technologies SDG2042X, serial SDG2XCAD1R0001, at 192.168.1.20", answer.Text);
        Assert.Contains("- dmm: Siglent Technologies SDM3065X", answer.Text);
        Assert.Contains("A script is running on it", answer.Text);
        Assert.Contains("DEVICE gen : SDG2042X", answer.Text);
        Assert.Contains("DEVICE dmm : SDM3065X", answer.Text);
        Assert.Empty(bench.Written);
    }

    [Fact]
    public async Task A_search_lists_catalog_entries_with_the_command_itself_weighed_first()
    {
        var bench = new FakeBench(new FakeInstrument(Generator, "192.168.1.20"));

        var answer = await Run(bench, BenchToolRunner.FindCommands, """{"words": "OUTP"}""");

        Assert.False(answer.IsError);
        Assert.Contains("C<n>:OUTP <ON|OFF>", answer.Text);
        Assert.Contains("C<n>:OUTP?", answer.Text);
        Assert.Empty(bench.Written);
    }

    [Fact]
    public async Task A_query_is_sent_and_its_reply_returned()
    {
        var bench = new FakeBench(new FakeInstrument(Meter, "192.168.1.21") { Replies = { ["MEAS:VOLT:DC?"] = "4.99876E+00" } });

        var answer = await Run(bench, BenchToolRunner.Query, """{"instrument": "dmm", "command": "MEAS:VOLT:DC?"}""");

        Assert.False(answer.IsError);
        Assert.Contains("4.99876E+00", answer.Text);
        Assert.Equal(["MEAS:VOLT:DC?"], bench.Written.Select(w => w.Command));
    }

    [Theory]
    [InlineData("C1:OUTP ON", "is not a query")]
    [InlineData("C1:OUTP ON;C1:OUTP?", "joins several")]
    [InlineData("*TST?", "self-test")]
    [InlineData("C1:OUTP?\nC1:OUTP ON", "one line")]
    public async Task A_query_that_is_not_one_plain_read_is_refused_and_sends_nothing(string command, string why)
    {
        var bench = new FakeBench(new FakeInstrument(Generator, "192.168.1.20"));

        var answer = await Run(bench, BenchToolRunner.Query, Args(("instrument", "gen"), ("command", command)));

        Assert.True(answer.IsError);
        Assert.Contains(why, answer.Text);
        Assert.Empty(bench.Written);
    }

    [Fact]
    public async Task A_screen_dump_asked_for_as_a_query_is_refused_rather_than_read_as_text()
    {
        var bench = new FakeBench(new FakeInstrument(Scope, "192.168.1.22"));

        var answer = await Run(bench, BenchToolRunner.Query, """{"command": ":DISPlay:DATA?"}""");

        Assert.True(answer.IsError);
        Assert.Contains("binary block", answer.Text);
        Assert.Empty(bench.Written);
    }

    [Fact]
    public async Task An_instrument_a_script_is_driving_is_left_alone()
    {
        var bench = new FakeBench(new FakeInstrument(Meter, "192.168.1.21") { Busy = true });

        var answer = await Run(bench, BenchToolRunner.Query, """{"command": "*IDN?"}""");

        Assert.True(answer.IsError);
        Assert.Contains("A script is running on dmm", answer.Text);
        Assert.Empty(bench.Written);
    }

    [Fact]
    public async Task An_instrument_nobody_has_open_is_refused_with_the_names_that_are()
    {
        var bench = new FakeBench(new FakeInstrument(Generator, "192.168.1.20"), new FakeInstrument(Meter, "192.168.1.21"));

        var answer = await Run(bench, BenchToolRunner.Query, """{"instrument": "psu", "command": "*IDN?"}""");

        Assert.True(answer.IsError);
        Assert.Contains("No instrument called \"psu\" is open", answer.Text);
        Assert.Contains("gen (Siglent Technologies SDG2042X", answer.Text);
        Assert.Empty(bench.Written);
    }

    [Theory]
    [InlineData("192.168.1.21")]
    [InlineData("SDM3065X")]
    [InlineData("SDM36HCD801207")]
    [InlineData("dmm (Siglent Technologies SDM3065X at 192.168.1.21)")]
    public async Task An_instrument_is_found_by_its_address_model_serial_or_echoed_line(string named)
    {
        var bench = new FakeBench(new FakeInstrument(Generator, "192.168.1.20"), new FakeInstrument(Meter, "192.168.1.21"));

        var answer = await Run(bench, BenchToolRunner.Query, Args(("instrument", named), ("command", "*IDN?")));

        Assert.False(answer.IsError);
        Assert.Equal("192.168.1.21", Assert.Single(bench.Written).Host);
    }

    [Fact]
    public async Task Send_is_neither_offered_nor_run_until_the_gate_allows_changes()
    {
        var bench = new FakeBench(new FakeInstrument(Generator, "192.168.1.20"));
        var runner = new BenchToolRunner(bench);

        Assert.DoesNotContain(runner.Tools, t => t.Name == BenchToolRunner.Send);
        var answer = await runner.RunToolAsync(BenchToolRunner.Send, """{"command": "C1:OUTP ON"}""", default);

        Assert.True(answer.IsError);
        Assert.Empty(bench.Written);

        runner.AllowEdits = true;
        Assert.Contains(runner.Tools, t => t.Name == BenchToolRunner.Send && t.Changes);
    }

    [Fact]
    public async Task A_documented_command_is_sent_and_the_queue_read_after_it()
    {
        var bench = new FakeBench(new FakeInstrument(Generator, "192.168.1.20"));

        var answer = await Run(bench, BenchToolRunner.Send, """{"command": "C1:OUTP ON"}""", edits: true);

        Assert.False(answer.IsError);
        Assert.Contains("Sent C1:OUTP ON to gen. Its error queue is empty.", answer.Text);
        Assert.Equal(["SYST:ERR?", "C1:OUTP ON", "SYST:ERR?"], bench.Written.Select(w => w.Command));
        Assert.Equal(["C1:OUTP ON"], bench.Instruments[0].Sent);
    }

    [Fact]
    public async Task What_the_instrument_said_before_is_kept_apart_from_what_the_command_caused()
    {
        var gen = new FakeInstrument(Generator, "192.168.1.20");
        gen.Errors.Enqueue("-221,\"Settings conflict\"");
        gen.ErrorsAfter["C1:OUTP ON"] = "-222,\"Data out of range\"";
        var bench = new FakeBench(gen);

        var answer = await Run(bench, BenchToolRunner.Send, """{"command": "C1:OUTP ON"}""", edits: true);

        Assert.True(answer.IsError);
        Assert.Contains("It then reported: -222,\"Data out of range\".", answer.Text);
        Assert.Contains("already held, from earlier: -221,\"Settings conflict\"", answer.Text);
    }

    [Fact]
    public async Task An_undocumented_command_is_refused_with_the_near_ones_until_the_model_says_it_means_it()
    {
        var bench = new FakeBench(new FakeInstrument(Generator, "192.168.1.20"));

        var refused = await Run(bench, BenchToolRunner.Send, """{"command": ":SOURce1:FREQuency 1000"}""", edits: true);

        Assert.True(refused.IsError);
        Assert.Contains("is not in gen's catalog", refused.Text);
        Assert.Contains("undocumented", refused.Text);
        Assert.Empty(bench.Written);

        var sent = await Run(bench, BenchToolRunner.Send, """{"command": ":SOURce1:FREQuency 1000", "undocumented": true}""", edits: true);

        Assert.False(sent.IsError);
        Assert.Equal([":SOURce1:FREQuency 1000"], bench.Instruments[0].Sent);
    }

    [Fact]
    public async Task An_instrument_that_never_answers_the_error_query_is_asked_once_and_said_to_be_unknown()
    {
        var bench = new FakeBench(new FakeInstrument(Generator, "192.168.1.20") { AnswersErrors = false });

        var answer = await Run(bench, BenchToolRunner.Send, """{"command": "C1:OUTP OFF"}""", edits: true);

        Assert.False(answer.IsError);
        Assert.Contains("could not be read", answer.Text);
        Assert.Equal(["SYST:ERR?", "C1:OUTP OFF"], bench.Written.Select(w => w.Command));
    }

    [Fact]
    public async Task A_query_that_acts_goes_through_send_and_comes_back_with_its_reply()
    {
        var bench = new FakeBench(new FakeInstrument(Generator, "192.168.1.20") { Replies = { ["*TST?"] = "0" } });

        var answer = await Run(bench, BenchToolRunner.Send, """{"command": "*TST?"}""", edits: true);

        Assert.False(answer.IsError);
        Assert.Contains("which answered: 0", answer.Text);
    }

    [Fact]
    public async Task The_screen_comes_back_as_a_png_and_its_size_in_words()
    {
        byte[] bmp = Bmp24(3, 2, (x, y) => ((byte)(x * 80), (byte)(y * 100), 7));
        var bench = new FakeBench(new FakeInstrument(Scope, "192.168.1.22") { Screen = bmp });

        var answer = await Run(bench, BenchToolRunner.LookAtScreen, "{}");

        Assert.False(answer.IsError);
        Assert.Contains("3 × 2 pixels", answer.Text);
        byte[] png = Convert.FromBase64String(answer.Png!);
        var (w, h, rgb) = ReadPng(png);
        Assert.Equal((3, 2), (w, h));
        Assert.Equal([160, 100, 7], rgb.Skip((1 * 3 + 2) * 3).Take(3).Select(b => (int)b));
    }

    [Fact]
    public async Task An_instrument_whose_guide_has_no_screen_dump_is_not_asked_for_one()
    {
        var bench = new FakeBench(new FakeInstrument(Meter, "192.168.1.21"));

        var answer = await Run(bench, BenchToolRunner.LookAtScreen, "{}");

        Assert.True(answer.IsError);
        Assert.Empty(bench.Written);
    }

    [Fact]
    public async Task A_proposed_script_is_handed_over_whole_never_sent_and_its_undocumented_lines_named()
    {
        var bench = new FakeBench(new FakeInstrument(Generator, "192.168.1.20"));
        string script = "C1:OUTP ON\nDELAY 500\nC1:FREQ 1000";

        var answer = await Run(bench, BenchToolRunner.ProposeScript, Args(("kind", "instrument"), ("script", script)));

        Assert.False(answer.IsError);
        Assert.Equal(script, answer.Proposal);
        Assert.Contains("3 lines", answer.Text);
        Assert.Contains("C1:FREQ 1000", answer.Text);
        Assert.DoesNotContain("C1:OUTP ON |", answer.Text);
        Assert.Empty(bench.Written);
    }

    [Fact]
    public async Task An_argument_the_tool_does_not_take_is_refused_with_the_ones_it_does()
    {
        var bench = new FakeBench(new FakeInstrument(Meter, "192.168.1.21"));

        var answer = await Run(bench, BenchToolRunner.Query, """{"channel": "dmm", "command": "*IDN?"}""");

        Assert.True(answer.IsError);
        Assert.Contains("query takes no channel. It takes instrument and command.", answer.Text);
        Assert.Empty(bench.Written);
    }

    [Fact]
    public async Task Arguments_sent_as_a_string_of_json_and_numbers_as_text_are_read_as_meant()
    {
        var bench = new FakeBench(new FakeInstrument(Meter, "192.168.1.21"));

        var waited = await Run(bench, BenchToolRunner.Wait, JsonSerializer.Serialize("""{"seconds": "0.01"}"""));

        Assert.False(waited.IsError);
        Assert.Contains("Waited 0.01 s.", waited.Text);

        var tooLong = await Run(bench, BenchToolRunner.Wait, """{"seconds": 600}""");
        Assert.True(tooLong.IsError);
    }

    [Fact]
    public async Task A_tool_switched_off_is_neither_offered_nor_run()
    {
        var bench = new FakeBench(new FakeInstrument(Meter, "192.168.1.21"));
        var runner = new BenchToolRunner(bench) { Off = new HashSet<string> { BenchToolRunner.Query } };

        Assert.DoesNotContain(runner.Tools, t => t.Name == BenchToolRunner.Query);
        var answer = await runner.RunToolAsync(BenchToolRunner.Query, """{"command": "*IDN?"}""", default);

        Assert.True(answer.IsError);
        Assert.Contains("switched off", answer.Text);
        Assert.Empty(bench.Written);
    }

    [Fact]
    public void Every_schema_uses_only_the_keywords_every_provider_reads()
    {
        string[] allowed = ["type", "description", "properties", "required", "items", "enum", "minItems", "maxItems"];

        foreach (LlmTool tool in BenchToolRunner.All)
        {
            var schema = JsonNode.Parse(tool.InputSchemaJson)!.AsObject();
            Assert.Equal("object", (string?)schema["type"]);
            Walk(schema, tool.Name);
        }

        void Walk(JsonObject node, string where)
        {
            foreach (var (key, value) in node)
            {
                Assert.True(allowed.Contains(key), $"{where} uses {key}");
                if (key == "properties")
                {
                    foreach (var (name, property) in value!.AsObject())
                        Walk(property!.AsObject(), $"{where}.{name}");
                }
            }
        }
    }

    [Fact]
    public async Task A_stop_while_a_call_runs_is_let_through_rather_than_said_as_a_failure()
    {
        var bench = new FakeBench(new FakeInstrument(Meter, "192.168.1.21"));
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new BenchToolRunner(bench).RunToolAsync(BenchToolRunner.Wait, """{"seconds": 5}""", stop.Token));
    }

    // ------------------------------------------------------------------- helpers

    private static Task<LlmToolOutput> Run(FakeBench bench, string tool, string json, bool edits = false)
        => new BenchToolRunner(bench) { AllowEdits = edits }.RunToolAsync(tool, json, default);

    private static string Args(params (string Name, string Value)[] pairs)
    {
        var o = new JsonObject();
        foreach (var (name, value) in pairs) o[name] = value;
        return o.ToJsonString();
    }

    private static byte[] Bmp24(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        int stride = ((width * 24 + 31) / 32) * 4;
        var b = new byte[54 + stride * height];
        b[0] = (byte)'B';
        b[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(2), b.Length);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(28), 24);
        for (int y = 0; y < height; y++)
        {
            int row = 54 + (height - 1 - y) * stride;    // bottom row first, as BMPs are
            for (int x = 0; x < width; x++)
            {
                var (r, g, bl) = pixel(x, y);
                b[row + x * 3] = bl;
                b[row + x * 3 + 1] = g;
                b[row + x * 3 + 2] = r;
            }
        }
        return b;
    }

    // Just enough of a PNG reader for what ScreenPicture writes: one IDAT, every row Sub-filtered.
    private static (int Width, int Height, byte[] Rgb) ReadPng(byte[] png)
    {
        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
        int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
        int at = 8;
        var data = new MemoryStream();
        while (at < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            if (type == "IDAT") data.Write(png, at + 8, length);
            at += 12 + length;
        }
        data.Position = 0;
        using var z = new ZLibStream(data, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        byte[] rows = raw.ToArray();

        var rgb = new byte[width * height * 3];
        int stride = width * 3;
        for (int y = 0; y < height; y++)
        {
            Assert.Equal(1, rows[y * (stride + 1)]);
            for (int i = 0; i < stride; i++)
            {
                byte left = i >= 3 ? rgb[y * stride + i - 3] : (byte)0;
                rgb[y * stride + i] = (byte)(rows[y * (stride + 1) + 1 + i] + left);
            }
        }
        return (width, height, rgb);
    }
}

/// <summary>An instrument on <see cref="FakeBench"/>: what it answers, what is in its error queue, and what it was sent.</summary>
internal sealed class FakeInstrument(string identity, string host)
{
    public string Identity { get; } = identity;
    public string Host { get; } = host;
    public bool Busy { get; init; }
    public bool AnswersErrors { get; init; } = true;
    public byte[] Screen { get; init; } = [];
    public Dictionary<string, string> Replies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Queue<string> Errors { get; } = new();

    /// <summary>What the instrument puts in its error queue when it is sent a command.</summary>
    public Dictionary<string, string> ErrorsAfter { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every command that reached it other than the error query — what the tools changed.</summary>
    public List<string> Sent { get; } = [];
}

/// <summary>A bench of <see cref="FakeInstrument"/>s, keeping everything written to any of them, error queries included.</summary>
internal sealed class FakeBench(params FakeInstrument[] instruments) : IAssistantBench
{
    public List<FakeInstrument> Instruments { get; } = [.. instruments];

    public List<(string Host, string Command)> Written { get; } = [];

    public Task<IReadOnlyList<BenchInstrument>> ListAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<BenchInstrument>>(
            [.. Instruments.Select(i => new BenchInstrument(i.Host, i.Identity, i.Host, "VXI-11", i.Busy))]);

    public Task<CommandReference?> CatalogAsync(BenchInstrument instrument, CancellationToken ct)
        => Task.FromResult(CommandReference.ForIdentity(instrument.Identity));

    public Task<string> QueryAsync(BenchInstrument instrument, string command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        FakeInstrument i = Find(instrument);
        Written.Add((i.Host, command));
        if (command == "SYST:ERR?")
        {
            if (!i.AnswersErrors) throw new TimeoutException("No reply within 2000 ms.");
            return Task.FromResult(i.Errors.Count > 0 ? i.Errors.Dequeue() : "0,\"No error\"");
        }
        i.Sent.Add(command);
        Raise(i, command);
        return Task.FromResult(i.Replies.TryGetValue(command, out string? reply) ? reply : i.Identity);
    }

    public Task SendAsync(BenchInstrument instrument, string command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        FakeInstrument i = Find(instrument);
        Written.Add((i.Host, command));
        i.Sent.Add(command);
        Raise(i, command);
        return Task.CompletedTask;
    }

    public Task<byte[]> CaptureScreenAsync(BenchInstrument instrument, CancellationToken ct)
    {
        FakeInstrument i = Find(instrument);
        Written.Add((i.Host, "(screen)"));
        return Task.FromResult(i.Screen);
    }

    private static void Raise(FakeInstrument i, string command)
    {
        if (i.ErrorsAfter.TryGetValue(command, out string? error)) i.Errors.Enqueue(error);
    }

    private FakeInstrument Find(BenchInstrument b) => Instruments.Single(i => i.Host == b.Key);
}
