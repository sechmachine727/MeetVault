# BLOCKERS.md — genuine blockers for user review

> Updated 2026-10-06 by autonomous run. Only entries that stop progress with no alternative go here.

## Active (needs you physically present)

### 1. UI walkthrough after install (open)
- Observed: 43/43 tests green, solution builds 0 warn/0 err, dev cert created+trusted (CN=AppPublisher, CurrentUser Root). Signed-MSIX repack lane running (Newton); portable exe at publish/win-x64/MeetVault.exe with 271MB models staged.
- Next: you install (signed MSIX or portable exe) and walk Dashboard/Live/Meetings/Details/Settings + overlay pin, light/dark, small/large window.

### 2. Spoken JA/VI accuracy (open, narrowed)
- Observed: your Recording.m4a validated (13.74 s; base 5 segs RTF 1.73, tiny 3 segs RTF 1.10; all windows lang en at low prob). The non-English portion is sung, not spoken JA/VI — ja/vi path still unexercised. See docs/TRILINGUAL-AUDIO-NOTES.md.
- Next: one short spoken JA + VI clip each (your voice is fine), or accept EN-only validation for MVP and defer.

### 3. Two-speaker loopback + Gemini live call (deferred per your call)
- Observed: diarizer pipeline proven single-voice; per-app WASAPI loopback helper still unwritten (mic-only MVP). Gemini transport tested vs stubs only.
- Next: your manual test — loopback device on the other machine after exe build; Gemini key entered in Settings with the build exec.

## Closed today
- Dev-cert approval: done — CN=AppPublisher self-signed to 2031, trusted in CurrentUser Root (machine Root needed admin; per-user trust validates install).
- Mic-present recording path: LivePage passes persisted audio.mic/audio.source to Start+Recover (was hardcoded/empty); NAudioCapture compiles + enumerates.
- Independent review findings: fixed via TDD (draft durability, recovery mic guard, enum tolerance, rename validation, whisper null guard, diarizer empty guard, atomic download) — 42->43 green, pushed.

## Worked around (no user action needed)
- Test host hangs in-sandbox: escalated `dotnet test` runs pass (43/43).
- `.git` read-only in sandbox: escalated shell for git writes.
- gh CLI at `C:/Program Files/GitHub CLI/gh.exe` (not on PATH).
- No signtool/makeappx on PATH: lane searching NuGet SDK tooling for signed repack.
- Subagents: per your decision all delegation uses muse-spark-1.3/max, coordinated with disjoint write scopes.

## Language support (user decision 2026-10-06)
- Transcription supports English, Japanese, Vietnamese, including mid-conversation switches (auto mode, constrained [en,ja,vi]).
- Multilingual `base` default; `tiny` for live windows; base reserved for refinement.
