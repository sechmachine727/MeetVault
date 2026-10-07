# MVP coverage ledger (generated 2026-10-06 from current tree)

> Evidence key: TEST = unit/integration test green this session; RUNTIME = executed against real engines/models; BUILD = compiles clean; MISSING = no evidence.

## 05-test-plan spine

| Item | Evidence | Status |
|---|---|---|
| Counter assignment (Male1/Female1/Male2, Unknown free) | TEST SpeakerLabelerTests + Session_UsesSeparateGenderCounters | proven |
| Rename propagation | TEST RenameSpeaker_PropagatesToSpeakers + DomainTests | proven (store layer; UI row-sync by inspection) |
| Edit + reassign | TEST DomainTests | proven (record level) |
| Finish-ordering transaction | TEST Save_RollsBack_OnMidTransactionFault (fault injection) | proven |
| Pause gap | TEST PauseGap (mutation-verified guard) + 70-min run | proven |
| Scripted-PCM protocol tests | RUNTIME pipeline harness (TTS->whisper->sherpa->session->SQLite) | proven for transcription path |
| 60-min timestamp stability | TEST SeventyMinutes (420 windows, monotonic, exact span) | proven |
| Chunked-write memory profile | TEST ChunkedWriteTests (file grows pre-dispose) | proven (media side) |
| Engine handshake/timeout | TEST EngineHandshakeTests (missing-model errors) | proven (presence; timeout untested) |
| Malformed config | TEST ConfigValidationTests | proven |
| Stop-ack / no leaks | TEST SessionLifecycleTests | proven |
| Windows manual pass | none | MISSING (needs you: mic + launch) |
| Bundled-install smoke | Portable fresh build launches; UI walkthrough all pages alive (UIA, 2026-10-06 evening); MSIX signed (install needs machine trust) | partial (install trust is user step) |
| Gemini success/failure | TEST GeminiTests + TransportTests (stubs); no live call | partial (needs key) |

## Spec FRs (01-requirements)

| FR | Evidence | Status |
|---|---|---|
| FR1 mic + app audio, live lines in seconds | RUNTIME mic enumeration (0 devices here); per-app loopback unwritten | partial (needs mic + native helper) |
| FR2 gendered turns + rename | TEST + RUNTIME single-voice Male 1 | proven single-voice; multi-voice unproven |
| FR3 pinnable overlay, rename both ways | BUILD (code present, unlaunched) | unproven at runtime |
| FR4 pause gap | TEST (mutation-verified) | proven |
| FR5 edit + reassign | Details debounced Repo.Save (89d6370); record-level TEST | proven (logic); UI flow unlaunched |
| FR6 local save + restart recovery | TEST recovery round-trip + UI wiring | proven (logic); UI flow unlaunched |
| FR7 manual Gemini upload | TEST stubs + real transport code; no live call | partial (needs key) |
| FR8 key in Credential Locker | BUILD (PasswordVault adapter wired) | code-complete, runtime unproven |
| FR9 every setting wired | mic/device/model/diarization/overlay/storage all gate behavior; app source + gender controls removed until their features exist (WASAPI loopback helper, gender classifier) | proven |

## NFRs

| NFR | Evidence | Status |
|---|---|---|
| NFR1 offline except upload | by construction (no network in engines) | holds, unmeasured |
| NFR2 conversational latency | RUNTIME per-tier RTF: tiny 0.36x live-capable, base 1.66x too slow live (refinement only) | proven per-tier; live loop unmeasured end-to-end |
| NFR3 bounded memory | TEST chunked writes; segments ~100B each | argued, unprofiled |
| NFR4 timestamp stability | TEST 70-min run | proven |
| NFR5 bundled offline installer | MSIX signed (needs machine trust for install); single canonical portable release at publish/win-x64 ships models + natives alongside (271MB) | partial (install trust is user step) |

## What only you can close

1. ~~Dev-cert approval~~ DONE 2026-10-06: CN=AppPublisher self-signed (thumb BBD1..., to 2031), trusted in CurrentUser Root. Signed-MSIX repack in progress (Newton lane); install + launch walkthrough still yours.
2. Mic-present recording: LivePage now passes persisted mic id to Start+Recover (was hardcoded/empty). Your Recording.m4a validated: 13.74 s, EN detected both windows, sung non-EN portion does NOT exercise ja/vi — spoken JA/VI sample still needed. Two-speaker loopback deferred to your other-machine test.
3. Gemini key -> live upload call (still yours, manual with build exec).

## Deltas this session (Observed)

### Deltas 2026-10-07 later (Observed, commits 92df6cf..36ed183)

- Delete: Meetings Delete button + LocalMeetingRepository.DeleteMeeting (meeting, segments, speakers, job, and the media file).
- System audio: NAudioCapture captures the mic OR the system loopback (WasapiLoopbackCapture resampled to 16 kHz mono); Settings Audio source. Loopback device opens and records (verified); audio content and per-process capture still need your machine.
- Logging: AppLog writes a level-filtered, size-rolled log to %TEMP%\MeetVault\app.log, set in Settings (default debug); covers repo, session, capture, diarizer, whisper backend, Gemini, nav, GPU, and page errors.
- Suite: 74/74 green (adds AppLog + DeleteMeeting tests).

