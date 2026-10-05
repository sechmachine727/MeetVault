# MeetVault MVP Build Ledger

> Goal: MeetVault WinUI 3 MVP initial implementation per docs/mvp-winui specs.
> Done predicate: domain + session + repository + Gemini uploader units green, solution builds, smoke-tested UI, blockers recorded.

## Constraints (Observed)
- .NET 8 SDK only (8.0.425); scaffold TFM net8.0-windows10.0.26100.0; WindowsAppSDK 1.8.260317003; Toolkit.Mvvm 8.4.2
- No winapp CLI, no Computer-Use/WinUI MCP tools, no requested subagent models -> inline work, manual UI verification
- Sandbox: .git read-only; git writes need escalated shell. gh at C:\Program Files\GitHub CLI\gh.exe (not on PATH)

## Phases
- [ ] P1 domain models: Speaker/Segment/Meeting/GeminiJob + gender counters + rename (xunit, TDD red-green)
- [ ] P2 MeetingSession state machine: start/pause/resume/stop + pause-gap invariant (fake engines)
- [ ] P3 LocalMeetingRepository: SQLite schema per 04-data-and-gemini + finish transaction
- [ ] P4 engine interfaces: IAudioCapture/ISpeechTranscriber/ISpeakerDiarizer/IGenderLabeler stubs behind boundaries
- [ ] P5 GeminiUploader: Files-API + interactions HttpClient path, Credential Locker key (unit-testable seams)
- [ ] P6 WinUI shell: NavigationView (Dashboard, Live, Meetings, Details, Settings) + overlay window
- [ ] P7 build + tests green + UI smoke + independent review + push

## Rulings
- R1: scaffold via official winui-mvvm template net8.0 (matches installed SDK); MVVM already present, reuse it.
- R2: solution layout: src/MeetVault (app), src/MeetVault.Core (domain+session+repo, testable), tests/MeetVault.Core.Tests.
- R3: xunit for tests (spec 05-test-plan). No FluentAssertions (license check cost) - built-in asserts.

## Progress
- Scaffold done: src/MeetVault (winui-mvvm, net8.0).

## Language support (user decision 2026-10-06)

- Transcription must support English, Japanese, and Vietnamese, including mid-conversation language switches.
- Consequence: model choice changes — `base.en-q5_1` (English-only) is OUT; multilingual `base` (~142 MiB) or larger becomes the default candidate.
- Open: whisper.cpp language detection per segment vs fixed multi-language mode; diarization/gender validation on JA/VI audio; model-size vs real-time CPU tradeoff.
- Status: requirement recorded; engine selection (P4) must validate JA/VI before locking the bundled model.
