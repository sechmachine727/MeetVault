# MeetVault web console — report

- **Date:** 2026-10-15 (UTC+07:00)
- **Branch:** `caption-bar-2026-10-10`
- **Deliverable:** a management web console served by the MeetVault backend, covering the desktop
  client's Cloud Storage surfaces, tested end to end, with ordered screenshots (this folder).
- **Plan of record:** `../../.hermes/plans/2026-10-15_001942-meetvault-web-cloud-console.md`

---

## 1. What shipped

One artifact, no new deployment: the existing backend image now serves a dependency-free static SPA
at `/` from `MVB_WEB_ROOT` (default `./web`, the image sets `/app/web`), same-origin with `/v1`, so
there is no CORS, no second container and no JavaScript build step.

| # | Desktop Cloud Storage behaviour | Web surface | Endpoint | Proof (spec) |
|---|---|---|---|---|
| 1 | Sign in / sign out, account shown | `#/login` + rail account chip | `/v1/auth/{login,logout,me}` | `01-auth` |
| 2 | Server-side meeting list | `#/meetings` rows | `GET /v1/meetings` | `02-list` |
| 3 | Status line + elapsed while processing | row status pill | `GET /v1/meetings`, `…/status` | `02-list`, `03-autorefresh` |
| 4 | Search / filter / paging | search, status filter, prev/next | `GET /v1/meetings?q&status&limit&offset` | `02-list` |
| 5 | Auto-refresh while processing | 2 s poll gate, idle = no polling | list + `/status` | `03-autorefresh` |
| 6 | Open (cloud copy) | `#/meetings/{id}` Overview | `GET /v1/meetings/{id}` | `04-detail` |
| 7 | Remove from cloud (confirm) | confirm dialog | `DELETE /v1/meetings/{id}` | `02-list`, `92-remove-meeting` |
| 8 | Transcript with speakers + click-to-seek | Transcript tab + player | `…/transcript`, `GET /v1/media/{id}` | `05-transcript` |
| 9 | Copy transcript text | Copy button | `…/transcript/text` | `05-transcript` |
| 10 | Re-run analysis | Re-run button + live progress | `POST …/process` | `04-detail` |
| 11 | Summary, agenda, sections | Analysis tab | `GET …/analysis` | `06-analysis` |
| 12 | Decisions + action items with citations | Analysis tab | same bundle | `06-analysis` |
| 13 | Ask about this meeting + history | Q&A tab | `POST/GET …/qa` | `07-qa` |
| 14 | Speakers rename / merge / delete | Speakers tab | `…/speakers`, `PATCH /v1/speakers/{id}`, `…/merge` | `11-speakers` |
| 15 | Transcript versions activate / delete | Versions tab | `…/transcripts`, `POST …/activate`, `DELETE` | `12-versions` |
| 16 | Jobs watch (SSE) + cancel | Jobs tab | `GET /v1/jobs/{id}/events`, `POST …/cancel` | `09-jobs`, `93-jobs-cancel` |
| 17 | Media list, playback, download, delete | Overview tab: **both** tracks (audio + screen video), kind and timeline offset shown, player swaps to `<video>` | `GET /v1/media/{id}`, `…/info`, `DELETE` | `04-detail`, `91-media-delete` |
| 18 | Export to Word | Export button (real `.docx` download) | `GET …/export.docx` | `08-export` |
| 19 | Mock output never disguised | per-object badges: "Mock transcript" / "Mock analysis" | `meeting.is_mock`, `run.is_mock` | `04-detail`, `06-analysis` |
| 20 | Stale analysis surfaced, not hidden | "Out of date" banner (genuinely stale kinds only) | `status.stale_kinds` minus `fresh_kinds` | `04-detail`, `12-versions` |
| 21 | — (new) | Service tab: effective providers | `/v1/system/{info,providers}` | `10-screenshots` (11-service) |

**Deliberate scope boundary:** recording, media upload and transcript import stay in the desktop
app — that is what the desktop's Cloud Storage screen does too. The console is read/manage/analyse.

