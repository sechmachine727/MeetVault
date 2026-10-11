# Third-party notices

MeetVault redistributes the following third-party components. Nothing here changes the licence
terms of those components.

## Vosk (speech recognition)

The app ships `libvosk.dll` through the `Vosk` NuGet package. Vosk and Kaldi are licensed
under the Apache License 2.0; copyright Alpha Cephei Inc. and contributors.

The staged models under `models/vosk/` are Vosk models for English, Japanese and
Vietnamese (`vosk-model-small-en-us-0.15`, `vosk-model-en-us-0.22-lgraph`,
`vosk-model-en-us-0.22`, `vosk-model-small-ja-0.22`, `vosk-model-ja-0.22`,
`vosk-model-small-vn-0.4`, `vosk-model-vn-0.4`), distributed by Alpha Cephei under the
Apache License 2.0 and not committed to this repository.

## ScreenRecorderLib (screen recording)

Screen-video capture for meetings uses `ScreenRecorderLib` v7.0.1 (MIT License, Copyright
(c) 2017 Sverre Skodje), shipped as an x64 C++/CLI mixed-mode `ScreenRecorderLib.dll` via
the NuGet package. It expects the Microsoft Visual C++ 2015-2022 x64 redistributable at
runtime; when that is missing the app reports "screen capture unavailable" and records
audio-only. Audio-only use of the app does not depend on it.

## Others

- **NAudio** (`NAudio`, `NAudio.Wasapi`) — audio capture and resampling. MIT License.
- **Microsoft.Data.Sqlite** / SQLite — the meetings database. SQLite is public domain;
  Microsoft.Data.Sqlite is MIT.
- **CommunityToolkit.Mvvm** — MIT License.
- **Windows App SDK / WinUI 3** and the **.NET runtime** — Microsoft licence terms.

The application and its packaged native libraries each keep their own upstream licences.
