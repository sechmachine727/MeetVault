# AGENTS.md — MeetVault

Repo-specific instructions for working in this repository. Global engineering rules still apply; this file adds only what is specific to MeetVault.

## What this is

A local-first Windows meeting recorder and transcriber: WinUI 3 app on .NET 8, with an on-device Vosk transcription pipeline (single-speaker by construction) and an optional cloud service (sign-in; backend-enhanced transcription, summaries, Word export). See [README.md](README.md) for the product overview and [docs/mvp-winui](docs/mvp-winui/00-spec-index.md) for the specification.

## Layout

- `src/MeetVault` — WinUI 3 app: XAML views, navigation, overlay window.
- `src/MeetVault.Core` — domain, session, engines, storage, backend client, logging. No UI or WinUI types here.
- `tests/MeetVault.Core.Tests` — xUnit tests for Core.
- `docs/mvp-winui` — frozen spec. `docs/COVERAGE.md` and `BLOCKERS.md` track current status.
- `.github` — the pull request review pipeline. See "Pull request review pipeline" below.

Views call `MeetingSession` and nothing else. Engines and storage sit behind interfaces in `EngineInterfaces.cs`; the repository is the single writer of persisted state.

## Build, test, run

```powershell
dotnet build MeetVault.sln -c Release
dotnet test tests\MeetVault.Core.Tests\MeetVault.Core.Tests.csproj -c Release --nologo -v q
dotnet publish src\MeetVault\MeetVault.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
```

- Use the .NET 8 SDK. If `dotnet` is not on `PATH`, use the full path (this machine has it at `C:\Program Files\dotnet\dotnet.exe`).
- Some tests load the real Vosk model and skip when the staged `models/` tree is absent.
- Build, test, publish, git, and the app are all runnable directly in this workspace now; if a command fails on workspace file permissions, that is an environment fault to fix, not a reason to escalate.
- `dotnet test` can abort with "vstest.console process failed to connect"; set `VSTEST_CONNECTION_TIMEOUT=240` and retry.

## Constraints — do not regress these

These keep the published app from crashing. Each was found the hard way.

- `PublishTrimmed` must stay `false`. ILLink strips CsWinRT ABI vtable glue (an `ItemsSource` NRE in `GetAbiToProjectionVftblPtr`) and used to halve the (since-removed) sherpa-onnx wrapper, which crashed at engine init.
- `App.xaml.cs` loads the app's own `onnxruntime.dll` through `NativeLibrary.Load` before `InitializeComponent`. The language-ID detector needs that assembly at version 1.31.0.0, but `Microsoft.WindowsAppSDK.ML` ships a same-named stub (0.0.0.0) that used to win the publish copy race — the app csproj excludes that package's assets (`ExcludeAssets="all"`) and the pin keeps the load path explicit. Without it the app picks up the WindowsAppRuntime framework copy and access-violates.
- The app project sets `WindowsAppSdkDeploymentManagerInitialize=false` and `WindowsAppSdkBootstrapInitialize=true`. The portable build has no package identity, and deployment auto-init fails without it.
- `publish/win-x64` is the only portable output. The `TrimForeignRuntimes` targets in `MeetVault.csproj` delete linux, macOS, and non-x64 native payloads after build and publish; keep them so both the portable build and the MSIX stay clean.
- Never commit `models/`, `cuda/`, or `vcruntime/`. They are gitignored and staged at the repository root. Staging procedure: [docs/CUDA-SETUP.md](docs/CUDA-SETUP.md). The **deliverable** carries only the models `EngineCatalog` offers (Vosk large-en + large-ja + langid); `tools/pack-portable.ps1` stages exactly those and the `TrimUnshippedModels` target drops anything else, so the portable zip stays shippable (~3.0 GB) instead of carrying every staged tier. The `Content` items below bundle `cuda/` and `vcruntime/` automatically.
- Do not hand a live `ObservableCollection` across the WinRT boundary. Snapshot with `.ToArray()` and refresh on change (see `LivePage`, `DetailsPage`, `OverlayWindow`).

## Pull request review pipeline

Three workflows under `.github/workflows` review a pull request and gate its draft state. Read the file headers; they carry the reasoning. What matters when changing them:

- `pr-review.yml` reviews an open pull request and posts a GitHub review with a verdict. A `critical` or `important` finding is a change request; anything else is accepted. The model returns one JSON object, described in [.github/codex/pr-review-prompt.md](.github/codex/pr-review-prompt.md).
- `pr-auto-draft.yml` returns the pull request to draft and clears the checklist on a push, or on a change request.
- `pr-ready-gate.yml` appends the checklist to a new pull request, re-opens a draft when every box is ticked and no review thread is unresolved, and sweeps open pull requests on a schedule.

