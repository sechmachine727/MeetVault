# BLOCKERS.md — genuine blockers for user review

> Updated 2026-10-11 (diarizer contract repaired; VAD-utterance pipeline + two-sighting speaker matcher live-verified on the looped two-voice fixture — 2 stable ids over 22 segments). Only items that stop progress with no local alternative go here. The code-cleanup items at the end are tracked for completeness and do not block anything.

## Resolved this run (Observed)

- Portable-exe crash family fixed. Root causes: (1) Release publish was IL-trimmed (`PublishTrimmed=True`), which stripped CsWinRT ABI glue (the `ItemsSource` NRE in `GetAbiToProjectionVftblPtr`) and halved the sherpa-onnx wrapper (engine-init access violation); (2) sherpa-onnx bound to the WindowsAppRuntime framework copy of `onnxruntime.dll` (base-name collision) and access-violated at engine init. Fixes: `PublishTrimmed=False`; `App.xaml.cs` pins the app's own `onnxruntime.dll` before WinUI init.
- Verdict: the portable exe launches, and the UI Automation walk over Dashboard/Meetings/Details/Settings/Live plus Start-new-meeting stays alive (no crash.log, no nav-errors.log). See docs/COVERAGE.md.

## Active (need you physically present — no local alternative)

### 1. Mic-present recording
- This machine reports a microphone now ("0: Microphone Array (MacBook Pro M)"), but unattended synthetic testing of a real recording is not something I can validate for you.
- Next: run `publish/win-x64/MeetVault.exe`, press Start recording, speak, Stop, then open the meeting in Meetings and press play. Report anything that looks wrong.

### 2. Spoken JA/VI accuracy + two-speaker separation
- `Recording.m4a` validated (EN detected; the non-English portion is sung, not spoken, so ja/vi stays unexercised). Diarizer separation + identity stability measured live on the synthetic A/B/A fixture loop (E1, meeting `cf852f755199402e9ff003ffc1fc8563`): 22 segments, 2 speaker ids, monotonic timeline; voice A → `spk_1` 15/15, voice B → `spk_2` 6/6 after its first sighting — the first sighting of a new voice deliberately keeps the previous speaker (two-sighting confirmation; at most one line per voice carries the previous id). Real conversational audio is still unexercised.
- Next: one short spoken JA clip and one VI clip, plus a real two-speaker sample (or the loopback machine) for separation.

### 3. Gemini live call
- Transport is implemented and unit-tested against stubs. No live call has run.
- Next: enter your Gemini API key in Settings (install the MSIX first; the portable exe has no Credential Locker), then Send to Gemini on a finished meeting.

### 4. MSIX install (needs an admin machine-root trust)
- The MSIX builds unsigned (no `signtool` on this box) and install needs the AppPublisher cert trusted at machine scope, which needs admin.
- Next: either install the signed MSIX after trusting the cert (thumb `BBD1DFCFC1F34E14A603110858C4CD019A7B70DD`), or keep using the portable exe.

### 5. GPU inference confirmation (needs a machine with a GPU)
- Detection is now per backend, and each device index is that backend's own. CUDA devices come from the NVIDIA driver API (`nvcuda.dll`: `cuInit`/`cuDeviceGetCount`/`cuDeviceGetName`/`cuDriverGetVersion`/`cuDeviceGetAttribute`). Vulkan devices come from `vulkan-1.dll` (`vkEnumeratePhysicalDevices`) in that loader's order. Whether ggml applies that same index, or compacts its device list first, is unverified on a host with more than one Vulkan device.
- The CUDA runtime no longer has to be installed by the user: `cublas64_13.dll`, `cublasLt64_13.dll` and `cudart64_13.dll` are now bundled next to `ggml-cuda-whisper.dll` (NVIDIA redistributables, staged from `cuda\win-x64`). What CUDA still needs cannot be bundled: the display driver. CUDA is offered only when the driver reports max CUDA 13.0 or newer AND the GPU is one the shipped CUDA native has code for (sm_8.6-8.9 or sm_120/121), both queried from the driver.
- Root cause of "CUDA is not detecting": the old probe called `cudaGetDeviceCount`, which is a CUDA *runtime* export that lives in cudart, never in `nvcuda.dll`. The driver exports the driver API instead, so the lookup always failed and CUDA read as zero devices on machines that plainly have a GPU. Fixed; Observed on this host: `cudaDevices=1` (GTX 1050 Ti), previously always 0.
- Second cause of "the GPU sits idle": the old Vulkan list reused DXGI adapter indices, which is a different list from Vulkan's, so the index handed to whisper.net could address another card. Fixed. Observed on this host: the new enumeration reports no Vulkan physical devices, which `vulkaninfo.exe` confirms ("Failed to detect any valid GPUs"), so the empty list is correct here rather than a regression.
- Still open, needs your physical machine: does Vulkan now run on the 4070 rather than the Intel iGPU, and what is the real per-window time on a physical card. Choose the GPU in Settings, record, then read `%TEMP%\MeetVault\app.log`: every transcription window logs its duration, the loaded backend, the detected language and the segment count, and ggml's own device-selection messages are routed into the same log.
- Observed on this host after bundling: `cudaRuntime=present` with no CUDA Toolkit installed, `cuda driver reports max CUDA 13.0`, and the capability gate correctly rejected this GTX 1050 Ti, which is sm_6.1 and so outside the shipped sm_8.6-8.9/sm_120-121 set. The GPU-load test also confirmed `ggml-cuda-whisper.dll` loads with only those three DLLs present.
- Your RTX 4070 is sm_8.9, so the only remaining question on your machine is the driver version (580+). If the picker still greys CUDA out, the reason line and `%TEMP%\MeetVault\app.log` now name the exact cause.
- Documented gap: DXGI cannot guarantee exclusion of render-capable indirect display drivers (it has no IDD flag). DXGI is now only the hardware inventory in the log, so it no longer influences the offered devices.

