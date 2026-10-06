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
| Bundled-install smoke | MSIX signed; portable fresh build launches (PID observed responding) | partial (page-level crashes under fix; see BLOCKERS) |
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
| FR9 every setting wired | mic/device/model/diarization/gender/overlay/storage all gate behavior (6bc2b1c); app-source persists preference only (WASAPI helper missing) | proven except app capture |

## NFRs

| NFR | Evidence | Status |
|---|---|---|
| NFR1 offline except upload | by construction (no network in engines) | holds, unmeasured |
| NFR2 conversational latency | RUNTIME per-tier RTF: tiny 0.36x live-capable, base 1.66x too slow live (refinement only) | proven per-tier; live loop unmeasured end-to-end |
| NFR3 bounded memory | TEST chunked writes; segments ~100B each | argued, unprofiled |
| NFR4 timestamp stability | TEST 70-min run | proven |
| NFR5 bundled offline installer | MSIX signed (needs machine trust for install); portable win-x64-fresh ships models alongside (271MB) | partial (install trust is user step) |

## What only you can close

1. ~~Dev-cert approval~~ DONE 2026-10-06: CN=AppPublisher self-signed (thumb BBD1..., to 2031), trusted in CurrentUser Root. Signed-MSIX repack in progress (Newton lane); install + launch walkthrough still yours.
2. Mic-present recording: LivePage now passes persisted mic id to Start+Recover (was hardcoded/empty). Your Recording.m4a validated: 13.74 s, EN detected both windows, sung non-EN portion does NOT exercise ja/vi — spoken JA/VI sample still needed. Two-speaker loopback deferred to your other-machine test.
3. Gemini key -> live upload call (still yours, manual with build exec).

## Deltas this session (Observed)

## Deltas 2026-10-06 afternoon (Observed, commits 4042f34..89d6370)

- UI crashes: Live NRE from missing TextBlockButtonStyle key (only Live+Overlay used it) fixed inline (b5133b3); Settings ToggleSwitch init-event crash guarded (24e5693); nav shows stack-bearing dialog + nav-errors.log; Live/Details lists converted ListView->ItemsControl after ComWrappers-vtable NRE stacks in your screenshots; vault never throws; all in publish/win-x64-fresh, awaiting your run.
- FR9 closed except app capture: real mic picker (NAudioCapture.ListDevices, persisted id), GPU CUDA device option (WhisperFactoryOptions UseGpu, CPU fallback), diarization toggle gates ONNX load, gender toggle switches counter/neutral labeler (NeutralSpeakerLabeler + test). 46/46 green.
- FR5 closed at logic level: Details edits debounce-save (800 ms) via Repo.Save with id/timing maps; demo seed stays memory-only and says so (89d6370).
- Suite: 46/46 (was 42/42, 43/43). Fresh portable publish carries models alongside (tiny 78MB, base 148MB, sherpa 46MB).

## Deltas 2026-10-06 morning (Observed)

- Review fixes (TDD red->green, 42/42): StartAsync persists draft (crash-safe without Checkpoint); RecoverAsync requires mic id when restarting capture; LivePage passes persisted audio.mic/audio.source; enum-tolerant loads (bad status->Failed, bad gender->Unknown, bad job->Failed); RenameSpeaker rejects empty; Whisper null-language guard.
- Audio probe (Harvey): base 5 segs RTF 1.73, tiny 3 segs RTF 1.10, all windows lang en low-prob (music), diarizer scope-local labels.
