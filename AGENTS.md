# AGENTS.md — MeetVault

Repo-specific instructions for working in this repository. Global engineering rules still apply; this file adds only what is specific to MeetVault.

## What this is

A local-first Windows meeting recorder and transcriber: WinUI 3 app on .NET 8, with an on-device transcription and diarization pipeline and an optional manual Gemini upload. See [README.md](README.md) for the product overview and [docs/mvp-winui](docs/mvp-winui/00-spec-index.md) for the specification.

## Layout

- `src/MeetVault` — WinUI 3 app: XAML views, navigation, overlay window.
- `src/MeetVault.Core` — domain, session, engines, storage, Gemini, logging. No UI or WinUI types here.
- `tests/MeetVault.Core.Tests` — xUnit tests for Core.
- `docs/mvp-winui` — frozen spec. `docs/COVERAGE.md` and `BLOCKERS.md` track current status.

Views call `MeetingSession` and nothing else. Engines and storage sit behind interfaces in `EngineInterfaces.cs`; the repository is the single writer of persisted state.

## Build, test, run

```powershell
dotnet build MeetVault.sln -c Release
dotnet test tests\MeetVault.Core.Tests\MeetVault.Core.Tests.csproj -c Release --nologo -v q
dotnet publish src\MeetVault\MeetVault.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
```

- Use the .NET 8 SDK. If `dotnet` is not on `PATH`, use the full path (this machine uses `D:\dotnet\dotnet.exe`).
- Some tests load the real Whisper model and skip when `models/ggml-tiny.bin` is absent.
- In this workspace the sandbox hangs non-escalated commands; run build, test, publish, git, and the app escalated.
- `dotnet test` can abort with "vstest.console process failed to connect" under the sandbox; set `VSTEST_CONNECTION_TIMEOUT=240` and run escalated.

## Constraints — do not regress these

These keep the published app from crashing. Each was found the hard way.

- `PublishTrimmed` must stay `false`. ILLink strips CsWinRT ABI vtable glue (an `ItemsSource` NRE in `GetAbiToProjectionVftblPtr`) and halves the sherpa-onnx wrapper, which crashes at engine init.
- `App.xaml.cs` loads the app's own `onnxruntime.dll` through `NativeLibrary.Load` before `InitializeComponent`. sherpa-onnx binds the DLL by base name; without the pin it picks up the WindowsAppRuntime framework copy and access-violates.
- The app project sets `WindowsAppSdkDeploymentManagerInitialize=false` and `WindowsAppSdkBootstrapInitialize=true`. The portable build has no package identity, and deployment auto-init fails without it.
- `publish/win-x64` is the only portable output. The `TrimForeignRuntimes` targets in `MeetVault.csproj` delete linux, macOS, and non-x64 native payloads after build and publish; keep them so both the portable build and the MSIX stay clean.
- Never commit `models/` or the staged native DLLs. They are gitignored and must be staged into the publish folder.
- Do not hand a live `ObservableCollection` across the WinRT boundary. Snapshot with `.ToArray()` and refresh on change (see `LivePage`, `DetailsPage`, `OverlayWindow`).

## Conventions

- Tests use xUnit and built-in asserts. FluentAssertions is intentionally absent.
- Log through `MeetVault.Core.AppLog` (`Info`, `Debug`, `Warn`, `Error`). The level is set in Settings and stored as `logging.level` (default `debug`). Log operational events in Core, not just inference.
- Persisted settings live in the `settings` table and are read with `LocalMeetingRepository.GetSetting`. Keep keys consistent between the writer and the reader.
- Segment ids are namespaced by meeting id (`{meetingId}-w{start}-{index}`) because `transcript_segments.id` is a global primary key.
- Media files are one WAV per meeting, kept open across pause and resume, under `%APPDATA%\MeetVault\media`. `StopAsync` finalizes the WAV header.
- Capture always combines the microphone and system audio, mixed to 16 kHz mono. `IAudioCapture.StartAsync(micId, appSource)` hints select the parts: `micId` is a device index or `off`; `appSource` is `off`, `all`, or a process id for one app (process loopback, with a fallback to system-wide).

## Verification expectations

- Build must stay at 0 warnings and 0 errors; run the test suite before claiming a change works.
- For UI changes, launch the built exe and inspect it. UI Automation driving through `System.Windows.Automation` from Windows PowerShell 5.1 is the working method here; `pwsh` is broken in this harness.
- A green suite is not product proof: transcription quality, diarization, and the live Gemini call need a machine with audio devices and a key. Track what remains in `BLOCKERS.md` rather than asserting it works.

## Where the truth lives

- `docs/COVERAGE.md` — requirement-to-evidence ledger and change history.
- `docs/BUILD-LEDGER.md` — historical build log with engine measurements (past tense; not current status).
- `BLOCKERS.md` — open items that need a specific machine or the user.
