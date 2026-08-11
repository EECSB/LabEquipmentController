namespace LabEquipmentController.Cli;

/// <summary>
/// Writes recorded rows out as they happen, rather than as a table at the end.
/// </summary>
/// <remarks>
/// A sweep that takes twenty minutes should be watchable for twenty minutes, and a table
/// printed after the last row is not. With <c>--stream</c> each row reaches stdout the
/// moment the runner records it, CSV-shaped and flushed, so
///
///     lec run 192.168.1.20 sweep.scpi --stream | tee run.csv
///
/// fills the terminal and the file together. Flushing is the whole point and easy to
/// forget: .NET buffers stdout when it is a pipe rather than a console, so without the
/// explicit flush the reader on the other end sees nothing for minutes and then
/// everything at once — which looks exactly like a hang.
/// </remarks>
public sealed class RowStream : IDisposable
{
    private readonly TextWriter? _writer;
    private readonly bool _ownsWriter;
    private IReadOnlyList<string> _columns;
    private bool _headerWritten;

    public bool Streaming => _writer is not null;

    public RowStream(ParsedCommand cmd, IReadOnlyList<string> columns, TextWriter stdout)
    {
        _columns = columns;
        if (!cmd.Has("stream")) return;

        // --stream --out <file> streams into the file instead of the terminal, which is
        // what a long unattended run wants; the terminal then carries only progress.
        string? path = cmd.Value("out");
        if (path is null) { _writer = stdout; _ownsWriter = false; }
        else { _writer = new StreamWriter(path) { AutoFlush = true }; _ownsWriter = true; }
    }

    public void Write(IReadOnlyList<string> values)
    {
        if (_writer is null) return;
        if (!_headerWritten)
        {
            // A script with no COLUMNS line still records rows; name them by position so
            // the stream is self-describing either way.
            if (_columns.Count == 0)
                _columns = Enumerable.Range(1, values.Count).Select(i => $"Column {i}").ToList();
            _writer.Write(Output.Csv(_columns, []));
            _headerWritten = true;
        }
        _writer.Write(Output.Csv([], [values]));
        _writer.Flush();
    }

    public void Dispose()
    {
        if (_ownsWriter) _writer?.Dispose();
    }
}
