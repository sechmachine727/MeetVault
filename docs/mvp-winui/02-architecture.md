# MeetVault WinUI 3 MVP — Architecture and Design

Audience: Windows implementer. Pre-read: `01-requirements.md`.

## 1. Shape (one good sketch)

```text
WinUI 3 views (Dashboard, Live, Meetings, Details, Settings, Overlay window)
  -> MeetingSession (owns start/pause/resume/stop, state machine)
  -> IAudioCapture (mic + WASAPI process-loopback app source -> 16 kHz mono PCM + media file)
  -> ISpeechTranscriber (Whisper.net 1.9.1 wrapping whisper.cpp v1.9.4 streaming -> words + timestamps; P/Invoke fallback if Win10 support is required)
  -> ISpeakerDiarizer (sherpa-onnx 1.13.8 C# offline diarization -> stable speaker turns on finalized windows)
  -> IGenderLabeler (provisional opt-in turn estimate -> Male N / Female N / Unknown; neutral IDs plus manual rename guaranteed)
  -> LocalMeetingRepository (SQLite metadata + media files; authoritative)
  -> GeminiUploader (manual Files-API upload + `POST /v1beta/interactions` on `gemini-3.8-flash`; Google GenAI .NET SDK 1.24.0 for files/status, HttpClient for interactions; Credential Locker key)
```

Dependency direction: views call MeetingSession only. Engines never touch UI or storage. Repository is the single writer of persisted state. One interface per engine keeps the Python diart path usable as an offline accuracy reference without shipping it.

## 2. Caller usage first

```csharp
var session = new MeetingSession(audio, transcriber, diarizer, gender, repository);
await session.StartAsync(micId, conferenceAppSource); // live lines flow via event
await session.PauseAsync();   // capture + transcription halt; gap recorded
await session.ResumeAsync();
await session.StopAsync();    // finalizes media + transcript + speakers
repository.RenameSpeaker(meetingId, speakerId, "Male 1");
await gemini.SendMeetingAsync(meetingId); // manual only
```

## 3. Core types and invariants

- `Speaker { Id (stable, e.g. spk_1), DisplayName (renamable), Gender (Male/Female/Unknown), GenderIndex }`. Display names `Male N` / `Female N` use separate counters; renaming never changes `Id`.
- `Segment { Id, SpeakerId, StartMs, EndMs, Text, IsFinal }`. Absolute meeting-relative milliseconds; edits and reassignment keep timings.
- `Meeting { Id, Title, StartedAt, EndedAt, Status, MediaPath, MediaMime }`. Status: recording, paused, processing, ready, failed. Finalization commits `EndedAt`, duration, status, transcript, and media path in one transaction.
- `GeminiJob { MeetingId, State, Attempts, LastError, ResultPaths }`. Manual trigger only, retry with backoff.
- Invariants: speaker `Id` immutable; transcript timings absolute; repository is source of truth reloaded on boot; API key never leaves Credential Locker except as request auth.

## 4. Data flow (live)

Mic + picked app source -> mix to 16 kHz mono PCM -> transcriber partials/finals -> diarizer assigns turn -> gender labeler names counter -> session appends segment -> Live view + overlay render; recorder writes chunked media in parallel. Pause stops both streams. Stop finalizes media, commits repository transaction, releases devices.

## 5. Error behavior

- Source unavailable or permission denied: meeting does not start; guidance names the app and the fix.
- Engine not ready at start: startup timeout with model status; no silent fallback.
- Gemini failure: job stays queued with error text; media and transcript are untouched; retry is manual.
- Crash mid-meeting: unfinished session reloads as recoverable draft on next launch.

## 6. Tradeoffs and rejects

- Native whisper.cpp plus ONNX engines over shipping the Python faster-whisper + diart sidecar: larger integration effort, but no Python runtime and true offline bundle. diart stays as accuracy reference only.
- Per-app capture over full loopback: narrower scope honoring the unwanted-audio concern, at the cost of a source picker and per-app failure modes.
- Manual Gemini upload over auto: user control and cost clarity; auto-sync deferred.
- SQLite metadata plus media files over blobs-in-DB: keeps the DB small and playback simple.

## 7. Open risks

- sherpa-onnx C# diarization is offline (finalized-window), and no validated drop-in gender classifier exists; validate clustering on target languages/noise/overlap, keep auto gender opt-in with `Unknown` fallback.
- Per-app capture uses WASAPI process loopback (min build 20348); wrap the Microsoft application-loopback sample in a native helper. `Windows.Graphics.Capture` covers imagery only.
- Long-session timestamp stability and chunked-write memory profile need synthetic >60-minute tests.
