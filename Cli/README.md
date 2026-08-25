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
| `Output/` | Table, CSV and JSON shapes; hand-written SVG plots; and `--stream`'s flushed row pump |

## Tests

`lec`'s own argument grammar, output shapes and exit codes are covered by
[Tests/Cli](../Tests/Cli), and CI additionally *runs* the built binary on all three
platforms — version, a catalog search, a plot — because a cross-compile that never runs
proves nothing about whether the catalogs load or something reached for a Windows-only API.
