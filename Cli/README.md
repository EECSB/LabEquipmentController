# `lec` — the command line

The same [Core](../Core) library behind the desktop app, with a terminal front end: for
benches without a desktop, and for scripting a measurement into CI or a cron job. It
targets plain `net10.0` rather than `net10.0-windows`, so unlike the GUI it runs anywhere
.NET does — **Windows, Linux and macOS**, all three exercised on every CI run.

```bash
dotnet run --project Cli/LabEquipmentController.Cli.csproj -- scan --range 192.168.1.20-60
```

## The verbs

| Command | What it does |
|---|---|
| `lec scan` | Sweep the subnet (or `--range`) and identify what answers |
| `lec interfaces` | List local interfaces worth scanning |
| `lec ports` | List this machine's serial ports — listed, never probed |
| `lec id <address>` | `*IDN?`, plus the family and catalog it resolves to |
| `lec send <address> <cmd>…` | Send commands; any containing `?` is read back |
| `lec run <address> <file>` | Run a `.scpi` script against one instrument |
| `lec seq <file> --device gen=…` | Run a multi-instrument `.seq` script |
| `lec watch <address> <query>…` | Poll on an interval, one CSV row per reading |
| `lec screenshot <address>` | Save the instrument's screen |
| `lec capture <address>` | Read a scope trace as CSV or SVG |
| `lec plot <csv-file>` | Draw a recorded CSV as an SVG chart |
| `lec catalog <text>` | Search all 36 catalogs, by syntax or description |
| `lec mcp [<address>…]` | Serve instruments to an MCP client — Claude Code, Claude Desktop |
| `lec version` | Version, runtime, and the catalog totals |

Addresses take a bare host (raw socket on 5025), `host:port`, `vxi://host`,
`serial://COM3`, or a full VISA resource string. `--json` and `--csv` make any result
machine-readable, `--out <file>` writes it to disk, and `--quiet` drops everything but the
result. Exit codes are 0 for success, 1 for a failure, 2 for a usage mistake — so `lec`
composes into a shell script.

## Serial

Every verb that takes an address takes a serial one, and nothing else changes:

```bash
lec ports
lec id serial://COM3
lec watch serial:///dev/ttyUSB0 "MEAS:VOLT:DC?" --every 1s --out log.csv
```

Line settings ride in the address, because a command line has one string and nothing else
to put them in — and because there is nothing on an RS-232 wire to negotiate them with:

```bash
lec id "serial://COM3?baud=115200&parity=even&term=crlf"
```

`baud`, `databits`, `parity`, `stopbits`, `flow` and `term`, defaulting to **9600-8-N-1, no
flow control, LF**. A bare number is the baud rate, so `serial://COM3?115200` works. Get
one wrong and nothing fails cleanly — an instrument at the wrong speed answers with
rubbish, and the wrong terminator looks exactly like a dead port — so an unreadable setting
is refused rather than defaulted.

**`lec ports` lists, it does not probe.** That is the one thing it does not share with
`lec scan`: a subnet sweep asks every address the same harmless question, while a serial
port cannot be asked anything until the settings already match, and the thing on the other
end may be a printer or a modem. In a shell (careful — this opens each port in turn, so
only run it where you know what is plugged in):

```bash
lec ports --csv | tail -n +2 | cut -d, -f2 | xargs -I{} lec id {} --quiet
```

## Live readings

`--stream` on `run` and `seq` sends each recorded row to stdout the moment it happens,
flushed, instead of printing a table when the script ends — so a twenty-minute sweep is
watchable for twenty minutes:

```bash
lec run 192.168.1.20 sweep.scpi --stream | tee live.csv
```

`lec watch` is the same idea without a script: poll one or more queries forever (or
`--count n` times) and emit a timestamped CSV row per reading.

```bash
lec watch 192.168.1.22 "MEASure:VOLTage:DC?" --every 500ms --out log.csv
```

## Pictures and plots

