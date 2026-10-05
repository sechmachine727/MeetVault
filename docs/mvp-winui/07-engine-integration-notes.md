# MeetVault WinUI 3 MVP — Engine Integration Notes

Audience: Windows implementer. Pre-read: `02-architecture.md`. Sources: vendor and project docs checked 2026-10-05/06 via web research (versions below); Context7 `whisper.cpp`, Gemini API, Windows App SDK docs; Microsoft docs MCP.

## 1. Transcription (whisper.cpp v1.9.4, 11 Sep 2026)

- Streaming pattern from the `whisper-stream` example: rolling mode (`--step 500 --length 5000`) rewrites one line in place and suits the partial line only; VAD sliding-window mode (`--step 0 --length 30000 -vth 0.6`) emits timestamped blocks (`[start --> end] text`) suited to segment parsing. MVP consumes timestamped finals for segments and rolling output for partials.
- C# route: Whisper.net 1.9.1 stable (1.9.2-preview1 prerelease) wraps the native library with CPU and optional accelerated runtimes. Constraint: its current Windows runtime requires Windows 11+ and the VS 2022 C++ Redistributable; x64 CPUs need AVX/AVX2/FMA/F16C unless using the NoAvx runtime. Direct P/Invoke to the whisper.cpp C API stays the fallback if Windows 10 support is required.
- Models: start with `base.en-q5_1` (~57 MB) for English-only meetings or multilingual `base` (~142 MiB); try `small.en-q5_1` (~182 MB) only if accuracy warrants the CPU cost and it stays real-time on target hardware. Record model name and version with each meeting.
- Boundary sits behind `ISpeechTranscriber`; the UI never parses engine text formats directly.

## 2. Diarization (sherpa-onnx 1.13.8, ONNX Runtime 1.28.2)

- Package `org.k2fsa.sherpa.onnx` (.NET 6+) ships prebuilt Windows native runtimes and a C# offline diarization API. Pipeline: segmentation model (`sherpa-onnx-pyannote-segmentation-3-0`, regular or INT8) plus speaker embeddings (example pairs `3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx`; NeMo alternatives documented) plus clustering; `sherpa-onnx-reverb-diarization-v1` also supplied.
- Constraint: the shipped C# example is offline diarization, so the MVP treats stable speaker assignments as a finalized-window or post-meeting step, not per-frame live labels. Validate clustering on target languages, meeting noise, and overlapping speech before shipping.
- Python diart remains an offline accuracy reference only and is never shipped.

## 3. Gender labeling (provisional — no drop-in model found)

- Web research found no maintained, validated, drop-in ONNX gender classifier for general meeting audio. Closest: an ECAPA-TDNN plus SVM project reporting 92.3% on its own Mozilla Common Voice v10 English test set (not a cross-domain guarantee, no ONNX-ready package); the MERaLiON-GR paper covers binary speech classification without a ready Windows ONNX release.
- MVP default: neutral stable IDs (`Speaker 1..N`) with manual rename to `Male N` / `Female N`; auto gender estimate is an opt-in provisional layer with an `Unknown` fallback that consumes no counter. Short clips, overlap, noise, and language shifts change predictions; vocal-cue estimates never determine identity.
- Boundary stays behind `IGenderLabeler` so a validated classifier drops in without touching UI or storage.

## 4. Conference-app capture (WASAPI process loopback)

- API: `ActivateAudioInterfaceAsync` with `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK` includes or excludes a target process plus children without capturing the whole endpoint. Minimum OS: Windows 10 build 20348 (Windows 11 qualifies); ordinary endpoint loopback remains the fallback path and is explicitly broader.
- Implementation: wrap the Microsoft application-loopback sample in a small native helper or C++/WinRT component feeding PCM to the recorder; `Windows.Graphics.Capture` covers screen imagery, process loopback covers app audio.

## 5. Overlay window (Windows App SDK)

- `CompactOverlay` is always on top but fixed to Small/Medium/Large 16:9 sizes with no free user resize. MVP uses an `OverlappedPresenter` with `IsAlwaysOnTop` toggled by the pin button for the scrollable transcript overlay with custom dimensions; `CompactOverlay` only if the PiP sizing fits. Keep a presenter reference for re-apply.

## 6. Gemini 3.8 Flash agentic video understanding

- Model `gemini-3.8-flash` (GA 2 Sep 2026): 1,048,576-token input, 65,536-token output; agentic processing pulls frames, audio, or transcripts as needed. Video guidance: up to ~3 h at low media resolution or ~1 h at high resolution for 1M-context models.
- Upload: Files API, poll `files.get` on ~5 s interval while `PROCESSING`, proceed on `ACTIVE`, fail on `FAILED`. File limits conflict across Google pages (20 GB paid-tier video guidance vs 2 GB per file / 20 GB per project / 48 h retention in Files docs): MVP enforces 2 GB per file until the account quota confirms otherwise.
- Prompt shape: video first, timestamped transcript JSON as text after it. .NET: Google GenAI SDK 1.24.0 (targets .NET 8 / .NET Standard 2.0) for file upload and status; its API surface has no Interactions client, so `POST /v1beta/interactions` goes over `HttpClient`. Key from Credential Locker at call time only. Record the model version with results.
