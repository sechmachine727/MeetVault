# BLOCKERS.md — genuine blockers for user review

> Updated 2026-10-07 by autonomous run. Only items that stop progress with no local alternative go here.

## Resolved this run (Observed)

- Portable-exe crash family fixed. Root causes: (1) Release publish was IL-trimmed (`PublishTrimmed=True`), which stripped CsWinRT ABI glue (the `ItemsSource` NRE in `GetAbiToProjectionVftblPtr`) and halved the sherpa-onnx wrapper (engine-init access violation); (2) sherpa-onnx bound to the WindowsAppRuntime framework copy of `onnxruntime.dll` (base-name collision) and access-violated at engine init. Fixes: `PublishTrimmed=False`; `App.xaml.cs` pins the app's own `onnxruntime.dll` before WinUI init.
- Verdict: the portable exe launches, and the UI Automation walk over Dashboard/Meetings/Details/Settings/Live plus Start-new-meeting stays alive (no crash.log, no nav-errors.log). See docs/COVERAGE.md.

## Active (need you physically present — no local alternative)

### 1. Mic-present recording
- This machine reports a microphone now ("0: Microphone Array (MacBook Pro M)"), but unattended synthetic testing of a real recording is not something I can validate for you.
- Next: run `publish/win-x64/MeetVault.exe`, press Start recording, speak, Stop, then open the meeting in Meetings and press play. Report anything that looks wrong.

### 2. Spoken JA/VI accuracy + two-speaker separation
- `Recording.m4a` validated (EN detected; the non-English portion is sung, not spoken, so ja/vi stays unexercised). Diarizer proves single-voice only.
- Next: one short spoken JA clip and one VI clip, plus a two-speaker sample (or the loopback machine) for separation.

### 3. Gemini live call
- Transport is implemented and unit-tested against stubs. No live call has run.
- Next: enter your Gemini API key in Settings (install the MSIX first; the portable exe has no Credential Locker), then Send to Gemini on a finished meeting.

### 4. MSIX install (needs an admin machine-root trust)
- The MSIX builds unsigned (no `signtool` on this box) and install needs the AppPublisher cert trusted at machine scope, which needs admin.
- Next: either install the signed MSIX after trusting the cert (thumb `BBD1DFCFC1F34E14A603110858C4CD019A7B70DD`), or keep using the portable exe.

### 5. GPU inference confirmation (needs a machine with a GPU)
- Detection enumerates real GPUs through DXGI (excludes indirect/virtual display drivers and WARP) and reports the inference APIs probed on this PC.
- Observed on your test machine: a Hyper-V VM with a paravirtualized NVIDIA GPU (drivers injected from the host). DXGI correctly lists that card (GPU-P is usable for compute, so it should be listed), but choosing it and starting a recording fell back to CPU. Vulkan passthrough is often absent in a GPU-P guest, which is the likely cause; CUDA or DirectML is the path inside such a VM. On this host (no CUDA export) the API line reads CPU, Vulkan, DirectML and the GPU is offered as Vulkan.
- Next: read the Live engine line and the log at %TEMP%\MeetVault\app.log after starting a recording with the GPU chosen. The log records the requested backend, the runtime order, and the accelerator that actually loaded (including a warning when the runtime loads CPU instead of the requested GPU), which tells us whether the failure is pairing, driver, or a missing CUDA runtime.
- Documented gap: DXGI cannot guarantee exclusion of render-capable indirect display drivers (it has no IDD flag). The strict discriminator is D3DKMT_ADAPTERTYPE.IndirectDisplayDevice; a D3DKMT probe here did not return usable adapter-type data, so it was not shipped.

## Not blockers (tracked for completeness)

- System-audio capture is implemented (WasapiLoopbackCapture -> 16 kHz mono), so a conference app playing through the speakers is recorded. Per-process capture (one app only, without the rest of the system mix) still needs the WASAPI process-loopback helper. Its audio content needs your machine to confirm.
- App logging: on by default at debug level, configurable in Settings, written to %TEMP%\MeetVault\app.log.
- No local gender classifier ships: labels are neutral `Speaker N`; the gender toggle was removed rather than left inert.
- `storage.path` governs export only; the transcript database stays in `%APPDATA%\MeetVault`.
