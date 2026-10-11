# BLOCKERS.md — genuine blockers for user review

> Updated 2026-10-11 (Vosk-only pipeline): the engine surface is one family — **Vosk**
> (English / Japanese / Vietnamese; small/medium/large model tiers, medium English-only),
> offline streaming, CPU-only, **single-speaker by construction**. Whisper, Moonshine, Zipformer, the sherpa diarization/VAD stack, the GPU
> backends and their packages/models were removed the same day. Only items that stop
> progress with no local alternative go here.

## Active (need you physically present — no local alternative)

### 1. Mic-present recording
- This machine reports a microphone now ("0: Microphone Array (MacBook Pro M)"), but unattended synthetic testing of a real recording is not something I can validate for you.
- Next: run `publish/win-x64/MeetVault.exe`, press Start recording, speak, Stop, then open the meeting in Meetings and press play. Report anything that looks wrong.

### 2. Spoken JA/VI accuracy
- The staged models: small-en/ja/vi and medium-en load and decode live; large-en loads in ~16 s and decodes faster than realtime (offline probe); large-ja/large-vi are staged with their files verified but not yet exercised. English is live-verified end-to-end (record → sentence-cased segments → save) and small vs medium were compared on a synthetic fixture; JA and VI are live-verified to load (`vosk: loaded dir=…`) and produce + save segments. Accuracy on real spoken JA/VI has not been judged.
- Next: one short spoken JA clip and one VI clip, with the matching language + size selected in Settings.

### 3. Gemini live call
- Transport is implemented and unit-tested against stubs. No live call has run.
- Next: enter your Gemini API key in Settings (install the MSIX first; the portable exe has no Credential Locker), then Send to Gemini on a finished meeting.

### 4. MSIX install (needs an admin machine-root trust)
- The MSIX builds unsigned (no `signtool` on this box) and install needs the AppPublisher cert trusted at machine scope, which needs admin.
- Next: either install the signed MSIX after trusting the cert (thumb `BBD1DFCFC1F34E14A603110858C4CD019A7B70DD`), or keep using the portable exe.

## Not blockers (tracked for completeness)

- Audio capture mixes the microphone and system output (verified: the log shows `capture: started mic=on system=all sources=2`). Per-app capture uses the WASAPI process-loopback API (`ProcessLoopbackCapture`); it falls back to system-wide capture when activation fails. The per-app path needs a check on a machine with the target app rendering audio.
- Capture is paced against the wall clock; the working WAV is 48 kHz mono and is compacted to `.m4a` (AAC 256 kbps) on Stop. If you ever hear a recording too quiet or too fast, check `capture: no audio samples were captured` and the pacing lines in `%TEMP%\MeetVault\app.log`.
- `storage.path` governs export only; the transcript database stays in `%APPDATA%\MeetVault`.
- Meetings recorded by the retired diarized pipeline keep their speaker rows and render them read-only in Details; new meetings are single-speaker and mint no speaker identities (the schema-required `spk_1` stamp is constant).
- Contract tests that need the real model (`VoskStreamingTranscriberTests`) self-skip (early return) when `models/` is unstaged; CI has no models, so CI cannot prove live decoding — local runs on this box cover it.
