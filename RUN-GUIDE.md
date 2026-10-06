# Run guide (local, 2026-10-06)

Launch this build:

    publish/win-x64-fresh/MeetVault.exe

- Self-contained, no dotnet needed. Models alongside in publish/win-x64-fresh/models (tiny 78MB, base 148MB, sherpa-seg 6MB, sherpa-emb 40MB).
- Live uses whatever whisper model is on disk (tiny-first); the Settings model choice applies when its file exists.
- With a mic, Live records + transcribes for real. Without models/mic it runs in demo mode (status line says so).
- Gemini key: Settings page. In the portable exe the vault may be unavailable; key reads return empty, writes are no-ops. Installed MSIX has full vault.
- Data: APPDATA/MeetVault/meetings.db (SQLite) + TEMP/MeetVault/media + TEMP/MeetVault/gemini. Crash diagnostics: TEMP/MeetVault/crash.log + TEMP/MeetVault/nav-errors.log.
- Known broken: Start-meeting crash under diagnosis; Live/Details/Settings nav may show an error dialog. Paste the dialog text or the two log files above.
- Old publish/win-x64 is a stale fallback; publish/msix is a stale copy, not an artifact.

Needs your ears: JA/VI accuracy, two-speaker separation, overlay feel, light/dark + small/large window pass.
