# The desktop app

The Windows front end, and the one the other two are measured against: **C# / WinForms** on
**.NET 10** (`net10.0-windows`). Everything only this app uses lives in this folder — one
`.cs` per window, the button glyphs, the publish profile and the installer.

What is on screen and where is [docs/UI-SPEC.md](../docs/UI-SPEC.md), which also states the
rule that the web build is a port of this one rather than a redesign of it.

## Requirements

- **Running the published build:** 64-bit Windows 10/11. Nothing else — the self-contained
  build bundles the .NET runtime.
- **Building from source:** the [.NET 10 SDK](https://dotnet.microsoft.com/download).

## Build & run

From the repository root:

```bash
dotnet run --project Desktop/LabEquipmentController.csproj
```

Or open `LabEquipmentController.slnx` in Visual Studio 2022 (17.14 or later, for the XML
solution format) and press F5.

**If the build fails with a file-lock error**, a previous `LabEquipmentController.exe` is
still running (Visual Studio also holds `Core.pdb`). Close it, or:

```bash
powershell -c "Get-Process LabEquipmentController -EA SilentlyContinue | Stop-Process -Force"
```

## Publish a single-file executable

A self-contained, single-file profile for 64-bit Windows is included:

```bash
dotnet publish Desktop/LabEquipmentController.csproj -p:PublishProfile=win-x64
```

The result is one `LabEquipmentController.exe` (~48 MB, runtime included) under
`Desktop/bin/Release/publish/win-x64/`. It runs on a clean Windows machine with no .NET
install.

For a much smaller, framework-dependent build (requires the .NET 10 Desktop Runtime on the
target machine) publish without self-containment instead:

```bash
dotnet publish Desktop/LabEquipmentController.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## Build the installer

The [releases page](https://github.com/EECSB/LabEquipmentController/releases) offers both
shapes: a **portable zip** carrying the self-contained build (~46 MB, runs anywhere), and
a **setup.exe** carrying the framework-dependent one (~4 MB, wants the .NET 10 Desktop
Runtime and offers to fetch it if missing). The installer is
[Inno Setup 6](https://jrsoftware.org/isinfo.php); its script is in [installer/](installer).

Publish the framework-dependent payload into the directory the script expects, then
compile it:

```bash
dotnet publish Desktop/LabEquipmentController.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishDir=bin\Release\publish\win-x64-fd\
```

```bash
"%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" Desktop\installer\LabEquipmentController.iss
```

The result is `Desktop\bin\LabEquipmentController-v<version>-setup.exe`. It installs
per-user into `%LocalAppData%\Programs` with no UAC prompt (the app needs no administrator
rights); the wizard offers a machine-wide install, and `/ALLUSERS` does the same from the
command line. Verify a build end to end — install, launch, uninstall — with:

```bash
powershell -ExecutionPolicy Bypass -File Desktop\installer\Test-Installer.ps1
```

## Where things are

One folder per area, the same words `Core/` and `Tests/` use, so a window, the logic under
it and its tests all sit under the same heading.

| Path | What |
|------|------|
| `Program.cs` | Entry point → `MainForm` |
| `Bench/` | `MainForm` — scan, discovered instruments, console tabs — plus `InstrumentConsole` and the window a tab detaches into |
| `Scripting/` | Both editors (`ScriptForm`, `SequenceForm`), the colouriser and completion (`ScriptEditor`), the Snippets menu, the language reference |
| `Ai/` | `ScriptAiForm` (the conversation, the drafts, the catalog check), `DatasheetExtractForm`, `AiSettingsForm`, the connection book, and the DPAPI key store |
| `Catalogs/` | `CommandLibraryForm` (all 36, with the guide beside them) and `CommandReferenceForm` (one family's, beside its console) |
| `Capture/` | `WaveformForm`, `ScreenCaptureForm`, `MultimeterReadoutForm` |
| `Results/` | `ResultsPanel` and `ResultPlotPanel` — the recorded table and its plot |
| `Ui/` | `ButtonStyle` (the metrics every button is built with), `AppIcons`, `SplitLayout`, `AboutForm` |
| `Assets/icons/` | The glyph artwork, embedded into the executable |
| `installer/` | Inno Setup script, and its install/launch/uninstall smoke test |

**Folders group; namespaces do not** — every type here stays in `LabEquipmentController`,
so nothing above changed a `using`. See ARCHITECTURE.md for why that is deliberate.

Settings are stored per-user at `%AppData%\LabEquipmentController\settings.json`, and the
AI key beside them, encrypted with DPAPI for the signed-in Windows account.

## Tests

There is no end-to-end suite for this app: it is the reference the web build is measured
against, and everything it does that is not pixels lives in `Core`, covered by
[Tests/](../Tests/README.md). UI changes are verified by running the app and looking at it —
[docs/SPEC.md](../docs/SPEC.md) §16 lists the traps a harness meets here, each of which has
cost real time.