Constraints found the hard way:

- `GITHUB_TOKEN` cannot approve: GitHub answers an `APPROVE` review with "GitHub Actions is not permitted to approve pull requests". An accepted verdict posts as a `COMMENT` review; only a change request uses `REQUEST_CHANGES`.
- Only `synchronize`, a push, and a change request draft a pull request. Drafting on open would suppress the first review, so an opened pull request is only given the checklist.
- The reviewer pins the rubric and `.github/codex/model-catalog.json` from the base commit, so a pull request cannot weaken the rules that judge it. A change to either file is reviewed by the base copy and takes effect on later pull requests.
- `.github/codex/model-catalog.json` is a trimmed copy of the local catalog. Keep every field; the CLI rejects an entry missing `shell_type` or `base_instructions`. It overrides `base_instructions` with a review-specific prompt.
- Pin actions to a commit SHA. Floating tags drift onto runtimes GitHub later deprecates.
- The confirmation checklist in the pull request description is owned by `.github/scripts/pr-ready-checklist.cjs`. Do not hand-edit between its markers; change the block in that module and its test.
- A branch five or more commits behind `main` cannot re-open: a review of a stale branch does not describe what merges.
- The review runs on `ubuntu-latest`, so it builds and tests `MeetVault.Core` and its tests only. The WinUI app targets `net8.0-windows10.0.26100.0` and does not build there.

## Conventions

- Tests use xUnit and built-in asserts. FluentAssertions is intentionally absent.
- Log through `MeetVault.Core.AppLog` (`Info`, `Debug`, `Warn`, `Error`). The level is set in Settings and stored as `logging.level` (default `debug`). Log operational events in Core, not just inference.
- Persisted settings live in the `settings` table and are read with `LocalMeetingRepository.GetSetting`. Keep keys consistent between the writer and the reader.
- Segment ids are minted as `s<guid32>-<n>` by each streaming-runner instance (`StreamingRunner`): `transcript_segments.id` is a global primary key, so every runner namespaces its own sequence with a full GUID — never restart a shared sequence at `s0`, and never shorten the namespace to a slice that can collide.
- Media lives under `%APPDATA%\MeetVault\media` as one append-safe WAV per meeting (`WavSink`, 48 kHz mono), kept open across pause and resume. `StopAsync` only pauses; `FinishAsync` ends a meeting: it closes the WAV and encodes it to `.m4a` (AAC 256 kbps via Media Foundation). The session deletes the working WAV after the row points at the `.m4a`, and keeps the WAV when the encoder is unavailable. Playback and the backend upload take the MIME from the extension.
- Capture always combines the microphone and system audio, mixed to 48 kHz mono for the archive, with a 16 kHz mono copy (`InferenceDownmixer`) for the recognizer. `IAudioCapture.StartAsync(micId, appSource)` hints select the parts: `micId` is a device index or `off`; `appSource` is `off`, `all`, or a process id for one app (process loopback, with a fallback to system-wide).
- There is no GPU path and no staged-payload bundling left: the app ships `libvosk.dll` via the Vosk NuGet package, resolves its models from `models/` next to the exe, and loads nothing by base-name tricks. Earlier GPU/CUDA/whisper rules were removed with those engines.

### Progress log

Record your work in `docs/progress/` as you go, so the next agent starts from evidence rather than rediscovering the session. Track one folder per contributor and one file per day, append new entries instead of rewriting old ones, and name the evidence for every claim. The rule and the required format are in [docs/progress/SKILL.md](docs/progress/SKILL.md); the section layout is in [docs/progress/TEMPLATE.md](docs/progress/TEMPLATE.md).

## Verification expectations

- Build must stay at 0 errors; run the test suite before claiming a change works. Three pre-existing CA1416 warnings remain in `ProcessLoopbackCapture.cs` (Marshal.ReleaseComObject call sites); do not add more.
- For UI changes, launch the built exe and inspect it. UI Automation driving through `System.Windows.Automation` from Windows PowerShell 5.1 is the working method here.
- A green suite is not product proof: transcription quality and the live cloud calls (backend analysis, Word export) need a machine with audio devices and a signed-in account. Track what remains in `BLOCKERS.md` rather than asserting it works.

## Where the truth lives

- `docs/COVERAGE.md` — requirement-to-evidence ledger and change history.
- `docs/BUILD-LEDGER.md` — historical build log with engine measurements (past tense; not current status).
- `docs/progress/` — per-contributor work log, one file per contributor per calendar day.
- `BLOCKERS.md` — open items that need a specific machine or the user.
- `.github/workflows/` and `.github/codex/` — the pull request review pipeline, its rubric, and its model catalog.
