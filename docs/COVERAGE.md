# MVP coverage ledger (generated 2026-10-06 from current tree)

> Evidence key: TEST = unit/integration test green this session; RUNTIME = executed against real engines/models; BUILD = compiles clean; MISSING = no evidence.

> Scope: this file is the requirement-to-evidence ledger. Per-session work history moved to [progress/](progress/) on 2026-10-07. The `Deltas` sections below are kept as archived history from before that move and are no longer appended to.

> Note (2026-10-11, Vosk-only consolidation): rows marked "retired 2026-10-11" cite tests or pipelines for features that were removed (gender labeling, diarization, rename, and the whisper/moonshine/zipformer engines); those tests no longer exist. Rows without the mark are believed current — verify against the tree before relying on them.

## 05-test-plan spine

| Item | Evidence | Status |
|---|---|---|
| Counter assignment (Male1/Female1/Male2, Unknown free) | TEST SpeakerLabelerTests + Session_UsesSeparateGenderCounters | retired 2026-10-11 (feature removed) |
| Rename propagation | TEST RenameSpeaker_PropagatesToSpeakers_KeepsSegmentJoin | proven (repo level; rename UI retired 2026-10-11) |
| Edit + reassign | TEST DomainTests | proven (record level) |
| Finish-ordering transaction | TEST Save_RollsBack_OnMidTransactionFault (fault injection) | proven |
| Pause gap | TEST PauseGap (mutation-verified guard) + 70-min run | proven |
| Scripted-PCM protocol tests | RUNTIME pipeline harness (TTS->whisper->sherpa->session->SQLite) | retired 2026-10-11 (pipeline replaced by Vosk streaming) |
| 60-min timestamp stability | TEST SeventyMinutes (420 windows, monotonic, exact span) | proven |
| Chunked-write memory profile | TEST ChunkedWriteTests (file grows pre-dispose) | proven (media side) |
| Engine handshake/timeout | TEST EngineCatalogTests (model availability / download state) | partial (missing-model UX; timeout untested) |
| Malformed config | TEST ConfigValidationTests | proven |
| Stop-ack / no leaks | TEST SessionLifecycleTests | proven |
| Windows manual pass | none | MISSING (needs you: mic + launch) |
| Bundled-install smoke | Portable fresh build launches; UI walkthrough all pages alive (UIA, 2026-10-06 evening); MSIX signed (install needs machine trust) | partial (install trust is user step) |
| Gemini success/failure | TEST GeminiTests + TransportTests (stubs); no live call | partial (needs key) |

## Language detection + screen recording (2026-10-12)

| Item | Evidence | Status |
|---|---|---|
| Auto language detection (VoxLingua107, ONNX) | TEST LangId* suites (real-model en/ja fixture check) + RUNTIME mid-meeting auto switch en→ja (published build: `live: language -> ja (auto)`, `meetings.language=ja`, JA segments transcribed) | proven on fixture speech |
| Language controls (Live / overlay / Settings) | RUNTIME UIA: overlay pick → persisted → engine swap both ways; Settings default + picker; Live combo reflects mode | proven |
| Engine swap relay (in-meeting) | TEST SwitchableStreamingTranscriberTests + P3 probe (5 real-engine cycles, 0 dupes) | proven |
| Screen recording lifecycle | TEST ScreenCaptureControllerTests (mutation-proven) + RUNTIME: start with meeting, pause finalizes (offset 2 ms), resume starts a new run (offset 50887 ms), stop finalizes; tracks + files on disk | proven |
| Screen capture sources | TEST CaptureSourceResolverTests + RUNTIME display capture (WGC forced — DesktopDuplication is black on this box); window source probe-level | proven (display); window probe |
| Live preview + recorded-video playback | RUNTIME: frame-API preview during a run; Details plays the track with the playhead line highlighted (screenshot verified) | proven |
| Overlay recording control + indicator | RUNTIME: auto open with the meeting and close on stop; button routes to the controller (not-running guard); real-click proof of the header controls | proven |
| media_tracks storage + delete integration | TEST BulkDeleteTests additions (removed / restored on failure / unmanaged reported; mutation-proven) | proven |

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

