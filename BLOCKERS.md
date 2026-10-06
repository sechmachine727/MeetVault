# BLOCKERS.md — genuine blockers for user review

> Updated 2026-10-06 by autonomous run. Only entries that stop progress with no alternative go here.

## Active (needs you physically present)

### 1. Page navigation failures (open, causes found, awaiting your run of b5133b3)
- Live NRE cause found by elimination: LivePage.xaml used Style TextBlockButtonStyle, a key absent from WinUI 3 defaults. Failed lookup at inflate throws inside InitializeComponent with no managed stack. Only Live and Overlay used that key; the working pages never did. Fixed in b5133b3 (inline borderless props), published to win-x64-fresh, untested by you. Overlay got the same fix, untested.
- Settings XamlParseException cause found: XAML parse fires toggle/combo events before all fields exist. Init guard in 24e5693, in the fresh build, untested by you.
- Details has no dialog evidence against it; untouched, no claim.
- Fresh build also has: stack-bearing nav dialog, nav-errors.log, crash.log, tiny-first model pick, vault-never-throws.
- Next: close the running app, run publish/win-x64-fresh/MeetVault.exe, try Live/Details/Settings/overlay. Paste any dialog text or the two log files under TEMP/MeetVault.

### 2. One admin command, then install + UI walkthrough (open)
- Observed: MSIX signed in place (self-signed AppPublisher, SHA256, thumb BBD1..., verifies clean) and portable exe refreshed (publish/win-x64/MeetVault.exe + 271MB models). Per-user Root trust is in place but the installer demands machine scope: Add-AppxPackage fails 0x800B0109 until then. Machine-trust workaround was rejected by auto-review (correctly — broader than your per-user approval), so this one is yours.
- Your single command (elevated PowerShell): find the cert by thumb BBD1DFCFC1F34E14A603110858C4CD019A7B70DD in Cert:\CurrentUser\My, export, then certutil -addstore Root. After that: Add-AppxPackage 'src/MeetVault/AppPackages/MeetVault_1.0.0.0_x64_Test/MeetVault_1.0.0.0_x64.msix', then walk Dashboard/Live/Meetings/Details/Settings + overlay pin, light/dark, small/large window.
- No-tust alternative: run publish/win-x64/MeetVault.exe directly (self-contained, models alongside) — packaged-identity features may behave differently than installed MSIX.

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
