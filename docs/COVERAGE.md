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
| Bundled-install smoke | MSIX builds; install blocked on signing | partial |
| Gemini success/failure | TEST GeminiTests + TransportTests (stubs); no live call | partial (needs key) |

## Spec FRs (01-requirements)

| FR | Evidence | Status |
|---|---|---|
| FR1 mic + app audio, live lines in seconds | RUNTIME mic enumeration (0 devices here); per-app loopback unwritten | partial (needs mic + native helper) |
| FR2 gendered turns + rename | TEST + RUNTIME single-voice Male 1 | proven single-voice; multi-voice unproven |
| FR3 pinnable overlay, rename both ways | BUILD (code present, unlaunched) | unproven at runtime |
| FR4 pause gap | TEST (mutation-verified) | proven |
| FR5 edit + reassign | UI present, record-level TEST | partial |
| FR6 local save + restart recovery | TEST recovery round-trip + UI wiring | proven (logic); UI flow unlaunched |
| FR7 manual Gemini upload | TEST stubs + real transport code; no live call | partial (needs key) |
| FR8 key in Credential Locker | BUILD (PasswordVault adapter wired) | code-complete, runtime unproven |
| FR9 every setting wired | Settings persist to SQLite | proven (persistence); effect wiring partial |

## NFRs

| NFR | Evidence | Status |
|---|---|---|
| NFR1 offline except upload | by construction (no network in engines) | holds, unmeasured |
| NFR2 conversational latency | unmeasured | MISSING |
| NFR3 bounded memory | TEST chunked writes; segments ~100B each | argued, unprofiled |
| NFR4 timestamp stability | TEST 70-min run | proven |
| NFR5 bundled offline installer | MSIX builds; models external (193MB, gitignored) | partial |

## What only you can close

1. ~~Dev-cert approval~~ DONE 2026-10-06: CN=AppPublisher self-signed (thumb BBD1..., to 2031), trusted in CurrentUser Root. Signed-MSIX repack in progress (Newton lane); install + launch walkthrough still yours.
2. Mic-present recording: LivePage now passes persisted mic id to Start+Recover (was hardcoded/empty). Your Recording.m4a validated: 13.74 s, EN detected both windows, sung non-EN portion does NOT exercise ja/vi — spoken JA/VI sample still needed. Two-speaker loopback deferred to your other-machine test.
3. Gemini key -> live upload call (still yours, manual with build exec).

## Deltas this session (Observed)

- Review fixes (TDD red->green, 42/42): StartAsync persists draft (crash-safe without Checkpoint); RecoverAsync requires mic id when restarting capture; LivePage passes persisted audio.mic/audio.source; enum-tolerant loads (bad status->Failed, bad gender->Unknown, bad job->Failed); RenameSpeaker rejects empty; Whisper null-language guard.
- Audio probe (Harvey): base 5 segs RTF 1.73, tiny 3 segs RTF 1.10, all windows lang en low-prob (music), diarizer scope-local labels.
