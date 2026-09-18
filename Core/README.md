# Core — the engine

Everything the app does that is not pixels: the three transports, discovery, instrument
identification, the script and sequence runners, capture decoding, the AI clients, settings,
and all 36 curated SCPI catalogs. Plain `net10.0`, **no UI dependency and nothing
Windows-only** — which is what lets one engine drive a WinForms app, a web server and a
cross-platform CLI.

That constraint is enforced rather than remembered: `System.Drawing` (screenshot decoding)
and DPAPI (the encrypted key store) would both be convenient here and both stay in the
desktop project instead, and `CA1416` is an error in the CLI and server builds.

## Use it in your own project

Published on NuGet as **`LabEquipmentController`** — [PACKAGE.md](PACKAGE.md) is the page
nuget.org renders, and the fuller tour.

```bash
dotnet add package LabEquipmentController
```

**1.0.0.** The public API is settled: what is named here is what will keep being named here,
and anything that has to change after this gets a major version rather than a quiet rename.
The code behind it is what the app and the CLI drive real instruments with every day.

```csharp
using LabEquipmentController;

using var client = new SerializedInstrumentClient(new ScpiClient("192.168.1.20", 5025));
await client.ConnectAsync();

string idn     = await client.QueryAsync("*IDN?");
var    family  = InstrumentProfile.FamilyForIdentity(idn);
var    catalog = CommandReference.ForFamily(family);   // 23,978 commands across 36 families
```

The package id drops the `.Core` suffix the assembly carries, so it does not read as a
.NET Core component; inside, the assembly is still `LabEquipmentController.Core.dll`.
Build it with:

```bash
dotnet pack Core\LabEquipmentController.Core.csproj -c Release
```

That produces `LabEquipmentController.<version>.nupkg` and a matching `.snupkg` of symbols
under `Core/bin/Release/`.

## Where things are

One folder per area, the same words [Tests/](../Tests/README.md) uses, so a file and the
tests that pin it sit under the same heading.

| Path | What |
|------|------|
| `AppInfo.cs` | Version, licence and repository, read off the assembly rather than written down |
| `Transport/` | `ScpiClient`, `Vxi11Client` and `SerialInstrumentClient` behind one interface, the message framing the socket and the serial port share, the serializer that admits one exchange at a time, VISA · `vxi://` · `serial://` addresses, deadlines, session history |
| `Discovery/` | The subnet sweep and host ranges, the serial-port list and the port sweep beside it, CSV export, and `*IDN?` → one of 37 families |
| `Catalogs/` | Loading the embedded catalogs, the SCPI syntax matcher, finding a guide on disk |
| `Capture/` | IEEE 488.2 blocks, the five waveform dialects and their arithmetic, multi-channel capture, the zoom view |
| `Scripting/` | Both runners, the language and its tokenizer, which instrument each `DEVICE` line binds to, the bundled examples, the reference text |
| `Results/` | Recorded series, the plot's arithmetic, unit guessing from a column name |
| `Settings/` | `UserSettings` and where it is stored |
| `Ai/` | The three provider shapes, datasheet extraction, the script author, extracted-catalog storage |
| `CommandData/` | The 36 curated catalogs, embedded as `commands.<family>.json` |

**Folders group; namespaces do not.** Every type here is in `LabEquipmentController`
however deep it sits — which is not tidiness lost but API kept: this assembly ships as a
NuGet package, so `LabEquipmentController.ScpiClient` is public surface, and a
folder-shaped namespace would break every consumer for nothing.

## The catalogs

`CommandData/` holds one JSON file per instrument family, embedded into the assembly as
`commands.<family>.json` — which is why a 5.9 MB DLL compresses into a 757 KB package and a
consumer needs no content files on disk. Each entry carries the guide's own syntax and
description, the document it came from, and how far it is trusted.

**One rule governs all of it: never invent SCPI** ([docs/SPEC.md](../docs/SPEC.md) §10).
Every command is transcribed from a vendor programming guide, and tests fail the build if a
quick command, readout query or bundled script line is not an instance of a documented
template. The catalogs are built offline by [tools/scpi-extract](../tools/scpi-extract) and
committed; no catalog is ever parsed out of a PDF at runtime.

## The two dependencies

**PdfPig**, and only for `DocumentText.ReadPdf` and `PageCount` in `Ai/` — turning a
datasheet the user picked into text, for a provider that will not take a file upload. DOCX
and TXT are read with the BCL alone, and what a model returns from any of them is
quarantined from the catalogs above (SPEC §11b). It is not a PDF *viewer*: the desktop app
shows a guide through WebView2, which is Windows-only and therefore not in here.

**System.IO.Ports**, and only for opening the serial port in `SerialInstrumentClient` —
everything above that is the same `ScpiFraming` the raw socket uses. Microsoft's own and
genuinely cross-platform, which is the test that let it in and the one WinUSB and libusb
fail; SPEC §17 draws the line, which is not "native code" (this has a 15 KB native shim per
non-Windows runtime, and it travels inside the package) but whether the user has to install
something first.

Everything else in this library is dependency-free, which is what lets the same assembly
run on Linux under the CLI and inside the container. Taking the package means taking both —
1.83 MB gzipped in a browser payload for PdfPig and 10.5 KB for the ports package, which is
the difference between a trade worth arguing about and one that is not.

## Publishing (trusted publishing, no API key)

Releases are published by GitHub Actions using
[NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing):
GitHub issues a short-lived signed OIDC token describing this repository and workflow,
nuget.org validates it against a registered policy and returns an API key that expires in
an hour. **No key is stored in this repository or in GitHub secrets.**

[`publish-nuget.yml`](../.github/workflows/publish-nuget.yml) is **manual only** — run it
from the Actions tab and type the version. Nothing publishes on a commit, and there is
deliberately no release trigger: this library and the desktop app version independently, so
an app release tagged `v1.1.0` would otherwise publish library version 1.1.0 permanently
without anyone deciding to. The workflow runs the full suite before it packs, and pushes
with `--skip-duplicate` so a re-run is harmless.

It needs two one-time settings:

| Where | What |
|---|---|
| nuget.org → your username → **Trusted Publishing** | A policy with Repository Owner `EECSB`, Repository `LabEquipmentController`, Workflow File `publish-nuget.yml` (name only, no path), Environment empty |
| GitHub → Settings → Secrets and variables → Actions | `NUGET_USER` = your nuget.org **username**, not your email |

The policy is keyed to the workflow's *file name*, so renaming that file breaks publishing
until the policy is updated. A policy on a private repository stays "temporarily active"
for seven days and becomes permanent on the first successful publish — nuget.org needs the
repository and owner IDs that only arrive inside a real token, which is what stops someone
deleting the repo and recreating it under the same name to publish as you.

## Tests

Everything here is covered by [Tests/](../Tests/README.md) against a fake instrument — no
hardware, no network. The parts that can only be proven against real equipment are in
[Tests/Bench](../Tests/Bench/README.md), off unless `LEC_BENCH=1`.