### Deltas 2026-10-07 (Observed, commits d6446b9..1128195)

- Nav: Details removed from the sidebar; it opens from a meeting row in Meetings/Dashboard.
- Live: recording starts only on an explicit Start recording press; Pause/Resume/Stop enable only in the matching state (verified via UIA in both states).
- Dashboard: Start new meeting moved top-right; Recent and Meetings tables wrapped in a card container with a "select to open details" tooltip.
- GPU (FR9): capability detection queries the CUDA device count and enumerates GPUs through DXGI (IDXGIFactory1::EnumAdapters1), which lists the real hardware adapters and excludes indirect/virtual display drivers (Sudomaker, Horizon IDD, Hyper-V Video) and WARP. Not EnumDisplayDevices, whose output includes those virtual adapters. Settings lists each GPU and reports the graphics/inference APIs probed on this PC ('available: CPU, Vulkan, DirectML'); a non-CUDA GPU is offered through the bundled Whisper.net.Runtime.Vulkan backend, and the runtime library order is set per backend so the choice sticks. Documented residual: DXGI has no indirect-display flag, so the strict discriminator is D3DKMT_ADAPTERTYPE.IndirectDisplayDevice; a D3DKMT probe on this host returned no render-capable adapters, so it was not shippable and DXGI (verified here) stays. Multi-GPU device ordinals are backend-specific and unverified; single-GPU is correct.
- Packaging: single canonical portable at publish/win-x64 (duplicate folders removed); a csproj target trims the linux/macos/arm runtime payload from both the portable and the MSIX.
- Suite: 63/63 green (adds GPU probe + device-setting encode/parse).

## Deltas 2026-10-06 evening (Observed, working tree)

- Root cause of the whole crash family found: the Release publish was IL-trimmed (PublishTrimmed=True), which stripped CsWinRT ABI glue (ItemsSource NRE) and halved the sherpa wrapper; separately, sherpa-onnx bound to the WindowsAppRuntime framework copy of onnxruntime.dll (base-name collision) and access-violated at engine init. Fixes: PublishTrimmed=False; App.xaml.cs pins the app's own onnxruntime.dll via NativeLibrary.Load before WinUI init.
- Proof: clean untrimmed publish; module trace shows sherpa loads with NO WindowsAppRuntime onnxruntime and the process stays alive; UIA walk over Dashboard/Meetings/Details/Settings/Live all alive=True, Start-new-meeting no longer crashes.
- Suite: 55/55 green (was 46); added RenameSpeakerTests + CounterGenderFallbackTests + MediaLinkTests + SettingsRoundTripTests + MultiMeetingIdTests.
- UI cleanup: users get plain sentences, not stack traces (MainWindow nav dialog, Live, Settings). Settings no longer dies on a failed write (warns once). Live shows real speaker labels + rename and drops whisper silence tokens. Details loads the real selected meeting, plays its saved audio, and sends via the real Gemini transport (canned demo transports removed). Demo seed data removed from the store. Meeting rows in Dashboard/Meetings are clickable (with UIA names) and open the clicked meeting in Details.
- Second review round (deepseek-v4.1-flash/max, read-only) fixed: cross-thread partial line, recovery timeline collision, in-recording checkpoint, per-meeting segment ids (a second meeting was unfinalizable), one WAV per session beside the DB, resume drafts from the Meetings list, plain user text. 55/55 green.
- Still unproven: multi-speaker separation, JA/VI accuracy, per-app WASAPI capture, live Gemini call, MSIX install (needs admin trust). Settings write is read-only only under the Codex sandbox; normal runs persist.

## Deltas 2026-10-06 afternoon (Observed, commits 4042f34..89d6370)

- UI crashes: Live NRE from missing TextBlockButtonStyle key (only Live+Overlay used it) fixed inline (b5133b3); Settings ToggleSwitch init-event crash guarded (24e5693); nav shows stack-bearing dialog + nav-errors.log; Live/Details lists converted ListView->ItemsControl after ComWrappers-vtable NRE stacks in your screenshots; vault never throws; all in publish/win-x64-fresh, awaiting your run.
- FR9 closed except app capture: real mic picker (NAudioCapture.ListDevices, persisted id), GPU CUDA device option (WhisperFactoryOptions UseGpu, CPU fallback), diarization toggle gates ONNX load. Gender toggle later removed (no classifier ships); labels are neutral Speaker N. 46/46 green at the time.
- FR5 closed at logic level: Details edits debounce-save (800 ms) via Repo.Save with id/timing maps; demo seed stays memory-only and says so (89d6370).
- Suite: 46/46 (was 42/42, 43/43). Fresh portable publish carries models alongside (tiny 78MB, base 148MB, sherpa 46MB).

## Deltas 2026-10-06 morning (Observed)

- Review fixes (TDD red->green, 42/42): StartAsync persists draft (crash-safe without Checkpoint); RecoverAsync requires mic id when restarting capture; LivePage passes persisted audio.mic/audio.source; enum-tolerant loads (bad status->Failed, bad gender->Unknown, bad job->Failed); RenameSpeaker rejects empty; Whisper null-language guard.
- Audio probe (Harvey): base 5 segs RTF 1.73, tiny 3 segs RTF 1.10, all windows lang en low-prob (music), diarizer scope-local labels.