## 2. How to run it

```sh
cd backend
DEMO_ROOT="C:/Users/<you>/AppData/Local/hermes/cache/scratch/mvweb"

sh web/run-demo-stack.sh start          # migrate + create-user + api + worker + demo data
# then open http://127.0.0.1:8099/  and sign in as demo@meetvault.local / demo-password-123
sh web/run-demo-stack.sh stop
```

Production is the same image: `docker compose up -d --build` (or the deployed stack) and open
`http://<host>:8080/` — the public target is `https://meetvault.supersanta.io.vn/`.

## 3. Screens (flow order)

| Screenshot | What it shows |
|---|---|
| `screens/01-sign-in.png` | The sign-in screen (the console is account-gated; every `/v1` call carries the session token) |
| `screens/02-meetings-list.png` | The cloud list: three meetings, status pills, "Analyzed" badge, search + status filter + paging, range "1–3 of 3" |
| `screens/03-remove-confirm.png` | The "Remove from cloud?" confirmation (states that the server copy goes, the local recording does not) |
| `screens/04-meeting-overview.png` | Meeting detail: facts (status/language/duration/created/counts), "Mock transcript" badge, both recordings (audio + screen video, with its `starts at 0:02` offset) with playback/download/delete |
| `screens/04b-screen-video.png` | The screen-recording track loaded into the shared player: the element has swapped to `<video>` and renders a real frame, caption "Playing: demo-1-screen.mp4 (video, 0:30)" |
| `screens/05-transcript.png` | Transcript tab: speaker-labelled, timed rows, click-to-seek, copy-text, the shared player |
| `screens/06-analysis.png` | Analysis tab: mock badge, run provenance, summary + bullets, agenda, sections, decisions, action items |
| `screens/07-qa.png` | Q&A tab: a stored answer with numbered citations that seek the recording, plus the ask box |
| `screens/08-speakers.png` | Speakers tab: rename / merge-into / delete with segment counts and speaking time |
| `screens/09-versions.png` | Versions tab: both transcript versions, the active one marked, activate/delete |
| `screens/10-jobs.png` | Jobs tab: the pipeline history with state, progress, mock marking, cancel (only while running) and the SSE "Live events" pane |
| `screens/11-service.png` | Service tab: what this server is actually running (`/v1/system/info` + `/v1/system/providers`) |
| `screens/12-list-processing.png` | A live processing row: "Processing · analyze running · 0% · 4m 30s" — the elapsed timer counting up |
| `screens/13-narrow-list.png` | The same list at 420 px: the rail collapses to a top bar, no horizontal overflow |

## 4. Test evidence

All commands were run in this session; the tails below are real.

```text
$ cd backend && ./.venv/Scripts/python.exe -m pytest -q
236 passed, 2 skipped, 1 warning in 49.21s            (baseline before this work: 214 passed, 2 skipped)

$ ./.venv/Scripts/python.exe -m ruff check src tests
All checks passed!
$ ./.venv/Scripts/python.exe -m ruff format --check src tests
110 files already formatted

$ cd backend/web && node --test "tests/*.test.mjs"
ℹ tests 30
ℹ pass 30
ℹ fail 0

$ cd backend/web && MVB_DEMO_URL=http://127.0.0.1:8099 npx playwright test --reporter=line
57 passed (59.8s)                                     (two projects: narrow 420 px, then desktop 1440 px)
```

- **Zero console errors** is asserted by every browser spec (`fixtures.js` collects
  `console` errors + `pageerror`). That is what turned the CSP defect below into a red test.
- **The exported Word file is a real document**, verified independently of the browser:
  `docx.Document(...)` → `paragraphs: 16`, containing the title, `00:30 long`, the mock-provenance
  note, the summary and its bullets (35 619 bytes).
- **The console is exercised against a real server, real worker and real media**: the demo stack
  runs `uvicorn` + `python -m mvb.jobs.worker` with `MVB_*_=mock`
  providers and 30 s/20 s/12 s WAVs **plus real H.264 MP4 screen tracks** (ffmpeg `testsrc`, uploaded
  as `kind=video&start_offset_ms=2000`) generated by the seed; the Q&A spec asks a new question and
  waits for the worker's answer to land.
