# MeetVault MVP Build Ledger

> Goal: MeetVault WinUI 3 MVP initial implementation per docs/mvp-winui specs.
> Done predicate: domain + session + repository + Gemini uploader units green, solution builds, smoke-tested UI, blockers recorded.

## Constraints (Observed)
- .NET 8 SDK only (8.0.425); scaffold TFM net8.0-windows10.0.26100.0; WindowsAppSDK 1.8.260317003; Toolkit.Mvvm 8.4.2
- No winapp CLI, no Computer-Use/WinUI MCP tools, no requested subagent models -> inline work, manual UI verification
- Sandbox: .git read-only; git writes need escalated shell. gh at C:\Program Files\GitHub CLI\gh.exe (not on PATH)

## Phases
- [ ] P1 domain models: Speaker/Segment/Meeting/GeminiJob + gender counters + rename (xunit, TDD red-green)
- [ ] P2 MeetingSession state machine: start/pause/resume/stop + pause-gap invariant (fake engines)
- [ ] P3 LocalMeetingRepository: SQLite schema per 04-data-and-gemini + finish transaction
- [ ] P4 engine interfaces: IAudioCapture/ISpeechTranscriber/ISpeakerDiarizer/IGenderLabeler stubs behind boundaries
- [ ] P5 GeminiUploader: Files-API + interactions HttpClient path, Credential Locker key (unit-testable seams)
- [ ] P6 WinUI shell: NavigationView (Dashboard, Live, Meetings, Details, Settings) + overlay window
- [ ] P7 build + tests green + UI smoke + independent review + push

## Rulings
- R1: scaffold via official winui-mvvm template net8.0 (matches installed SDK); MVVM already present, reuse it.
- R2: solution layout: src/MeetVault (app), src/MeetVault.Core (domain+session+repo, testable), tests/MeetVault.Core.Tests.
- R3: xunit for tests (spec 05-test-plan). No FluentAssertions (license check cost) - built-in asserts.

## Progress
- Scaffold done: src/MeetVault (winui-mvvm, net8.0).

## Language support (user decision 2026-10-06)

- Transcription must support English, Japanese, and Vietnamese, including mid-conversation language switches.
- Consequence: model choice changes — `base.en-q5_1` (English-only) is OUT; multilingual `base` (~142 MiB) or larger becomes the default candidate.
- Open: whisper.cpp language detection per segment vs fixed multi-language mode; diarization/gender validation on JA/VI audio; model-size vs real-time CPU tradeoff.
- Status: requirement recorded; engine selection (P4) must validate JA/VI before locking the bundled model.

## P4 engine-selection brief (verified 2026-10-06 via nuget.org)

- Whisper.net 1.9.1 stable confirmed latest stable (1.9.2-preview1 prerelease exists, do NOT take). Spec 07 pin holds.
- sherpa-onnx (org.k2fsa.sherpa.onnx) 1.13.8 confirmed latest. Spec pin holds.
- Google.GenAI 1.24.0 confirmed latest. Spec pin holds.
- Microsoft.ML.OnnxRuntime now at 1.30.0; spec 07 says 1.28.2 — re-verify compat with sherpa-onnx 1.13.8 at P4 time, prefer sherpa's pinned dependency.
- Multilingual requirement (EN/JA/VI) OVERRIDES spec 07 model pick: base.en-q5_1 is English-only, default to multilingual base (~142 MiB); validate JA/VI + mid-conversation switching before locking bundle.
- Net8 compat: Whisper.net targets net8 (verify at restore); sherpa-onnx .NET 6+ (fine); Google.GenAI targets net8 / netstandard2.0 (fine).


## Speech validation (2026-10-06, runtime evidence)

- Model: multilingual ggml-base.bin (147,951,465 bytes, HuggingFace via WhisperGgmlDownloader).
- EN TTS sample (System.Speech Zira): transcribed near-perfect with correct timestamps.
- Language detection constrained to [en, ja, vi]: EN sample -> en p=1.00.
- JA/VI accuracy NOT validated (no local voices); needs your review with real audio.
- Fixes landed: Whisper.net.Runtime package; WAV-header wrap (ProcessAsync needs RIFF); LanguageDetected event; trim-safe JSON already in place.


## Diarization validation (2026-10-06, runtime evidence)

- Models: sherpa-seg.onnx (6MB pyannote) + sherpa-emb.onnx (40MB eres2net), loaded OK, Process() runs without crash.
- Two-tone smoke (220Hz vs 440Hz): both assigned spk_1 — segmentation trained on speech, tones carry no speaker cues. Pipeline proven, separation quality NOT proven.
- Needs: real two-speaker EN/JA/VI audio for accuracy validation before shipping claims.


## Capture validation (2026-10-06, runtime evidence)

- NAudioCapture (WaveInEvent 16kHz mono, chunked WAV + PcmReady frames) compiles clean.
- This machine: 0 mic devices (headless). Enumeration proven, recording path unverified here — needs your machine with a mic.
- Per-app conference capture (WASAPI process loopback) still needs the native helper; mic-only for now.


## End-to-end pipeline (2026-10-06, runtime evidence)

- TTS speech -> Whisper (2 segments, near-perfect) -> sherpa diarizer (spk_1) -> CounterGenderLabeler (Male 1) -> MeetingSession -> SQLite (1 Ready meeting, segments + speaker persisted). PIPELINE OK.
- Uses real adapters throughout except mic (NullAudio; no mic on this machine) and per-segment diarizer bridge (window-level assignment).


