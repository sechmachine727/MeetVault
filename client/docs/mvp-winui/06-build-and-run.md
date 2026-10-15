# MeetVault WinUI 3 MVP — Build and Run (Windows)

All paths and commands below are relative to `client/` (this directory).

Audience: Windows implementer. Pre-read: `01-requirements.md`.

This document describes the build and run workflow for the delivered app. For current status and open items, see [../../README.md](../../README.md) and [../../BLOCKERS.md](../../BLOCKERS.md).

## 1. Prerequisites

- Windows 10 version 2004 (build 19041) or later, or Windows 11.
- .NET 8 SDK.
- No Python required. Models are staged next to the executable; the installer bundles them.

## 2. Build and test

```powershell
dotnet build MeetVault.sln -c Release
dotnet test tests\MeetVault.Core.Tests\MeetVault.Core.Tests.csproj -c Release --nologo -v q
```

Tests that need the real Vosk models skip when `models/vosk/` is absent.

## 3. Publish the portable build

`publish/win-x64` is the single canonical portable output.

```powershell
# One canonical portable deliverable: publish, stage only the shipped models (see the note below),
# trim, zip, and print the size + SHA256.
powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack-portable.ps1
```

*(Spec text above is frozen. The models actually staged today are `large-en` + `large-ja`; the
Vietnamese and small/medium tiers were removed on 2026-10-15 — see [../ASR-ENGINES.md](../ASR-ENGINES.md).)*

The deliverable carries only the models `EngineCatalog` offers — see [../ASR-ENGINES.md](../ASR-ENGINES.md).
Staging more than that does not ship: the script and the `TrimUnshippedModels` target drop everything
else, which is what keeps the zip shippable (~3.0 GB) instead of carrying every staged tier.

## Installer (regular users)

The portable zip needs the user to unzip it and keep the folder together. For a normal Windows
install — one file, per-user, no admin, models included — build the Inno Setup installer:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-installer.ps1
```

It stages the same payload (through `pack-portable.ps1 -SkipZip`), compiles `installer\MeetVault.iss`
and prints the setup's size + SHA256. Output: `publish\MeetVaultSetup-1.0.0.exe`; installing it needs
no elevation (it lands in `%LOCALAPPDATA%\Programs\MeetVault`). Details, the signing steps and the
elevated cases: [../installer/README.md](../installer/README.md).

The native libraries come from the package references during publish (`libvosk.dll` via the Vosk package). Copied by hand: `models/` (the Vosk and langid models) and — for a portable build on a machine without the VC++ redistributable — the staged `vcruntime/win-x64` DLLs (see the app project's Content items).

## 4. Package the MSIX

```powershell
dotnet build src\MeetVault\MeetVault.csproj -c Release -r win-x64 -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true
```

The package is written to `src/MeetVault/AppPackages/MeetVault_1.0.0.0_x64_Test/MeetVault_1.0.0.0_x64.msix`. Installing it needs the signing certificate trusted at machine scope, which needs administrator rights; see [../../BLOCKERS.md](../../BLOCKERS.md).

## 5. Verify

Run the unit suite, then the manual acceptance pass in `05-test-plan.md` on a machine with a microphone. The event log and `%TEMP%\MeetVault\app.log` record runtime failures; `%TEMP%\MeetVault\crash.log` and `nav-errors.log` capture crashes and navigation errors.

## 6. Constraints

- Keep `PublishTrimmed` false. ILLink strips the CsWinRT ABI glue the WinUI bindings need and the app crashes at startup. See the repository `AGENTS.md`.
- Keep the `App.xaml.cs` onnxruntime pin: the app loads its own `onnxruntime.dll` via `NativeLibrary.Load` before `InitializeComponent` (crash prevention against the same-named WindowsAppSDK.ML module).
- No foreign runtime payloads ship: the natives are `libvosk.dll` (Vosk NuGet) and `onnxruntime.dll` (Microsoft.ML.OnnxRuntime), and the staged payloads are `models/` and `vcruntime/win-x64` (VC++ CRT for ScreenRecorderLib on win-x64 builds).