## Deltas (archived history, superseded by [progress/](progress/))

### Deltas 2026-10-07 night, review round 3 (CUDA architecture set)

- The capability gate was wrong, and the audit proved it by parsing the cubin metadata in the
  published `ggml-cuda-whisper.dll`: the `.nv_fatb` section holds SASS for exactly sm_86 (x23),
  sm_89 (x31), sm_120a (x32) and sm_121a (x33), with no sm_75/sm_80/sm_90 and no PTX at all.
- The gate said "sm_7.5 or newer", which is unsound: sm_90 (H100) is newer than sm_86 yet has no
  embedded code, so the picker would offer CUDA on Turing, A100 and H100 and then fall back to
  CPU at init. Replaced with a predicate over the architectures the build actually carries:
  sm_8.6-8.9 (a cubin runs on later minors of its major, so the sm_86 image covers 8.6-8.9) or
  sm_120/121. Docs corrected in README, RUN-GUIDE, AGENTS.md, BLOCKERS.md and here; the old wording
  promised A100/RTX-2060 support this build does not have.
- RUNTIME here: `device capabilities [sm_6.1]`, refused, which is correct for both reasons.
- Also deleted 2023 MB of stale CUDA copies that the removed Copy target had left in x86/x64
  build outputs; the RID condition keeps them out of non-x64 outputs now.
- Unverified: CUDA init and kernel launch on a real sm_86/89/12x card (none here), and whether
  a future whisper.net build widens the architecture set, which would let the gate relax again.
- The accepted set is now pinned by a test, `CudaCapabilityTests`: it asserts the predicate over
  Maxwell/Pascal/Turing/Ampere/Ada/Hopper/Blackwell and over a future 12.2 and 13.x, so a
  whisper.net bump that changes the shipped cubins fails loudly instead of silently
  over-advertising CUDA. Writing that test caught the second-order error in the first fix, where
  `major == 12` admitted a future 12.2 device; the predicate now bounds the Blackwell minor
  explicitly, because its cubins are architecture-specific and get no forward-compatibility.
  Suite: 95/95.


### Deltas 2026-10-07 night, review round (bundled VC++ runtime)

- The audit found a pre-existing gap that the CUDA bundling made visible: every whisper native
  set (`runtimes\win-x64`, `runtimes\cuda\win-x64`, `runtimes\vulkan\win-x64`) imports
  `MSVCP140.dll`, `VCRUNTIME140.dll` and `VCRUNTIME140_1.dll`, and the ggml base/cpu natives also
  import `VCOMP140.DLL`. None were in the published output. On a machine with no VC++ 2015-2022
  redistributable that fails `WhisperFactory` for CPU as well as GPU, so it was never a
  CUDA-only problem. Verified independently by scanning the published natives.
- Fixed by staging those four DLLs under `vcruntime/win-x64` (gitignored, 904 KB) and shipping
  them beside each native set; the loader searches the loaded DLL's own directory because .NET
  uses `LOAD_WITH_ALTERED_SEARCH_PATH`, so the app root is not sufficient.
- The staging switched from a post-build `Copy` target to `Content` items with `Link` metadata.
  Two reasons: `PublishDir` is always defined by the SDK, so the old target also wrote a duplicate
  506 MB into `bin\...\publish` on every plain build; and the MSIX packaging reader uses the
  project content list, not files a target dropped into the output, so the old shape would not
  have been packaged.
- CUDA gating tightened: the runtime check now requires both `cublas64_13.dll` and
  `cublasLt64_13.dll` (cuBLAS 13 loads through cuBLASLt, so a single-file check passed a
  half-staged set), and compute capability is queried per device so a device the native has no code for is
  filtered out of the picker rather than offered and then rejected at init.
- `docs/THIRD-PARTY-NOTICES.md` is now shipped inside the package, not just in the repo.
- RUNTIME here after the change: `cudaDevices=1 cudaRuntime=present`,
  `device capabilities [sm_6.1]`, and the gate rejecting it. Package 1.22 GB unpacked.
