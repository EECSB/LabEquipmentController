using System.Net;
using System.Text;

namespace LabEquipmentController.Cli;

/// <summary>The verbs. Each returns a process exit code: 0 done, 1 failed, 2 misused.</summary>
public static class Commands
{
    public const int Ok = 0, Failed = 1, Misused = 2;

    /// <summary>Where a command's output goes — the terminal, or a file if --out was given.</summary>
    private static int Emit(ParsedCommand cmd, string text, TextWriter stdout)
    {
        string? path = cmd.Value("out");
        if (path is null) { stdout.Write(text); return Ok; }
        File.WriteAllText(path, text);
        if (!cmd.Has("quiet")) stdout.Write($"Wrote {path}\n");
        return Ok;
    }

    /// <summary>
    /// Render a recorded table in whichever shape was asked for, including --svg, which
    /// the generic renderer cannot do because it needs to know which column is the x axis.
    /// </summary>
    private static int EmitTable(ParsedCommand cmd, IReadOnlyList<string> headers,
                                 IReadOnlyList<IReadOnlyList<string>> rows, string title, TextWriter stdout)
    {
        if (!cmd.Has("svg")) return Emit(cmd, Output.Render(cmd, headers, rows), stdout);

        var series = Plot.FromTable(headers, rows);
        string xLabel = headers.Count > 0 ? headers[0] : "";
        string yLabel = series.Count == 1 ? series[0].Name : "";
        return Emit(cmd, Plot.Svg(title, xLabel, yLabel, series), stdout);
    }

    // ---------------------------------------------------------------- interfaces

    public static int Interfaces(ParsedCommand cmd, TextWriter stdout)
    {
        var rows = NetworkScanner.GetLocalInterfaces()
            .Select(i => (IReadOnlyList<string>)new[]
            {
                i.Name, i.Address.ToString(), $"/{i.PrefixLength}",
                i.HostCount.ToString(), i.HasGateway ? "yes" : "no",
            })
            .ToList();
        return Emit(cmd, Output.Render(cmd, ["Interface", "Address", "Prefix", "Hosts", "Gateway"], rows), stdout);
    }

    // ---------------------------------------------------------------------- scan

    public static async Task<int> Scan(ParsedCommand cmd, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (!cmd.TryInt("timeout", 2000, out int timeout))
            return Fail(stderr, "--timeout must be a positive number of milliseconds.", Misused);

        var ports = new List<int>();
        foreach (string p in (cmd.Value("ports") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(p.Trim(), out int n) || n is < 1 or > 65535)
                return Fail(stderr, $"'{p.Trim()}' is not a port number.", Misused);
            ports.Add(n);
        }
        if (ports.Count == 0) ports.AddRange(NetworkScanner.CommonScpiPorts);

        // Which subnet: the named interface, or the first one with a gateway, which is the
        // same choice the GUI's interface list makes by default.
        var interfaces = NetworkScanner.GetLocalInterfaces();
        if (interfaces.Count == 0)
            return Fail(stderr, "No usable network interface found.", Failed);

        LocalInterface? chosen = null;
        string? want = cmd.Value("interface");
        if (want is not null)
        {
            chosen = interfaces.FirstOrDefault(i =>
                i.Address.ToString() == want ||
                i.Name.Contains(want, StringComparison.OrdinalIgnoreCase));
            if (chosen is null)
                return Fail(stderr, $"No interface matches '{want}'. Run 'lec interfaces' to list them.", Misused);
        }
        chosen ??= interfaces.FirstOrDefault(i => i.HasGateway) ?? interfaces[0];

        List<IPAddress> hosts;
        bool capped;
        string? spec = cmd.Value("range");
        if (spec is not null)
        {
            if (!HostRange.TryParse(spec, chosen.Address, out var range, out string error) || range is null)
                return Fail(stderr, error.Length > 0 ? error : $"'{spec}' is not an address range.", Misused);
            hosts = range.Enumerate(65536, out capped);
        }
        else
        {
            hosts = NetworkScanner.EnumerateHosts(chosen.Address, chosen.Mask, 65536, out capped);
        }

        if (!cmd.Has("quiet"))
            stderr.Write($"Scanning {hosts.Count} address(es) on {chosen.Name} " +
                         $"({string.Join(", ", ports)})...\n");
        // The cap is said out loud rather than silently truncating the sweep — a scan that
        // quietly skipped half the subnet reads exactly like a subnet with nothing on it.
        if (capped && !cmd.Has("quiet"))
            stderr.Write("Note: the range was larger than 65536 addresses and was cut short.\n");

        var found = new List<ScpiDevice>();
        var progress = new Progress<ScpiDevice>(d =>
        {
            lock (found) found.Add(d);
            if (!cmd.Has("quiet") && !cmd.Has("json") && !cmd.Has("csv") && cmd.Value("out") is null)
                stderr.Write($"  {d.Endpoint}  {d.TransportName}  {d.Identity}\n");
        });

        List<ScpiDevice> devices;
        try
        {
            devices = await NetworkScanner.ScanAsync(hosts, ports, timeout, timeout, null, ct, progress);
        }
        catch (OperationCanceledException) { return Fail(stderr, "Scan cancelled.", Failed); }

        if (cmd.Has("csv") && cmd.Value("out") is not null)
            return Emit(cmd, ScanResultExport.ToCsv(devices), stdout);

        var rows = devices
            .Select(d => (IReadOnlyList<string>)new[]
                { d.Address.ToString(), d.Port.ToString(), d.TransportName, d.Identity })
            .ToList();

        if (rows.Count == 0 && !cmd.Has("json") && !cmd.Has("csv"))
        {
            stderr.Write("No instruments answered.\n");
            return Ok;
        }
        return Emit(cmd, Output.Render(cmd, ["Address", "Port", "Transport", "Identity"], rows), stdout);
    }

