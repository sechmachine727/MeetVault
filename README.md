# MeetVault

A local-first Windows meeting recorder and transcriber. It captures audio, transcribes it on-device, shows the transcript live, and can send a finished meeting to Gemini for a refined summary. Nothing leaves the machine except a manual Gemini upload.

Built with C# and WinUI 3 on .NET 8. There is no login, no subscription, and no cloud storage.

## Status

Working MVP, tested on Windows 11. The solution builds with 0 errors; three pre-existing `CA1416` warnings remain in `ProcessLoopbackCapture.cs` (COM release call sites) — do not add more. The Core test suite passes 229 tests. The portable build launches and records, transcribes, and saves meetings. See [BLOCKERS.md](BLOCKERS.md) for what still needs a specific machine to verify.

Per-process audio capture is implemented but has not been exercised against a machine with the target app playing. The app supports English, Japanese, and Vietnamese with Vosk's small/medium/large model tiers (medium is English-only); meetings default to Auto-detect, which identifies the spoken language with a staged VoxLingua107 model (see Speech models) and switches the engine live. JA/VI accuracy has not been verified with real speech.

## Features

- Record the microphone and system output together, mixed into one track, so your own voice is never lost. The system side can capture every app or a single chosen app (a conference call).
- On-device streaming transcription (Vosk, small/medium/large tiers), so transcription runs offline. The open sentence appears live as a normal transcript line while you speak.
- Automatic language detection (SpeechBrain VoxLingua107 via ONNX): new meetings default to Auto-detect and the engine follows the spoken language mid-meeting; picking a language pins it. The same language control appears in the meeting window, the overlay, and Settings.
- Screen recording alongside the audio: a whole display or a single window, video-only MP4, enabled by default and paused with the meeting; each run is saved as a track with its timeline offset.
- Live transcript view with a pinnable, always-on-top overlay (auto-opens with the meeting; recording indicator + screen-recording control included).
- Meetings list with per-meeting delete; Details view with audio playback, screen-video playback synced to the transcript timeline (current line highlighted, click a line to seek), transcript edit, copy, and export.
- Optional Gemini refinement with the API key stored in Windows Credential Locker.
- Configurable file logging for support and debugging.

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11.
- The .NET 8 SDK to build. Running the published portable build needs no .NET install.
- Speech models in `models/` for live transcription. They are not committed; see [Speech models](#speech-models).

## Layout

```text
src/MeetVault              WinUI 3 app: views, navigation, overlay window
src/MeetVault.Core         Domain, session, engine, storage, Gemini, logging
tests/MeetVault.Core.Tests xUnit tests for Core
docs/                      Spec (docs/mvp-winui) and living status documents
publish/win-x64            Canonical portable build (gitignored)
```

The app calls `MeetingSession`; the engine and storage sit behind interfaces and never touch the UI.

## Build and test

Replace `dotnet` with your SDK path if it is not on `PATH`.

```powershell
dotnet build MeetVault.sln -c Release
dotnet test tests\MeetVault.Core.Tests\MeetVault.Core.Tests.csproj -c Release --nologo -v q
```

The suite's Vosk contract test early-returns (reported as passed) when `models/vosk/small-en` is unstaged — a green CI run does not exercise live decoding.

## Run

Build the portable release into the canonical folder, then copy in the speech models:

```powershell
rmdir /s /q publish\win-x64
dotnet publish src\MeetVault\MeetVault.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
Copy-Item -Recurse -Force models publish\win-x64\models
publish\win-x64\MeetVault.exe
```

The publish brings in the native libraries the app loads (`libvosk.dll` via the Vosk package, `onnxruntime.dll` via `Microsoft.ML.OnnxRuntime`, and ScreenRecorderLib's x64 mixed-mode assembly). Screen recording additionally needs the Microsoft Visual C++ 2015-2022 x64 redistributable at runtime; without it the app reports "screen capture unavailable" and records audio-only.

The portable build has no package identity, so Windows Credential Locker is unavailable and a Gemini key may not persist across runs. Install the MSIX for the full feature set.

The MSIX is produced by `dotnet build` with the Appx targets enabled:

```powershell
dotnet build src\MeetVault\MeetVault.csproj -c Release -r win-x64 -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true
```

The package lands at `src/MeetVault/AppPackages/MeetVault_1.0.0.0_x64_Test/MeetVault_1.0.0.0_x64.msix`. Installing it needs the signing certificate trusted at machine scope, which needs administrator rights. See [BLOCKERS.md](BLOCKERS.md).

## Speech models

Place these under `models/`. They are gitignored and must be staged next to the executable for a portable run.

| Folder | Language | Size tier | Download |
| --- | --- | --- | --- |
| `vosk/small-en` | English | Small | ~40 MB |
| `vosk/medium-en` | English | Medium | ~128 MB |
| `vosk/large-en` | English | Large | ~1.8 GB |
| `vosk/small-ja` | Japanese | Small | ~48 MB |
| `vosk/large-ja` | Japanese | Large | ~1 GB |
| `vosk/small-vi` | Vietnamese | Small | ~32 MB |
| `vosk/large-vi` | Vietnamese | Large | ~78 MB |

The auto-detect language-ID model goes under `models/langid/`: `voxlingua107.onnx` (SpeechBrain
VoxLingua107 ECAPA checkpoint exported to ONNX, `christopherthompson81/voxlingua107-lid-onnx`)
and its `lang_map.json`. Without this folder, Auto-detect is unavailable and meetings fall
back to the configured language.

All are Vosk models (alphacephei.com/vosk/models), offline and streaming. Japanese and
Vietnamese have no medium tier — the picker shows those rows disabled. Staging commands and
the exact required files: [docs/ASR-ENGINES.md](docs/ASR-ENGINES.md).

## Configuration

Settings persist in `%APPDATA%\MeetVault\meetings.db` (SQLite, `settings` table). The Settings screen covers:

- **Display language**: the interface language — English or Japanese. **System default** follows the Windows display language; an explicit choice applies after a restart.
- **Audio**: microphone device, record-microphone on/off, record-system-audio on/off, and the system-audio source (all apps, or one running app). Microphone and system audio are mixed; disabling one records the other alone.
- **Transcription**: Language (English / Japanese / Vietnamese) and Model size (Small / Medium / Large). Offline streaming, runs on CPU. The caption under the controls explains what each size means; a tier whose files are missing shows "— not installed" and Live falls back to an installed model. Model staging: [docs/ASR-ENGINES.md](docs/ASR-ENGINES.md).
- **Overlay**: keep on top, background darkness (default 0.20), font size (default 18), lines shown.
- **Storage**: export folder.
- **Logging**: log level (default `debug`), open log folder, show log.
- **Gemini**: API key.

## Data and logs

| Path | Contents |
| --- | --- |
| `%APPDATA%\MeetVault\meetings.db` | Meetings, transcripts, jobs, settings |
| `%APPDATA%\MeetVault\media` | Recordings: `.m4a` (AAC 256 kbps), plus the working `.wav` when encoding was unavailable |
| `%APPDATA%\MeetVault\gemini` | Saved Gemini results |
| `%TEMP%\MeetVault\app.log` | Application log |
| `%TEMP%\MeetVault\crash.log`, `nav-errors.log` | Crash and navigation diagnostics |

## Documentation

- [docs/mvp-winui](docs/mvp-winui/00-spec-index.md): the frozen MVP specification.
- [docs/ASR-ENGINES.md](docs/ASR-ENGINES.md): the speech model — staging, behavior, measured facts.
- [docs/COVERAGE.md](docs/COVERAGE.md): requirement-to-evidence ledger and change history.
- [docs/BUILD-LEDGER.md](docs/BUILD-LEDGER.md): historical build log with engine measurements.
- [docs/progress/](docs/progress/SKILL.md): per-contributor work log, one file per contributor per day.
- [RUN-GUIDE.md](RUN-GUIDE.md): launch steps and location of data.
- [BLOCKERS.md](BLOCKERS.md): open items that need a specific machine.

## Known gaps

- Audio capture mixes the microphone and system output. Capturing one application uses the WASAPI process-loopback API (`ProcessLoopbackCapture`), which falls back to system-wide capture when the API is unavailable; the per-app path is not yet confirmed on a machine with the target app playing.
- JA/VI accuracy needs validation with real speech.
- The Gemini call has been tested against stubs, not against the live API.
- Meetings recorded by the retired diarized pipeline keep their speaker rows in Details with the speaker picker still editable (only rename was removed); new meetings are single-speaker by construction.

## Links

The **AGENTS.md** file at the repository root gives agent and contributor instructions for this repo.
