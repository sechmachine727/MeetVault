# MeetVault independent audit — ponytail-review lens (2026-10-06)

Auditor model: ocg/muse-spark-1.3-contributor (max effort). Scope: src/MeetVault.Core, src/MeetVault XAML/codebehind, tests, csproj files, docs. Every finding is grounded in auditor reads of the current tree via exec_command this session. No source files were edited; this report is the single write.

## Material findings (ranked)

### M1. LivePage OnPcm passes windowStartMs=0 for every window — all live segment timestamps collapse onto one origin
File: src/MeetVault/Views/LivePage.xaml.cs (OnPcm body calls TranscribeWindowAsync(window, 0, "live")). WhisperTranscriber.TranscribeWindowAsync (src/MeetVault.Core/WhisperTranscriber.cs:57) offsets each emitted segment by the passed windowStartMs. A constant 0 means window N segments carry window-local timestamps, so a long live session persists overlapping/resetting timings instead of the monotonic span LongSessionTests proves for the synthetic path. Why it matters: timestamps are load-bearing transcript data; every live meeting through this path ships wrong timings to SQLite and Gemini. Smallest fix: accumulate an offset and pass it; add one test emitting two windows asserting seg[1].StartMs >= seg[0].EndMs.

### M2. NAudioCapture.StartAsync has a duplicated IsCapturing early-return (lines 19 and 22) with device validation between them
File: src/MeetVault.Core/NAudioCapture.cs:19 and :22 (findstr confirmed both guards). The first returns before mic-id validation; the second is dead code on the same condition. Why it matters: dead branch hides intended order — validation should run before any return so a bad mic id errors even when already capturing. Smallest fix: delete one guard; keep validate-then-return; CaptureValidationTests stays green.

### M3. Speaker.GenderIndex is write-only metadata — set to 0 at every creation site, never read
Files: src/MeetVault.Core/Domain.cs:3 (field); src/MeetVault.Core/MeetingSession.cs:99 (literal 0); src/MeetVault.Core/LocalMeetingRepository.cs:45 (persisted) plus LoadSpeakers readback; zero non-test readers (findstr: only Domain.cs, repo read/write). Display names already carry the counter ("Male 1"). Why it matters: persisted column + record field + reader/writer with no consumer is dead flexibility. Smallest fix: delete the field and drop from INSERT/SELECT (column default stays for compat).

### M4. DetailsPage Gemini path sends a demo meeting through DemoFileReader/DemoTransport while presenting a real flow
Files: src/MeetVault/Views/DetailsPage.xaml.cs:176 area (SendMeetingAsync call) plus tail DemoFileReader (constant size, 1024 zero bytes) and DemoTransport (canned CannedResult), Seed rows (:17). Send succeeds and persists a Succeeded job without touching real media or network; tooltip says "Run the Gemini job against the demo transport". Why it matters: success UI plus persisted Succeeded job for a canned result violates evidence-before-claims; COVERAGE.md correctly says "partial (needs key)" but the in-app affordance reads as done. Smallest fix: label the outcome "demo result — no upload", skip SaveJob or flag demo, disable Send when _meeting is null instead of DemoMeetings.All[0] fallback.

### M5. SherpaDiarizer.AssignTurn (interface method) throws by construction; the session always calls it
Files: src/MeetVault.Core/SherpaDiarizer.cs:30 (AssignTurn routes to AssignWindow(Array.Empty<float>()) which throws on empty); src/MeetVault.Core/MeetingSession.cs:94-99 (OnSegment calls AssignTurn). With the real diarizer wired, every live segment throws inside OnSegment — the LivePage WindowDiarizer adapter exists solely to dodge this by never calling inner AssignTurn. Why it matters: the interface promises per-segment assignment the real implementation cannot honor; production relies on an adapter bypassing the interface method. Smallest fix: change the interface to carry window audio (AssignWindow) or buffer last-window audio; delete WindowDiarizer once the contract carries audio.

### M6. LivePage swallows engine-construction and per-window failures silently
Files: src/MeetVault/Views/LivePage.xaml.cs:52 (catch fall through to fakes), :94 (catch next-window-retries), WindowDiarizer.AssignWindowSamples (:147, catch{}). Why it matters: corrupt model, load failure, or diarizer regression all present as "demo mode" or quiet gaps with no cause surfaced. Smallest fix: keep fakes fallback but set EngineStatus/StatusBar text with caught exception type + message; count consecutive window failures and surface after N.

### M7. Gemini key checked in two places with two ISecretStore instances and no documented precedence
Files: src/MeetVault/Views/SettingsPage.xaml.cs:10 (static Locker + GeminiApiKey getter); DetailsPage Gemini_Click builds its own CredentialLockerSecretStore while GeminiUploader.SendMeetingAsync also gates on _secrets.Get("GeminiApiKey"). Why it matters: duplicated secret-store ownership is how keys end up in the wrong place. Smallest fix: single shared ISecretStore (e.g. AppStore.Secrets) used by Settings, Details, and uploader.

