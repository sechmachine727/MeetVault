# MeetVault WinUI 3 MVP — Data Model and Gemini Integration

Audience: Windows implementer. Pre-read: `01-requirements.md`, `02-architecture.md`.

## 1. SQLite schema (metadata only; media stays files)

```text
meetings(id, title, started_at, ended_at, duration_ms, status, media_path, media_mime, created_at, updated_at)
speakers(meeting_id, speaker_id, display_name, gender, gender_index)
transcript_segments(id, meeting_id, speaker_id, start_ms, end_ms, text, is_final, created_at, updated_at)
settings(key, value)
gemini_jobs(id, meeting_id, state, attempts, last_error, result_paths, updated_at)
```

Files: one recording per meeting under `%APPDATA%\MeetVault\media`, named `<yyyyMMdd-HHmmss>-<id>.wav` while the meeting is open and compacted on finish to the same name with an `.m4a` extension. When AAC encoding is unavailable the WAV is kept. Transcript JSON is an export/interchange artifact, not a second database.

## 2. Gemini upload contract — Gemini 3.8 Flash agentic video understanding (manual only)

- Model: Gemini 3.8 Flash with agentic video understanding (exact model ID verified against Google AI docs on the build machine).
- Trigger: Send to Gemini button on a finished meeting. Payload: saved media file as primary input plus transcript JSON (segments with speaker ids, display names, timings) as supporting context plus meeting metadata.
- Auth: API key from Windows Credential Locker (`PasswordVault`); never written to disk, logs, or settings files.
- Results stored locally: refined transcript, meeting analysis, summary; linked to the meeting and viewable in Details. Model version recorded with results.
- Failures: job row keeps state, attempts, and last error; retry is manual; original media and transcript untouched.

## 3. Privacy and security

- Capture records the microphone and whole-system loopback together by default, and can target a single process instead. Offline transcription and storage by default; network used only for the explicit Gemini upload. Key stays in Credential Locker. No secrets in source or config.
