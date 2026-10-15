# MeetVault

A local-first Windows meeting recorder and transcriber. It captures audio, transcribes it on-device, and shows the transcript live. Recording and transcription never need an account or a network; signing in to the optional cloud service adds backend-enhanced transcription, summaries and Word export.

> This README and every command in it are relative to `client/` (the app's directory in the
> monorepo). Repo-wide docs are in the repository root.

Built with C# and WinUI 3 on .NET 8. Local use needs no account; an optional cloud service (with sign-in) provides enhanced transcription, summaries and cloud storage of your meetings.

## Status

Working MVP, tested on Windows 11. The solution builds with 0 errors; three pre-existing `CA1416` warnings remain in `ProcessLoopbackCapture.cs` (COM release call sites) — do not add more. The Core test suite passes 372 tests. The portable build launches and records, transcribes, and saves meetings. See [BLOCKERS.md](BLOCKERS.md) for what still needs a specific machine to verify.

Per-process audio capture is implemented but has not been exercised against a machine with the target app playing. The app supports English and Japanese with Vosk's large model tier; meetings default to Auto-detect, which identifies the spoken language with a staged VoxLingua107 model (see Speech models) and switches the engine live. JA accuracy has not been verified with real speech.

## Features

- Record the microphone and system output together, mixed into one track, so your own voice is never lost. The system side can capture every app or a single chosen app (a conference call).
- On-device streaming transcription (Vosk, the large tier for English and Japanese), so transcription runs offline. The open sentence appears live as a normal transcript line while you speak.
- Automatic language detection (SpeechBrain VoxLingua107 via ONNX): new meetings default to Auto-detect and the engine follows the spoken language mid-meeting; picking a language pins it. The same language control appears in the meeting window, the overlay, and Settings.
- Screen recording alongside the audio: a whole display or a single window, video-only MP4, enabled by default and paused with the meeting; each run is saved as a track with its timeline offset.
- Live transcript view with a pinnable, always-on-top overlay (auto-opens with the meeting; recording indicator + screen-recording control included).
- Meetings list with per-meeting delete; Details view with audio playback, screen-video playback synced to the transcript timeline (current line highlighted, click a line to seek), transcript edit, copy, and export.
- Account screen with sign-in: multi-user accounts on the cloud service; the session token lives in the Windows Credential Locker and the email in the settings DB. Recording and transcription never require an account.
- Cloud Storage screen: the server-side list of the signed-in user's meetings (the server is the only source of truth; local-only meetings never appear), with Open (Details opens the local record when the meeting was uploaded from this machine, otherwise the cloud copy), Remove from cloud, and auto-refresh while anything is processing.
- Details ▸ Analyze meeting: a six-state workflow (Not analyzed / Uploading / Queued / Processing / Analyzed / Analysis failed) that uploads the recording, transcript and screen tracks, runs the backend pipeline, and swaps in the backend-enhanced speaker-labelled transcript.
- Details ▸ Meeting summary: summary, agenda & discussion, decisions, action items and Q&A with citation seek, an interactive ask box, Copy summary, and Export to Word through the backend's docx endpoint.
- Configurable file logging for support and debugging.

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11.
- The .NET 8 SDK to build. Running the published portable build needs no .NET install.
- Speech models in `models/` for live transcription. They are not committed; see [Speech models](#speech-models).

## Layout

```text
src/MeetVault              WinUI 3 app: views, navigation, overlay window
src/MeetVault.Core         Domain, session, engine, storage, backend client, logging
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

The suite's Vosk contract test early-returns (reported as passed) when `models/vosk/large-ja` is unstaged — a green CI run does not exercise live decoding.

## Run

Build the portable release into the canonical folder, stage only the shipped models, and zip:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack-portable.ps1
publish\win-x64\MeetVault.exe
```

The publish brings in the native libraries the app loads (`libvosk.dll` via the Vosk package, `onnxruntime.dll` via `Microsoft.ML.OnnxRuntime`, and ScreenRecorderLib's x64 mixed-mode assembly). Screen recording additionally needs the Microsoft Visual C++ 2015-2022 x64 redistributable at runtime; without it the app reports "screen capture unavailable" and records audio-only.

For a **regular-user install** (one file, no admin, no UAC, models included) build the per-user installer instead: `tools\build-installer.ps1` → `publish\MeetVaultSetup-1.0.0.exe`. See [installer/README.md](installer/README.md).

The portable build and the installer have no package identity, but the WinRT Windows Credential Locker still works for them (verified: a token stored by an unpackaged run was read back by a freshly installed build). If the Locker is ever unavailable — a locked-down machine — the app falls back to a DPAPI-protected file under `%APPDATA%\MeetVault\secrets` (current user only, never plaintext), so a sign-in can never silently fail to persist. See [installer/README.md](installer/README.md).

The MSIX is produced by `dotnet build` with the Appx targets enabled:

```powershell
dotnet build src\MeetVault\MeetVault.csproj -c Release -r win-x64 -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true
```

The package lands at `src/MeetVault/AppPackages/MeetVault_1.0.0.0_x64_Test/MeetVault_1.0.0.0_x64.msix`. Installing it needs the signing certificate trusted at machine scope, which needs administrator rights. See [BLOCKERS.md](BLOCKERS.md).

## Speech models

Place these under `models/`. They are gitignored and must be staged next to the executable for a portable run.

| Folder | Language | Size tier | Download | Shipped |
| --- | --- | --- | --- | --- |
| `vosk/large-en` | English | Large | ~1.8 GB | yes |
| `vosk/large-ja` | Japanese | Large | ~1 GB | yes |

Only the shipped rows reach the deliverable — `tools/pack-portable.ps1` stages exactly those and the
`TrimUnshippedModels` target drops everything else. See [docs/ASR-ENGINES.md](docs/ASR-ENGINES.md)
for the not-shipped tiers and the retired Vietnamese models.

The auto-detect language-ID model goes under `models/langid/`: `voxlingua107.onnx` (SpeechBrain
VoxLingua107 ECAPA checkpoint exported to ONNX, `christopherthompson81/voxlingua107-lid-onnx`)
and its `lang_map.json`. Without this folder, Auto-detect is unavailable and meetings fall
back to the configured language.

All are Vosk models (alphacephei.com/vosk/models), offline and streaming. Neither shipped
language has a small or medium tier in this build — the picker shows only Large. Staging commands and
the exact required files: [docs/ASR-ENGINES.md](docs/ASR-ENGINES.md).

## Configuration

Settings persist in `%APPDATA%\MeetVault\meetings.db` (SQLite, `settings` table). The Settings screen covers:

- **Display language**: the interface language — English or Japanese. **System default** follows the Windows display language; an explicit choice applies after a restart.
- **Audio**: microphone device, record-microphone on/off, record-system-audio on/off, and the system-audio source (all apps, or one running app). Microphone and system audio are mixed; disabling one records the other alone.
- **Transcription**: Language (English / Japanese) and Model size (Large). Offline streaming, runs on CPU. The caption under the controls explains what each size means; a tier whose files are missing shows "— not installed" and Live falls back to an installed model. Model staging: [docs/ASR-ENGINES.md](docs/ASR-ENGINES.md).
- **Overlay**: keep on top, background darkness (default 0.20), font size (default 18), lines shown.
- **Storage**: export folder.
- **Logging**: log level (default `debug`), open log folder, show log.
- **Cloud service**: the app ships pointing at `https://meetvault.supersanta.io.vn`; Settings ▸ Server URL overrides it. Switching servers signs you out and clears the per-meeting cloud links and cached server documents. The session token is held in the Windows Credential Locker (never in the database).

## Data and logs

| Path | Contents |
| --- | --- |
| `%APPDATA%\MeetVault\meetings.db` | Meetings, transcripts, jobs, settings |
| `%APPDATA%\MeetVault\media` | Recordings: `.m4a` (AAC 256 kbps), plus the working `.wav` when encoding was unavailable |
| `%APPDATA%\MeetVault\gemini` | Legacy saved Gemini results (the feature was removed; the files are left in place) |
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
- Japanese accuracy needs validation with real speech.
- Cloud features (sign-in, Analyze meeting, Cloud Storage, Word export) are implemented and covered by the Core suite, and the live flow against the deployed backend has been exercised (sign-in, Cloud Storage listing, a full API smoke run on 2026-10-15); Analyze → summary → Word export on a fresh real recording is the remaining owner-side check (see [docs/COVERAGE.md](docs/COVERAGE.md)).
- Meetings recorded by the retired diarized pipeline keep their speaker rows in Details with the speaker picker still editable (only rename was removed); new meetings are single-speaker by construction.

## Links

The **AGENTS.md** file at the repository root gives agent and contributor instructions for this repo.
