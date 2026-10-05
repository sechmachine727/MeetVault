# MeetVault WinUI 3 MVP — UX Design

Audience: Windows implementer. Pre-read: `01-requirements.md`.

App anchor: document/session editor silhouette (light chrome, content-first) with a `NavigationView` for Dashboard, Live, Meetings, Details, Settings. Overlay is a separate compact topmost window, not a page.

## 1. Screens

- Dashboard: quick start (mic + source status, model status), recent meetings table (title, date, duration, status, open). One primary action: Start new meeting.
- Live: transcript column (scrollable, speaker pill + text per line, partial line at bottom), side panel (speakers with rename boxes, audio sources with levels, engine status), actions Pause/Resume, Show overlay, Stop. Pause shows gapped state explicitly.
- Overlay: `OverlappedPresenter` window with pin toggled via `IsAlwaysOnTop` (free resize and custom dimensions for a scrollable transcript; `CompactOverlay` rejected because it locks to fixed 16:9 PiP sizes), last N lines, scroll, click speaker name to rename; pin, opacity, font size, line count controls.
- Meetings: search plus meeting cards with status and duration.
- Details: media player, meeting info, transcript with per-line rename/edit/reassign, Copy and Export, Send to Gemini button with job state and returned analysis/summary pane.
- Settings: Audio (mic, conference-app source picker, levels), Transcription (model, device, diarization and gender toggles), Overlay (pin, opacity, font, lines), Storage (location, usage), Speech status, Gemini key status. No unwired controls.

## 2. Controls (WinUI mapping)

- Navigation: `NavigationView`; search: `AutoSuggestBox`; lists: `ListView` with `Grid` item templates (no `DataGrid`); booleans: `ToggleSwitch`; model/device pickers: `ComboBox`; numbers: `NumberBox`; status and async progress: `InfoBar`; blocking choices: `ContentDialog`; rename and row actions: `Flyout`/`MenuFlyout`.

## 3. States and accessibility

- Every view implements populated, empty, loading, error, and disabled states with a next action; destructive Stop confirms and states the gap consequence. Keyboard-operable throughout with visible focus; no color-only speaker meaning (name text always present); reflow and text scaling honored.

## 4. Verification

- Render on the Windows machine at small and large widths, light and dark, with realistic long meetings; screenshot before/after for visual changes; first-time-user pass: purpose, state, primary action, and next step identifiable.
