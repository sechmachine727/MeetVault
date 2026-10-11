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

## Others

- **NAudio** (`NAudio`, `NAudio.Wasapi`) — audio capture and resampling. MIT License.
- **Microsoft.Data.Sqlite** / SQLite — the meetings database. SQLite is public domain;
  Microsoft.Data.Sqlite is MIT.
- **CommunityToolkit.Mvvm** — MIT License.
- **Windows App SDK / WinUI 3** and the **.NET runtime** — Microsoft licence terms.

The application and its packaged native libraries each keep their own upstream licences.
