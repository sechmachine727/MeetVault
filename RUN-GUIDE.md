# Run guide (local, 2026-10-06)

Ready to launch on your return:

```
.\publish\win-x64\MeetVault.exe
```

- Self-contained: no dotnet install needed. Models ship alongside in `publish/win-x64/models/` (ggml-base.bin 148MB, sherpa-seg.onnx 6MB, sherpa-emb.onnx 40MB).
- With a mic attached, Live page records + transcribes for real (status line reads `live: whisper base + sherpa diarizer`).
- Without models/mic it runs in demo mode (status line says so).
- Gemini key: Settings page -> stored in Windows Credential Locker. Send-to-Gemini on a finished meeting uploads for real when a key exists.
- Data lands in `%APPDATA%/MeetVault/meetings.db` (SQLite) + `%TEMP%/MeetVault/media` + `%TEMP%/MeetVault/gemini`.

Needs your ears: JA/VI accuracy, two-speaker separation quality, overlay feel, light/dark + small/large window pass.

