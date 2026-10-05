# BLOCKERS.md — genuine blockers for user review

> Updated by autonomous run. Only entries that stop progress with no alternative go here.

## Active

### 1. Requested subagent models absent from default tool catalog (worked around)
- Asked: `router/ocg-space-bunny-free` + `9router/ocg-muse-spark-1.3-contributor`.
- Tool `multi_agent_v1__spawn_agent` advertises only gpt-6-luna / gpt-6.1-sol / DeepSeek routes.
- Evidence: both slugs exist in `C:/Users/sechmachine/.codex/opencodex-catalog.json`; user steering confirmed `model` override accepts them (probe `9router/ocg-space-bunny-free` spawned and answered).
- Status: WORKING — using `9router/ocg-space-bunny-free`/`max` for coding per steering. Independent review via `9router/ocg-muse-spark-1.3-contributor` still to be exercised.

### 2. No Computer-Use / WinUI MCP tools in session (open)
- Asked: `@Computer` UI testing after build; `@WinUI` UI/UX.
- Observed: no `computer-*` or `winapp`/`winui` MCP tools loaded; only skill markdown files.
- Impact: app builds (13/13 unit tests green) but no automated UI launch/verification yet.
- Next: manual launch verification on user machine, or user grants a UI-automation path.

### 3. Test host hangs inside sandbox (worked around)
- `dotnet test` never completes in-sandbox; needs escalated shell per run.
- Status: WORKING — escalated runs pass (13/13).

### 4. XAML compiler opacity (worked around, documented)
- `XamlCompiler.exe` exits 1 with no message; C# errors surface only when XAML is minimal.
- Two real defects found via bisect: positional `record TranscriptLine` (init-only props break XamlTypeInfo.g.cs) fixed to class; `ListView.ScrollTo` (MAUI API) fixed to `ScrollIntoView`.
- Remaining overlay polish (FontSize binding cleared as innocent; ScrollViewer mode + AutomationProperties untested) deferred to keep momentum.

## Resolved
- gh CLI path (C:/Program Files/GitHub CLI/gh.exe, not on PATH).
- .git read-only in sandbox (escalated shell for git writes).
- WinApp CLI absent; official `dotnet new winui-mvvm` templates used instead (net8.0).


## Language support (user decision 2026-10-06)

- Transcription must support English, Japanese, and Vietnamese, including mid-conversation language switches.
- Consequence: model choice changes — `base.en-q5_1` (English-only) is OUT; multilingual `base` (~142 MiB) or larger becomes the default candidate.
- Open: whisper.cpp language detection per segment vs fixed multi-language mode; diarization/gender validation on JA/VI audio; model-size vs real-time CPU tradeoff.
- Status: requirement recorded; engine selection (P4) must validate JA/VI before locking the bundled model.
