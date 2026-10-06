# BLOCKERS.md — genuine blockers for user review

> Updated by autonomous run. Only entries that stop progress with no alternative go here.

## Active

### 1. UI smoke test: no automation path in session (open)
- Asked: `@Computer` UI testing after build.
- Observed: no computer-use MCP tools loaded in this session; launching a packaged GUI unattended would change machine state without you present to see it.
- Evidence: app builds clean (solution-wide 0 warnings/errors), 26/26 unit tests green — but no runtime launch evidence.
- Next: you launch the app and walk Dashboard/Live/Meetings/Details/Settings + overlay, or grant a UI-automation path.

### 2. P4 real engines: needs multilingual validation with you present (open)
- Transcription must support EN/JA/VI with mid-conversation switching (your 2026-10-06 decision).
- Evidence: Whisper.net 1.9.1 / sherpa-onnx 1.13.8 / Google.GenAI 1.24.0 verified current on nuget.org; multilingual `base` is the default candidate; `base.en-q5_1` is OUT.
- Open: model download (~142 MiB+), JA/VI accuracy validation, diarization on JA/VI audio, CPU real-time check, WASAPI process-loopback native helper.
- Next: you confirm the engine-integration step (downloads + mic/app capture work) before P4 code lands.

## Worked around (no user action needed)
- Test host hangs in-sandbox: escalated `dotnet test` runs pass (26/26).
- `.git` read-only in sandbox: escalated shell for git writes.
- gh CLI at `C:/Program Files/GitHub CLI/gh.exe` (not on PATH).
- XAML compiler opacity: bisected and fixed two real defects (positional record, MAUI `ScrollTo`); FontSize binding verified innocent and shipped.
- Subagent models: per your decision all delegation uses muse-spark-1.3/max; two independent reviews completed (Parfit, Nietzsche).
- WinApp CLI absent: official `dotnet new winui-mvvm` templates used (net8.0).

## Language support (user decision 2026-10-06)
- Transcription must support English, Japanese, and Vietnamese, including mid-conversation language switches.
- Consequence: `base.en-q5_1` (English-only) is OUT; multilingual `base` (~142 MiB) or larger is the default candidate.
- Status: requirement recorded in ledger + P4 brief; engine selection must validate JA/VI before locking the bundled model.

