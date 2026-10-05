# MeetVault WinUI 3 MVP — Requirements

Audience: Windows implementer. Goal: shippable local-first MVP. Status: draft for review.

## 1. Product goal

A native Windows app that records a meeting, shows live transcription with speaker turns, and lets the user send a finished meeting to Gemini for a refined transcript, analysis, and summary. All capture, transcription, and storage stay on the machine. No login. No subscription. No cloud storage.

## 2. Scope

In scope: mic plus user-picked conference-app audio; local transcription and speaker separation; auto gendered labels with separate Male and Female counters; scrollable live transcript; pinnable always-on-top overlay with click-to-rename and scroll; local recording plus SQLite metadata; transcript text edit and speaker reassignment; manual per-meeting Gemini upload with Credential Locker key storage; full settings where every setting is wired or hidden.

Out of scope: login, accounts, subscription and billing, cloud sync and storage, automatic upload, biometric identity, action items and reminders as product surfaces (Gemini may return them as text only), mobile apps.

## 3. Functional requirements

- FR1: User starts a meeting, picks mic and one conference-app source, and sees live transcript lines within a few seconds.
- FR2: Live view shows speaker turns labeled Male N or Female N, Unknown when confidence is low; user can rename any speaker and all rows update.
- FR3: Overlay is a separate pinnable topmost window showing recent lines, scrollable, click-to-rename synced both ways.
- FR4: Pause halts capture and transcription together and resumes with a gap; skipped audio never reappears. Stop finalizes and saves.
- FR5: User can edit segment text and reassign any segment to another speaker before or after the meeting.
- FR6: Finished meeting saves media plus transcript, speakers, and timings locally and survives restart.
- FR7: User presses Send to Gemini on a finished meeting; app sends media plus transcript JSON to Gemini 3.8 Flash (agentic video understanding) and stores the returned transcript, analysis, and summary locally.
- FR8: Gemini API key is stored in Windows Credential Locker, never in files or logs.
- FR9: Every settings control is wired to real behavior; unwired options are absent.

## 4. Non-functional requirements

- NFR1: Transcription runs fully offline; a meeting never requires network except the manual Gemini upload.
- NFR2: Live latency stays conversational (partial line visible quickly, finals within seconds).
- NFR3: Multi-hour meetings record without unbounded RAM growth (chunked writes).
- NFR4: Timestamps stay absolute across long sessions with no reset or drift.
- NFR5: Installer is bundled and offline-capable; no Python install, no manual model download.

## 5. Acceptance criteria

- Clean install on Windows 11 (Whisper.net runtime floor; Windows 10 build 20348+ only via P/Invoke fallback path) records mic plus Teams or Zoom, shows gendered speaker turns, pauses with a gap, finishes to a playable local meeting, renames sync to overlay, edits persist, manual Gemini upload returns and stores analysis, restart recovers state.

## 6. Evidence and open unknowns

- Behavior reference: `Wireframes/Desktop/MeetVault-Desktop-Demo/` (frozen; neutral Speaker N, Tauri shell, Python sidecar).
- Platform facts verified via Microsoft docs MCP: WASAPI loopback capture; inbox SQLite via `Microsoft.Data.Sqlite`; Credential Locker `PasswordVault` for WinUI/desktop.
- Resolved by web research 2026-10-06: whisper.cpp v1.9.4; Whisper.net 1.9.1 stable; start `base.en-q5_1` (~57 MB) or `base` (~142 MiB); sherpa-onnx 1.13.8 with ONNX Runtime 1.28.2; WASAPI process loopback (`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, min build 20348) for per-app capture; Gemini `gemini-3.8-flash` via Files API plus `POST /v1beta/interactions`.
- Remaining provisional: no drop-in ONNX gender classifier found — auto gender is an opt-in estimate layer with `Unknown` fallback; neutral IDs plus manual rename is the guaranteed path. Full citations in `07-engine-integration-notes.md`.
