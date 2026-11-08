using LabEquipmentController.Web.Bench;
using LabEquipmentController.Web.Client.Contracts;

namespace LabEquipmentController.Tests;

/// <summary>
/// A run's record on disk (<see cref="RunFiles"/>): each list in a file of its own, read back from
/// any point, bounded by what it may spend, and gone with its run.
/// </summary>
public sealed class RunFilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lec-runfiles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch { /* a temp folder that outlives the run is not worth failing a test over */ }
    }

    private static ScriptOutputLine Line(int i) => new($"line {i}", "Info");

    [Fact]
    public void A_list_reads_back_from_any_point_on_either_side_of_where_it_marks()
    {
        var files = new RunFiles(_folder);
        using var list = files.Open<ScriptOutputLine>("run.lines");

        for (int i = 0; i < 3000; i++) list.Add(Line(i));

        Assert.Equal(3000, list.Count);

        // A mark every 1024 items: from one, from either side of one, and from nowhere near one.
        foreach (int from in new[] { 0, 1, 1023, 1024, 1025, 2047, 2048, 2999 })
        {
            var (items, start) = list.Read(from, 10, long.MaxValue);

            Assert.Equal(from, start);
            Assert.Equal(Enumerable.Range(from, Math.Min(10, 3000 - from)).Select(i => $"line {i}"), items.Select(l => l.Text));
        }

        // Past the end is the end, said as where the nothing starts; and before the start is the start.
        Assert.Equal((0, 3000), (list.Read(5000, 10, long.MaxValue).Items.Count, list.Read(5000, 10, long.MaxValue).From));
        Assert.Equal(0, list.Read(-4, 10, long.MaxValue).From);
    }

    [Fact]
    public void A_read_stops_at_its_count_or_its_bytes_and_always_takes_one()
    {
        var files = new RunFiles(_folder);
        using var list = files.Open<ScriptOutputLine>("run.lines");

        for (int i = 0; i < 100; i++) list.Add(Line(i));

        Assert.Equal(5, list.Read(0, 5, long.MaxValue).Items.Count);

        // A byte, which no item fits in, still answers one: a reader always gets further.
        Assert.Equal("line 0", Assert.Single(list.Read(0, 100, 1).Items).Text);

        // A hundred bytes is a few lines, and the last of them is the one that reached it.
        var (few, _) = list.Read(0, 100, 100);
        Assert.InRange(few.Count, 2, 99);
        Assert.Equal(Enumerable.Range(0, few.Count).Select(i => $"line {i}"), few.Select(l => l.Text));
    }

    [Fact]
    public void A_line_longer_than_one_read_of_the_file_comes_back_whole_and_is_stepped_over_whole()
    {
        var files = new RunFiles(_folder);
        using var list = files.Open<RecordedRow>("run.rows");

        // Two hundred thousand characters: several of the reads the list makes of its file.
        string wide = new('7', 200_000);
        list.Add(new RecordedRow(["1", wide]));
        list.Add(new RecordedRow(["2", "0.5"]));

        var (both, _) = list.Read(0, 10, long.MaxValue);
        Assert.Equal(wide, both[0].Values[1]);
        Assert.Equal(["2", "0.5"], both[1].Values);

        // And one read from past it lands on the next, whole.
        Assert.Equal(["2", "0.5"], Assert.Single(list.Read(1, 10, long.MaxValue).Items).Values);
    }

    [Fact]
    public void Past_its_bound_a_list_keeps_nothing_more_and_counts_what_it_did_not_keep()
    {
        // A hundred and twenty bytes: three lines of this size and not a fourth, with room left over.
        var files = new RunFiles(_folder, mostBytes: 120);
        using var list = files.Open<ScriptOutputLine>("run.lines");

        for (int i = 0; i < 10; i++) list.Add(Line(i));

        // Something small enough to fit in what is left is not kept either: after one is not kept,
        // none is, so what was kept is the first of them and what was not is all after it.
        list.Add(new ScriptOutputLine("", ""));

        Assert.InRange(list.Count, 1, 9);
        Assert.Equal(11, list.Count + list.Dropped);
        Assert.Equal(Enumerable.Range(0, list.Count).Select(i => $"line {i}"), list.Read(0, 100, long.MaxValue).Items.Select(l => l.Text));
    }

    [Fact]
    public void A_forgotten_list_takes_its_file_with_it_and_a_new_server_sweeps_what_a_killed_one_left()
    {
        var files = new RunFiles(_folder);
        var gone = files.Open<ScriptOutputLine>("gone.lines");
        gone.Add(Line(1));
        Assert.True(File.Exists(Path.Combine(_folder, "gone.lines")));

        gone.Dispose();
        Assert.False(File.Exists(Path.Combine(_folder, "gone.lines")));

        // What a server that was killed rather than stopped leaves behind, beside a run still going.
        File.WriteAllText(Path.Combine(_folder, "killed.rows"), "{\"values\":[\"1\"]}\n");
        using var held = files.Open<ScriptOutputLine>("held.lines");
        held.Add(Line(7));

        _ = new RunFiles(_folder);

        Assert.False(File.Exists(Path.Combine(_folder, "killed.rows")));

        // The run still going reads what it kept, whichever way the system takes a held file's name.
        Assert.Equal("line 7", Assert.Single(held.Read(0, 10, long.MaxValue).Items).Text);
    }

    [Fact]
    public void A_list_that_cannot_be_made_keeps_nothing_and_counts_everything_rather_than_refusing_the_run()
    {
        var files = new RunFiles(_folder);
        using var first = files.Open<ScriptOutputLine>("same.lines");

        // The same name twice: the second file cannot be made, which stands for any disk that refuses one.
        using var second = files.Open<ScriptOutputLine>("same.lines");
        second.Add(Line(1));
        second.Add(Line(2));

        Assert.Equal(0, second.Count);
        Assert.Equal(2, second.Dropped);
        Assert.Empty(second.Read(0, 10, long.MaxValue).Items);
    }
}
