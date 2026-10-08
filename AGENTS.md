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
- Never commit `models/`, `cuda/`, or `vcruntime/`. They are gitignored and staged at the repository root. `models/` is copied into the publish folder by hand; the `Content` items below bundle `cuda/` and `vcruntime/` automatically. Staging procedure: [docs/CUDA-SETUP.md](docs/CUDA-SETUP.md).
- Do not hand a live `ObservableCollection` across the WinRT boundary. Snapshot with `.ToArray()` and refresh on change (see `LivePage`, `DetailsPage`, `OverlayWindow`).

## Conventions

- Tests use xUnit and built-in asserts. FluentAssertions is intentionally absent.
- Log through `MeetVault.Core.AppLog` (`Info`, `Debug`, `Warn`, `Error`). The level is set in Settings and stored as `logging.level` (default `debug`). Log operational events in Core, not just inference.
- Persisted settings live in the `settings` table and are read with `LocalMeetingRepository.GetSetting`. Keep keys consistent between the writer and the reader.
- Segment ids are namespaced by meeting id (`{meetingId}-w{start}-{index}`) because `transcript_segments.id` is a global primary key.
- Media lives under `%APPDATA%\MeetVault\media` as one append-safe WAV per meeting (`WavSink`, 48 kHz mono), kept open across pause and resume. `StopAsync` only pauses; `FinishAsync` ends a meeting: it closes the WAV and encodes it to `.m4a` (AAC 256 kbps via Media Foundation). The session deletes the working WAV after the row points at the `.m4a`, and keeps the WAV when the encoder is unavailable. Playback and Gemini take the MIME from the extension.
- Capture always combines the microphone and system audio, mixed to 48 kHz mono for the archive, with a 16 kHz mono copy (`InferenceDownmixer`) for Whisper and the diarizer. `IAudioCapture.StartAsync(micId, appSource)` hints select the parts: `micId` is a device index or `off`; `appSource` is `off`, `all`, or a process id for one app (process loopback, with a fallback to system-wide).
- GPU detection is per backend and its device indices are that backend's own: CUDA through `nvcuda.dll` (`cuInit`/`cuDeviceGetCount`/`cuDeviceGetName`/`cuDriverGetVersion`/`cuDeviceGetAttribute` for compute capability), Vulkan through `vulkan-1.dll` (`vkEnumeratePhysicalDevices`). Never reuse DXGI adapter indices as backend device indices; they are different lists and the wrong one silently runs on another card. Vulkan indices mirror the filter ggml-vulkan applies before numbering, because whisper.cpp's `gpu_device` counts GPU/IGPU entries in ggml's own list and a raw `vkEnumeratePhysicalDevices` index is wrong whenever a filtered-out device precedes the selected one. That filter is `ggml_vk_device_is_supported` (llama.cpp `ggml/src/ggml-vulkan/ggml-vulkan.cpp`): the device type is discrete or integrated and `VkPhysicalDeviceVulkan11Features.storageBuffer16BitAccess` is set. Verified against that source, not on a multi-device host, and two ggml behaviours are not mirrored and can still shift the index: it merges entries that report the same driver/UUID, and when every device is filtered out it falls back to the first non-CPU device.
- Third-party payloads are bundled, not required from the user. Each is staged under a gitignored folder at the repo root (like `models/`) and wired in as `Content` items with a `Link` in `src\MeetVault\MeetVault.csproj`, so publish and the MSIX packaging both pick them up. Staging procedure: [docs/CUDA-SETUP.md](docs/CUDA-SETUP.md). The following are bundled:
  - `cuda\win-x64` -> `runtimes\cuda\win-x64`: `cublas64_13.dll`, `cublasLt64_13.dll`, `cudart64_13.dll` (NVIDIA redistributables, CUDA Toolkit EULA Attachment A). `cudart64_13.dll` is additionally copied beside the exe: whisper.net's `CudaHelper.IsCudaAvailable` decides whether CUDA is usable by loading `cudart64_13` by bare name, which searches the exe directory, System32 and PATH, never `runtimes\cuda\win-x64`. Without that copy the CUDA probe fails on any machine that has no CUDA 13 toolkit on PATH, and transcription silently falls back to CPU however capable the GPU is.
  - `vcruntime\win-x64` -> each of `runtimes\win-x64`, `runtimes\cuda\win-x64` and `runtimes\vulkan\win-x64`: `msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll`, `vcomp140.dll`. The whisper natives link these, and the loader searches the loaded DLL's own directory (not the app directory), so they must sit beside each native set. Without them whisper fails to load on a machine with no VC++ redistributable, on CPU as well as GPU.
- CUDA also needs driver 580+ and a GPU the shipped native has code for, both queried with `cuDriverGetVersion` and `cuDeviceGetAttribute`, never assumed. The native is SASS-only for sm_86/sm_89/sm_120a/sm_121a, and a cubin runs on later minors of the same major, so the supported set is sm_8.6-8.9 and sm_120/121 (the Blackwell cubins are architecture-specific, so they do not widen); never write this as "X or newer", because sm_90 is newer than sm_86 and unsupported. Devices outside that set are filtered out of the picker. `nvcudart_hybrid64.dll` is a driver component (not redistributable, and not a load-time dependency) and `nvrtc64_130_0.dll` is optional (needed only for cuBLAS JIT kernels), so neither is bundled.

### Progress log

Record your work in `docs/progress/` as you go, so the next agent starts from evidence rather than rediscovering the session. Track one folder per contributor and one file per day, append new entries instead of rewriting old ones, and name the evidence for every claim. The rule and the required format are in [docs/progress/SKILL.md](docs/progress/SKILL.md); the section layout is in [docs/progress/TEMPLATE.md](docs/progress/TEMPLATE.md).

## Verification expectations

- Build must stay at 0 errors; run the test suite before claiming a change works. Six pre-existing CA1416 warnings remain in GpuProbe.cs and ProcessLoopbackCapture.cs (Marshal.ReleaseComObject / GetObjectForIUnknown call sites); do not add more.
- For UI changes, launch the built exe and inspect it. UI Automation driving through `System.Windows.Automation` from Windows PowerShell 5.1 is the working method here; `pwsh` is broken in this harness.
- A green suite is not product proof: transcription quality, diarization, and the live Gemini call need a machine with audio devices and a key. Track what remains in `BLOCKERS.md` rather than asserting it works.

## Where the truth lives

- `docs/COVERAGE.md` — requirement-to-evidence ledger and change history.
- `docs/BUILD-LEDGER.md` — historical build log with engine measurements (past tense; not current status).
- `docs/progress/` — per-contributor work log, one file per contributor per calendar day.
- `BLOCKERS.md` — open items that need a specific machine or the user.
