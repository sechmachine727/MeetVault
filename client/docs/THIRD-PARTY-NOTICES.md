# Third-party notices

MeetVault redistributes the following third-party components. Nothing here changes the licence
terms of those components.

## Vosk (speech recognition)

The app ships `libvosk.dll` through the `Vosk` NuGet package. Vosk and Kaldi are licensed
under the Apache License 2.0; copyright Alpha Cephei Inc. and contributors.

The **redistributed** models under `models/vosk/` are the Vosk English and Japanese large
models (`vosk-model-en-us-0.22`, `vosk-model-ja-0.22`) — the only two the deliverable ships —
distributed by Alpha Cephei under the Apache License 2.0 and not committed to this repository.
Every other model is no longer part of the deliverable: the Vietnamese models
(`vosk-model-small-vn-0.4`, `vosk-model-vn-0.4`) and the small/medium tiers
(`vosk-model-small-en-us-0.15`, `vosk-model-en-us-0.22-lgraph`, `vosk-model-small-ja-0.22`).

## ScreenRecorderLib (screen recording)

Screen-video capture for meetings uses `ScreenRecorderLib` v7.0.1 (MIT License, Copyright
(c) 2017 Sverre Skodje), shipped as an x64 C++/CLI mixed-mode `ScreenRecorderLib.dll` via
the NuGet package. The package does not carry the C++ runtime it links against, so the app
stages the Microsoft Visual C++ 2015-2022 x64 redistributable DLLs beside it (staged
payload under `vcruntime/win-x64`, win-x64 builds only — see the app project's Content
items); no machine-wide redistributable install is required. Should they nevertheless be
missing, the app reports "screen capture unavailable" and records audio-only; audio-only
use of the app does not depend on them.

## Others

- **NAudio** (`NAudio`, `NAudio.Wasapi`) — audio capture and resampling. MIT License.
- **Microsoft.Data.Sqlite** / SQLite — the meetings database. SQLite is public domain;
  Microsoft.Data.Sqlite is MIT.
- **CommunityToolkit.Mvvm** — MIT License.
- **Microsoft.ML.OnnxRuntime** — the language-detection model runtime (VoxLingua107);
  its native `onnxruntime.dll` ships with the app and is loaded directly. MIT License.
- **VoxLingua107 language-ID model** — SpeechBrain's `lang-id-voxlingua107-ecapa` checkpoint
  exported to ONNX (`christopherthompson81/voxlingua107-lid-onnx`); staged under
  `models/langid/`, not committed to this repository. SpeechBrain is Apache-2.0; the
  VoxLingua107 dataset is CC BY 4.0.
- **Windows App SDK / WinUI 3** and the **.NET runtime** — Microsoft licence terms.

The application and its packaged native libraries each keep their own upstream licences.