    // ------------------------------------------------------------------------ id

    public static async Task<int> Identify(ParsedCommand cmd, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (cmd.Operands.Count < 1) return Fail(stderr, "Usage: lec id <address>", Misused);
        if (!Endpoint.TryParse(cmd.Operands[0], out var ep, out string error)) return Fail(stderr, error, Misused);
        if (!cmd.TryInt("timeout", 5000, out int timeout))
            return Fail(stderr, "--timeout must be a positive number of milliseconds.", Misused);

        using var client = ep.CreateClient(timeout);
        try
        {
            await client.ConnectAsync(ct);
            string idn = (await client.QueryAsync("*IDN?", ct)).Trim();
            var family = InstrumentProfile.FamilyForIdentity(idn);
            var catalog = CommandReference.ForFamily(family);

            var rows = new List<IReadOnlyList<string>>
            {
                new[] { "Address",  ep.ToString() },
                new[] { "Identity", idn },
                new[] { "Family",   family.ToString() },
                new[] { "Catalog",  catalog is null ? "(none)" : $"{catalog.Instrument} — {catalog.Commands.Count} commands" },
            };
            return Emit(cmd, Output.Render(cmd, ["Field", "Value"], rows), stdout);
        }
        catch (Exception ex) { return Fail(stderr, ex.Message, Failed); }
    }

    // ---------------------------------------------------------------------- send

    public static async Task<int> Send(ParsedCommand cmd, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (cmd.Operands.Count < 2) return Fail(stderr, "Usage: lec send <address> <command> [<command>...]", Misused);
        if (!Endpoint.TryParse(cmd.Operands[0], out var ep, out string error)) return Fail(stderr, error, Misused);
        if (!cmd.TryInt("timeout", 5000, out int timeout))
            return Fail(stderr, "--timeout must be a positive number of milliseconds.", Misused);

        using var client = ep.CreateClient(timeout);
        var sb = new StringBuilder();
        try
        {
            await client.ConnectAsync(ct);
            foreach (string command in cmd.Operands.Skip(1))
            {
                // The same rule the console and both script runners use: a '?' makes it a
                // query. Nothing here decides on the user's behalf what an instrument
                // ought to answer.
                if (ScpiClient.IsQuery(command))
                {
                    string reply = (await client.QueryAsync(command, ct)).Trim();
                    if (cmd.Has("quiet")) sb.Append(reply).Append('\n');
                    else sb.Append(command).Append(" -> ").Append(reply).Append('\n');
                }
                else
                {
                    await client.SendAsync(command, ct);
                    if (!cmd.Has("quiet")) sb.Append(command).Append('\n');
                }
            }
        }
        catch (Exception ex) { stdout.Write(sb.ToString()); return Fail(stderr, ex.Message, Failed); }

        return Emit(cmd, sb.ToString(), stdout);
    }

