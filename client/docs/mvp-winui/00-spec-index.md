# MeetVault WinUI 3 MVP — Spec Index

This is the frozen specification the implementation was built against. It describes the intended product, not the current state of the code. For what the app does today and what is still open, read [../../README.md](../../README.md), [../COVERAGE.md](../COVERAGE.md), and [../../BLOCKERS.md](../../BLOCKERS.md).

Read in order: `01-requirements.md`, `02-architecture.md`, `03-ux-design.md`, `04-data-and-gemini.md`, `05-test-plan.md`, `06-build-and-run.md`, `07-engine-integration-notes.md`, `08-risks-and-ponytail.md`.

Locked decisions: fresh C# WinUI 3 app; whisper.cpp plus a native/ONNX diarizer plus a local gender labeler; microphone plus user-picked conference-app audio; separate Male N and Female N counters with an Unknown fallback; pinnable overlay with scroll and rename; SQLite metadata plus media files; transcript edit plus reassignment; manual Gemini 3.8 Flash upload with a Credential Locker key; pause halts both streams with a gap; bundled installer; fully wired settings.

Divergences from this spec in the current build, recorded so the spec is not mistaken for behavior:

- Transcription is English and Japanese only, at Vosk's large tier, with mid-conversation switching when Auto-detect is on. Vietnamese and the small/medium tiers were removed from the deliverable on 2026-10-15 (user decision); the spec's English-only model choice still does not apply.
- Speaker labels are neutral (`Speaker N`). No gender classifier ships, so the Male/Female counters are unused in the app.
- Audio capture records the microphone and whole-system loopback together, mixed into one track, and can instead target a single process through WASAPI process loopback. That path is implemented in managed code (`ProcessLoopbackCapture`) rather than the native helper this spec proposed, and it has not been exercised against a machine with the target app playing.
- Recordings are flat files under `%APPDATA%\MeetVault\media`, named by timestamp. Each is kept as a WAV while the meeting is open and compacted to AAC `.m4a` at 256 kbps on finish, with the WAV kept when encoding is unavailable. The spec's per-meeting `recording.webm` layout does not apply.
- The `speakers` table's `gender` and `gender_index` columns round-trip but no consumer reads them.
- The Gemini integration was removed entirely (source, strings, and settings) on 2026-10-10. The
  optional post-meeting enhancement is now the MeetVault backend service (sign-in, backend-enhanced
  transcription, summaries, Q&A, Word export).

MVP test spine: the spec's spine, and the current evidence status of each item, are tracked in [../COVERAGE.md](../COVERAGE.md).