- **`/v1` is unchanged**: `/openapi.json`, `/docs`, `/healthz`, `/readyz` and the 401-without-token
  behaviour are asserted by `tests/integration/test_web_app.py` (19 tests).

## 5. Findings (each one found by testing, with its evidence)

1. **The page CSP blocked every recording.** `media-src 'self'` refuses the `blob:` object URL the
   console must use (a media element cannot send the bearer header). Chromium logged
   `Loading media from 'blob:http://…' violates the following Content Security Policy directive:
   "media-src 'self'"` and every seek silently did nothing. Fixed: `media-src 'self' blob:`,
   pinned by `test_root_serves_the_app_shell`.
2. **`create_app()` pinned the dev database for a test session.** `mvb.main` runs
   `app = create_app()` at import time and `tests/integration/test_health.py` imports it at
   COLLECTION time, so reading the lru-cached `get_settings()` there froze the ambient database: the
   first test of every session ran against `backend/_local.db` instead of its throwaway file —
   symptom `UNIQUE constraint failed: users.email` in `tests/e2e/test_full_flow.py`, plus stray rows
   in the dev DB. Fixed with a fresh `Settings()`; pinned by a subprocess cache-size test (proven to
   bite: the mutant fails, the fix passes).
3. **A wrong password showed "Your session expired".** The API client fired its auth-failure hook on
   any 401, including `POST /v1/auth/login`, which re-rendered the app and wiped the real message.
   Fixed and pinned in `tests/api.test.mjs`.
4. **Elapsed times were 7 hours wrong.** SQLite returns `DateTime(timezone=True)` NAIVE, so
   `/status.active_job_started_at` arrives as `2026-10-14T17:59:19.728358`; `Date.parse` read it as
   local time (UTC+07:00 here). A job started 92 s earlier rendered as **"7h 5m"**. Fixed in
   `lib/format.js` (`parseServerTime` pins a missing offset to UTC) with a unit test.
