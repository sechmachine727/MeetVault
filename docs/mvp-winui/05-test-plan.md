# MeetVault WinUI 3 MVP — Test Plan

Audience: Windows implementer. Pre-read: `01-requirements.md`, `02-architecture.md`.

## 1. Unit (xUnit, no devices)

- Gender counters: mixed turn sequence yields `Male 1`, `Female 1`, `Male 2` with separate counters; low confidence yields `Unknown` without consuming a counter.
- Rename propagation: rename updates every segment row, speaker list, and overlay model; stable `Id` unchanged.
- Edit plus reassign: text edit keeps timings; reassignment moves the segment and preserves history.
- Finish transaction: `EndedAt`, duration, status, transcript, and media path commit atomically; interrupted commit leaves prior state intact.
- Pause gap: paused interval produces no segments and no media; resume continues with a marked gap.

## 2. Integration (scripted PCM and fixtures)

- Engine ready handshake with startup timeout; partials then finals with absolute timestamps.
- Malformed config and unsupported rate rejected with actionable errors.
- Stop returns shutdown acknowledgement; no session leaks.
- Synthetic 60-minute timestamp run: no reset or drift.
- Chunked-write memory profile stays bounded.

## 3. Manual on Windows (acceptance)

- Mic plus Teams or Zoom capture with per-app picker; deny-permission path shows guidance.
- Overlay pin, opacity, font, line count, scroll, rename both ways.
- Pause and resume gap; stop to playable local meeting; restart recovery.
- Gemini upload success stores transcript, analysis, summary with model version; failure preserves originals with retry.
- Bundled install smoke: no Python, no manual model download, offline meeting works; Windows 11 plus VS 2022 C++ Redistributable present (Whisper.net floor), NoAvx runtime path covered on non-AVX hardware.
- Gemini failure modes: file over 2 GB refused before upload; `FAILED` file state surfaces retry; interactions call without SDK support covered by `HttpClient` path.
