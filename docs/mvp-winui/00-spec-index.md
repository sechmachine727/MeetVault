# MeetVault WinUI 3 MVP — Spec Index

This is the frozen specification the implementation was built against. It describes the intended product, not the current state of the code. For what the app does today and what is still open, read [../../README.md](../../README.md), [../COVERAGE.md](../COVERAGE.md), and [../../BLOCKERS.md](../../BLOCKERS.md).

Read in order: `01-requirements.md`, `02-architecture.md`, `03-ux-design.md`, `04-data-and-gemini.md`, `05-test-plan.md`, `06-build-and-run.md`, `07-engine-integration-notes.md`, `08-risks-and-ponytail.md`.

Locked decisions: fresh C# WinUI 3 app; whisper.cpp plus a native/ONNX diarizer plus a local gender labeler; microphone plus user-picked conference-app audio; separate Male N and Female N counters with an Unknown fallback; pinnable overlay with scroll and rename; SQLite metadata plus media files; transcript edit plus reassignment; manual Gemini 3.8 Flash upload with a Credential Locker key; pause halts both streams with a gap; bundled installer; fully wired settings.

Divergences from this spec in the current build, recorded so the spec is not mistaken for behavior:

- Transcription is multilingual (English, Japanese, Vietnamese) with mid-conversation switching, per the user decision on 2026-10-06. The spec's English-only model choice does not apply.
- Speaker labels are neutral (`Speaker N`). No gender classifier ships, so the Male/Female counters are unused in the app.
- Audio capture is microphone or whole-system loopback. Per-process conference-app capture is not implemented.
- Gemini upload is manual, as specified. It has been tested against stubs only.

MVP test spine: counter assignment, rename propagation, edit plus reassign, finish-ordering transaction, pause gap, scripted-PCM protocol tests, Windows manual pass (capture, overlay, recovery, playback, Gemini success and failure), bundled-install smoke.

