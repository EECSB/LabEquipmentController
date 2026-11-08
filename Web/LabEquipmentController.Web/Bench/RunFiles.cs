using System.Buffers;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace LabEquipmentController.Web.Bench;

/// <summary>
/// Where the runs' records keep what the runs said and recorded: a folder of files, two for each
/// run, each gone when its run is forgotten.
/// </summary>
/// <remarks>
/// <strong>On disk, because a record held in memory had to be small.</strong> Until 2026-09-23 a
/// record held twenty thousand lines and as many rows in memory, and a run that went past them went
/// on with the rest counted and not kept: a bound set by what memory could spare, on a server a host
/// starts with three quarters of a GiB. A file spends the disk instead, and <see cref="MostBytes"/>
/// is what one list of one run may spend of it.
///
/// <strong>Each file is held open by the server that made it and never opened by name again.</strong>
/// Everything is read and written through that one handle, so a file a server can delete is a file
/// nobody holds: the leftovers of a server that was killed rather than stopped, which a server
/// sweeps away as it starts. On Windows a held file cannot be deleted at all; on Linux deleting one
/// takes its name and not its contents from whoever holds it. Either way two servers sharing a data
/// folder cannot take each other's runs, which is what running two from source on one machine does.
/// </remarks>
public sealed class RunFiles
{
    /// <summary>What one list of one run may take on disk unless told otherwise: a GiB.</summary>
    public const long DefaultMostBytes = 1L << 30;

    public RunFiles(string folder, long mostBytes = DefaultMostBytes)
    {
        Folder = folder;
        MostBytes = mostBytes;
        Directory.CreateDirectory(folder);
        Sweep();
    }

    public string Folder { get; }

    public long MostBytes { get; }

    /// <summary>
    /// The folder under this server's data, beside everything else it keeps; or, where that cannot be
    /// written, one under the machine's temporary folder, said in the log. A bench whose data folder is
    /// read-only still runs, as it did while its records were in memory.
    /// </summary>
    public static RunFiles Under(string dataDirectory, ILogger log)
    {
        string folder = Path.Combine(dataDirectory, "runs");

        try
        {
            return new RunFiles(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Runs cannot be kept in {Folder}; keeping them in the temporary folder instead.", folder);
            return Temporary();
        }
    }

    /// <summary>
    /// One folder under the machine's temporary one, shared by every server that uses it — tests,
    /// mostly — which the rule above makes safe.
    /// </summary>
    internal static RunFiles Temporary(long mostBytes = DefaultMostBytes)
        => new(Path.Combine(Path.GetTempPath(), "LabEquipmentController", "runs"), mostBytes);

    /// <summary>
    /// A list for one run, in a file of its own. One that cannot be made keeps nothing and counts
    /// everything it is given, so the run goes on and its reader is told the record is incomplete:
    /// a measurement is not refused because its record could not be written down.
    /// </summary>
    internal KeptList<T> Open<T>(string name)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var file = File.OpenHandle(Path.Combine(Folder, name), FileMode.CreateNew, FileAccess.ReadWrite,
                                       FileShare.None, FileOptions.DeleteOnClose);
            return new KeptList<T>(file, MostBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new KeptList<T>(null, 0);
        }
    }

    private void Sweep()
    {
        foreach (string file in Directory.EnumerateFiles(Folder))
        {
            try { File.Delete(file); }
            catch (IOException) { }                    // held: a run of another server, still going
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>
/// One list of a run's record, what it said or what it recorded, kept in a file: added to as the
/// run goes, and read from any point.
/// </summary>
/// <remarks>
/// <strong>A line of JSON an item</strong>, which is what makes a point in the list findable without
/// an index of every item: the serializer never writes a newline inside a value, so the items are
/// the lines. Where every <see cref="Stride"/>th item starts is kept, and a read from anywhere else
/// counts lines on from the one before it.
///
/// <strong>Once one item is not kept, none after it is.</strong> An item that did not fit, or a write
/// the disk refused, would otherwise leave a hole that reads as whole around it, and "the first N,
/// and not the M after them" is only true if all M are after.
///
/// Not safe for two callers at once; the record's lock is what orders them.
/// </remarks>
internal sealed class KeptList<T>(SafeFileHandle? file, long most) : IDisposable
{
    internal const int Stride = 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ReadOnlyMemory<byte> Newline = "\n"u8.ToArray();

    private readonly List<long> _marks = [0];
    private long _length;
    private bool _closed = file is null;

    /// <summary>How many it holds.</summary>
    public int Count { get; private set; }

    /// <summary>How many it was given and did not keep.</summary>
    public int Dropped { get; private set; }

    public void Add(T item)
    {
        if (!_closed)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(item, Json);

            if (_length + json.Length + 1 <= most)
            {
                try
                {
                    RandomAccess.Write(file!, new ReadOnlyMemory<byte>[] { json, Newline }, _length);
                    _length += json.Length + 1;
                    if (++Count % Stride == 0) _marks.Add(_length);
                    return;
                }
                catch (IOException)
                {
                    // The disk is full, or gone. Counted below, like anything else not kept.
                }
            }

            _closed = true;
        }

        Dropped++;
    }

    /// <summary>
    /// The items from <paramref name="from"/> on, and where they start: <paramref name="from"/>, held
    /// between none and all of them. At most <paramref name="most"/> items, and none after the one
    /// that reaches <paramref name="mostBytes"/>; but always one if there is one, so a reader always
    /// gets further.
    /// </summary>
    public (List<T> Items, int From) Read(int from, int most, long mostBytes)
    {
        int start = Math.Clamp(from, 0, Count);
        var items = new List<T>();
        if (start == Count || most <= 0) return (items, start);

        int skip = start % Stride;
        long at = _marks[start / Stride];
        long taken = 0;
        var line = new ArrayBufferWriter<byte>();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

        try
        {
            while (at < _length)
            {
                int got = RandomAccess.Read(file!, buffer.AsSpan(0, (int)Math.Min(buffer.Length, _length - at)), at);
                if (got <= 0) break;
                at += got;

                var rest = buffer.AsSpan(0, got);
                while (rest.Length > 0)
                {
                    int end = rest.IndexOf((byte)'\n');
                    if (end < 0)
                    {
                        // A line that goes on into the next read. One being skipped is not kept.
                        if (skip == 0) line.Write(rest);
                        break;
                    }

                    if (skip > 0)
                    {
                        skip--;
                    }
                    else
                    {
                        ReadOnlySpan<byte> whole = rest[..end];
                        if (line.WrittenCount > 0)
                        {
                            line.Write(whole);
                            whole = line.WrittenSpan;
                        }

                        items.Add(JsonSerializer.Deserialize<T>(whole, Json)!);
                        taken += whole.Length + 1;
                        line.ResetWrittenCount();

                        if (items.Count >= most || taken >= mostBytes) return (items, start);
                    }

                    rest = rest[(end + 1)..];
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (items, start);
    }

    public void Dispose()
    {
        _closed = true;
        file?.Dispose();
    }
}
