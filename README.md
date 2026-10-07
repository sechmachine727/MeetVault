# MeetVault

A local-first Windows meeting recorder and transcriber. It captures audio, transcribes it on-device, separates speakers, shows the transcript live, and can send a finished meeting to Gemini for a refined summary. Nothing leaves the machine except a manual Gemini upload.

Built with C# and WinUI 3 on .NET 8. There is no login, no subscription, and no cloud storage.

## Status

Working MVP, tested on Windows 11. The solution builds clean (0 warnings, 0 errors) and the Core test suite passes 74 tests. The portable build launches and records, transcribes, and saves meetings. See [BLOCKERS.md](BLOCKERS.md) for what still needs a specific machine to verify.

Not implemented: per-process audio capture, a bundled gender classifier, and automatic Gemini upload. The app supports English, Japanese, and Vietnamese; JA/VI accuracy has not been verified with real speech.

## Features

- Record the microphone and system output together, mixed into one track, so your own voice is never lost. The system side can capture every app or a single chosen app (a conference call).
- On-device transcription (whisper.cpp via Whisper.net), so transcription runs offline.
- Speaker separation (sherpa-onnx diarization) with renameable speaker labels.
- Live transcript view with a Pinnable, always-on-top overlay.
- Meetings list with per-meeting delete; Details view with audio playback, transcript edit, speaker reassignment, copy, and export.
- Optional Gemini refinement with the API key stored in Windows Credential Locker.
- Configurable file logging for support and debugging.

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11.
- The .NET 8 SDK to build. Running the published portable build needs no .NET install.
- Speech models in `models/` for live transcription and diarization. They are not committed; see [Speech models](#speech-models).

## Layout

```text
src/MeetVault              WinUI 3 app: views, navigation, overlay window
src/MeetVault.Core         Domain, session, engines, storage, Gemini, logging
tests/MeetVault.Core.Tests xUnit tests for Core
docs/                      Spec (docs/mvp-winui) and living status documents
publish/win-x64            Canonical portable build (gitignored)
```

The app calls `MeetingSession`; engines and storage sit behind interfaces and never touch the UI.

## Build and test

Replace `dotnet` with your SDK path if it is not on `PATH` (for example `D:\dotnet\dotnet.exe`).

```powershell
dotnet build MeetVault.sln -c Release
dotnet test tests\MeetVault.Core.Tests\MeetVault.Core.Tests.csproj -c Release --nologo -v q
```

The test suite includes smoke tests that load the real Whisper model when `models/ggml-tiny.bin` is present and skip when it is not.

## Run

Build the portable release into the canonical folder, then stage the native libraries and models:

```powershell
rmdir /s /q publish\win-x64
dotnet publish src\MeetVault\MeetVault.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
rem Stage onnxruntime.dll, sherpa-onnx-c-api.dll, and models/ into publish\win-x64
publish\win-x64\MeetVault.exe
```

The portable build has no package identity, so Windows Credential Locker is unavailable and a Gemini key may not persist across runs. Install the MSIX for the full feature set.

The MSIX is produced by `dotnet build` with the Appx targets enabled:

```powershell
dotnet build src\MeetVault\MeetVault.csproj -c Release -r win-x64 -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true
```

The package lands at `src/MeetVault/AppPackages/MeetVault_1.0.0.0_x64_Test/MeetVault_1.0.0.0_x64.msix`. Installing it needs the signing certificate trusted at machine scope, which needs administrator rights. See [BLOCKERS.md](BLOCKERS.md).

## Speech models

Place these under `models/`. They are gitignored and must be staged next to the executable for a portable run.

| File | Purpose | Size |
| --- | --- | --- |
| `ggml-tiny.bin` | Live transcription (fast) | ~78 MB |
| `ggml-base.bin` | Post-meeting refinement (accurate) | ~148 MB |
| `sherpa-seg.onnx` | Speaker segmentation | ~6 MB |
| `sherpa-emb.onnx` | Speaker embeddings | ~40 MB |

`WhisperTranscriber.DownloadModelAsync` fetches a Whisper model. The sherpa models come from the sherpa-onnx project.

## Configuration

Settings persist in `%APPDATA%\MeetVault\meetings.db` (SQLite, `settings` table). The Settings screen covers:

- **Audio**: microphone device, record-microphone on/off, record-system-audio on/off, and the system-audio source (all apps, or one running app). Microphone and system audio are mixed; disabling one records the other alone.
- **Transcription**: model (tiny or base), device (CPU, or a detected GPU), speaker diarization.
- **Overlay**: keep on top, opacity, font size, lines shown.
- **Storage**: export folder.
- **Logging**: log level (default `debug`), open log folder, show log.
- **Gemini**: API key.

## GPU inference

Transcription can run on a GPU. The Settings device picker lists the GPUs the machine exposes, queried through DXGI, which excludes indirect and virtual display adapters. Each GPU is offered through a backend it can use: CUDA when the NVIDIA driver and CUDA runtime are present, otherwise Vulkan. The Live screen reports the backend that actually loaded, so a requested GPU that falls back to CPU is visible in the status line. The log records the reason.

Vulkan passthrough is often unavailable inside a Hyper-V GPU-P virtual machine; use CUDA or a physical GPU there.

## Data and logs

| Path | Contents |
| --- | --- |
| `%APPDATA%\MeetVault\meetings.db` | Meetings, transcripts, speakers, jobs, settings |
| `%APPDATA%\MeetVault\media` | Recorded WAV files |
| `%APPDATA%\MeetVault\gemini` | Saved Gemini results |
| `%TEMP%\MeetVault\app.log` | Application log |
| `%TEMP%\MeetVault\crash.log`, `nav-errors.log` | Crash and navigation diagnostics |

## Documentation

- [docs/mvp-winui](docs/mvp-winui/00-spec-index.md): the frozen MVP specification.
- [docs/COVERAGE.md](docs/COVERAGE.md): requirement-to-evidence ledger and change history.
- [docs/BUILD-LEDGER.md](docs/BUILD-LEDGER.md): historical build log with engine measurements.
- [RUN-GUIDE.md](RUN-GUIDE.md): launch steps and location of data.
- [BLOCKERS.md](BLOCKERS.md): open items that need a specific machine.

## Known gaps

- Audio capture mixes the microphone and system output. Capturing one application uses the WASAPI process-loopback API (`ProcessLoopbackCapture`), which falls back to system-wide capture when the API is unavailable; the per-app path is not yet confirmed on a machine with the target app playing.
- No gender classifier ships, so speaker labels are neutral (`Speaker 1`, `Speaker 2`).
- Two-speaker separation and JA/VI accuracy need validation with real speech.
- The Gemini call has been tested against stubs, not against the live API.

## Links

The **AGENTS.md** file at the repository root gives agent and contributor instructions for this repo.
