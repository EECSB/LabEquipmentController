using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

public class ScriptRunnerTests
{
    private static async Task<(FakeInstrumentClient client, List<string> output)> Run(
        string script, CancellationToken ct = default, string? throwOn = null)
    {
        var client = new FakeInstrumentClient { ThrowOn = throwOn };
        var output = new List<string>();
        await ScriptRunner.RunAsync(script, client, (t, k) => output.Add($"{k}:{t}"), _ => { }, ct);
        return (client, output);
    }

    /// <summary>Run and keep the recorded rows, for the COLUMNS/RECORD statements.</summary>
    private static async Task<(FakeInstrumentClient client, List<SequenceRow> rows)> RunRecording(
        string script, Dictionary<string, string>? responses = null)
    {
        var client = new FakeInstrumentClient();
        if (responses != null)
            foreach (var kv in responses) client.Responses[kv.Key] = kv.Value;

        var rows = new List<SequenceRow>();
        await ScriptRunner.RunAsync(script, client, (_, _) => { }, rows.Add, default);
        return (client, rows);
    }

    [Fact]
    public async Task Sends_commands_and_queries_in_order()
    {
        var (c, _) = await Run("*IDN?\n:RUN\n:CHANnel1:SCALe?");
        Assert.Equal(new[] { "QUERY:*IDN?", "SEND::RUN", "QUERY::CHANnel1:SCALe?" }, c.Log);
    }

    [Fact]
    public async Task Comments_and_blank_lines_are_ignored()
    {
        var (c, _) = await Run("# comment\n\n// another\n*IDN?");
        Assert.Equal(new[] { "QUERY:*IDN?" }, c.Log);
    }

    [Fact]
    public async Task Print_emits_info_without_touching_instrument()
    {
        var (c, output) = await Run("PRINT hello world");
        Assert.Empty(c.Log);
        Assert.Contains("Info:hello world", output);
    }

    [Fact]
    public async Task Query_response_is_reported()
    {
        var (_, output) = await Run("*IDN?");
        Assert.Contains("Response:resp:*IDN?", output);
    }

    [Fact]
    public async Task Repeat_runs_the_block_n_times()
    {
        var (c, _) = await Run("REPEAT 3\n  A?\nEND");
        Assert.Equal(new[] { "QUERY:A?", "QUERY:A?", "QUERY:A?" }, c.Log);
    }

    [Fact]
    public async Task Repeat_zero_skips_the_block()
    {
        var (c, _) = await Run("REPEAT 0\n  A?\nEND\nB?");
        Assert.Equal(new[] { "QUERY:B?" }, c.Log);
    }

    [Fact]
    public async Task Nested_repeat_expands_correctly()
    {
        var (c, _) = await Run("REPEAT 2\n A?\n REPEAT 3\n  B?\n END\nEND");
        Assert.Equal(
            new[] { "QUERY:A?", "QUERY:B?", "QUERY:B?", "QUERY:B?", "QUERY:A?", "QUERY:B?", "QUERY:B?", "QUERY:B?" },
            c.Log);
    }

    [Fact]
    public async Task End_without_repeat_reports_error_and_stops()
    {
        var (c, output) = await Run("A?\nEND\nB?");
        Assert.Equal(new[] { "QUERY:A?" }, c.Log);
        Assert.Contains(output, l => l.StartsWith("Error:"));
    }

    [Fact]
    public async Task Delay_waits_the_requested_time()
    {
        var sw = Stopwatch.StartNew();
        await Run("DELAY 300");
        sw.Stop();
        Assert.InRange(sw.ElapsedMilliseconds, 250, 2000);
    }

    [Fact]
    public async Task Cancellation_during_delay_stops_promptly_and_skips_rest()
    {
        var cts = new CancellationTokenSource();
        var client = new FakeInstrumentClient();
        var task = ScriptRunner.RunAsync("A?\nDELAY 5000\nB?", client, (_, _) => { }, _ => { }, cts.Token);
        await Task.Delay(150);
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1500, $"unwound in {sw.ElapsedMilliseconds} ms");
        Assert.DoesNotContain("QUERY:B?", client.Log);
        Assert.Contains("QUERY:A?", client.Log);
    }

    [Fact]
    public async Task Error_stops_the_script_at_the_failing_command()
    {
        var (c, output) = await Run("A?\n:BAD\nC?", throwOn: ":BAD");
        Assert.Contains("QUERY:A?", c.Log);
        Assert.DoesNotContain("QUERY:C?", c.Log);
        Assert.Contains(output, l => l.StartsWith("Error:ERROR on line"));
    }

    [Fact]
    public async Task Wait_is_an_alias_for_delay()
    {
        var sw = Stopwatch.StartNew();
        await Run("WAIT 200");
        sw.Stop();
        Assert.InRange(sw.ElapsedMilliseconds, 150, 2000);
    }

    // --------------------------------------------------------------- COLUMNS / -> / RECORD

    [Fact]
    public void Columns_reads_the_declared_headings()
    {
        Assert.Equal(new[] { "Freq", "Vpp" }, ScriptRunner.Columns("COLUMNS Freq, Vpp\n*IDN?"));
        Assert.Empty(ScriptRunner.Columns("*IDN?"));
    }

    [Fact]
    public async Task Capture_names_a_reply_and_record_writes_it_out()
    {
        var (client, rows) = await RunRecording("MEAS:VOLT?  -> v\nRECORD $v");

        // The arrow and its name are not part of the command that reaches the instrument.
        Assert.Contains("QUERY:MEAS:VOLT?", client.Log);
        SequenceRow row = Assert.Single(rows);
        Assert.Equal("resp:MEAS:VOLT?", Assert.Single(row.Values));
    }

    [Fact]
    public async Task Record_takes_several_values_and_repeats_with_the_loop()
    {
        var (_, rows) = await RunRecording(
            "COLUMNS A, B\nREPEAT 3\n  A?  -> a\n  B?  -> b\n  RECORD $a, $b\nEND");

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(new[] { "resp:A?", "resp:B?" }, r.Values));
    }

    [Fact]
    public async Task Columns_line_is_not_sent_to_the_instrument()
    {
        var (client, _) = await RunRecording("COLUMNS Freq, Vpp\n*IDN?");
        Assert.DoesNotContain(client.Log, l => l.Contains("COLUMNS"));
    }

    [Fact]
    public async Task Captured_values_substitute_into_later_commands()
    {
        var (client, _) = await RunRecording(
            "LEVEL? -> lv\nVOLT $lv",
            new Dictionary<string, string> { ["LEVEL?"] = "2.5" });

        Assert.Contains("SEND:VOLT 2.5", client.Log);
    }

    /// <summary>
    /// Substitution happens before the line is classified, so a captured value carrying a '?'
    /// turns the command it lands in into a query. The sequence runner has always behaved
    /// this way; pinned here so the two stay the same rather than drifting apart quietly.
    /// </summary>
    [Fact]
    public async Task A_substituted_question_mark_makes_the_line_a_query()
    {
        var (client, _) = await RunRecording(
            "LEVEL? -> lv\nVOLT $lv",
            new Dictionary<string, string> { ["LEVEL?"] = "what?" });

        Assert.Contains("QUERY:VOLT what?", client.Log);
    }
}