`lec screenshot` writes the instrument's own image bytes — the format is the instrument's
choice, so a Rigol sends BMP and a Tektronix set to PNG sends PNG, and the file extension
is corrected to match what actually arrived rather than what you named it. Nothing is
re-encoded: the only in-box converter is Windows-only, and a native imaging dependency
would cost this tool its portability.

**Plots** come out as SVG, for the same reason — it is text, it opens in any browser, it
scales, and it needs no library. `lec capture --svg` draws a scope trace, `--svg` on
`run`/`seq` draws whatever the script recorded, and `lec plot` draws a CSV recorded
earlier by any of them (or by the GUI).

## Claude Code and Claude Desktop: `lec mcp`

`lec mcp` serves the instruments named on its command line to a Model Context Protocol
client, over standard input and output, with the tools the app's AI assistant has (SPEC
§11d): `list_instruments`, `find_commands`, `query`, `send`, `read_errors`,
`look_at_screen`, `wait` and `propose_script`. The client starts it:

```bash
claude mcp add lec -- lec mcp 192.168.1.20 vxi://192.168.1.21
```

For Claude Desktop, the same command and arguments go under `mcpServers` in its
configuration file.

- **The addresses given are all a model can reach.** There is no tool that dials another
  one. An instrument that was off when the client started the server is tried again
  whenever the instruments are listed.
- **`send` is the only tool that changes an instrument**, one command a call, and it is
  marked destructive so the client asks before it runs. It refuses a command the
  instrument's catalog does not document unless the model says it means it, and reads the
  error queue before and after so the answer says what that command caused.
  **`--read-only`** serves a bench nothing may change: `send` is not offered.
- **A script is only proposed.** `propose_script` checks it against the catalogs and hands
  it back whole; nothing here runs it. Save it and use `lec run` or `lec seq`.
- **Standard output is the protocol's.** What a person would want to read, such as which
  instruments answered, goes to standard error, which clients keep as the server's log.
- **It can work the bench page you have open instead.** An instrument takes one connection at a
  time, so one the bench already holds cannot also be reached from here. Under the gear on the
  bench page, AI settings, MCP server, press Start and tell Claude the code it shows: `link_tab`
  joins the two, and every tool then works the instruments open on that page, each call listed
  there, until `unlink_tab` or Stop. `claude mcp add lec -- lec mcp` needs no addresses for this.
  It listens on 127.0.0.1 port 43717 (`--link-port` for another, `0` for none), and the code is
  the lock.

## Build a standalone binary

For whichever machine will run it:

```bash
dotnet publish Cli/LabEquipmentController.Cli.csproj -c Release -r linux-x64 --self-contained false -p:PublishSingleFile=true
```

Swap `linux-x64` for `osx-arm64`, `osx-x64`, `win-x64` or `linux-arm64`. The result is a
single ~11 MB `lec` that needs the .NET 10 runtime; add `--self-contained true` for one
that needs nothing at all.

**On Linux and macOS, build the projects rather than the solution** — the solution
contains the WinForms app, which is Windows-only by nature:

```bash
dotnet build Cli/LabEquipmentController.Cli.csproj && dotnet test Tests/LabEquipmentController.Tests.csproj
```

## Where things are

| Path | What |
|------|------|
| `Program.cs` | Verb dispatch, Ctrl+C, exit codes |
| `Endpoint.cs` | A parsed address (Core's `InstrumentAddress`) → a serialized client to talk to it with |
| `Capture.cs` | Image-format sniffing, CSV reading, interval parsing |
| `Verbs/` | The verbs themselves — scan · id · send · run · seq · watch · … — and the argument grammar and usage text behind them |
| `Mcp/` | `lec mcp`: the instruments it was started for (`McpBench`) and the protocol's two handlers over Core's bench tools (`McpServing`) |
| `Output/` | Table, CSV and JSON shapes; hand-written SVG plots; and `--stream`'s flushed row pump |

## Tests

`lec`'s own argument grammar, output shapes and exit codes are covered by
[Tests/Cli](../Tests/Cli), and CI additionally *runs* the built binary on all three
platforms — version, a catalog search, a plot — because a cross-compile that never runs
proves nothing about whether the catalogs load or something reached for a Windows-only API.