## Smoke test attempt (2026-10-06, runtime evidence)

- Direct exe launch: managed crash REGDB_E_CLASSNOTREG in DeploymentManager.AutoInitialize — packaged app has no identity outside MSIX. NOT an app bug.
- MSIX builds: src/MeetVault/AppPackages/MeetVault_1.0.0.0_x64_Test/MeetVault_1.0.0.0_x64.msix.
- Install blocked: package unsigned (0x800B0100). Needs self-signed dev cert + trust + Add-AppxPackage — awaiting your explicit go-ahead (machine trust change).


## Latency measurement (2026-10-06, this machine, runtime evidence)

- Multilingual base model, CPU: 13.9 s audio -> 23.1 s transcribe (RTF 1.66x, slower than real-time).
- Implication: 10 s live windows would lag ~7 s per window cumulative. MVP options: (a) longer windows with explicit lag, (b) smaller/faster model for live + base for post-meeting refinement, (c) GPU/accelerated runtime. Decision needed before live-latency claims.
- NFR2 status: finals-within-seconds NOT met on this hardware with base model.


## Tiny-model measurement (2026-10-06, runtime evidence)

- ggml-tiny.bin (78MB): 9.4 s audio -> 3.3 s transcribe (RTF 0.36x, ~3x faster than real-time), near-perfect transcript.
- Two-tier story now evidence-backed: tiny for live windows (10 s audio ~= 3.6 s lag), multilingual base for post-meeting refinement.
- Multilingual note: tiny sample was EN; JA/VI accuracy per-tier still needs real voices.


## Session 2026-10-06 late (runtime evidence)

- Trilingual clip (Harvey): Recording.m4a 352,458 B -> 13.74 s PCM; base 5 segs RTF 1.73 (w0 en 0.426, w1 en 0.3288), tiny 3 segs RTF 1.10 (en 0.186/0.3929); diarizer scope-local labels (whole-clip spk_1 vs windows spk_2). Sung non-EN portion exercises no ja/vi path. Full notes: docs/TRILINGUAL-AUDIO-NOTES.md.
- Independent review (Fermat, read-only): 5 material findings (empty-input diarizer collapse, crash-durability gap, recovery mic bypass, validation gaps, non-atomic download). All fixed TDD red->green: MeetingSession persists draft at Start; RecoverAsync requires mic id when restarting capture; LivePage passes persisted audio.mic/audio.source; enum-tolerant loads; RenameSpeaker/Whisper guards; SherpaDiarizer empty-window guard; DownloadModelAsync tmp+move.
- Cert (approved Create-trust-install): CN=AppPublisher CodeSigningCert to 2031-10-06, trusted in CurrentUser Root (machine Root denied without admin). Signed-MSIX repack lane (Newton) in flight; portable exe publish/win-x64/MeetVault.exe refreshed with models staged.
- Suite: 43/43 green, solution 0 warn/0 err. Pushed through 0f2a221.


## Portable-exe launch fix (2026-10-06, your crash events, runtime evidence)

- Your events: both publish/msix and publish/win-x64 exes died REGDB_E_CLASSNOTREG — first in DeploymentManager.AutoInitialize, then (after disabling it) in WinUI XAML Application.Start.
- Root cause: EnableMsixTooling forces WindowsPackageType=MSIX even for portable publish, so DeploymentManager auto-init (packaged-identity only) fired; and Bootstrap DDLM was off (it defaults on only for WindowsPackageType=None), leaving no framework resolution.
- Fix (src/MeetVault/MeetVault.csproj): WindowsAppSdkDeploymentManagerInitialize=false + WindowsAppSdkBootstrapInitialize=true. MSIX packaging unaffected (identity path intact).
- Verified: republished exe launched, PID 23412 responding=True, zero fresh 1000/1026 events. Pushed as ac379be.
- Note: publish/msix/MeetVault.exe is a stale copy from the earlier MSIX-layout publish — use publish/win-x64/MeetVault.exe.


## Nav-crash fixes + dashboard redesign (2026-10-06, your screenshot + events, runtime evidence)

- Your report: Live/Details/top-Settings crashed (native 0xc000027b in Microsoft.UI.Xaml.dll); bottom-Settings opened Dashboard; Settings appeared twice; dashboard looked bare.
- Causes: NavigationView renders its own built-in Settings item alongside our explicit one (duplication); built-in item has no Tag so selection fell through (bottom-Settings -> Dashboard); Live constructed ONNX engines synchronously in ctor (any throw = nav crash); no nav failure barrier.
- Fixes: removed explicit Settings item, route built-in IsSettingsSelected -> SettingsPage; try/catch around RootFrame.Navigate with Dashboard fallback; Live engine construction fully inside try (falls back to demo fakes).
- Dashboard (Operate mode): greeting + engine line, readiness strip (mic/app-source/engine + accent Start button), column-header recent table (title/when/duration/status), teaching empty state. No new deps, same controls.
- Verified: build 0/0, 43/43 green, republished to publish/win-x64 (needed you to close the running app: file lock). Pushed as 0619da1.
- Computer-use UI debugging: unavailable this session (no MCP tools; node_repl sky init sandbox-blocked). Static analysis + crash-lane triangulation instead.

