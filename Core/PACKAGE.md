# LabEquipmentController

Talk to lab instruments over Ethernet from .NET, using **SCPI** — and know that every
command you send came out of the manufacturer's own programming guide.

This is the engine behind [Lab Equipment
Controller](https://github.com/EECSB/LabEquipmentController): a Windows desktop app and a
cross-platform `lec` command line. The package has no UI dependency and targets plain
`net10.0`, so it runs on Windows, Linux and macOS.

```bash
dotnet add package LabEquipmentController --prerelease
```

> **Beta.** The transports, catalogs and runners are the same code the desktop app and CLI
> have been driving instruments with, and the whole surface is covered by 1,239 tests. What
> is not yet settled is the *shape* of the public API — names and signatures may still move
> before 1.0.0, because nothing outside this repository has used them yet. Pin the exact
> version if that matters to you.

## What it gives you

**Two transports, behind one interface.** A raw TCP socket (usually port 5025) and a
hand-written **VXI-11** client for instruments that speak nothing else — including the
RPC portmapper lookup that VXI-11 requires and that most examples hard-code wrongly.

```csharp
using LabEquipmentController;

using var client = new SerializedInstrumentClient(new ScpiClient("192.168.1.20", 5025));
await client.ConnectAsync();
string idn = await client.QueryAsync("*IDN?");
```

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

**35 curated command catalogs — 23,174 commands**, embedded as resources, covering
**Rohde & Schwarz, Rigol, Siglent, Keysight, Chroma, B&K Precision, GW Instek, Keithley,
Tektronix and Fluke** — oscilloscopes, multimeters, power supplies, electronic loads,
signal generators, SMUs and spectrum analyzers. Each entry carries the guide's own syntax
and description, the document it came from, and whether it has ever been confirmed against
real hardware.

```csharp
var family  = InstrumentProfile.FamilyForIdentity(idn);   // *IDN? → one of 36 families
var catalog = CommandReference.ForFamily(family);
foreach (var c in catalog!.Commands.Where(c => c.BenchVerified))
    Console.WriteLine($"{c.Syntax}  —  {c.Description}");
```

**One rule governs all of it: never invent SCPI.** Every command is transcribed from a
vendor programming guide — not a forum, not another vendor's guide, not a plausible guess.
518 of the 23,174 entries have additionally been confirmed on real instruments; the rest
are marked as guide-only, honestly, rather than presented as tested.

**A small script language**, with a runner for one instrument and a runner for several at
once — `REPEAT`, `DELAY`, captured values, and a recorded results table.

**Waveform and screenshot decoding.** IEEE 488.2 binary blocks, and the per-vendor
arithmetic that turns raw bytes into volts and seconds — which differs between every
manufacturer and is where a wrong answer looks most like a right one.

**Instrument identity.** `*IDN?` → one of 36 families → that family's quick commands,
capture dialect and catalog.

## What it does not do

No UI, by design. No USB-TMC, GPIB or serial — Ethernet only. `ScpiClient` is line-based;
bulk binary belongs to the capture path.

## Licence and provenance

MIT. The catalogs are transcriptions of publicly downloadable vendor programming guides;
the guides themselves are **not** redistributed here, and each catalog names the document
it came from so you can check it against your own copy.