    // ----------------------------------------------------------------------- run

    public static async Task<int> Run(ParsedCommand cmd, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (cmd.Operands.Count < 2) return Fail(stderr, "Usage: lec run <address> <script-file>", Misused);
        if (!Endpoint.TryParse(cmd.Operands[0], out var ep, out string error)) return Fail(stderr, error, Misused);
        string path = cmd.Operands[1];
        if (!File.Exists(path)) return Fail(stderr, $"No such script: {path}", Misused);
        if (!cmd.TryInt("timeout", 5000, out int timeout))
            return Fail(stderr, "--timeout must be a positive number of milliseconds.", Misused);

        string script = File.ReadAllText(path);
        var columns = ScriptRunner.Columns(script);
        var rows = new List<IReadOnlyList<string>>();
        bool failed = false;
        using var stream = new RowStream(cmd, columns, stdout);

        using var client = ep.CreateClient(timeout);
        try
        {
            await client.ConnectAsync(ct);
            await ScriptRunner.RunAsync(script, client,
                (line, kind) =>
                {
                    if (kind == ScriptOutputKind.Error) { failed = true; stderr.Write(line + "\n"); }
                    else if (!cmd.Has("quiet")) stderr.Write(line + "\n");
                },
                row => { rows.Add(row.Values); stream.Write(row.Values); },
                ct);
        }
        catch (Exception ex) { return Fail(stderr, ex.Message, Failed); }

        // A script that recorded nothing is a script that ran for its side effects; there
        // is no table to print and that is not a failure.
        if (rows.Count > 0 && !stream.Streaming)
            EmitTable(cmd, columns.Count > 0 ? columns : InferColumns(rows), rows, Path.GetFileName(path), stdout);

        return failed ? Failed : Ok;
    }

    // ----------------------------------------------------------------------- seq