### M8. DetailsPage transcript edits and speaker reassigns live only in memory; nothing persists or warns
Files: src/MeetVault/Views/DetailsPage.xaml.cs (LineText_TextChanged updates LineStatus only; SpeakerPicker_SelectionChanged sets line.Speaker only; Seed comment :16 admits edits stay in memory though LocalMeetingRepository.Save/RenameSpeaker now exist). Why it matters: UI counts "N edited" and offers copy/export, but closing the page loses edits silently. Smallest fix: debounce-save via Repo.Save on edit, or show "edits not saved" until wired; update the stale comment either way.

### M9. Dashboard/Meetings header grids duplicate row-template column metrics by hand
Files: src/MeetVault/Views/DashboardPage.xaml and MeetingsPage.xaml (header Grid + ItemsControl row Grid each declare 2*/`/Auto sets with MinWidth 90/80). Why it matters: any column change needs four coordinated edits; drift already shows as MinWidth on TextBlocks instead of columns. Smallest fix: extract one shared row UserControl/style with a single column definition source.

### M10. LivePage Pause/Resume/Stop handlers assume _session non-null but OnLoaded can leave it null
File: src/MeetVault/Views/LivePage.xaml.cs (_session = null!; OnLoaded catch leaves null; Pause_Click/Resume_Click/Stop_Click dereference). InvalidOperationException is caught but NullReferenceException from null _session is not — failed start plus Pause crashes instead of "Nothing to pause". Smallest fix: null-check _session in each handler or disable buttons until the session is ready.

## Nitpicks (one line each)
- NAudioCapture.DescribeDevices enumerates devices twice per bad-id error; build the string once.
- WhisperTranscriber accepts any 2-letter language code but Known lists only en/ja/vi — validate against the real set or accept-all explicitly.
- GeminiHttpTransport poll loop hardcodes 120x5s (10 min) with no jitter/backoff; extract constants.
- GeminiUploader.safeId strips to empty then fails — validate meeting.Id at entry instead.
- LocalMeetingRepository.Save deletes + reinserts all segments per save; fine at MVP scale, note the O(n) ceiling.
- SettingsPage.Refresh persists every setting on each keystroke; harmless now, extend the LostFocus pattern if the usage-walk grows.
- AppStore.SeedDemoData inserts demo meetings into the real user DB on first run; flag them so seed rows are never mistaken for user data.
- LivePage transcript ListView lacks auto-scroll on new lines (overlay has ScrollIntoView; live does not).
- DetailsPage fixed Width=160 speaker column clips long renamed speakers on narrow windows; use MinWidth + star sizing.
- RUN-GUIDE status string says whisper base while LivePage code sets whisper tiny (base reserved for refinement) — pick one string.
- Spec 07 still names base.en-q5_1 as the starting model while BUILD-LEDGER records multilingual base as locked default — update 07 section 1.
- Doc test counts disagree (38/38 vs 42/42 vs 43/43) — stamp counts with date/commit.
- ChunkedWriteTests exercises NAudio.Wave directly, not NAudioCapture chunking — rename or route through the real class.
- DiarizerContractTests needs real sherpa models on disk, so dotnet test fails without the download — gate behind skip when models absent.

## Explicit non-findings (checked and cleared)
- SQL injection in LoadUnfinished string-concat status filter: cleared — values are nameof() constants, not user input; parameterized elsewhere.
- Pause-gap data loss: cleared — PauseGap test plus 70-min monotonic run plus RecoveryTests round-trip cover it.
- Gemini atomic result write (.tmp + move): cleared — correct, tested by Success_StoresResult.
- Credential Locker exception swallowing (Get null on failure): cleared as acceptable — fail-closed to key-missing guidance, never leaks; Set replace-on-exists handled.
- Overlay Pin/opacity/font/lines wiring: cleared — x:Bind TwoWay plus Settings persist plus Refresh single-writer observed.
- CounterGenderLabeler vs SpeakerLabeler split: cleared — counters vs per-speaker memo plus estimate routing; justified, not single-use abstraction.
- Raw-SQL-over-EF choice: cleared — single-writer file DB, transaction rollback tested.
- Enum-tolerant reads (bad status/gender/job fallbacks): cleared — ReviewFixTests.Load_BadEnum plus repo TryParse fallbacks observed.
- TransportTests asserting URLs contain KEY: cleared — key-in-query is the API documented shape, test asserts presence not leakage.
- DetailsPage export overwrite confirm plus clipboard fallback: cleared — ContentDialog gate plus COMException/IO catches observed.
- DetailsPage export overwrite confirm plus clipboard fallback: cleared — ContentDialog gate plus COMException/IO catches observed.

---

# Round 2 — fresh-eyes re-audit (2026-10-06, same auditor/model)

Brief: re-audit the whole tree as if round 1 never happened; confirm or retract each round-1 finding against the current tree (HEAD 9629628); report new material findings; verdict GREEN or numbered findings. Verification this round: dotnet test MeetVault.sln exit 0, 43/43 passed; dotnet build --no-restore exit 0, 0 warnings 0 errors.

## Round-1 finding dispositions

- M1 (window offset 0): CONFIRMED fixed. OnPcm now accumulates _windowOffsetMs in 10 s steps (diff b4658ea..HEAD). Residual nit: offset advances even when TranscribeWindowAsync throws, so one failed window shifts later timings by 10 s; advance only on success.
- M2 (duplicated IsCapturing guard): CONFIRMED fixed. Single guard after validation with comment (NAudioCapture.cs). CaptureValidationTests green in the 43/43 run.
- M3 (GenderIndex write-only): CONFIRMED still present, decline ACCEPTED with reservation. Zero non-test readers still holds (Domain.cs:3, MeetingSession.cs:99 literal 0, repo persist/readback). Shipped-SQLite-column compat is legitimate; revisit if a second release ships without a reader.
- M4 (demo Gemini presented as real): PARTIALLY fixed. Send copy now reads "Demo result shown: no network call, no media uploaded" (DetailsPage.xaml.cs:184). Residue filed as R2-1 (demo Succeeded job persisted, no demo flag) plus nit (DemoMeetings.All[0] fallback :175 when _meeting is null).
- M5 (SherpaDiarizer.AssignTurn throws): RETRACTED as live defect, kept as design debt. AssignTurn still routes to AssignWindow(empty) which throws (SherpaDiarizer.cs:30), but the only wired path uses WindowDiarizer (never calls inner AssignTurn) and DiarizerContractTests pins the empty-guard. Any future direct user of SherpaDiarizer via the interface hits the throw — contract promises what the class cannot do.
- M6 (swallowed engine failures): CONFIRMED fixed at init gate. Ctor catch sets _mode "demo (engine init failed: <Type>)". Per-window catches still swallow — accepted (transient retry), init-type label covers diagnosability. Closed.
- M7 (two secret stores): CONFIRMED fixed. AppStore.Secrets singleton; Settings Locker delegates; Details uses AppStore.Secrets. Closed.
- M8 (in-memory-only edits): comment fix CONFIRMED, behavior decline ACCEPTED. Seed comment now names the unwired save. Save itself remains open, tracked not masked.
- M9 (duplicated header/row metrics): DECLINE ACCEPTED. Column sets match across the four sites (verified current text). Shared UserControl heavier than duplication; standing nit only.
- M10 (null _session deref): CONFIRMED fixed. Pause/Resume/Stop null-check _session. Closed.

## New material findings (fresh eyes)

### R2-1. Demo Gemini run persists a Succeeded job row with no demo flag; restart presents it as a real prior result
File: src/MeetVault/Views/DetailsPage.xaml.cs:177 (SaveJob unconditional) + ShowPriorJob (LoadJob display, no provenance). The M4 copy fix corrected the send-time message, but a DemoTransport row is indistinguishable from a real success. Next Details open expands "Job <id> Succeeded" with canned JSON as if Gemini returned it. Why it matters: the false-evidence problem moved from the status line into the database. Smallest fix: skip SaveJob for DemoTransport runs, or persist a "demo — no upload" marker ShowPriorJob already renders via LastError path.

### R2-2. M1 offset and pause-gap interact: a window straddling Pause finalizes with a 10 s offset spanning a longer wall gap
Files: src/MeetVault/Views/LivePage.xaml.cs (OnPcm advances _windowOffsetMs per finalized window regardless of pause) + MeetingSession pause-gate (OnSegment drops paused segments). A half-filled window at Pause completes after Resume with a flat 10 s step for audio spanning the pause duration. Why it matters: NFR4 absolute-timestamp claim. Smallest fix: clear the partial _window buffer on Pause (or re-anchor offset at Resume); one test with pause between two windows asserting continuity modulo the documented gap.

## Standing nits carried forward
GenderIndex zero-read; AssignTurn contract debt; header-metric quadruplication; DemoMeetings.All[0] null fallback; offset-advance-on-failure; doc count drift (38/38 vs 43/43); DiarizerContractTests hard-requires sherpa models on disk.

## Verdict: 2 numbered findings (R2-1, R2-2). Not GREEN.

---

# Round 3 — final (2026-10-06, HEAD 93f58ff)

- R2-1: CONFIRMED fixed. DetailsPage.xaml.cs:178 gates SaveJob on transport-is-not-DemoTransport; no canned Succeeded row reaches the DB or ShowPriorJob. Minimal and correct.
- R2-2: CONFIRMED fixed. Pause_Click clears the partial _window buffer; Resume re-anchors _windowOffsetMs to max EndMs of session segments (0 when empty). Post-gap windows continue from the last persisted timing, never span the pause. Edge case (resume with zero segments) handled inline.
- Verification this round: dotnet test exit 0, 43/43 passed; dotnet build --no-restore exit 0, 0 warnings 0 errors. Whole-codebase sweep across all three rounds surfaced no further material findings.

## Verdict: GREEN.
