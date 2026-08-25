# LabEquipmentController

Discover and talk to lab instruments from .NET over Ethernet or RS-232, using **SCPI**.

This is the engine behind [Lab Equipment
Controller](https://github.com/EECSB/LabEquipmentController), a software suite for
discovering and controlling lab instruments (oscilloscopes, function generators, …). It
scans the local network, lists the instruments it finds, and lets you connect to several at
once and drive each one from a command console, instrument-aware quick-command buttons, or
a small scripting window.

Originally built as a desktop app with **C# / WinForms** targeting **.NET 10**
(`net10.0-windows`), and then extended so the same core engine also drives
**[a web version](https://github.com/EECSB/LabEquipmentController/blob/master/Web/README.md)**,
hosted in a Docker container, and
**[a cross-platform CLI](https://github.com/EECSB/LabEquipmentController/blob/master/Cli/README.md)**
(`lec`) for benches without a desktop and for scripting a measurement into CI. **This
package is that engine**, for driving instruments from your own code: no UI dependency, and
plain `net10.0`, so it runs on Windows, Linux and macOS.

The transports, catalogs and runners are the same code the desktop app and CLI have been
driving real instruments with, and the whole surface is covered by 1,775 tests.

**The [repository README](https://github.com/EECSB/LabEquipmentController) is the fuller
picture** — what the desktop app and the web build look like, screenshots of a real bench,
and the reasoning behind each part. This page is the library on its own.

## What it gives you

**Three transports, behind one interface.** A raw TCP socket (usually port 5025), a
hand-written **VXI-11** client for instruments that speak nothing else — including the
RPC portmapper lookup that VXI-11 requires and that most examples hard-code wrongly — and
**RS-232**, for the bench where the instrument predates Ethernet or simply has a serial
socket on the back.

```csharp
using LabEquipmentController;

using var client = new SerializedInstrumentClient(new ScpiClient("192.168.1.20", 5025));
await client.ConnectAsync();
string idn = await client.QueryAsync("*IDN?");
```

Or let an address decide, which is what the apps do — one parser for every spelling a
person might type, and one place that knows which transport goes with which:

```csharp
InstrumentAddress.TryParse("serial://COM3?baud=115200", out var address, out string error);
using var serial = new SerializedInstrumentClient(address.CreateClient(timeoutMs: 5000));
await serial.ConnectAsync();
```

`serial://COM3`, `serial:///dev/ttyUSB0` and `ASRL3::INSTR` all reach a serial port;
`vxi://host`, `tcp://host`, `host:port`, a bare host and a `TCPIP0::…` resource string all
reach a LAN one. Line settings ride along in the address — `?baud=115200&parity=even` —
because there is nothing on an RS-232 wire to negotiate them with, and they default to
9600-8-N-1. `SerialPorts.Names()` lists the ports without opening any; `SerialScanner`
opens the ones it is given, at the rates it is given, and checks what comes back before
calling it an identity — an instrument at the wrong baud rate does not fail to answer, it
answers with rubbish.

`SerializedInstrumentClient` admits one exchange at a time and queues the rest. That is
not politeness: VXI-11 writes a request record and reads its reply off the same stream, so
two overlapping calls interleave and corrupt both.

**Discovery.** Sweep a subnet or an address range and identify whatever answers.

```csharp
var iface = NetworkScanner.GetLocalInterfaces().First(i => i.HasGateway);
HostRange.TryParse("20-60", iface.Address, out var range, out _);
var found = await NetworkScanner.ScanAsync(
    range!.Enumerate(65536, out _), NetworkScanner.CommonScpiPorts,
    connectTimeoutMs: 2000, idnTimeoutMs: 2000, progress: null, ct: default);
```

Serial has its own, and the difference is the physics rather than a preference. Listing
opens nothing; probing opens the ports you name at the rates you name, in order, and stops
at the first that answers. Nothing sweeps parity or framing — that is a combinatorial
search against hardware that cannot say "wrong number".

```csharp
foreach (SerialDevice port in SerialScanner.List())        // opens nothing
    Console.WriteLine(port.PortName);

var asked = await SerialScanner.ScanAsync(
    SerialPorts.Names(), SerialScanner.CommonBaudRates, idnTimeoutMs: 1500);

foreach (SerialDevice port in asked)
    Console.WriteLine($"{port.PortName}  {port.SettingsText}  {port.IdentityText}");
```

Every port comes back, answered or not: one that says nothing is still a port, still
connectable at settings the scan did not try, so `IdentityText` distinguishes *not asked*
from *asked and silent* from *answered with something that was not an identity* — which is
the fact that sends you to the baud rate rather than to the cable.

**36 curated command catalogs — 23,978 commands**, embedded as resources, covering
**Rohde & Schwarz, Rigol, Siglent, Keysight, Chroma, B&K Precision, GW Instek, Keithley,
Tektronix and Fluke** — oscilloscopes, multimeters, power supplies, electronic loads,
signal generators, SMUs and spectrum analyzers. Each entry carries the guide's own syntax
and description, the document it came from, and whether it has ever been confirmed against
real hardware.

```csharp
var family  = InstrumentProfile.FamilyForIdentity(idn);   // *IDN? → one of 37 families
var catalog = CommandReference.ForFamily(family);
foreach (var c in catalog!.Commands.Where(c => c.BenchVerified))
    Console.WriteLine($"{c.Syntax}  —  {c.Description}");
```

**One rule governs all of it: never invent SCPI.** Every command is transcribed from a
vendor programming guide — not a forum, not another vendor's guide, not a plausible guess.
518 of the 23,978 entries have additionally been confirmed on real instruments; the rest
are marked as guide-only, honestly, rather than presented as tested.

**A small script language**, with a runner for one instrument and a runner for several at
once — `REPEAT`, `DELAY`, captured values, and a recorded results table.

Run one against an instrument and keep every number it reads. `ReadingSeries` is the
recorder the app's own results table is built on, and `TryParseReading` is why it survives
contact with real instruments: a meter answers `+1.234560E-01`, some append a unit, some
return several comma-separated fields, and it is parsed invariantly — on a machine whose
decimal separator is a comma, the obvious `double.Parse` misreads every value silently.

```csharp
string script = """
    # Ten peak-to-peak readings, a second apart.
    REPEAT 10
        :MEASure:VPP? CHANnel1
        DELAY 1000
    END
    """;

var readings = new ReadingSeries();
var clock = Stopwatch.StartNew();

await ScriptRunner.RunAsync(
    script, client,
    output: (line, kind) =>
    {
        if (kind == ScriptOutputKind.Response && ReadingSeries.TryParseReading(line, out double v))
            readings.Add(clock.Elapsed.TotalSeconds, v);
    },
    record: _ => { },          // rows, for the multi-instrument runner
    ct: default);

File.WriteAllText("vpp.csv", readings.ToCsv("Vpp (V)"));

var (min, max, mean) = readings.Statistics();
Console.WriteLine($"{readings.Count} readings, {min:g4} to {max:g4}, mean {mean:g4}");
```

**Waveform and screenshot decoding.** IEEE 488.2 binary blocks, and the per-vendor
arithmetic that turns raw bytes into volts and seconds — which differs between every
manufacturer and is where a wrong answer looks most like a right one.

Save the oscilloscope's screen to a file. The setup commands and the capture command are
the instrument's own, out of its catalog, so nothing here needs to know which scope it is:

```csharp
string idn = await client.QueryAsync("*IDN?");
InstrumentProfile profile = InstrumentProfile.ForIdentity(idn);

if (profile.ScreenCaptureCommand is { Length: > 0 } grab)
{
    foreach (string setup in profile.ScreenCaptureSetup)
        await client.SendAsync(setup);

    byte[] image = await client.QueryBinaryAsync(grab);
    File.WriteAllBytes("screen.png", image);
}
```

One thing worth knowing: **the instrument chooses the format**, not you. A Rigol DS2000
sends PNG, several others send BMP, and writing a BMP into a file called `.png` is how a
screenshot becomes unopenable three tools later. Sniff the first bytes if you care —
`89 50 4E 47` is PNG, `42 4D` is BMP — which is exactly what `lec screenshot` does before
it picks the extension.

And the trace behind it, as numbers rather than pixels:

```csharp
if (profile.SupportsWaveformCapture)
{
    WaveformCapture wave = await WaveformReader.ReadAsync(
        client, profile.WaveformDialect, channel: 1);

    File.WriteAllText("channel1.csv", wave.ToCsv());   // Time (s), Voltage (V)
    Console.WriteLine($"{wave.Samples.Count:N0} samples at {1 / wave.XIncrement:g3} Sa/s");
}
```

`ResultPlot` holds the arithmetic behind the app's charts if you would rather draw than
export — `Build` turns a table of strings into series, `Axis` picks the range and the
ticks, and `CanBeLogarithmic` answers whether a log axis is honest for that data. Drawing
is left to you: the CLI writes SVG by hand for exactly this reason, since a plotting
package would cost this library its portability.

**Instrument identity.** `*IDN?` → one of 37 families → that family's quick commands,
capture dialect and catalog.

## What it does not do

No UI, by design. No USB-TMC and no GPIB, and not for the same reason each: GPIB needs a
controller card and its manufacturer's driver, which is the vendor-SDK dependency this
library exists to avoid, while USB-TMC needs libusb already installed on the machine and,
on Windows, a driver bound to the device by hand.

Serial was on that list until it was built, and it turned out to cost what it was predicted
to: SCPI over RS-232 is line-based in exactly the way a raw socket is, so
`SerialInstrumentClient` is a short file over the same framing.
[SPEC §17](https://github.com/EECSB/LabEquipmentController/blob/master/docs/SPEC.md) has the
argument for all three; nothing further is planned.

**Discovery works differently over serial**, and that is not a gap either. A subnet sweep
asks every address the same harmless question; a serial port cannot be asked anything until
its baud rate and framing already match, and what is on the other end may be a printer. So
the two halves a sweep runs together are split: listing the ports opens nothing, and
opening them is a call you make, over the ports you choose and the rates you name.

## Where to read further

- **[The repository README](https://github.com/EECSB/LabEquipmentController)** — the whole
  project: the desktop app, the web build, the `lec` command line, and screenshots of all
  three against a real bench.
- **[SPEC.md](https://github.com/EECSB/LabEquipmentController/blob/master/docs/SPEC.md)** —
  what every part does and why, including the arguments this library lost as well as the
  ones it won.
- **[ARCHITECTURE.md](https://github.com/EECSB/LabEquipmentController/blob/master/docs/ARCHITECTURE.md)**
  — how the pieces fit, with the diagram.
- **[Core/README.md](https://github.com/EECSB/LabEquipmentController/blob/master/Core/README.md)**
  — this assembly folder by folder, and its two dependencies.

## Licence and provenance

MIT. The catalogs are transcriptions of publicly downloadable vendor programming guides;
the guides themselves are **not** redistributed here, and each catalog names the document
it came from so you can check it against your own copy.