- Known gap, not fixed: the MSIX has not been rebuilt for any of this and is stale (it still
  carries foreign `runtimes\linux-*`/`macos-*` payloads that the trim targets delete). The
  `Content` switch is what should let a future MSIX rebuild pick the payloads up, but that is
  unverified. The portable exe is the artifact that carries CUDA and the VC++ runtime today.


### Deltas 2026-10-07 night (CUDA runtime bundled)

- The CUDA runtime is now shipped with the app instead of being a user install. The shipped
  CUDA 13 native imports exactly three runtime DLLs; `ggml-cuda-whisper.dll` was load-tested
  on this host and resolves with only those present:

      cublas64_13.dll 49.4 MB + cublasLt64_13.dll 456 MB + cudart64_13.dll 0.4 MB

  They are NVIDIA redistributables from the CUDA Toolkit redist archives (EULA Attachment A),
  the same components PyTorch and onnxruntime-gpu redistribute. Staged under `cuda/win-x64`
(gitignored, like `models/`) and shipped next to the native as `Content` items.
- Two facts drove the design. `cuda_cudart`, `libcublas` and `libcublasLt` are separate
  redistributables, so the bundle has to name the components, not a toolkit. And
  `nvcudart_hybrid64.dll` is a *driver* component (found only under DriverStore), so it is
  deliberately not bundled; the load test confirms it is not a load-time dependency of the
  chain. `nvrtc64_130_0.dll` is likewise optional (JIT kernels only).
- CUDA now needs no toolkit install but still cannot escape the driver, so both remaining
  requirements are queried rather than assumed: `cuDriverGetVersion` (needs 13000 = CUDA 13.0)
  and `cuDeviceGetAttribute` compute capability against the architectures the shipped native
  carries (sm_8.6-8.9 and sm_120/121; it is SASS-only, so "or newer" is the wrong test). The picker states which one failed.
- RUNTIME here: `cudaDevices=1 cudaRuntime=present`, no CUDA Toolkit installed, driver reports
  max CUDA 13.0, and the capability gate rejected this GTX 1050 Ti with
  "no CUDA device here has code in this build". The probe also runs and logs at app
  startup now, not only when Settings is opened.
- Package: the portable zip grew from 488 MB to 872 MB. The MSIX was not rebuilt.
- Suite: 82/82; build 0 errors.


### Deltas 2026-10-07 evening (Observed, working tree)

Reported from a physical machine (RTX 4070 Laptop + Intel UHD): CUDA never detected, Vulkan
slow with the discrete GPU idle, saved audio muffled, WAV storage heavy.

- CUDA detection was structurally broken: `GpuProbe` called `cudaGetDeviceCount`, a CUDA
  *runtime* API export that lives in cudart, not in `nvcuda.dll`. The driver exports the
  driver API instead, so the export lookup always failed and CUDA read as zero devices.
  Verified on this host: `cuInit`/`cuDeviceGetCount` exported = True, `cudaGetDeviceCount`
  = False. Now uses `cuInit` + `cuDeviceGetCount` + `cuDeviceGetName`. RUNTIME here:
  cudaDevices=1 (GTX 1050 Ti), previously always 0.
- CUDA is offered only when the runtime whisper.net's CUDA build links against
  (`cublas64_13.dll`) is installed; otherwise the picker greys it out and names the reason
  rather than offering a backend that silently falls back to CPU.
- Vulkan devices are enumerated from `vulkan-1.dll` (`vkEnumeratePhysicalDevices`) in the
loader's own index order. The old list reused DXGI adapter indices, which is a different
list, so the index handed to whisper.net could address another card (discrete GPU
selected, integrated GPU doing the work and idling). RUNTIME here: no Vulkan devices,
cross-checked against `vulkaninfo.exe` ("Failed to detect any valid GPUs").
  ggml's filter is mirrored: `ggml_vk_device_is_supported` (llama.cpp
  `ggml/src/ggml-vulkan/ggml-vulkan.cpp`) keeps a physical device when its type is discrete
  or integrated and `VkPhysicalDeviceVulkan11Features.storageBuffer16BitAccess` is set, and
  ggml numbers the kept devices in loader order, which is what whisper.cpp's `gpu_device`
  counts. Verified against that source, not on hardware. Unverified: a host with more than one
  Vulkan device. Two ggml behaviours are still not mirrored and can shift the index - it merges
  entries that report the same driver/UUID, and when every device is filtered out it falls back
  to the first non-CPU device.
