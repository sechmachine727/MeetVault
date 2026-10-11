# MeetVault WinUI 3 MVP — Build and Run (Windows)

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

Tests that load the real Whisper model skip when `models/ggml-tiny.bin` is absent.

## 3. Publish the portable build

`publish/win-x64` is the single canonical portable output.

```powershell
rmdir /s /q publish\win-x64
dotnet publish src\MeetVault\MeetVault.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
Copy-Item -Recurse -Force models publish\win-x64\models
publish\win-x64\MeetVault.exe
```

The native libraries come from the package references during publish (`libvosk.dll` via the Vosk package). Only `models/` is copied by hand.

## 4. Package the MSIX

```powershell
dotnet build src\MeetVault\MeetVault.csproj -c Release -r win-x64 -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true
```

The package is written to `src/MeetVault/AppPackages/MeetVault_1.0.0.0_x64_Test/MeetVault_1.0.0.0_x64.msix`. Installing it needs the signing certificate trusted at machine scope, which needs administrator rights; see [../../BLOCKERS.md](../../BLOCKERS.md).

## 5. Verify

Run the unit suite, then the manual acceptance pass in `05-test-plan.md` on a machine with a microphone. The event log and `%TEMP%\MeetVault\app.log` record runtime failures; `%TEMP%\MeetVault\crash.log` and `nav-errors.log` capture crashes and navigation errors.

## 6. Constraints

- Keep `PublishTrimmed` false. ILLink strips CsWinRT ABI glue and the sherpa-onnx wrapper, which crashes the app. See the repository `AGENTS.md`.
- `App.xaml.cs` pins the app's own `onnxruntime.dll` before WinUI starts. Keep that pin.