5. **The stale signal cried stale over current data.** `stale_kinds` legitimately lists a kind whose
   superseded document exists while a fresh one does too, so after a re-run the console showed
   "Out of date: action_items, agenda, decisions, summary" — as a full-page banner AND as a per-row
   "Outdated: …" pill, and again on the Analysis tab (the bundle copies status' unfiltered list) —
   over an analysis that was current (visible in `screens/12-list-processing.png` before the fix).
   Fixed with the same rule the desktop client uses — stale minus fresh (`outOfDateKinds`), and
   against the bundle's own contents (`outOfDateBundleKinds`), both unit-tested; all three surfaces
   now report nothing, and the re-generated screenshot confirms it.
6. **The mock badge said the wrong thing.** A row-level badge driven by the aggregate
   `/status.mock` (true whenever ANY step ran mocked) reads as "your analysis is fake" even for a
   genuine analysis — the same trap the desktop hit. Fixed: badges now name the object they describe
   — "Mock transcript" (the active transcript's provider) and "Mock analysis" (`run.is_mock`), and
   the list row carries no unprovable claim.
7. **Deleting a transcript version silently destroyed the analysis.** `analysis_runs` and
   `analysis_documents` cascade on `transcript_id`, so deleting the version the analysis came from
   empties the summary, agenda, decisions, action items AND answers. Measured on a fresh seed:

   ```text
   --- 1. after the seeded pipeline
       /status fresh_kinds = ['action_items','agenda','decisions','qa','section_summary','summary']
       documents (incl stale) by kind = {agenda:1, section_summary:3, action_items:1, qa:1, summary:1, decisions:1}
   --- 3. DELETE /v1/transcripts/<diarized> (non-active, analysis-bearing) -> 200
       /status fresh_kinds = []
       documents (incl stale) by kind = {}
       /analysis summary present = False
   ```

   Fixed in the UI: the delete now opens a confirm dialog that states the cascade. **Backend
   follow-up (open):** the API gives the caller no way to know what a delete will take with it — a
   `cascade_counts` field on the response, or a refusal unless `?force=true`, would be the honest
   contract.
8. **Activating a different version leaves a stale analysis looking current.** The same probe shows
   that after `POST /v1/transcripts/<uploaded>/activate` the server keeps `fresh_kinds` and every
   document's `stale=False` exactly as before, so the console presents an analysis derived from a
   transcript that is no longer active — with no staleness signal. **Backend follow-up (open):**
   `docs/INTEGRATION.md` promises "a new transcript or media asset marks dependents stale";
   activating an existing version should do the same. `12-versions.spec.js` pins the observed
   behaviour with a comment naming it as a finding, so the fix will surface as a deliberate change.
9. **Search only reacted on blur.** The box used `change`, so typing then reading the list showed
   stale rows. Now `input` (debounced 250 ms).
10. **Deleting the recording that was playing left a dead player.** The blob was revoked before the
    "is this the loaded asset" comparison, so the element kept a revoked URL. Fixed by capturing the
    flag first, then re-resolving the player source.
11. **The shared player was audio-only, so a screen recording could not be watched.** The player was
    a hardcoded `<audio>` element, so loading the video track played its audio and showed nothing —
    and nothing failed loudly. Found when the owner asked whether video had been tested at all.
    Fixed: `loadAsset(asset)` swaps the element to `<video>` for a video asset and updates the
    caption; the seed now uploads a real MP4 per meeting and `04-detail` asserts two rows, the
    `starts at 0:02` offset, the `<video>` element with a real duration, and a video download
    (`screens/04b-screen-video.png` shows a rendered frame).
12. **Two conditional header elements rendered as the literal text "nullnull".** A conditional child
    (`cond ? el(...) : null`) passed to the raw DOM `append()` is stringified, not skipped, unlike
    the `el()` helper. Fixed with `appendAll()` in `lib/dom.js`, and the a11y spec now walks every
    text node on the list and all seven tabs and fails on a node whose whole content is
    "null"/"undefined" — proven to bite (the mutant fails, the fix passes).
13. **Documentation drift (reported, not changed):** `backend/README.md` §Status still says
    "157 tests pass" (the suite is 236) and `docs/ACCEPTANCE.md`/`COVERAGE` rows predate the web
    console. Out of scope here; worth a docs sweep.

## 6. Architecture

`screens/../console-architecture.png` (source: `console-architecture.drawio`) — request path and
console modules, with what is new (blue/green/orange) versus reused unchanged (grey).

## 7. Not done / gated

| Item | State |
|---|---|
| **Public host** `https://meetvault.supersanta.io.vn` | **BLOCKED, not simulated.** Verified this session: `/healthz`, `/`, `/assets/app.js` and `/docs` all answer `000` — the vhost/cert is still not installed on the server (the owner has a pending installer, per the 2026-10-14 DNS plan). Everything above was verified against a local `uvicorn`. |
| **nginx must proxy `/` and `/assets/`**, not only `/v1` | Unverified (server-side). If it proxies only specific paths, the page will load with no CSS/JS. |
| **CI job `web-console`** | Added to `.github/workflows/backend-ci.yml` (node unit tests + the full browser suite against `web/run-demo-stack.sh`, plus a console assertion inside the existing docker smoke). **Unexercised**: no Docker daemon on this box and the branch is not pushed, so it is written from the commands verified locally, not from a green run. |
| **Browser-side upload / recording** | Not built (deliberate parity boundary). Would need multipart upload + progress in the browser — a follow-up wave. |
| **Japanese UI** | English only (the desktop is en/ja). |
| **Session token in `localStorage`** | Mitigated by a strict CSP (`script-src 'self'`), no third-party script, no `innerHTML`, and clearing on sign-out/401. An httpOnly cookie session needs backend auth changes — follow-up. |
| **No rate limiting on `POST /v1/auth/login`** | Pre-existing; exposing a browser console makes the credential surface public. Recommend a throttle/lockout before the public launch. |