## Not blockers (tracked for completeness)

- Microphone and system audio are always mixed (verified: the log shows `capture: started mic=on system=all sources=2`). Per-app capture uses the WASAPI process-loopback API (`ProcessLoopbackCapture`); it falls back to system-wide capture when activation fails. Activation was not confirmed on the test VM (process loopback is often unsupported in a Hyper-V guest), so the per-app path needs a check on a machine with the target app rendering audio.
- Pending-speech drop at pause/stop/unload is logged, not silent: the shared `DropPendingUtterance` closes the open VAD utterance and warns `live: <pause|stop|unload> dropped Xs of pending speech (not transcribed; the recording keeps it)`. Observed live: `live: pause dropped 4.9s…` and `live: stop dropped 3.1s…`. Follow-up idea: transcribe the pending utterance at stop instead of dropping it (the audio is in the recording).

### 6. System-audio capture timing (fixed, verify on your machine)
- Root cause of "YouTube played but the transcript was empty": the mixer pump was not clocked to real time. `WdlResamplingSampleProvider` pads silence when starved, so the loop free-ran and wrote audio roughly 100x too fast (a 74 s recording produced a 339 MB WAV). Whisper then saw time-scaled near-silence. Fixed by pacing the pump against the wall clock (verified then at 16 kHz: an 11.7 s recording produced 375 KB = 32 KB/s).
- On the test VM no audio flows at all (`mic=0 sys=0` from both devices), so the capture itself could not be exercised with real sound here. Play a video on your machine, record a few seconds, and confirm the transcript and a normal-sized file. The working WAV is now 48 kHz mono (~94 KB/s, full bandwidth); on Stop it is compacted to `.m4a` at 256 kbps (~32 KB/s). Listen to a finished recording: it should no longer sound muffled.
- The log now warns `capture: no audio samples were captured` when the devices open but deliver nothing.
- App logging: on by default at debug level, configurable in Settings, written to %TEMP%\MeetVault\app.log.
- No local gender classifier ships: labels are neutral `Speaker N`; the gender toggle was removed rather than left inert.
- `storage.path` governs export only; the transcript database stays in `%APPDATA%\MeetVault`.

### Open code cleanup (recorded 2026-10-07, not fixed)

- `Speaker.GenderIndex` and `Speaker.Gender` reach the `speakers` table but no production consumer reads either value. Declared at `src/MeetVault.Core/Domain.cs:3`, written at `src/MeetVault.Core/LocalMeetingRepository.cs:45` and `:47`, read back at `LocalMeetingRepository.cs:131` and `:135`, and constructed in production only with the literal `Gender.Unknown, 0` at `src/MeetVault.Core/MeetingSession.cs:176`. Non-zero values appear only in test fixtures. Speaker display names already carry the counter, so the two columns hold nothing the app uses. Minimal fix: drop both fields from the record, the `INSERT`, and the `SELECT`, and leave the columns at their defaults so databases already on disk keep opening.
- ~~`ISpeakerDiarizer.AssignTurn` cannot be satisfied by the real implementation.~~ **FIXED 2026-10-11 (Phase 3).** The interface now carries the audio (`SpeakerHint AssignTurn(TurnAudio)`), `SherpaDiarizer` implements it for real (per-turn diarization + cross-turn identity by embedding match, empty audio → Unknown, never throws), `MeetingSession.NoteTurnAudio` resolves one hint per finalized turn, and the `WindowDiarizer` adapter was deleted. Verified by `DiarizerContractTests` (synthetic two-voice fixture: distinct speakers, stable ids) + `SessionTests`/`RenameSpeakerTests`; suite 135/135.