- whisper.net's native logger (`LogProvider.AddLogger`) is routed into `AppLog`, so ggml
  device-selection and CUDA/Vulkan init messages reach the app log, and each transcription
  window logs size, loaded backend, duration, detected language, and segment count.
- Audio fidelity: capture mixed everything to 16 kHz mono before writing, which is what made
  recordings sound muffled. It now mixes at 48 kHz mono for the archive and derives a 16 kHz
  mono copy (`InferenceDownmixer`) for Whisper and the diarizer. The microphone opens at
  48 kHz with a documented 16 kHz fallback.
- Storage: `FinishAsync` (new on `IAudioCapture`; `StopAsync` is now strictly pause) closes
  the append-safe WAV and encodes it to AAC 256 kbps `.m4a` through Media Foundation; the
  session deletes the WAV once the row points at the `.m4a`, and keeps it when the encoder
  is unavailable. Playback and Gemini get
  the MIME from the extension. RUNTIME here: a 2 s 48 kHz tone round-trips to `.m4a` within
  0.3 s of the original duration and smaller than the WAV.
- Suite: 80/80 green (was 77/77); added `ArchiveAndDownmixTests`. Build stays at the 6
  pre-existing CA1416 warnings, 0 errors.
- Still unproven: transcription time on a physical GPU, per-app WASAPI capture, two-speaker
  separation, JA/VI accuracy, live Gemini call. The MSIX copy under `src/.../AppPackages`
  was not rebuilt for this change.
- Independent audit (9router/ocg-deepseek-v4.1-flash, max, read-only) reproduced the CUDA and
  Vulkan interop findings and the 80/80 suite, and raised 12 findings; all 12 are fixed in the
  same working tree. The material ones: the capture pump thread had no exception guard, and an
  unhandled exception on a raw Thread terminates the process; the WAV encode ran on the UI
  thread; a failed recovery leaked the reuse path, so the next meeting would record into the
  crashed meeting's file; the WAV was deleted before the row was updated; the pump ran Whisper
  and the diarizer inline, dropping archive audio and shortening the file; per-app capture read
  bytes in the pre-start default format; and the single-source path could lose wall-clock
  duration. Suite: 81/81 after the fixes.
- The change was re-reviewed after every round (four passes). Round 2 confirmed all 12 fixes
  and raised 6 more; round 3 confirmed those fixes and raised 4 (device teardown after a dead
  pump, a throwing failure handler, a non-atomic flag, a reuse path left in the resume slot);
  the final pass found no Medium, High, or Critical defect. Suite: 82/82.


### Deltas 2026-10-07 later (Observed, commits 92df6cf..36ed183)

### Deltas 2026-10-07 (audio combination)

### Deltas 2026-10-07 (capture timing + GPU report)

- Fixed empty transcripts from system-audio capture: the mixer pump ran unclocked and wrote ~100x too much audio (a 74 s recording produced a 339 MB WAV), time-shifting everything. Paced to 16 kHz real time (11.7 s -> 375 KB verified). The log warns when devices open but deliver no samples.
- GPU: the API line now lists only Whisper.net backends (CPU, CUDA, Vulkan); DirectML removed as misleading. Observed: Vulkan backend loads on the GPU-P VM but the vGPU does not accelerate.
- Per-app capture falls back to system-wide loopback when process-loopback activation fails.

- Capture always mixes the microphone and system audio (verified: `capture: started mic=on system=all sources=2`). The microphone/loopback either-or picker is gone; Settings has record-microphone and record-system-audio toggles plus a system-audio source list (all apps or one app).
- Per-app capture adds `ProcessLoopbackCapture` (WASAPI process loopback) with a fallback to system-wide loopback; activation is unverified on the test VM.
- Suite: 77/77 green (adds capture validation and a process-loopback probe).

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
- Suite at the time: 63/63 green (adds GPU probe + device-setting encode/parse) — superseded; see docs/progress/ for current runs.

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
