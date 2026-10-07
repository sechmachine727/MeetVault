# MeetVault

A local-first Windows meeting recorder and transcriber. It captures audio, transcribes it on-device, separates speakers, shows the transcript live, and can send a finished meeting to Gemini for a refined summary. Nothing leaves the machine except a manual Gemini upload.

Built with C# and WinUI 3 on .NET 8. There is no login, no subscription, and no cloud storage.

## Status

Working MVP, tested on Windows 11. The solution builds with 0 errors; six pre-existing `CA1416` warnings remain in the GPU probe and the process-loopback capture. The Core test suite passes 95 tests. The portable build launches and records, transcribes, and saves meetings. See [BLOCKERS.md](BLOCKERS.md) for what still needs a specific machine to verify.

Per-process audio capture is implemented but has not been exercised against a machine with the target app playing. No gender classifier ships, so speaker labels stay neutral, and the Gemini upload is manual by design. The app supports English, Japanese, and Vietnamese; JA/VI accuracy has not been verified with real speech.

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
- Staged third-party runtimes in `cuda/` and `vcruntime/` to build a package that can use CUDA without the CUDA Toolkit or the Visual C++ Redistributable installed on the target machine. They are not committed; see [CUDA setup](docs/CUDA-SETUP.md).

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

Build the portable release into the canonical folder, then copy in the speech models:

```powershell
rmdir /s /q publish\win-x64
dotnet publish src\MeetVault\MeetVault.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
Copy-Item -Recurse -Force models publish\win-x64\models
publish\win-x64\MeetVault.exe
```

The publish brings in the native libraries the app loads, including every Whisper backend. To add CUDA to the package, stage `cuda/` and `vcruntime/` as described in [CUDA setup](docs/CUDA-SETUP.md) before publishing.

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

Transcription can run on a GPU. The Settings device picker lists what each backend can actually drive, and every device index is that backend's own.

CUDA needs no toolkit install: cuBLAS, cuBLASLt and the CUDA runtime ship with the app, next to the ggml CUDA native. What CUDA does need is an NVIDIA driver that supports CUDA 13 (driver 580 or newer) and a GPU the shipped CUDA native has code for: sm_8.6-8.9 (RTX 30 and 40 series) or sm_120/121 (RTX 50 series). The native is SASS-only with no PTX, so Turing (RTX 20 series), A100 and H100 are refused rather than offered and then silently run on CPU. Both are queried from the driver rather than guessed from the card's name, and the picker stays greyed out with the reason when they do not hold. Vulkan devices come from `vulkan-1.dll` in that loader's order. Whether the loader's index is the one ggml applies, or whether ggml compacts its device list first, is unverified on hosts with more than one Vulkan device. The Live screen reports the backend that actually loaded, so a requested GPU that falls back to CPU is visible in the status line, and the startup log records the driver version, the detected compute capability, and why a backend was rejected.

The device picker offers the backends Whisper.net supports: CPU, CUDA, and Vulkan. DirectML is not a Whisper.net backend and is not offered.

Vulkan passthrough is often unavailable on a paravirtualized GPU (Hyper-V GPU-P), so the backend can load while the GPU stays idle. Use CUDA or a physical GPU there.

To build a package that carries the CUDA runtime, so CUDA works without the CUDA Toolkit installed, see [CUDA setup](docs/CUDA-SETUP.md).

## Data and logs

| Path | Contents |
| --- | --- |
| `%APPDATA%\MeetVault\meetings.db` | Meetings, transcripts, speakers, jobs, settings |
| `%APPDATA%\MeetVault\media` | Recordings: `.m4a` (AAC 256 kbps), plus the working `.wav` when encoding was unavailable |
| `%APPDATA%\MeetVault\gemini` | Saved Gemini results |
| `%TEMP%\MeetVault\app.log` | Application log |
| `%TEMP%\MeetVault\crash.log`, `nav-errors.log` | Crash and navigation diagnostics |

## Documentation

- [docs/mvp-winui](docs/mvp-winui/00-spec-index.md): the frozen MVP specification.
- [docs/COVERAGE.md](docs/COVERAGE.md): requirement-to-evidence ledger and change history.
- [docs/BUILD-LEDGER.md](docs/BUILD-LEDGER.md): historical build log with engine measurements.
- [docs/CUDA-SETUP.md](docs/CUDA-SETUP.md): stage the CUDA and C++ runtimes and build them into the app.
- [docs/progress/](docs/progress/SKILL.md): per-contributor work log, one file per contributor per day.
- [RUN-GUIDE.md](RUN-GUIDE.md): launch steps and location of data.
- [BLOCKERS.md](BLOCKERS.md): open items that need a specific machine.

## Known gaps

- Audio capture mixes the microphone and system output. Capturing one application uses the WASAPI process-loopback API (`ProcessLoopbackCapture`), which falls back to system-wide capture when the API is unavailable; the per-app path is not yet confirmed on a machine with the target app playing.
- No gender classifier ships, so speaker labels are neutral (`Speaker 1`, `Speaker 2`).
- Two-speaker separation and JA/VI accuracy need validation with real speech.
- The Gemini call has been tested against stubs, not against the live API.

## Links

The **AGENTS.md** file at the repository root gives agent and contributor instructions for this repo.
