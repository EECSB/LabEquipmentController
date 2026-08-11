using System.Globalization;

namespace LabEquipmentController.Cli;

/// <summary>What the user asked for, once the argument list has been read.</summary>
public sealed class ParsedCommand
{
    public string Verb { get; init; } = "";
    /// <summary>Arguments that were not options, in order.</summary>
    public IReadOnlyList<string> Operands { get; init; } = Array.Empty<string>();
    /// <summary>Options by name, without the leading dashes. A flag maps to "".</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } =
        new Dictionary<string, string>();
    /// <summary>Set when the arguments could not be read; everything else is then undefined.</summary>
    public string? Error { get; init; }

    public bool Has(string name) => Options.ContainsKey(name);

    public string? Value(string name) => Options.TryGetValue(name, out var v) && v.Length > 0 ? v : null;

    /// <summary>An option that must be a positive integer — a timeout, a limit, a port.</summary>
    public bool TryInt(string name, int fallback, out int value)
    {
        value = fallback;
        var raw = Value(name);
        if (raw is null) return true;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return false;
        value = n;
        return true;
    }
}

/// <summary>
/// Reads an argument list. Deliberately hand-written rather than pulled from a package:
/// the whole grammar is "verb, operands, and --name[=value] options", and a dependency
/// that has to be restored on three platforms to parse that is a poor trade.
/// </summary>
public static class CommandLine
{
    /// <summary>Options that take a value; everything else is a flag.</summary>
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "range", "ports", "timeout", "limit", "family", "device", "out", "port", "interface",
        "every", "count", "channel",
    };

    public static ParsedCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new ParsedCommand { Verb = "help" };

        var operands = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // --device is the one option that may be repeated (one per instrument in a
        // sequence), so its values are collected into a single semicolon-joined string
        // rather than the last one silently winning.
        var repeated = new List<string>();
        string verb = "";

        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];

            if (a is "-h" or "--help" or "-?" or "help" && verb.Length == 0)
                return new ParsedCommand { Verb = "help", Operands = operands };

            if (a.StartsWith("--", StringComparison.Ordinal) || (a.Length > 1 && a[0] == '-' && !char.IsDigit(a[1])))
            {
                string name = a.TrimStart('-');
                string? inline = null;
                int eq = name.IndexOf('=');
                if (eq >= 0) { inline = name[(eq + 1)..]; name = name[..eq]; }

                if (name.Length == 0)
                    return new ParsedCommand { Error = $"'{a}' is not an option name." };

                if (ValueOptions.Contains(name))
                {
                    string? value = inline;
                    if (value is null)
                    {
                        // A value may follow as the next argument, but never one that is
                        // itself an option — "--range --json" is a missing value, not a
                        // range of "--json".
                        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                            return new ParsedCommand { Error = $"--{name} needs a value." };
                        value = args[++i];
                    }
                    if (name.Equals("device", StringComparison.OrdinalIgnoreCase)) repeated.Add(value);
                    else options[name] = value;
                }
                else
                {
                    if (inline is not null)
                        return new ParsedCommand { Error = $"--{name} is a flag and takes no value." };
                    options[name] = "";
                }
                continue;
            }

            if (verb.Length == 0) verb = a;
            else operands.Add(a);
        }

        if (repeated.Count > 0) options["device"] = string.Join(";", repeated);
        if (verb.Length == 0) return new ParsedCommand { Verb = "help" };

        return new ParsedCommand { Verb = verb.ToLowerInvariant(), Operands = operands, Options = options };
    }

    public const string Usage = """
        lec — Lab Equipment Controller, from a terminal.

        Discovers and drives SCPI instruments over Ethernet. Runs on Windows, Linux and
        macOS; every command it can send comes from a catalog transcribed from a vendor's
        own programming guide.

        USAGE
          lec <command> [arguments] [options]

        COMMANDS
          scan                     Sweep the network for instruments and identify them
          interfaces               List local network interfaces worth scanning
          id <address>             Ask one instrument what it is (*IDN?)
          send <address> <cmd>...  Send commands; any containing '?' is read back
          run <address> <file>     Run a .scpi script against one instrument
          seq <file>               Run a multi-instrument script (.seq)
          watch <address> <query>  Poll queries on an interval, one CSV row per reading
          screenshot <address>     Save the instrument's screen, in the format it sends
          capture <address>        Read a scope trace as CSV or SVG
          plot <csv-file>          Draw a recorded CSV as an SVG chart
          catalog <text>           Search the curated command catalogs
          version                  Print version and catalog totals

        ADDRESSES
          A bare host              192.168.1.20        (raw socket, port 5025)
          Host and port            192.168.1.20:5025
          VXI-11                   vxi://192.168.1.20  or  192.168.1.20:111
          A VISA resource string   TCPIP0::192.168.1.20::inst0::INSTR

        OPTIONS
          --range <spec>     Addresses to scan: 192.168.1.20-60, a bare 20-60, a /28 block,
                             or any comma-separated mixture. Default: the whole subnet.
          --interface <ip>   Which local interface's subnet to scan.
          --ports <list>     Ports to probe, comma-separated. Default: 5025,111,5555.
          --timeout <ms>     Per-operation timeout. Default: 5000 (2000 while scanning).
          --device <a=addr>  Bind a sequence alias to an address. Repeat per instrument.
          --every <interval> How often to poll while watching: 500ms, 2s, 1m. Default: 1s.
          --count <n>        Stop watching after n readings. Default: until Ctrl+C.
          --channel <n>      Which scope channel to capture. Default: 1.
          --limit <n>        Cap the rows a search prints. Default: 40.
          --family <name>    Restrict a catalog search to one instrument family.
          --out <file>       Write the result to a file instead of the terminal.
          --json  --csv      Machine-readable output.
          --svg              Draw the result as an SVG chart instead of a table.
          --stream           Emit each recorded row as it happens, not at the end.
          --quiet            Only results; no progress or headings.
          --help             This text.

        EXAMPLES
          lec scan --range 192.168.1.20-60
          lec id 192.168.1.20
          lec send 192.168.1.20 "*IDN?" ":MEASure:VPP? CHANnel1"
          lec run 192.168.1.20 sweep.scpi --out readings.csv
          lec run 192.168.1.20 sweep.scpi --stream | tee live.csv
          lec seq filter.seq --device gen=192.168.1.21 --device scope=192.168.1.20
          lec watch 192.168.1.22 "MEASure:VOLTage:DC?" --every 500ms --out log.csv
          lec screenshot 192.168.1.20 --out screen.png
          lec capture 192.168.1.20 --channel 1 --svg --out trace.svg
          lec plot readings.csv --out readings.svg
          lec catalog "VOLTage:DC" --family KeysightMultimeter

        The screen-capture format is the instrument's choice — a Rigol sends BMP, a
        Tektronix set to PNG sends PNG. The file is written in whatever arrived, and the
        extension corrected to match rather than trusting the name you gave.

        Commands drive real equipment. Check what is wired up before running a sweep.
        """;
}
