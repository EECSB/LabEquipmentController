# Tests

The xUnit suite. Everything the app does that is not pixels lives in [Core](../Core), and
this is what holds it to its word — **1,821 tests against a fake instrument**, no hardware
and no network:

```bash
dotnet test Tests\LabEquipmentController.Tests.csproj
```

The bench tests in [Bench/](Bench/README.md) are in the same project and **skipped unless
`LEC_BENCH=1`**, so a run on a machine with no instruments is green and complete.

## One folder per area

| Folder | What it pins |
|---|---|
| `Ai/` | The three provider request shapes, the script author and its conversation, what it is told about the bench, the connection book, datasheet-to-text |
| `Catalogs/` | The guards that make SPEC §10 mechanical: coverage, freshness against disk, misprints, descriptions, the syntax matcher, extraction recipes, guide lookup |
| `Capture/` | Waveform dialects and their arithmetic, IEEE 488.2 blocks, multi-channel capture, the zoom view |
| `Transport/` | Raw socket, message framing, serial line settings and addresses, the serializer, VISA resource strings, deadlines, session history |
| `Discovery/` | Subnet sweep, host ranges, the serial port list and what counts as an identity on one, CSV export in both headers, `*IDN?` classification, live command discovery |
| `Scripting/` | Both runners, their threading, the language, the bundled examples |
| `Results/` | Recorded series, the plot's arithmetic, unit guessing |
| `Settings/` | The settings round-trip, and that a window writing its own fields keeps everyone else's |
| `Cli/` | `lec`'s grammar, output shapes and exit codes, and `lec seq` run end to end against fake instruments |
| `Web/` | The server's API surface, a sequence run through its own session and run code, what its script writer sends, and the icon the browser build serves |
| `Bench/` | The real instruments — off unless asked for. [Its own README](Bench/README.md) |

`FakeInstrumentClient.cs` stays at the root, because every folder reaches for it.

**The namespaces are flat on purpose.** Moving files into folders did not move them into
namespaces: `--filter FullyQualifiedName~Bench` is how the gated suite is selected, and
every other documented and remembered filter goes on working as it did.

```bash
dotnet test Tests\LabEquipmentController.Tests.csproj --filter "FullyQualifiedName~Catalog"
dotnet test Tests\LabEquipmentController.Tests.csproj --filter "FullyQualifiedName!~Bench"
```

## What is worth knowing about them

**The catalog guards are the backbone.** Every quick-command button, readout query and
bundled script line must be an instance of a syntax template in that family's catalog, or
the build fails — which is what stops an invented command ever reaching an instrument
(SPEC §10). `EmbeddedCatalogFreshnessTests` byte-compares every embedded resource against
the file on disk, so a stale build cannot quietly ship an old catalog, and
`CatalogTableTests` fails when the per-family table in SPEC.md drifts from what the
catalogs actually hold.

**The serial transport is tested where its logic is, not where its hardware is.** A build
server has no COM port, so `ScpiFramingTests` drives `ScpiFraming` — the message framing the
socket and the serial port share — against a scripted stream that delivers exactly what a
test says, when it says: a reply that stops mid-number, a block whose payload contains
newlines, a driver-level timeout, a hang-up before the terminator. What genuinely needs a
port is four bench tests, skipped unless `LEC_SERIAL` says where it is (see
[Bench/](Bench/README.md)).

The serial *scan* splits the same way. `SerialScannerTests` pins everything that is a
decision rather than an I/O — how a written list of baud rates is read, what a row says
about itself in each of its three states, and what counts as an identity rather than as
bytes arriving at the wrong speed. Opening ports is left to the bench suite, because there
is no honest way to fake a UART at the wrong baud rate and that is precisely the case worth
catching.

**Some tests exist only to fail.** `Rigol_and_Keysight_disagree_when_yorigin_is_not_zero`
passes only while the two decoders disagree: the vendors share a preamble layout and not an
arithmetic, so a decoder "simplified" into the other would draw a plausible trace at the
wrong offset and nothing else would notice.

## The other suites

- **The web client** is covered end to end by Playwright — see [Web/README.md](../Web/README.md).
- **The catalog toolchain** has its own checks: `node tools/scpi-extract/tests.js`, 37 cases,
  one per parser behaviour that regressed while the catalogs were being built.
- **The desktop app** has no end-to-end suite. It is the reference the web build is measured
  against, and its logic is covered here through `Core`.