    public static async Task<int> Sequence(ParsedCommand cmd, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (cmd.Operands.Count < 1) return Fail(stderr, "Usage: lec seq <script-file> --device <alias>=<address> ...", Misused);
        string path = cmd.Operands[0];
        if (!File.Exists(path)) return Fail(stderr, $"No such script: {path}", Misused);
        if (!cmd.TryInt("timeout", 5000, out int timeout))
            return Fail(stderr, "--timeout must be a positive number of milliseconds.", Misused);

        string script = File.ReadAllText(path);
        var required = SequenceRunner.Requirements(script);

        var bindings = new Dictionary<string, Endpoint>(StringComparer.OrdinalIgnoreCase);
        foreach (string binding in (cmd.Value("device") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = binding.IndexOf('=');
            if (eq <= 0) return Fail(stderr, $"--device wants <alias>=<address>, got '{binding}'.", Misused);
            string alias = binding[..eq].Trim();
            if (!Endpoint.TryParse(binding[(eq + 1)..], out var ep, out string error))
                return Fail(stderr, $"--device {alias}: {error}", Misused);
            bindings[alias] = ep;
        }

        // The GUI binds aliases by picking discovered instruments off a list; here the user
        // types them, so the script's own DEVICE lines are what says whether they got them
        // all. Reporting every missing alias at once beats failing on the first.
        var missing = required.Where(r => !bindings.ContainsKey(r.Alias)).ToList();
        if (missing.Count > 0)
            return Fail(stderr, "This script needs an address for: " +
                string.Join(", ", missing.Select(m => $"{m.Alias} ({m.Model})")) +
                ".\nGive each one with --device <alias>=<address>.", Misused);

        var clients = new Dictionary<string, IInstrumentClient>(StringComparer.OrdinalIgnoreCase);
        var columns = SequenceRunner.Columns(script);
        var rows = new List<IReadOnlyList<string>>();
        bool failed = false;
        using var stream = new RowStream(cmd, columns, stdout);
        try
        {
            foreach (var (alias, ep) in bindings)
            {
                var client = ep.CreateClient(timeout);
                clients[alias] = client;
                if (!cmd.Has("quiet")) stderr.Write($"Connecting {alias} -> {ep}\n");
                await client.ConnectAsync(ct);
            }

            await SequenceRunner.RunAsync(script,
                alias => clients.TryGetValue(alias, out var c) ? c : null,
                (line, kind) =>
                {
                    if (kind == ScriptOutputKind.Error) { failed = true; stderr.Write(line + "\n"); }
                    else if (!cmd.Has("quiet")) stderr.Write(line + "\n");
                },
                row => { rows.Add(row.Values); stream.Write(row.Values); },
                ct);
        }
        catch (Exception ex) { return Fail(stderr, ex.Message, Failed); }
        finally
        {
            foreach (var c in clients.Values) c.Dispose();
        }

        if (rows.Count > 0 && !stream.Streaming)
            EmitTable(cmd, columns.Count > 0 ? columns : InferColumns(rows), rows, Path.GetFileName(path), stdout);

        return failed ? Failed : Ok;
    }

    /// <summary>A script with no COLUMNS line still records rows; name them positionally.</summary>
    private static IReadOnlyList<string> InferColumns(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        int n = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        return Enumerable.Range(1, n).Select(i => $"Column {i}").ToList();
    }

    // ------------------------------------------------------------------- catalog

    public static int Catalog(ParsedCommand cmd, TextWriter stdout, TextWriter stderr)
    {
        string needle = cmd.Operands.Count > 0 ? cmd.Operands[0] : "";
        if (!cmd.TryInt("limit", 40, out int limit))
            return Fail(stderr, "--limit must be a positive number.", Misused);

        var families = Enum.GetValues<InstrumentFamily>().AsEnumerable();
        string? wantFamily = cmd.Value("family");
        if (wantFamily is not null)
        {
            var matched = Enum.GetValues<InstrumentFamily>()
                .Where(f => f.ToString().Contains(wantFamily, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matched.Count == 0)
                return Fail(stderr, $"No instrument family matches '{wantFamily}'.", Misused);
            families = matched;
        }

        var rows = new List<IReadOnlyList<string>>();
        int total = 0;
        foreach (var family in families)
        {
            var catalog = CommandReference.ForFamily(family);
            if (catalog is null) continue;
            foreach (var c in catalog.Commands)
            {
                if (needle.Length > 0 && !SearchHit(c, needle)) continue;
                total++;
                if (rows.Count < limit)
                    rows.Add(new[] { family.ToString(), c.Syntax, c.BenchVerified ? "yes" : "", c.Description });
            }
        }

        string text = Output.Render(cmd, ["Family", "Syntax", "Verified", "Description"], rows);
        // Truncation is stated, not silent: a search that quietly showed the first 40 of
        // 900 hits reads like a search that found 40.
        if (total > rows.Count && !cmd.Has("json") && !cmd.Has("csv") && !cmd.Has("quiet"))
            text += $"\n{rows.Count} of {total} matches shown. Use --limit to see more.\n";
        return Emit(cmd, text, stdout);
    }

    // ---------------------------------------------------------------- screenshot

    public static async Task<int> Screenshot(ParsedCommand cmd, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (cmd.Operands.Count < 1) return Fail(stderr, "Usage: lec screenshot <address> [--out <file>]", Misused);
        if (!Endpoint.TryParse(cmd.Operands[0], out var ep, out string error)) return Fail(stderr, error, Misused);
        // A full-screen BMP is around a megabyte; the GUI gives the transfer the same
        // headroom over the user's timeout rather than failing a working capture.
        if (!cmd.TryInt("timeout", 15000, out int timeout))
            return Fail(stderr, "--timeout must be a positive number of milliseconds.", Misused);

        using var client = ep.CreateClient(timeout);
        try
        {
            await client.ConnectAsync(ct);
            string idn = (await client.QueryAsync("*IDN?", ct)).Trim();
            var profile = InstrumentProfile.ForIdentity(idn);

            string? capture = profile.ScreenCaptureCommand;
            if (string.IsNullOrEmpty(capture))
                return Fail(stderr,
                    $"No screen-capture command is documented for this instrument ({InstrumentProfile.FamilyForIdentity(idn)}).",
                    Failed);

            foreach (string setup in profile.ScreenCaptureSetup)
                await client.SendAsync(setup, ct);

            byte[] data = await client.QueryBinaryAsync(capture, ct);
            if (data.Length == 0) return Fail(stderr, "The instrument returned no image data.", Failed);

            string requested = cmd.Value("out") ?? "screenshot.png";
            string path = Capture.PathFor(requested, data);
            File.WriteAllBytes(path, data);

            if (!cmd.Has("quiet"))
            {
                // Say so when the name had to change: the instrument chooses the format,
                // and silently writing a BMP into a file called .png is how a screenshot
                // ends up unopenable three tools later.
                if (!string.Equals(path, requested, StringComparison.Ordinal))
                    stderr.Write($"The instrument sent {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()}, " +
                                 $"not {Path.GetExtension(requested).TrimStart('.').ToUpperInvariant()}; wrote {path} instead.\n");
                stdout.Write($"Wrote {path} ({data.Length:N0} bytes) from {capture}\n");
            }
            return Ok;
        }
        catch (Exception ex) { return Fail(stderr, ex.Message, Failed); }
    }

    // ------------------------------------------------------------------- capture

    public static async Task<int> CaptureWaveform(ParsedCommand cmd, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (cmd.Operands.Count < 1) return Fail(stderr, "Usage: lec capture <address> [--channel <n>] [--svg] [--out <file>]", Misused);
        if (!Endpoint.TryParse(cmd.Operands[0], out var ep, out string error)) return Fail(stderr, error, Misused);
        if (!cmd.TryInt("timeout", 15000, out int timeout))
            return Fail(stderr, "--timeout must be a positive number of milliseconds.", Misused);
        if (!cmd.TryInt("channel", 1, out int channel))
            return Fail(stderr, "--channel must be a positive number.", Misused);

        using var client = ep.CreateClient(timeout);
        try
        {
            await client.ConnectAsync(ct);
            string idn = (await client.QueryAsync("*IDN?", ct)).Trim();
            var profile = InstrumentProfile.ForIdentity(idn);

            if (!profile.SupportsWaveformCapture)
                return Fail(stderr,
                    $"No waveform-transfer dialect is documented for this instrument ({InstrumentProfile.FamilyForIdentity(idn)}).",
                    Failed);

            var capture = await WaveformReader.ReadAsync(client, profile.WaveformDialect, channel, ct);
            if (capture.Samples.Count == 0) return Fail(stderr, "The instrument returned no samples.", Failed);

            if (!cmd.Has("quiet"))
                stderr.Write($"{capture.Samples.Count:N0} samples, {Output.Seconds(capture.XIncrement * capture.Samples.Count)} s\n");

            string text = cmd.Has("svg")
                ? Capture.ToSvg(capture, $"{idn} — channel {channel}")
                : Capture.ToCsv(capture);
            return Emit(cmd, text, stdout);
        }
        catch (Exception ex) { return Fail(stderr, ex.Message, Failed); }
    }

    // ---------------------------------------------------------------------- plot

    /// <summary>Draw a CSV recorded earlier — by this CLI, the GUI, or anything else.</summary>
    public static int PlotFile(ParsedCommand cmd, TextWriter stdout, TextWriter stderr)
    {
        if (cmd.Operands.Count < 1) return Fail(stderr, "Usage: lec plot <csv-file> [--out <file.svg>]", Misused);
        string path = cmd.Operands[0];
        if (!File.Exists(path)) return Fail(stderr, $"No such file: {path}", Misused);

        var (headers, rows) = Capture.ReadCsv(File.ReadAllText(path));
        if (headers.Count < 2) return Fail(stderr, "A plot needs at least two columns: an x axis and one series.", Misused);

        var series = Plot.FromTable(headers, rows);
        if (series.Count == 0)
            return Fail(stderr, "No column after the first held numbers to plot.", Failed);

        string svg = Plot.Svg(Path.GetFileName(path), headers[0],
                              series.Count == 1 ? series[0].Name : "", series);
        return Emit(cmd, svg, stdout);
    }

    // --------------------------------------------------------------------- watch

    public static async Task<int> Watch(ParsedCommand cmd, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (cmd.Operands.Count < 2)
            return Fail(stderr, "Usage: lec watch <address> <query> [<query>...] [--every 1s] [--count <n>]", Misused);
        if (!Endpoint.TryParse(cmd.Operands[0], out var ep, out string error)) return Fail(stderr, error, Misused);
        if (!cmd.TryInt("timeout", 5000, out int timeout))
            return Fail(stderr, "--timeout must be a positive number of milliseconds.", Misused);
        if (!Capture.TryParseInterval(cmd.Value("every") ?? "1s", out int everyMs))
            return Fail(stderr, "--every wants a duration: 500ms, 2s, 1m.", Misused);
        if (!cmd.TryInt("count", int.MaxValue, out int count))
            return Fail(stderr, "--count must be a positive number.", Misused);

        var queries = cmd.Operands.Skip(1).ToList();
        var headers = new List<string> { "Seconds" };
        headers.AddRange(queries);

        // The output is CSV on stdout, flushed per reading, because the point of watching
        // is to pipe it somewhere — tee, a logger, a plotting script — while it runs.
        TextWriter writer = cmd.Value("out") is string p ? new StreamWriter(p) { AutoFlush = true } : stdout;
        try
        {
            using var client = ep.CreateClient(timeout);
            await client.ConnectAsync(ct);

            if (!cmd.Has("quiet"))
                stderr.Write($"Polling {ep} every {everyMs} ms. Ctrl+C to stop.\n");
            writer.Write(Output.Csv(headers, []));
            writer.Flush();

            var started = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
            {
                var row = new List<string> { Output.Seconds(started.Elapsed.TotalSeconds) };
                foreach (string q in queries)
                {
                    try { row.Add((await client.QueryAsync(q, ct)).Trim()); }
                    catch (OperationCanceledException) { throw; }
                    // One failed reading should not end a long watch: record it and carry
                    // on, so a momentary timeout leaves a gap rather than a stopped log.
                    catch (Exception ex) { row.Add(""); stderr.Write($"! {q}: {ex.Message}\n"); }
                }
                writer.Write(Output.Csv([], [row]));
                writer.Flush();

                if (i + 1 < count)
                {
                    try { await Task.Delay(everyMs, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        catch (OperationCanceledException) { /* Ctrl+C is how a watch normally ends. */ }
        catch (Exception ex) { return Fail(stderr, ex.Message, Failed); }
        finally { if (!ReferenceEquals(writer, stdout)) writer.Dispose(); }

        return Ok;
    }

    /// <summary>Does this entry answer the search text?</summary>
    /// <remarks>
    /// Three ways, because a catalog entry is written the way a *guide* prints it and a
    /// search is typed the way a command is *sent*. "VOLTage:DC:NPLC" is a real Fluke
    /// command and appears nowhere in that catalog as a substring, because the entry reads
    /// "[SENSe:]VOLTage[:DC]:NPLC" — optional nodes and all. ScpiSyntax already knows how
    /// to decide whether a typed command is an instance of a printed template; that is the
    /// same question, and asking it here means the search agrees with the guard that keeps
    /// invented commands out of the app.
    /// </remarks>
    internal static bool SearchHit(CommandRef c, string needle)
        => c.Syntax.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || c.Description.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || ScpiSyntax.Matches(needle, c.Syntax);

    // ------------------------------------------------------------------- version

    public static int Version(ParsedCommand cmd, TextWriter stdout)
    {
        int catalogs = 0, commands = 0, verified = 0;
        foreach (var family in Enum.GetValues<InstrumentFamily>())
        {
            var c = CommandReference.ForFamily(family);
            if (c is null) continue;
            catalogs++;
            commands += c.Commands.Count;
            verified += c.Commands.Count(x => x.BenchVerified);
        }
        var version = typeof(Commands).Assembly.GetName().Version;
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "lec",      version?.ToString(3) ?? "1.0.0" },
            new[] { "Runtime",  Environment.Version.ToString() },
            new[] { "Platform", System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim() },
            new[] { "Catalogs", catalogs.ToString() },
            new[] { "Commands", commands.ToString() },
            new[] { "Bench-verified", verified.ToString() },
        };
        return Emit(cmd, Output.Render(cmd, ["Item", "Value"], rows), stdout);
    }

    private static int Fail(TextWriter stderr, string message, int code)
    {
        stderr.Write(message.TrimEnd() + "\n");
        return code;
    }
}
