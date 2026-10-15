# Plan: MeetVault web console — manage your cloud meetings from the browser

- **Plan id:** `2026-10-15_001942-meetvault-web-cloud-console`
- **Date:** 2026-10-15 00:19 (UTC+07:00)
- **Repo:** `D:/workspace/MeetVault` (monorepo: `client/` WinUI 3 + .NET 8, `backend/` FastAPI + SQLAlchemy)
- **Branch to work on:** `caption-bar-2026-10-10`
- **Baseline HEAD (verified at plan time):** `98216f4` — tree dirty only with the known untracked `.hermes/`, `EVALUATION.md`, `eval-evidence/`. If HEAD differs or the tree is dirty beyond that, STOP and report.
- **Baseline gates (verified at plan time, do not "improve"):** backend suite `214 passed, 2 skipped`; `ruff check src tests` → `All checks passed!`; `ruff format --check src tests` → clean.
- **Owner directive (verbatim):** *"I want my meetvault backend to have a website that would serve the purpose of managing user's meeting. All the function of the website will reflect exactly what user can do with their cloud storage on the front end. Create a modern UI or copy from current desktop front end. after finishing you need to test thoroughly. And capture the sceens for me for report."*
- **Executor:** an implementer with zero context on this codebase and questionable taste. Every path, command and expected output below was verified against the tree at `98216f4`. If a file does not look as described, STOP and re-read it before editing. **Never guess a field name: every JSON field used below was read out of `backend/src/mvb/schemas/`.**

---

## 1. Goal

Serve a management web console from the MeetVault backend that gives a signed-in user everything the desktop client's **Cloud Storage** screen (and the cloud surfaces it opens) gives them — list, open, inspect transcript/analysis/Q&A/speakers/versions/jobs, re-run analysis, export Word, remove — tested end to end and delivered with ordered screenshots for the owner's report.

---

## 2. Current context / assumptions (verified at plan time)

### EXISTS — do NOT rebuild

- **The backend is a FastAPI service** with one app factory: `backend/src/mvb/main.py` → `create_app()`. Routers mounted: health (unprefixed), `system`, `auth`, `meetings`, `media`, `transcripts`, `speakers`, `jobs`, `analysis` — all under `/v1`. FastAPI also serves `/openapi.json` and `/docs` (Swagger UI) today. **There is no static/template serving anywhere** (`find backend -iname '*static*' -o -iname '*.jinja*'` → nothing).
- **Auth is per-user bearer session tokens.** `POST /v1/auth/login {email,password}` → `{token, expires_at, user:{id,email,display_name}}`; `POST /v1/auth/logout`; `GET /v1/auth/me` → `{user, expires_at, issued_at}`. `require_user` guards every `/v1` route. Missing/unknown token → `401 unauthorized`; expired/revoked → `401 session_expired`. Errors always use the envelope `{"error":{"code","message","detail"},"request_id"}` (`backend/src/mvb/schemas/common.py`).
- **Listing is paged**: `Page[T] = {items, total, limit, offset}`; `GET /v1/meetings` takes `q`, `status`, `include_deleted`, `limit` (1..200), `offset`.
- **The Cloud Storage list fields already exist** on `MeetingOut`: `id, title, status, language, source, external_id, started_at, ended_at, duration_ms, metadata, deleted_at, created_at, updated_at, has_analysis, latest_job_type, latest_job_state`. `MeetingDetailOut` adds `media_count, transcript_count, active_transcript_id, speaker_count, is_mock`.
- **`MeetingStatusOut`** = `status, steps{}, progress, active_job_id, active_transcript_id, fresh_kinds[], stale_kinds[], mock, duration_ms, active_job_started_at, error` — `active_job_started_at` drives the desktop row's elapsed time.
- **Analysis is one bundle**: `GET /v1/meetings/{id}/analysis` → `{meeting_id, transcript_id, run{provider,is_mock,…}|null, summary|null, agenda|null, sections[], section_summaries[], decisions|null, action_items|null, qa[], stale_kinds[], mock}`. Payload shapes (from `docs/INTEGRATION.md` §1.6): `summary {text, bullets[]}`, `agenda {items:[{title,topic,start_ms,end_ms}]}`, `section_summaries[i].payload {summary,bullets[],decisions[],action_items[]}`, `decisions {items:[{text,decided_by[],at_ms,segment_ids[]}]}`, `action_items {items:[{text,owner,due,segment_ids[]}]}`. `segment_ids` are verified server-side, so a citation is always seekable.
- **Deployment**: `backend/Dockerfile` (python:3.12-slim, `WORKDIR /app`, `COPY src ./src`, `COPY pyproject.toml README.md ./`, `pip install ".[prod]"`, entrypoint runs `alembic upgrade head`, `CMD uvicorn mvb.main:app`), `backend/compose.yaml` (postgres + api + worker), CI `.github/workflows/backend-ci.yml`. The public host is `https://meetvault.supersanta.io.vn` (nginx vhost + Let's Encrypt, installed by a server-side script per `.hermes/plans/2026-10-14_224643-frontend-use-deployed-backend-dns.md`).
- **Local toolchain on this box:** Node **v26.7.0** + npm **11.19.0** (verified `node --version`), backend venv `.venv/Scripts/python.exe`, ffmpeg/ffprobe present, **no Docker daemon**.
- **Test harness patterns to copy:** `backend/tests/conftest.py` has an autouse `_clean_env` fixture that clears every `MVB_*` env var and sets a throwaway SQLite `MVB_DATABASE_URL` + `MVB_MEDIA_ROOT`, plus a `client` fixture (`TestClient(create_app())`) and an `auth_user`/`auth_headers` pair. `backend/tests/helpers.py` has `make_wav()` (ffmpeg sine) and `seed_meeting_with_audio()`.

### MISSING — the delta this plan delivers

1. No static file serving, no `web/` directory, no SPA, no templates.
2. No `MVB_WEB_ROOT` setting; `Settings` (`backend/src/mvb/config.py`) has no web-related field.
3. No browser-usable media playback path (`/v1/media/{id}` needs `Authorization`, which an `<audio src>` cannot send).
4. No JS test tooling at all; no `package.json` in the repo.
5. The Docker image does not ship any web assets.
6. **The public host does not answer right now** — verified this session: `curl https://meetvault.supersanta.io.vn/healthz` → `healthz=000` (TLS/host not installed yet; the owner has a pending installer on the server). So the live public check is a gated step, not a promise. All screenshots are produced against a local uvicorn.

### Assumptions

- The deployed stack is this same code (`backend/`), so one image serves both the API and the console; no second deployment artifact is needed.
- The nginx vhost proxies `/` (not only `/v1`) to the container — **unverified**, see Open questions.
- Browsers are modern (ES2022 modules, `<dialog>`, CSS custom properties) — no IE/legacy support.
- The web console is management-only: **recording and uploads stay on the desktop client** (that is what "cloud storage management" means on the desktop too — the Cloud Storage screen only lists, opens and removes).

---

## 3. Scope — feature parity matrix (the definition of done)

Every row must exist, work, and be covered by a test + a screenshot. Left column = what the desktop client does today (source: `client/README.md` §Features and `backend/docs/INTEGRATION.md` §"Implemented client (2026-10-13)").

| # | Desktop Cloud Storage behaviour | Web surface | Endpoint(s) |
|---|---|---|---|
| 1 | Sign in / sign out, show the account | `#/login` + account chip in the top bar | `POST /v1/auth/login`, `POST /v1/auth/logout`, `GET /v1/auth/me` |
| 2 | Server-side meeting list (server is the only source of truth) | `#/meetings` rows (title, created-at, duration) | `GET /v1/meetings` |
| 3 | Row status line + mock badge + elapsed while processing | row status line ("Processing · 47% · 1m 32s") | `GET /v1/meetings` (+ `GET /v1/meetings/{id}/status` for processing rows) |
| 4 | Search / filter / paging | search box, status filter, prev/next | `GET /v1/meetings?q&status&limit&offset` |
| 5 | Auto-refresh while anything is processing | 2 s poll gate, stops when idle | list + `/status` |
| 6 | Open (cloud copy → details) | `#/meetings/{id}` Overview tab | `GET /v1/meetings/{id}` |
| 7 | Remove from cloud (confirm) | confirm dialog | `DELETE /v1/meetings/{id}` |
| 8 | Server transcript: speakers, times, click-to-seek | Transcript tab + player | `GET /v1/meetings/{id}/transcript`, `GET /v1/media/{asset_id}` |
| 9 | Copy transcript text | Copy button | `GET /v1/meetings/{id}/transcript/text` |
| 10 | Re-run analysis (6-state workflow) | Re-run button + live progress | `POST /v1/meetings/{id}/process`, `GET …/status` |
| 11 | Summary, agenda & discussion, sections | Analysis tab | `GET /v1/meetings/{id}/analysis` |
| 12 | Decisions + action items with citation seek | Analysis tab | same bundle |
| 13 | Ask about this meeting + history | Q&A tab | `POST /v1/meetings/{id}/qa`, `GET /v1/meetings/{id}/qa` |
| 14 | Speakers rename / merge / delete | Speakers tab | `GET /v1/meetings/{id}/speakers`, `PATCH /v1/speakers/{id}`, `POST /v1/speakers/{id}/merge`, `DELETE /v1/speakers/{id}` |
| 15 | Transcript versions activate / delete | Versions tab | `GET /v1/meetings/{id}/transcripts`, `POST /v1/transcripts/{id}/activate`, `DELETE /v1/transcripts/{id}` |
| 16 | Jobs: watch (SSE) + cooperative cancel | Jobs tab | `GET /v1/meetings/{id}/jobs`, `GET /v1/jobs/{id}`, `GET /v1/jobs/{id}/events`, `POST /v1/jobs/{id}/cancel` |
| 17 | Media list, playback, download, delete | Overview tab | `GET /v1/meetings/{id}/media`, `GET /v1/media/{asset_id}`, `GET /v1/media/{asset_id}/info`, `DELETE /v1/media/{asset_id}` |
| 18 | Export to Word | Export button (downloads `.docx`) | `GET /v1/meetings/{id}/export.docx` |
| 19 | Mock output is never disguised | `Mock data` badge on every mock-derived surface | `bundle.mock`, `status.mock`, `run.is_mock`, `is_mock` |
| 20 | Stale analysis is surfaced, not hidden | "Outdated: …" banner (stale_kinds) | `status.stale_kinds`, `bundle.stale_kinds` |
| 21 | — (new, cheap) | Service tab: providers + effective engine | `GET /v1/system/info`, `GET /v1/system/providers` |

**Out of scope (deliberate, state it in the report):** browser-side recording, media/transcript upload, browser-side accounts/registration, Japanese UI, and real-time collaborative editing. Upload from the browser is the one genuine parity gap — see Open questions Q4.

---

## 4. Architecture / proposed approach

Serve a **dependency-free static SPA from the existing FastAPI app** at `MVB_WEB_ROOT` (default `./web`), mounted at `/assets` with `/` returning `index.html`, so the console is same-origin with `/v1` (no CORS, no second container, no nginx change beyond proxying `/`, and the whole thing rides the existing Docker image). The SPA is plain ES modules + one CSS file (no framework, no bundler, no build step) because this repo has no JS toolchain and the value is in the feature parity, not in a build pipeline; pure logic lives in `web/lib/*.js` so it is unit-testable with `node --test` (zero npm deps), and Playwright (the only devDependency) drives the real browser for E2E + the ordered screenshots. The UI copies the desktop's information architecture (list → detail with tabs) and its Fluent-ish design tokens (cards, accents, light/dark, one shared status vocabulary), not its XAML.

```
browser ──HTTPS──▶ nginx vhost (meetvault.supersanta.io.vn)
                        │  proxy_pass 127.0.0.1:8080
                        ▼
              FastAPI (backend/src/mvb/main.py)
                ├─ "/"            → web/index.html        (FileResponse, no-cache + CSP)
                ├─ "/assets/*"    → web/**                (StaticFiles)
                ├─ "/v1/**"       → the existing routers (bearer session tokens)
                ├─ "/docs"        → Swagger UI (unchanged)
                └─ DB (sqlite/postgres) + media volume + worker (+ providers)
```

---

## 5. Conventions and iron rules (read before any task)

- Backend commands run from `D:/workspace/MeetVault/backend` in git-bash; the interpreter is `./.venv/Scripts/python.exe`. Web commands that use node/npm run from `D:/workspace/MeetVault/backend/web`.
- **One commit per task**, staging exactly the named files (`git add <paths>`), never `git add -A`; never commit `.hermes/`, `models/`, `EVALUATION.md`, `eval-evidence/`, `node_modules/`. Never push — pushing is owner-gated.
- Commit style: `feat(web):`, `test(web):`, `fix(web):`, `docs:`, `ci(web):`, `build(docker):`.
- The backend Python suite (`214 passed, 2 skipped`) and `ruff check src tests` / `ruff format --check src tests` must be green at every commit that touches `backend/src` or `backend/tests`.
- TDD per task: write the failing test → run it → **observe the exact RED** → implement minimally → run → GREEN → commit. A test that never failed proves nothing.
- The test project rule that governs the client applies in spirit here: **logic that deserves a unit test goes in a module a test can import without a browser** (`web/lib/*.js` under `node --test`); DOM wiring is verified by Playwright.
- No inline `style=""` attributes in the SPA HTML and no inline `<script>`: the page is served with a strict CSP (`style-src 'self'`, `script-src 'self'`). Dynamically styling an element is done via `el.style.setProperty(...)` (allowed by CSP) or a class. A CSP violation is a **console error** — and the E2E suite asserts zero console errors, so a violation fails the build instead of silently un-styling the UI.
- Scratch files (seeded DBs, media, logs, Playwright artifacts) go in `C:/Users/supersanta/AppData/Local/hermes/cache/scratch/mvweb/` — never in the repo. Define once and reuse:
  `DEMO_ROOT="C:/Users/supersanta/AppData/Local/hermes/cache/scratch/mvweb"`.
- Windows path rules that bite here (from the project's own history): native tools need `C:/…` forward-slash paths, never `/c/…`; on this box `cd /d/workspace/...` works (bash builtin) while a native tool must get `D:/workspace/...`.
- Never write API keys into any file. The demo runs with `MVB_ANALYZER=mock` — no key needed for any task in this plan.

---

## 6. Step-by-step tasks

### Phase A — the backend serves the console

#### T0 — pre-flight baseline (read-only, no commit, ~4 min)

```bash
cd /d/workspace/MeetVault
git rev-parse --short HEAD && git status --short
# expect: 98216f4 and only ".hermes/" "EVALUATION.md" "eval-evidence/" untracked lines
cd backend
./.venv/Scripts/python.exe -m pytest -q 2>&1 | tail -1
# expect: 214 passed, 2 skipped
./.venv/Scripts/python.exe -m ruff check src tests && ./.venv/Scripts/python.exe -m ruff format --check src tests
# expect: All checks passed!   then   108 files already formatted
node --version && npm --version
# expect: v26.7.0 and 11.19.0  (if node is missing, STOP and report — Phase B–E need it)
curl -s -m 10 -o /dev/null -w 'public_healthz=%{http_code}\n' https://meetvault.supersanta.io.vn/healthz
# expect 000 today (host not installed yet) — record the number; T34 re-checks it
```

Write the four numbers down; every later gate is compared against them.

---

#### T1 — `MVB_WEB_ROOT` setting (TDD, ~5 min)

**RED.** Create `backend/tests/unit/test_web_settings.py` (the `tests/unit/` directory already exists):

```python
from __future__ import annotations

from pathlib import Path

from mvb.config import Settings


def test_web_root_defaults_to_the_repo_relative_web_dir() -> None:
    assert Settings().web_root == Path("./web")


def test_web_root_can_be_overridden_by_env(monkeypatch) -> None:
    monkeypatch.setenv("MVB_WEB_ROOT", "/srv/console")
    assert Settings().web_root == Path("/srv/console")
```

Run:

```bash
cd /d/workspace/MeetVault/backend
./.venv/Scripts/python.exe -m pytest tests/unit/test_web_settings.py -q
```

Expected RED: `2 failed` with `AttributeError: 'Settings' object has no attribute 'web_root'` (pydantic raises `ValidationError`/`AttributeError` depending on version — either way it must FAIL here).

**GREEN.** In `backend/src/mvb/config.py`, add one field after `media_root`:

```python
    media_root: Path = Path("./data/media")
    web_root: Path = Path("./web")
```

Re-run the same command → `2 passed`.

Commit: `feat(config): MVB_WEB_ROOT for the management web console`.

---

#### T2 — serve `/` and `/assets/*` from the app (TDD, ~8 min)

**RED.** Create `backend/tests/integration/test_web_app.py`:

```python
from __future__ import annotations

from pathlib import Path

import pytest

# The console's real source directory (backend/web), resolved from this file, never from cwd.
WEB_ROOT = Path(__file__).resolve().parents[2] / "web"


@pytest.fixture(autouse=True)
def _web_root_env(monkeypatch: pytest.MonkeyPatch) -> None:
    """Point the app at the real web dir. autouse => this runs after conftest's own autouse
    `_clean_env` (which deletes every MVB_* var), so the value is not wiped."""
    monkeypatch.setenv("MVB_WEB_ROOT", str(WEB_ROOT))


def test_root_serves_the_app_shell(client) -> None:
    res = client.get("/")
    assert res.status_code == 200
    assert res.headers["content-type"].startswith("text/html")
    assert "MeetVault" in res.text
    assert res.headers["cache-control"] == "no-cache"
    assert "default-src 'self'" in res.headers["content-security-policy"]


def test_assets_are_served_with_the_right_mime_types(client) -> None:
    css = client.get("/assets/app.css")
    assert css.status_code == 200
    assert css.headers["content-type"].startswith("text/css")
    js = client.get("/assets/app.js")
    assert js.status_code == 200
    assert "javascript" in js.headers["content-type"]


def test_unknown_asset_is_a_404(client) -> None:
    assert client.get("/assets/does-not-exist.js").status_code == 404


def test_api_docs_and_openapi_are_still_served(client) -> None:
    assert client.get("/openapi.json").status_code == 200
    assert client.get("/docs").status_code == 200


def test_api_routes_are_unaffected(client) -> None:
    assert client.get("/healthz").status_code == 200
    assert client.get("/v1/meetings").status_code == 401


def test_missing_web_root_degrades_to_api_only(client, monkeypatch) -> None:
    """A machine with only the Python package must still boot and serve the API."""
    from fastapi.testclient import TestClient

    from mvb.config import get_settings
    from mvb.main import create_app

    monkeypatch.setenv("MVB_WEB_ROOT", "/definitely/not/here")
    get_settings.cache_clear()
    with TestClient(create_app()) as solo:
        assert solo.get("/healthz").status_code == 200
        assert solo.get("/").status_code == 404
```

Run it → expected RED for the first five tests (`404` on `/`, because nothing is mounted). The last test may already pass — that is fine, it is the regression guard for T2's error path.

**GREEN.** In `backend/src/mvb/main.py`:

1. Extend the imports:

```python
from pathlib import Path

from fastapi import FastAPI, Request
from fastapi.responses import FileResponse
from fastapi.staticfiles import StaticFiles
```

2. Add the module-level helper (put it directly under `log = logging.getLogger(__name__)`):

```python
# The console page is dynamic-ish (it boots against the live API): never let a proxy or the
# browser cache it. The CSP keeps the page to its own origin — the session token lives in
# localStorage, so a third-party script or an injected inline script must not be able to run.
_WEB_INDEX_HEADERS = {
    "Cache-Control": "no-cache",
    "Content-Security-Policy": (
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; "
        "media-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'"
    ),
}


def mount_web_console(app: FastAPI, settings: Settings) -> None:
    """Serve the management console from MVB_WEB_ROOT (default ./web).

    A missing directory is NOT an error: the promise is that the service boots anywhere, so an
    API-only machine (no web assets on disk) logs and carries on.
    """
    root: Path = settings.web_root
    index = root / "index.html"
    if not index.is_file():
        log.warning("web_ui_disabled web_root=%s reason=missing_index", root)
        return
    app.mount("/assets", StaticFiles(directory=root), name="web-assets")

    @app.get("/", include_in_schema=False)
    def web_index() -> FileResponse:  # pragma: no cover - trivial pass-through
        return FileResponse(index, media_type="text/html", headers=_WEB_INDEX_HEADERS)

    log.info("web_ui_enabled web_root=%s", root)
```

3. Call it in `create_app()` — the `settings` object is **not** created there today, so add the call as the last line before `return app`:

```python
    from mvb.config import get_settings

    mount_web_console(app, get_settings())
    return app
```

(Importing `get_settings` at function level keeps `create_app()` free of module-import ordering surprises; `lifespan()` already imports it at call time.)

**Now create the two placeholder files so the tests have something to serve.** `backend/web/index.html`:

```html
<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>MeetVault — Cloud</title>
    <link rel="stylesheet" href="/assets/app.css" />
  </head>
  <body>
    <div id="app" class="app-shell"><main id="view" class="view"></main></div>
    <script type="module" src="/assets/app.js"></script>
  </body>
</html>
```

`backend/web/app.css`: `/* MeetVault console — filled in by T7 */`
`backend/web/app.js`: `// MeetVault console — filled in by T13`

Re-run:

```bash
./.venv/Scripts/python.exe -m pytest tests/integration/test_web_app.py -q
```

Expected GREEN: `6 passed`.

Commit: `feat(backend): serve the management web console from MVB_WEB_ROOT`.

---

#### T3 — media playback needs a bearer header: add a blob helper on the API side of the contract (decision task, ~6 min)

**Why this task exists:** `GET /v1/media/{asset_id}` requires `Authorization: Bearer …`. An `<audio>`/`<video>` element **cannot send headers**, and the backend must not accept the token in a query string (it would leak into proxy logs). Decision (implemented in T18): the console fetches the asset with `fetch()` + header, wraps the body in `blob:` and points the player at that object URL — playback and seeking both work off the in-memory blob.

**Do:** add a note to `backend/docs/API.md` under the Media table so the contract states the console's consumer rule (no code change):

```markdown
## Browser playback

`GET /v1/media/{asset_id}` is bearer-only and the token is never accepted as a query parameter.
A browser player therefore cannot use the URL directly: a web client fetches the asset with the
`Authorization` header, wraps the response in a `blob:` object URL and plays that (seeking works
off the blob). `Range` on this endpoint stays for non-browser clients.
```

**Verify:** `grep -n "Browser playback" backend/docs/API.md` → one hit.

Commit: `docs(api): browser media playback rule (blob, never a token query param)`.

---

#### T4 — regression gate for Phase A (~3 min)

```bash
cd /d/workspace/MeetVault/backend
./.venv/Scripts/python.exe -m pytest -q 2>&1 | tail -1
# expect: 222 passed, 2 skipped   (214 baseline + 2 settings + 6 web-app tests)
./.venv/Scripts/python.exe -m ruff check src tests && ./.venv/Scripts/python.exe -m ruff format --check src tests
# expect: All checks passed!  /  110 files already formatted
```

If the counts differ from the arithmetic, find out which test did not run before continuing (`git status --short` must show no stray files). No commit for this task — it is the gate that closes Phase A.

---

### Phase B — the console: shell, pure logic, API client

#### T5 — `web/package.json` + the pure-logic test runner (~4 min)

Create `backend/web/package.json`:

```json
{
  "name": "meetvault-web",
  "private": true,
  "version": "0.1.0",
  "type": "module",
  "description": "MeetVault management console (static SPA served by the FastAPI backend) + its browser tests",
  "scripts": {
    "test": "node --test tests/",
    "e2e": "playwright test",
    "e2e:report": "playwright show-report"
  },
  "devDependencies": {
    "@playwright/test": "^1.49.0"
  }
}
```

Add to `backend/.gitignore` (append; keep the file's existing lines):

```text
web/node_modules/
web/test-results/
web/playwright-report/
web/.playwright/
```

Add to `backend/.dockerignore` (append):

```text
web/node_modules/
web/tests/
web/e2e/
web/package.json
web/package-lock.json
web/playwright.config.js
```

**Verify:**

```bash
cd /d/workspace/MeetVault/backend/web
node --test tests/ 2>&1 | tail -3
# expect: "# tests 0" ... "# pass 0" (no test files yet — node exits 0; if it exits non-zero
# because tests/ does not exist, create it: mkdir -p tests && touch tests/.gitkeep)
```

Commit: `build(web): package.json, ignore rules for node_modules and test artifacts`.

---

#### T6 — `web/lib/format.js` — the pure formatting/vocabulary layer (TDD, ~10 min)

**RED.** Create `backend/web/tests/format.test.mjs`:

```js
import test from "node:test";
import assert from "node:assert/strict";

import {
  formatDuration,
  formatElapsed,
  formatDate,
  formatBytes,
  statusKind,
  statusLabel,
  isProcessing,
  describeJobState,
  friendlyError,
  pagingRange,
} from "../lib/format.js";

test("formatDuration renders h:mm:ss above an hour and m:ss below", () => {
  assert.equal(formatDuration(0), "0:00");
  assert.equal(formatDuration(7000), "0:07");
  assert.equal(formatDuration(83_000), "1:23");
  assert.equal(formatDuration(3_723_000), "1:02:03");
  assert.equal(formatDuration(null), "—");
});

test("formatElapsed counts up from an ISO start time", () => {
  const start = "2026-10-15T00:00:00Z";
  const now = Date.parse(start) + 92_000;
  assert.equal(formatElapsed(start, now), "1m 32s");
  assert.equal(formatElapsed(start, Date.parse(start) + 4_000), "4s");
  assert.equal(formatElapsed(null, now), "");
});

test("formatDate is a stable, locale-independent label", () => {
  assert.equal(formatDate("2026-10-14T22:10:00Z"), "14 Oct 2026, 22:10");
  assert.equal(formatDate(null), "—");
});

test("formatBytes uses binary-ish units with one decimal", () => {
  assert.equal(formatBytes(0), "0 B");
  assert.equal(formatBytes(999), "999 B");
  assert.equal(formatBytes(4_404_019), "4.2 MB");
});

test("statusKind folds the server vocabulary into the UI's status set", () => {
  assert.equal(statusKind({ status: "ready" }), "ready");
  assert.equal(statusKind({ status: "processing" }), "processing");
  assert.equal(statusKind({ status: "failed" }), "failed");
  assert.equal(statusKind({ status: "uploaded", latest_job_state: "running" }), "processing");
  assert.equal(statusKind({ status: "uploaded", latest_job_state: "queued" }), "queued");
  assert.equal(statusKind({ status: "uploaded", latest_job_state: "failed" }), "failed");
  assert.equal(statusKind({ status: "uploaded" }), "unprocessed");
  assert.equal(statusKind({ status: "deleted" }), "deleted");
});

test("statusLabel gives the human wording", () => {
  assert.equal(statusLabel("ready"), "Ready");
  assert.equal(statusLabel("processing"), "Processing");
  assert.equal(statusLabel("queued"), "Queued");
  assert.equal(statusLabel("failed"), "Failed");
  assert.equal(statusLabel("unprocessed"), "Not processed");
});

test("isProcessing is true only for queued/running jobs", () => {
  assert.equal(isProcessing("queued"), true);
  assert.equal(isProcessing("running"), true);
  assert.equal(isProcessing("succeeded"), false);
  assert.equal(isProcessing(null), false);
});

test("describeJobState humanises a pipeline step state map", () => {
  assert.equal(describeJobState({ steps: { transcribe: "succeeded", diarize: "running" } }), "diarize running");
  assert.equal(describeJobState({ steps: { analyze: "queued" } }), "analyze queued");
  assert.equal(describeJobState({ steps: {} }), "");
});

test("friendlyError maps the error envelope codes", () => {
  assert.equal(friendlyError({ code: "not_found" }), "That meeting is not on the server.");
  assert.equal(friendlyError({ code: "session_expired" }), "Your session expired. Sign in again.");
  assert.equal(friendlyError({ code: "unauthorized" }), "Sign in to use the cloud.");
  assert.equal(friendlyError({ code: "no_analysis" }), "There is no current analysis to export yet.");
  assert.equal(friendlyError({ code: "network_error" }), "The service is unreachable.");
  assert.equal(friendlyError({ code: "who_knows" }), "who_knows");
});

test("pagingRange describes the page window", () => {
  assert.deepEqual(pagingRange({ total: 40, limit: 20, offset: 0 }), {
    from: 1,
    to: 20,
    total: 40,
    hasPrev: false,
    hasNext: true,
  });
  assert.deepEqual(pagingRange({ total: 40, limit: 20, offset: 20 }), {
    from: 21,
    to: 40,
    total: 40,
    hasPrev: true,
    hasNext: false,
  });
  assert.deepEqual(pagingRange({ total: 0, limit: 20, offset: 0 }), {
    from: 0,
    to: 0,
    total: 0,
    hasPrev: false,
    hasNext: false,
  });
});
```

Run:

```bash
cd /d/workspace/MeetVault/backend/web
node --test tests/ 2>&1 | tail -5
```

Expected RED: `# fail 12` with `Cannot find module .../lib/format.js`.

**GREEN.** Create `backend/web/lib/format.js`:

```js
// Pure presentation logic for the MeetVault console. No DOM, no fetch — unit-tested by
// `node --test tests/`. Keep it that way: anything that needs a browser belongs in a view.

const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

export function formatDuration(ms) {
  if (ms === null || ms === undefined || Number.isNaN(ms)) return "—";
  const total = Math.max(0, Math.round(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const mm = h > 0 ? String(m).padStart(2, "0") : String(m);
  return `${h > 0 ? `${h}:` : ""}${mm}:${String(s).padStart(2, "0")}`;
}

export function formatElapsed(startedAtIso, nowMs) {
  if (!startedAtIso) return "";
  const started = Date.parse(startedAtIso);
  if (Number.isNaN(started)) return "";
  const secs = Math.max(0, Math.floor(((nowMs ?? Date.now()) - started) / 1000));
  if (secs < 60) return `${secs}s`;
  const m = Math.floor(secs / 60);
  const s = secs % 60;
  if (m < 60) return `${m}m ${s}s`;
  return `${Math.floor(m / 60)}h ${m % 60}m`;
}

export function formatDate(iso) {
  if (!iso) return "—";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "—";
  const pad = (n) => String(n).padStart(2, "0");
  return `${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}, ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}`;
}

export function formatBytes(bytes) {
  if (!bytes) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  const shown = unit === 0 ? String(Math.round(value)) : value.toFixed(1);
  return `${shown} ${units[unit]}`;
}

// Server vocabulary (docs/API.md): status = uploaded|processing|ready|failed|deleted;
// latest_job_state = queued|running|succeeded|failed|cancelled.
export function statusKind(meeting) {
  if (!meeting) return "unprocessed";
  if (meeting.status === "deleted") return "deleted";
  if (meeting.status === "failed") return "failed";
  if (meeting.status === "ready") return "ready";
  const job = meeting.latest_job_state;
  if (job === "running") return "processing";
  if (job === "queued") return "queued";
  if (job === "failed") return "failed";
  return "unprocessed";
}

export function statusLabel(kind) {
  return {
    ready: "Ready",
    processing: "Processing",
    queued: "Queued",
    failed: "Failed",
    unprocessed: "Not processed",
    deleted: "Deleted",
  }[kind] ?? kind;
}

export function isProcessing(state) {
  return state === "queued" || state === "running";
}

export function describeJobState(status) {
  const steps = (status && status.steps) || {};
  const running = Object.entries(steps).find(([, state]) => state === "running");
  if (running) return `${running[0]} running`;
  const queued = Object.entries(steps).find(([, state]) => state === "queued");
  if (queued) return `${queued[0]} queued`;
  return "";
}

const ERROR_TEXT = {
  not_found: "That meeting is not on the server.",
  session_expired: "Your session expired. Sign in again.",
  unauthorized: "Sign in to use the cloud.",
  invalid_credentials: "That email and password do not match.",
  no_analysis: "There is no current analysis to export yet.",
  network_error: "The service is unreachable.",
  payload_too_large: "That file is too large for the server.",
  validation_error: "The server rejected that request.",
};

export function friendlyError(errorBody) {
  const code = errorBody && errorBody.code ? errorBody.code : "error";
  return ERROR_TEXT[code] ?? code;
}

export function pagingRange({ total, limit, offset }) {
  if (!total) return { from: 0, to: 0, total: 0, hasPrev: false, hasNext: false };
  const from = offset + 1;
  const to = Math.min(offset + limit, total);
  return { from, to, total, hasPrev: offset > 0, hasNext: to < total };
}
```

Re-run → expected GREEN: `# pass 12` / `# fail 0`.

Commit: `feat(web): pure formatting + status vocabulary for the console (TDD)`.

---

#### T7 — `web/lib/router.js` — hash routing (TDD, ~7 min)

Hash routing (not history API) on purpose: no server-side catch-all route is needed, so `/docs` and `/v1` keep working untouched.

**RED.** Create `backend/web/tests/router.test.mjs`:

```js
import test from "node:test";
import assert from "node:assert/strict";

import { parseHash, toHash } from "../lib/router.js";

test("an empty or root hash is the meetings list", () => {
  assert.deepEqual(parseHash(""), { name: "meetings", id: null, params: {} });
  assert.deepEqual(parseHash("#/"), { name: "meetings", id: null, params: {} });
  assert.deepEqual(parseHash("#/meetings"), { name: "meetings", id: null, params: {} });
});

test("a meeting route carries the id and the tab", () => {
  assert.deepEqual(parseHash("#/meetings/abc-123"), {
    name: "meeting",
    id: "abc-123",
    params: {},
  });
  assert.deepEqual(parseHash("#/meetings/abc-123?tab=transcript"), {
    name: "meeting",
    id: "abc-123",
    params: { tab: "transcript" },
  });
});

test("login and system are recognised", () => {
  assert.deepEqual(parseHash("#/login"), { name: "login", id: null, params: {} });
  assert.deepEqual(parseHash("#/system"), { name: "system", id: null, params: {} });
});

test("an unknown route falls back to the meetings list", () => {
  assert.deepEqual(parseHash("#/nope/deep"), { name: "meetings", id: null, params: {} });
});

test("toHash builds the canonical form", () => {
  assert.equal(toHash("meetings"), "#/meetings");
  assert.equal(toHash("meeting", { id: "m1" }), "#/meetings/m1");
  assert.equal(toHash("meeting", { id: "m1", params: { tab: "analysis" } }), "#/meetings/m1?tab=analysis");
});
```

Run → expected RED: `Cannot find module .../lib/router.js`.

**GREEN.** Create `backend/web/lib/router.js`:

```js
// Hash routing keeps the SPA server-free: no catch-all route, so /v1, /docs and /openapi.json
// stay exactly as they were. Pure functions -> unit-tested.

const TAB_ROUTES = new Set(["meetings", "login", "system"]);

export function parseHash(hash) {
  const raw = (hash || "").replace(/^#/, "");
  const [pathPart, queryPart] = raw.split("?");
  const segments = pathPart.split("/").filter(Boolean);
  const params = Object.fromEntries(new URLSearchParams(queryPart || ""));
  if (segments.length === 0) return { name: "meetings", id: null, params };
  if (segments[0] === "meetings" && segments[1]) {
    return { name: "meeting", id: decodeURIComponent(segments[1]), params };
  }
  if (TAB_ROUTES.has(segments[0])) return { name: segments[0], id: null, params };
  return { name: "meetings", id: null, params };
}

export function toHash(name, { id, params } = {}) {
  const query = params && Object.keys(params).length ? `?${new URLSearchParams(params)}` : "";
  if (name === "meeting" && id) return `#/meetings/${encodeURIComponent(id)}${query}`;
  return `#/${name}${query}`;
}
```

Re-run → `# pass 5`.

Commit: `feat(web): hash router (no server catch-all needed) (TDD)`.

---

#### T8 — `web/lib/state.js` — session store + poll gate (TDD, ~7 min)

**RED.** Create `backend/web/tests/state.test.mjs`:

```js
import test from "node:test";
import assert from "node:assert/strict";

import { createSessionStore, shouldPoll, POLL_MS } from "../lib/state.js";

function fakeStorage() {
  const map = new Map();
  return {
    getItem: (k) => (map.has(k) ? map.get(k) : null),
    setItem: (k, v) => map.set(k, String(v)),
    removeItem: (k) => map.delete(k),
  };
}

test("the session store round-trips the token and the user", () => {
  const store = createSessionStore(fakeStorage());
  assert.equal(store.getToken(), null);
  store.save("tok-1", { email: "a@b.c", display_name: "A" });
  assert.equal(store.getToken(), "tok-1");
  assert.equal(store.getUser().display_name, "A");
  store.clear();
  assert.equal(store.getToken(), null);
  assert.equal(store.getUser(), null);
});

test("a corrupt user blob does not throw", () => {
  const storage = fakeStorage();
  storage.setItem("mvweb.user", "{not json");
  const store = createSessionStore(storage);
  assert.equal(store.getUser(), null);
});

test("shouldPoll is true while any row has a queued or running job", () => {
  assert.equal(shouldPoll([]), false);
  assert.equal(shouldPoll([{ latest_job_state: "succeeded" }]), false);
  assert.equal(shouldPoll([{ latest_job_state: "succeeded" }, { latest_job_state: "running" }]), true);
  assert.equal(shouldPoll([{ latest_job_state: "queued" }]), true);
});

test("the poll interval is the contract's 2 s", () => {
  assert.equal(POLL_MS, 2000);
});
```

Run → RED (`Cannot find module`).

**GREEN.** Create `backend/web/lib/state.js`:

```js
// Session persistence + the auto-refresh gate. Pure (storage is injected) so node --test can
// exercise it without a browser.

export const POLL_MS = 2000;
const TOKEN_KEY = "mvweb.token";
const USER_KEY = "mvweb.user";

export function createSessionStore(storage) {
  return {
    getToken() {
      return storage.getItem(TOKEN_KEY);
    },
    getUser() {
      try {
        const raw = storage.getItem(USER_KEY);
        return raw ? JSON.parse(raw) : null;
      } catch {
        return null;
      }
    },
    save(token, user) {
      storage.setItem(TOKEN_KEY, token);
      storage.setItem(USER_KEY, JSON.stringify(user ?? {}));
    },
    clear() {
      storage.removeItem(TOKEN_KEY);
      storage.removeItem(USER_KEY);
    },
  };
}

// Auto-refresh only while something is actually moving — an idle console must not hammer the API.
export function shouldPoll(meetings) {
  return (meetings || []).some((m) => m.latest_job_state === "queued" || m.latest_job_state === "running");
}
```

Re-run → `# pass 4`.

Commit: `feat(web): session store + poll gate (TDD)`.

---

#### T9 — `web/lib/api.js` — the contract client (TDD, ~10 min)

**RED.** Create `backend/web/tests/api.test.mjs`:

```js
import test from "node:test";
import assert from "node:assert/strict";

import { ApiError, createApi } from "../lib/api.js";

function jsonResponse(status, body) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
    text: async () => JSON.stringify(body),
    headers: new Headers({ "content-type": "application/json" }),
  };
}

test("a request carries the bearer token and parses JSON", async () => {
  const calls = [];
  const api = createApi({
    fetchImpl: async (url, opts) => {
      calls.push({ url, opts });
      return jsonResponse(200, { items: [], total: 0, limit: 20, offset: 0 });
    },
    onAuthFailure: () => {},
  });
  api.setToken("tok-9");
  const page = await api.listMeetings({ q: "sync" });
  assert.equal(calls[0].url, "/v1/meetings?q=sync");
  assert.equal(calls[0].opts.headers.Authorization, "Bearer tok-9");
  assert.equal(page.total, 0);
});

test("the error envelope becomes an ApiError with its code", async () => {
  const api = createApi({
    fetchImpl: async () => jsonResponse(404, { error: { code: "not_found", message: "Meeting not found" }, request_id: "r1" }),
    onAuthFailure: () => {},
  });
  await assert.rejects(
    () => api.getMeeting("gone"),
    (err) => {
      assert.ok(err instanceof ApiError);
      assert.equal(err.status, 404);
      assert.equal(err.code, "not_found");
      assert.equal(err.requestId, "r1");
      return true;
    },
  );
});

test("a 401 fires the auth-failure hook", async () => {
  let fired = 0;
  const api = createApi({
    fetchImpl: async () => jsonResponse(401, { error: { code: "session_expired", message: "expired" }, request_id: "r2" }),
    onAuthFailure: () => {
      fired += 1;
    },
  });
  await assert.rejects(() => api.me());
  assert.equal(fired, 1);
});

test("a transport failure becomes a network_error ApiError, not a raw TypeError", async () => {
  const api = createApi({
    fetchImpl: async () => {
      throw new TypeError("Failed to fetch");
    },
    onAuthFailure: () => {},
  });
  await assert.rejects(
    () => api.me(),
    (err) => {
      assert.equal(err.code, "network_error");
      assert.equal(err.status, 0);
      return true;
    },
  );
});

test("a 202 process response and an OkResponse DELETE both parse", async () => {
  const bodies = [
    jsonResponse(202, { meeting_id: "m1", steps: ["analyze"], job_ids: ["j1"], enqueued: 1 }),
    jsonResponse(200, { ok: true, detail: {} }),
  ];
  const api = createApi({ fetchImpl: async () => bodies.shift(), onAuthFailure: () => {} });
  const processed = await api.process("m1", { steps: ["analyze"], force: true });
  assert.equal(processed.enqueued, 1);
  const removed = await api.deleteMeeting("m1");
  assert.equal(removed.ok, true);
});
```

Run → RED.

**GREEN.** Create `backend/web/lib/api.js`:

```js
// The MeetVault backend client for the browser. One place that knows the contract in
// backend/docs/API.md: paths, the error envelope, the bearer header.

export class ApiError extends Error {
  constructor(status, envelope) {
    const body = (envelope && envelope.error) || { code: "error", message: "Request failed" };
    super(body.message || body.code);
    this.name = "ApiError";
    this.status = status;
    this.code = body.code;
    this.detail = body.detail || {};
    this.requestId = (envelope && envelope.request_id) || null;
  }
}

export function createApi({ fetchImpl, onAuthFailure, baseUrl = "" } = {}) {
  const doFetch = fetchImpl || globalThis.fetch.bind(globalThis);
  let token = null;
  const fail = onAuthFailure || (() => {});

  async function request(method, path, { body, raw = false, accept } = {}) {
    const headers = {};
    if (token) headers.Authorization = `Bearer ${token}`;
    if (body !== undefined) headers["Content-Type"] = "application/json";
    if (accept) headers.Accept = accept;
    let res;
    try {
      res = await doFetch(baseUrl + path, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
      });
    } catch {
      throw new ApiError(0, { error: { code: "network_error", message: "The service is unreachable." } });
    }
    if (res.status === 401) fail();
    if (!res.ok) {
      let envelope = null;
      try {
        envelope = await res.json();
      } catch {
        envelope = null;
      }
      throw new ApiError(res.status, envelope);
    }
    if (raw) return res;
    if (res.status === 204) return null;
    const text = typeof res.text === "function" ? await res.text() : "";
    return text ? JSON.parse(text) : null;
  }

  const qs = (params) => {
    const search = new URLSearchParams();
    for (const [key, value] of Object.entries(params || {})) {
      if (value !== undefined && value !== null && value !== "") search.set(key, String(value));
    }
    const str = search.toString();
    return str ? `?${str}` : "";
  };

  return {
    setToken(value) {
      token = value || null;
    },
    // Auth
    login: (email, password) => request("POST", "/v1/auth/login", { body: { email, password } }),
    logout: () => request("POST", "/v1/auth/logout"),
    me: () => request("GET", "/v1/auth/me"),
    // Meetings
    listMeetings: (params) => request("GET", `/v1/meetings${qs(params)}`),
    getMeeting: (id) => request("GET", `/v1/meetings/${id}`),
    deleteMeeting: (id) => request("DELETE", `/v1/meetings/${id}`),
    status: (id) => request("GET", `/v1/meetings/${id}/status`),
    process: (id, params) => request("POST", `/v1/meetings/${id}/process${qs(params)}`),
    // Media
    listMedia: (id, params) => request("GET", `/v1/meetings/${id}/media${qs(params)}`),
    mediaInfo: (assetId) => request("GET", `/v1/media/${assetId}/info`),
    deleteMedia: (assetId) => request("DELETE", `/v1/media/${assetId}`),
    async mediaBlob(assetId) {
      const res = await request("GET", `/v1/media/${assetId}`, { raw: true });
      return res.blob();
    },
    // Transcripts
    transcript: (id) => request("GET", `/v1/meetings/${id}/transcript`),
    transcriptText: (id) => request("GET", `/v1/meetings/${id}/transcript/text`, { accept: "text/plain" }),
    transcripts: (id, params) => request("GET", `/v1/meetings/${id}/transcripts${qs(params)}`),
    activateTranscript: (tid) => request("POST", `/v1/transcripts/${tid}/activate`),
    deleteTranscript: (tid) => request("DELETE", `/v1/transcripts/${tid}`),
    // Speakers
    speakers: (id) => request("GET", `/v1/meetings/${id}/speakers`),
    renameSpeaker: (sid, label) => request("PATCH", `/v1/speakers/${sid}`, { body: { label } }),
    mergeSpeaker: (sid, intoId) => request("POST", `/v1/speakers/${sid}/merge`, { body: { into_id: intoId } }),
    deleteSpeaker: (sid) => request("DELETE", `/v1/speakers/${sid}`),
    // Analysis
    analysis: (id) => request("GET", `/v1/meetings/${id}/analysis`),
    qa: (id) => request("GET", `/v1/meetings/${id}/qa`),
    ask: (id, question, sectionId) =>
      request("POST", `/v1/meetings/${id}/qa`, { body: { question, top_k: 8, section_id: sectionId ?? null } }),
    analysisRuns: (id) => request("GET", `/v1/meetings/${id}/analysis/runs`),
    // Jobs
    jobs: (id) => request("GET", `/v1/meetings/${id}/jobs`),
    job: (jobId) => request("GET", `/v1/jobs/${jobId}`),
    cancelJob: (jobId) => request("POST", `/v1/jobs/${jobId}/cancel`),
    // Export + system
    exportUrl: (id) => `/v1/meetings/${id}/export.docx`,
    async exportDocx(id) {
      const res = await request("GET", `/v1/meetings/${id}/export.docx`, { raw: true });
      return res.blob();
    },
    systemInfo: () => request("GET", "/v1/system/info"),
    providers: () => request("GET", "/v1/system/providers"),
  };
}
```

Re-run → `# pass 5`.

**Field-name audit before committing:** open `backend/src/mvb/schemas/speakers.py` and confirm the PATCH body field and the merge body field. The plan asserts `label` and `into_id`. If either differs (the API map says `PATCH /v1/speakers/{id}` takes `label` and `POST /v1/speakers/{id}/merge` folds "into another"), fix `api.js` to the real names **and** note the correction in the commit message. Never ship a guessed field.

Commit: `feat(web): backend API client (TDD)`.

---

#### T10 — `web/lib/dom.js` — a 30-line element helper (~5 min)

Create `backend/web/lib/dom.js` (DOM-only, so covered by the E2E suite rather than node --test):

```js
// Minimal element builder. Keeps the views readable without a framework and without innerHTML
// (which the page CSP forbids anyway).

export function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props || {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key === "class") node.className = value;
    else if (key === "text") node.textContent = value;
    else if (key === "dataset") Object.assign(node.dataset, value);
    else if (key === "style") Object.assign(node.style, value); // CSSOM: allowed under style-src 'self'
    else if (key.startsWith("on") && typeof value === "function") node.addEventListener(key.slice(2), value);
    else if (key in node) node[key] = value;
    else node.setAttribute(key, value === true ? "" : String(value));
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

export function clear(node) {
  while (node.firstChild) node.removeChild(node.firstChild);
  return node;
}

// XSS-safe text join for server-provided strings.
export function textList(items, separator = " · ") {
  return (items || []).map((item) => String(item)).join(separator);
}
```

**Verify:** `node --check backend/web/lib/dom.js` → no output, exit 0. (`node --check` does not run the DOM code, only parses it.)

Commit: `feat(web): DOM element helper (no innerHTML, CSP-safe)`.

---

#### T11 — `web/app.css` — the modern UI (~12 min)

Create `backend/web/app.css` with the full content below. It mirrors the desktop's Fluent-ish language: card surfaces, one accent, light/dark from `prefers-color-scheme`, a rounded left rail on wide screens that collapses to a top bar on narrow ones, and a status pill per state.

```css
/* MeetVault console — one file, no build step. Tokens follow the desktop client's palette. */
:root {
  --bg: #f3f3f6;
  --surface: #ffffff;
  --surface-2: #fafafd;
  --border: #e2e2ea;
  --text: #1a1a1f;
  --muted: #5d5d6b;
  --accent: #0f6cbd;
  --accent-fg: #ffffff;
  --accent-soft: #e7f1fb;
  --danger: #c42b1c;
  --ok: #0f7b0f;
  --warn: #9d5d00;
  --radius: 10px;
  --radius-sm: 6px;
  --shadow: 0 1px 2px rgba(16, 16, 24, 0.06), 0 8px 24px rgba(16, 16, 24, 0.06);
  --rail: 216px;
}

@media (prefers-color-scheme: dark) {
  :root {
    --bg: #17171b;
    --surface: #212126;
    --surface-2: #26262c;
    --border: #383840;
    --text: #f2f2f5;
    --muted: #a9a9b6;
    --accent: #5aa9f0;
    --accent-fg: #0d1b2a;
    --accent-soft: #1d3145;
    --danger: #ff9aa3;
    --ok: #6ccb5f;
    --warn: #ffd166;
    --shadow: 0 1px 2px rgba(0, 0, 0, 0.5), 0 8px 24px rgba(0, 0, 0, 0.35);
  }
}

* { box-sizing: border-box; }

html, body { height: 100%; }

body {
  margin: 0;
  background: var(--bg);
  color: var(--text);
  font: 15px/1.5 "Segoe UI Variable Text", "Segoe UI", system-ui, -apple-system, sans-serif;
}

a { color: var(--accent); text-decoration: none; }
a:hover { text-decoration: underline; }

/* ---------- shell ---------- */
.app-shell { display: grid; grid-template-columns: var(--rail) 1fr; min-height: 100vh; }

.rail {
  background: var(--surface);
  border-right: 1px solid var(--border);
  padding: 20px 14px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.brand { display: flex; align-items: baseline; gap: 8px; padding: 0 8px 16px; }
.brand-mark {
  display: inline-flex; align-items: center; justify-content: center;
  width: 28px; height: 28px; border-radius: 8px;
  background: var(--accent); color: var(--accent-fg);
  font-weight: 700; font-size: 12px;
}
.brand-name { font-weight: 600; }
.brand-sub { color: var(--muted); font-size: 13px; }

.rail nav { display: flex; flex-direction: column; gap: 2px; }
.rail nav a {
  padding: 9px 10px; border-radius: var(--radius-sm); color: var(--text); font-weight: 500;
}
.rail nav a:hover { background: var(--surface-2); text-decoration: none; }
.rail nav a[aria-current="page"] { background: var(--accent-soft); color: var(--accent); }

.rail footer { margin-top: auto; padding: 10px 8px 0; border-top: 1px solid var(--border); }

.account { display: flex; flex-direction: column; gap: 2px; padding: 4px 0 0; }
.account .who { font-weight: 600; font-size: 13px; overflow-wrap: anywhere; }
.account .mail { color: var(--muted); font-size: 12px; overflow-wrap: anywhere; }

main.view { padding: 28px 32px 64px; max-width: 1240px; }

h1 { font-size: 26px; margin: 0 0 4px; font-weight: 600; }
h2 { font-size: 18px; margin: 0 0 12px; font-weight: 600; }
h3 { font-size: 15px; margin: 0 0 8px; font-weight: 600; }
p.sub { color: var(--muted); margin: 0 0 20px; }

/* ---------- controls ---------- */
button, .btn {
  font: inherit; font-weight: 500;
  background: var(--surface); color: var(--text);
  border: 1px solid var(--border); border-radius: var(--radius-sm);
  padding: 7px 14px; cursor: pointer;
}
button:hover, .btn:hover { background: var(--surface-2); }
button.primary, .btn.primary { background: var(--accent); border-color: var(--accent); color: var(--accent-fg); }
button.primary:hover { filter: brightness(1.06); }
button.danger { color: var(--danger); }
button.ghost { background: transparent; border-color: transparent; }
button:disabled { opacity: 0.5; cursor: default; }
button[aria-busy="true"] { cursor: progress; }

input[type="text"], input[type="search"], input[type="email"], input[type="password"], select, textarea {
  font: inherit; color: var(--text); background: var(--surface);
  border: 1px solid var(--border); border-radius: var(--radius-sm); padding: 7px 10px;
}
input:focus-visible, select:focus-visible, textarea:focus-visible, button:focus-visible, a:focus-visible {
  outline: 2px solid var(--accent); outline-offset: 1px;
}
label.field { display: flex; flex-direction: column; gap: 5px; font-size: 13px; color: var(--muted); }

.toolbar { display: flex; flex-wrap: wrap; gap: 10px; align-items: center; margin-bottom: 18px; }
.toolbar .spacer { flex: 1; }

/* ---------- cards / rows ---------- */
.card {
  background: var(--surface); border: 1px solid var(--border);
  border-radius: var(--radius); padding: 16px; box-shadow: var(--shadow);
}
.card + .card { margin-top: 14px; }

.rows { display: flex; flex-direction: column; gap: 10px; }
.row {
  display: grid; grid-template-columns: 1fr auto; gap: 14px; align-items: center;
  background: var(--surface); border: 1px solid var(--border);
  border-radius: var(--radius); padding: 14px 16px;
}
.row:hover { border-color: var(--accent); }
.row .title { font-weight: 600; overflow-wrap: anywhere; }
.row .meta { color: var(--muted); font-size: 13px; }
.row .status { font-size: 13px; margin-top: 4px; display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.row .actions { display: flex; gap: 8px; align-items: center; }

.pill {
  display: inline-flex; align-items: center; gap: 5px;
  padding: 2px 9px; border-radius: 999px; font-size: 12px; font-weight: 600;
  border: 1px solid var(--border); background: var(--surface-2);
}
.pill[data-kind="ready"] { color: var(--ok); border-color: currentColor; }
.pill[data-kind="processing"], .pill[data-kind="queued"] { color: var(--accent); border-color: currentColor; }
.pill[data-kind="failed"] { color: var(--danger); border-color: currentColor; }
.pill[data-kind="unprocessed"] { color: var(--muted); }
.pill.mock { color: var(--warn); border-color: currentColor; }
.pill.stale { color: var(--warn); border-color: currentColor; }

.progress { height: 6px; background: var(--surface-2); border: 1px solid var(--border); border-radius: 999px; overflow: hidden; }
.progress > span { display: block; height: 100%; background: var(--accent); width: 0; }
.progress[data-indeterminate="true"] > span { width: 35%; animation: slide 1.4s ease-in-out infinite; }
@keyframes slide { 0% { margin-left: -35%; } 100% { margin-left: 100%; } }

.empty { color: var(--muted); padding: 32px 4px; }

/* ---------- tabs ---------- */
.tabs { display: flex; gap: 4px; border-bottom: 1px solid var(--border); margin: 18px 0 18px; flex-wrap: wrap; }
.tabs button {
  border: none; background: transparent; border-radius: 0; padding: 8px 12px;
  border-bottom: 2px solid transparent; color: var(--muted); font-weight: 500;
}
.tabs button[aria-selected="true"] { color: var(--accent); border-bottom-color: var(--accent); }

.grid-2 { display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: 14px; }

dl.facts { display: grid; grid-template-columns: max-content 1fr; gap: 6px 16px; margin: 0; }
dl.facts dt { color: var(--muted); font-size: 13px; }
dl.facts dd { margin: 0; }

table.segments { width: 100%; border-collapse: collapse; font-size: 14px; }
table.segments th, table.segments td { text-align: left; padding: 7px 8px; border-bottom: 1px solid var(--border); vertical-align: top; }
table.segments th { color: var(--muted); font-weight: 600; font-size: 12px; text-transform: uppercase; letter-spacing: 0.04em; }
table.segments tr.active { background: var(--accent-soft); }
table.segments td.time { color: var(--muted); white-space: nowrap; font-variant-numeric: tabular-nums; cursor: pointer; }
table.segments td.speaker { white-space: nowrap; font-weight: 600; }

ul.bullets { margin: 0; padding-left: 20px; }
ul.bullets li { margin-bottom: 6px; }

.citation { font-size: 12px; color: var(--muted); margin-top: 4px; }
.citation button { padding: 1px 7px; font-size: 12px; }

.player audio, .player video { width: 100%; }

.toast {
  position: fixed; left: 50%; bottom: 28px; transform: translateX(-50%);
  background: var(--surface); color: var(--text); border: 1px solid var(--border);
  border-radius: var(--radius); padding: 10px 16px; box-shadow: var(--shadow); z-index: 20;
}

dialog {
  border: 1px solid var(--border); border-radius: var(--radius); padding: 20px;
  background: var(--surface); color: var(--text); box-shadow: var(--shadow); max-width: 460px;
}
dialog::backdrop { background: rgba(0, 0, 0, 0.45); }
dialog .dialog-actions { display: flex; justify-content: flex-end; gap: 10px; margin-top: 18px; }

.banner { border-left: 3px solid var(--warn); background: var(--surface-2); padding: 10px 14px; border-radius: var(--radius-sm); margin-bottom: 14px; }
.banner.error { border-left-color: var(--danger); }

/* ---------- sign-in ---------- */
.signin { display: grid; place-items: center; min-height: 100vh; padding: 24px; }
.signin .card { width: min(400px, 100%); display: flex; flex-direction: column; gap: 14px; }

/* ---------- responsive ---------- */
@media (max-width: 860px) {
  .app-shell { grid-template-columns: 1fr; }
  .rail { flex-direction: row; align-items: center; gap: 12px; border-right: none; border-bottom: 1px solid var(--border); overflow-x: auto; }
  .rail nav { flex-direction: row; }
  .rail footer { margin: 0; border: none; padding: 0; }
  main.view { padding: 18px 16px 48px; }
  .row { grid-template-columns: 1fr; }
}
```

**Verify:** `node --check backend/web/app.css` fails (it is not JS — expected). Instead verify by loading it in T13's smoke test; for now just check the file exists and is non-empty:

```bash
wc -c backend/web/app.css   # expect > 4000
```

Commit: `feat(web): console stylesheet (light/dark Fluent-ish tokens, responsive rail)`.

---

#### T12 — `web/index.html` — the final shell (~6 min)

Replace `backend/web/index.html` with the real shell:

```html
<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <meta name="color-scheme" content="light dark" />
    <title>MeetVault — Cloud</title>
    <link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'%3E%3Crect width='32' height='32' rx='7' fill='%230f6cbd'/%3E%3Ctext x='16' y='22' font-family='Arial' font-size='14' font-weight='bold' fill='white' text-anchor='middle'%3EMV%3C/text%3E%3C/svg%3E" />
    <link rel="stylesheet" href="/assets/app.css" />
  </head>
  <body>
    <div id="app" class="app-shell"></div>
    <div id="toast" class="toast" role="status" aria-live="polite" hidden></div>
    <script type="module" src="/assets/app.js"></script>
  </body>
</html>
```

Ignore the HTML formatting drift; the only hard requirements are: the `data:` favicon (no extra request, CSP-safe via `img-src 'self' data:`), the stylesheet and the module script at `/assets/…`, and the two mount points `#app` / `#toast`.

Commit: `feat(web): console shell markup`.

---

### Phase C — the screens

Every view follows the same shape: an exported `render<View>(ctx)` that builds DOM with `el()` and returns it. `ctx` = `{ api, store, navigate, toast, session }`.

#### T13 — `web/app.js` — boot, session, routing (~12 min)

Create `backend/web/app.js`:

```js
// Console entry point: session bootstrap, rail, hash routing, view mounting.
import { createApi, ApiError } from "./lib/api.js";
import { createSessionStore } from "./lib/state.js";
import { parseHash, toHash } from "./lib/router.js";
import { el, clear } from "./lib/dom.js";
import { friendlyError } from "./lib/format.js";
import { renderSignIn } from "./views/signin.js";
import { renderMeetings } from "./views/meetings.js";
import { renderMeeting } from "./views/meeting.js";
import { renderSystem } from "./views/system.js";

const store = createSessionStore(window.localStorage);
const api = createApi({ onAuthFailure: () => hardSignOut("session_expired") });
api.setToken(store.getToken());

const toastNode = document.getElementById("toast");
let toastTimer = null;
function toast(message) {
  toastNode.textContent = message;
  toastNode.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => {
    toastNode.hidden = true;
  }, 4200);
}

function navigate(name, options) {
  window.location.hash = toHash(name, options);
}

function hardSignOut(reason) {
  store.clear();
  api.setToken(null);
  if (reason) toast(friendlyError({ code: reason }));
  navigate("login");
}

const ctx = { api, store, navigate, toast, session: null, signOut: () => hardSignOut(null) };

function renderRail() {
  const shell = document.getElementById("app");
  clear(shell);
  const route = parseHash(window.location.hash);
  const user = ctx.session?.user;
  const nav = el(
    "nav",
    {},
    el("a", { href: "#/meetings", dataset: { nav: "meetings" }, "aria-current": route.name === "meeting" || route.name === "meetings" ? "page" : null }, "Meetings"),
    el("a", { href: "#/system", dataset: { nav: "system" }, "aria-current": route.name === "system" ? "page" : null }, "Service"),
  );
  const footer = el(
    "footer",
    {},
    user
      ? el(
          "div",
          { class: "account" },
          el("span", { class: "who", text: user.display_name || user.email }),
          el("span", { class: "mail", text: user.email }),
          el("button", { class: "ghost", type: "button", onclick: async () => {
            try { await api.logout(); } catch { /* revoking a dead token is not an error */ }
            hardSignOut(null);
            toast("Signed out.");
          } }, "Sign out"),
        )
      : el("a", { href: "#/login" }, "Sign in"),
  );
  const rail = el(
    "aside",
    { class: "rail" },
    el("div", { class: "brand" }, el("span", { class: "brand-mark", text: "MV" }), el("span", { class: "brand-name", text: "MeetVault" }), el("span", { class: "brand-sub", text: "Cloud" })),
    nav,
    footer,
  );
  const main = el("main", { class: "view", id: "view", "aria-live": "polite" });
  shell.append(rail, main);
  return main;
}

async function renderRoute() {
  const route = parseHash(window.location.hash);
  const main = renderRail();
  try {
    if (!ctx.session && store.getToken()) {
      ctx.session = await api.me();
    }
  } catch {
    ctx.session = null;
  }
  if (route.name === "login" || (!ctx.session && route.name !== "system")) {
    main.append(renderSignIn(ctx));
    return;
  }
  if (route.name === "meeting" && route.id) {
    main.append(await renderMeeting(ctx, route));
    return;
  }
  if (route.name === "system") {
    main.append(await renderSystem(ctx));
    return;
  }
  main.append(await renderMeetings(ctx, route));
}

export async function signIn(email, password) {
  const result = await api.login(email, password);
  store.save(result.token, result.user);
  api.setToken(result.token);
  ctx.session = { user: result.user, expires_at: result.expires_at };
  return result;
}

window.addEventListener("hashchange", () => {
  renderRoute().catch((err) => {
    const main = document.getElementById("view");
    if (main) main.append(el("div", { class: "banner error", text: err instanceof ApiError ? friendlyError(err) : String(err) }));
  });
});

renderRoute().catch((err) => {
  document.getElementById("app").append(el("div", { class: "banner error", text: String(err) }));
});
```

Note for the implementer: `renderSignIn` needs to trigger a re-render after a successful login, so `views/signin.js` calls `ctx.navigate("meetings")` **and** `window.location.reload()`-free re-render by calling `signIn` then `ctx.reload?.()`. To keep this simple and honest, export a `reload` on `ctx` from `app.js`:

```js
ctx.reload = () => renderRoute();
```

Commit: `feat(web): console boot, session bootstrap and routing`.

---

#### T14 — `web/views/signin.js` (~7 min)

Create `backend/web/views/signin.js`:

```js
import { el } from "../lib/dom.js";
import { friendlyError } from "../lib/format.js";

export function renderSignIn(ctx) {
  const error = el("div", { class: "banner error", hidden: true });
  const email = el("input", { type: "email", id: "email", autocomplete: "username", required: true });
  const password = el("input", { type: "password", id: "password", autocomplete: "current-password", required: true });
  const submit = el("button", { class: "primary", type: "submit" }, "Sign in");
  const form = el(
    "form",
    {
      onsubmit: async (event) => {
        event.preventDefault();
        error.hidden = true;
        submit.disabled = true;
        submit.setAttribute("aria-busy", "true");
        try {
          const { signIn } = await import("../app.js");
          await signIn(email.value.trim(), password.value);
          ctx.toast("Signed in.");
          ctx.reload();
        } catch (err) {
          const code = err && err.code ? err.code : "error";
          error.textContent = friendlyError({ code });
          error.hidden = false;
        } finally {
          submit.disabled = false;
          submit.removeAttribute("aria-busy");
        }
      },
    },
    el("h2", {}, "Sign in to the cloud"),
    el("p", { class: "sub" }, "Your recordings and analysis live on this server. The desktop app uses the same account."),
    error,
    el("label", { class: "field" }, "Email", email),
    el("label", { class: "field" }, "Password", password),
    submit,
  );
  return el("div", { class: "signin" }, el("div", { class: "card" }, form));
}
```

**Verification for T14 (first live check of the shell):** run the dev server and load the page in the E2E harness later; for now the check is a manual one-shot with curl + a headless browser in T28. Do not skip it there.

Commit: `feat(web): sign-in screen`.

---

#### T15 — `web/views/meetings.js` — the Cloud Storage list (~14 min)

This is the parity core (matrix rows 2–7, 19, 20). Create `backend/web/views/meetings.js`:

```js
import { el, clear } from "../lib/dom.js";
import { formatDate, formatDuration, formatElapsed, statusKind, statusLabel, describeJobState, friendlyError, pagingRange, isProcessing } from "../lib/format.js";
import { shouldPoll, POLL_MS } from "../lib/state.js";

const PAGE_SIZE = 20;

export async function renderMeetings(ctx, route) {
  const state = {
    q: route.params.q || "",
    status: route.params.status || "",
    offset: Number(route.params.offset || 0),
    page: null,
    statusById: {},
    pollTimer: null,
    disposed: false,
  };

  const rows = el("div", { class: "rows" });
  const summary = el("p", { class: "sub", "data-testid": "page-summary" });
  const listCard = el("div", { class: "card" }, rows, summary);
  const staleBanner = el("div", { class: "banner", hidden: true, "data-testid": "stale-banner" });

  const search = el("input", {
    type: "search",
    placeholder: "Search titles…",
    value: state.q,
    "aria-label": "Search meetings",
    onchange: (event) => {
      state.q = event.target.value.trim();
      state.offset = 0;
      ctx.navigate("meetings", { params: params() });
    },
  });
  const statusFilter = el(
    "select",
    {
      "aria-label": "Filter by status",
      onchange: (event) => {
        state.status = event.target.value;
        state.offset = 0;
        ctx.navigate("meetings", { params: params() });
      },
    },
    ...[["", "All statuses"], ["ready", "Ready"], ["processing", "Processing"], ["uploaded", "Uploaded"], ["failed", "Failed"]].map(([value, label]) =>
      el("option", { value, selected: state.status === value }, label),
    ),
  );
  const refresh = el("button", { type: "button", onclick: () => load(true) }, "Refresh");
  const toolbar = el("div", { class: "toolbar" }, search, statusFilter, refresh);

  function params() {
    const out = {};
    if (state.q) out.q = state.q;
    if (state.status) out.status = state.status;
    if (state.offset) out.offset = String(state.offset);
    return out;
  }

  function renderRow(meeting) {
    const kind = statusKind(meeting);
    const status = state.statusById[meeting.id] || null;
    const elapsed = status && status.active_job_started_at ? formatElapsed(status.active_job_started_at, Date.now()) : "";
    const step = status ? describeJobState(status) : "";
    const progressText = status && typeof status.progress === "number" && isProcessing(meeting.latest_job_state) ? `${Math.round((status.progress || 0) * 100)}%` : "";
    const statusBits = [statusLabel(kind), step, progressText, elapsed].filter(Boolean);
    const badges = [];
    if (meeting.has_analysis) badges.push(el("span", { class: "pill", text: "Analyzed" }));
    if (status && status.mock) badges.push(el("span", { class: "pill mock", "data-testid": "mock-badge", text: "Mock data" }));
    if (status && status.stale_kinds && status.stale_kinds.length) {
      badges.push(el("span", { class: "pill stale", "data-testid": "stale-pill", text: `Outdated: ${status.stale_kinds.join(", ")}` }));
    }
    return el(
      "div",
      { class: "row", dataset: { row: meeting.id, kind } },
      el(
        "div",
        {},
        el("div", { class: "title", text: meeting.title }),
        el("div", { class: "meta", text: `${formatDate(meeting.created_at)} · ${formatDuration(meeting.duration_ms)} · ${meeting.language || "auto"}` }),
        el("div", { class: "status" }, el("span", { class: "pill", dataset: { kind }, "data-testid": "status", text: statusBits.join(" · ") }), ...badges),
      ),
      el(
        "div",
        { class: "actions" },
        el("button", { class: "primary", type: "button", dataset: { action: "open", id: meeting.id }, onclick: () => ctx.navigate("meeting", { id: meeting.id }) }, "Open"),
        el("button", { class: "danger", type: "button", dataset: { action: "remove", id: meeting.id }, onclick: () => confirmRemove(meeting) }, "Remove from cloud"),
      ),
    );
  }

  function confirmRemove(meeting) {
    const dialog = el(
      "dialog",
      { "data-testid": "remove-dialog" },
      el("h3", {}, "Remove from cloud?"),
      el("p", {}, `“${meeting.title}” will be deleted from the server, including its recordings on the server. The local recording on this machine is not touched.`),
      el(
        "div",
        { class: "dialog-actions" },
        el("button", { type: "button", class: "cancel", onclick: () => dialog.close() }, "Cancel"),
        el(
          "button",
          {
            type: "button",
            class: "primary confirm",
            onclick: async () => {
              try {
                await ctx.api.deleteMeeting(meeting.id);
                dialog.close();
                ctx.toast("Removed from cloud.");
                await load(true);
              } catch (err) {
                dialog.close();
                ctx.toast(friendlyError(err));
              }
            },
          },
          "Remove",
        ),
      ),
    );
    document.body.append(dialog);
    dialog.addEventListener("close", () => dialog.remove());
    dialog.showModal();
  }

  function schedulePoll() {
    clearTimeout(state.pollTimer);
    if (state.disposed) return;
    const anyProcessing = shouldPoll(state.page?.items || []);
    if (!anyProcessing) return;
    state.pollTimer = setTimeout(() => load(false), POLL_MS);
  }

  async function load(showSpinner) {
    if (state.disposed) return;
    if (showSpinner) refresh.disabled = true;
    try {
      const page = await ctx.api.listMeetings({ q: state.q, status: state.status, limit: PAGE_SIZE, offset: state.offset });
      state.page = page;
      // Elapsed time and progress live on /status, not on the list payload: fetch it for the
      // rows that are actually moving (bounded fan-out).
      const moving = page.items.filter((m) => isProcessing(m.latest_job_state));
      const statuses = await Promise.all(
        moving.map((m) =>
          ctx.api.status(m.id).then(
            (value) => [m.id, value],
            () => [m.id, null],
          ),
        ),
      );
      state.statusById = Object.fromEntries(statuses);
      clear(rows);
      const stale = Object.values(state.statusById).flatMap((s) => (s ? s.stale_kinds : [])).filter(Boolean);
      if (stale.length) {
        staleBanner.textContent = `Some analysis is out of date for the material that is now active: ${[...new Set(stale)].join(", ")}. Re-run analysis to refresh it.`;
        staleBanner.hidden = false;
      } else {
        staleBanner.hidden = true;
      }
      if (!page.items.length) {
        rows.append(el("div", { class: "empty", "data-testid": "empty", text: state.q || state.status ? "No meetings match that filter." : "No meetings on the server yet. Send one from the desktop app." }));
      } else {
        for (const meeting of page.items) rows.append(renderRow(meeting));
      }
      const range = pagingRange(page);
      summary.textContent = `${range.from}–${range.to} of ${range.total}`;
    } catch (err) {
      rows.append(el("div", { class: "banner error", text: friendlyError(err) }));
    } finally {
      refresh.disabled = false;
      schedulePoll();
    }
  }

  const view = el(
    "div",
    {},
    el("h1", {}, "Meetings in the cloud"),
    el("p", { class: "sub" }, "The server is the only source of truth here: meetings recorded locally but never uploaded do not appear."),
    toolbar,
    staleBanner,
    listCard,
  );

  // Stop polling when the view is replaced.
  window.addEventListener(
    "hashchange",
    () => {
      state.disposed = true;
      clearTimeout(state.pollTimer);
    },
    { once: true },
  );

  await load(true);
  return view;
}
```

Commit: `feat(web): cloud meetings list (search, filter, auto-refresh, remove)`.

---

#### T16 — `web/views/meeting.js` — detail with tabs (~20 min)

Matrix rows 6, 8–18. Create `backend/web/views/meeting.js`. Structure it exactly like this (tabs are rendered by a small local helper so each tab is independently testable in the E2E suite):

```js
import { el, clear, textList } from "../lib/dom.js";
import {
  formatDate, formatDuration, formatElapsed, statusKind, statusLabel, describeJobState,
  friendlyError, isProcessing, formatBytes,
} from "../lib/format.js";

const TABS = ["overview", "transcript", "analysis", "qa", "speakers", "versions", "jobs"];

export async function renderMeeting(ctx, route) {
  const meetingId = route.id;
  const state = {
    tab: TABS.includes(route.params.tab) ? route.params.tab : "overview",
    meeting: null,
    status: null,
    blobUrls: [],
    disposed: false,
    pollTimer: null,
  };

  const head = el("div", {});
  const body = el("div", { "data-testid": "tab-body" });

  function releaseBlobs() {
    for (const url of state.blobUrls) URL.revokeObjectURL(url);
    state.blobUrls = [];
  }

  // A media element cannot send the Authorization header, so play/download works off a blob
  // fetched with the header (see backend/docs/API.md "Browser playback").
  async function blobFor(assetId) {
    const blob = await ctx.api.mediaBlob(assetId);
    const url = URL.createObjectURL(blob);
    state.blobUrls.push(url);
    return { url, blob };
  }

  async function loadStatus() {
    state.status = await ctx.api.status(meetingId).catch(() => null);
  }

  function schedulePoll() {
    clearTimeout(state.pollTimer);
    if (state.disposed) return;
    if (!state.status || !isProcessing(state.meeting?.latest_job_state)) return;
    state.pollTimer = setTimeout(async () => {
      await Promise.all([loadHead(), renderTab()]);
      schedulePoll();
    }, 2000);
  }

  async function loadHead() {
    state.meeting = await ctx.api.getMeeting(meetingId);
    await loadStatus();
    clear(head);
    const kind = statusKind(state.meeting);
    const status = state.status || {};
    const step = describeJobState(status);
    const elapsed = status.active_job_started_at ? formatElapsed(status.active_job_started_at, Date.now()) : "";
    head.append(
      el("h1", { text: state.meeting.title }),
      el("p", { class: "sub", text: `${formatDate(state.meeting.created_at)} · ${formatDuration(state.meeting.duration_ms)} · ${state.meeting.language || "auto"} · ${state.meeting.media_count} recording(s), ${state.meeting.transcript_count} transcript version(s), ${state.meeting.speaker_count} speaker(s)` }),
      el(
        "div",
        { class: "status" },
        el("span", { class: "pill", dataset: { kind }, "data-testid": "status", text: [statusLabel(kind), step, elapsed].filter(Boolean).join(" · ") }),
        state.meeting.has_analysis ? el("span", { class: "pill", text: "Analyzed" }) : null,
        status.mock ? el("span", { class: "pill mock", "data-testid": "mock-badge", text: "Mock data" }) : null,
      ),
      el("div", { class: "toolbar" }, el("button", { type: "button", dataset: { action: "rerun" }, onclick: () => rerun() }, "Re-run analysis"), el("button", { type: "button", dataset: { action: "export" }, onclick: () => exportDocx() }, "Export to Word"), el("button", { class: "danger", type: "button", dataset: { action: "remove" }, onclick: () => removeMeeting() }, "Remove from cloud")),
      status.error ? el("div", { class: "banner error", "data-testid": "job-error", text: `Last job failed: ${status.error}` }) : null,
      status.stale_kinds && status.stale_kinds.length ? el("div", { class: "banner", "data-testid": "stale-banner", text: `Out of date: ${status.stale_kinds.join(", ")} — re-run analysis to refresh.` }) : null,
    );
  }

  async function rerun() {
    try {
      await ctx.api.process(meetingId, { steps: "analyze", force: true });
      ctx.toast("Analysis queued.");
      await loadHead();
      await renderTab();
      schedulePoll();
    } catch (err) {
      ctx.toast(friendlyError(err));
    }
  }

  async function exportDocx() {
    try {
      const blob = await ctx.api.exportDocx(meetingId);
      const url = URL.createObjectURL(blob);
      state.blobUrls.push(url);
      const anchor = el("a", { href: url, download: `${state.meeting.title.replace(/[^\w\-]+/g, "-")}.docx` });
      document.body.append(anchor);
      anchor.click();
      anchor.remove();
      ctx.toast("Word export downloaded.");
    } catch (err) {
      ctx.toast(friendlyError(err));
    }
  }

  function removeMeeting() {
    const dialog = el(
      "dialog",
      { "data-testid": "remove-dialog" },
      el("h3", {}, "Remove from cloud?"),
      el("p", {}, "The meeting, its server recordings and its analysis are deleted from the server. The local recording is not touched."),
      el(
        "div",
        { class: "dialog-actions" },
        el("button", { type: "button", class: "cancel", onclick: () => dialog.close() }, "Cancel"),
        el("button", { type: "button", class: "primary confirm", onclick: async () => {
          try {
            await ctx.api.deleteMeeting(meetingId);
            dialog.close();
            ctx.toast("Removed from cloud.");
            ctx.navigate("meetings");
          } catch (err) { dialog.close(); ctx.toast(friendlyError(err)); }
        } }, "Remove"),
      ),
    );
    document.body.append(dialog);
    dialog.addEventListener("close", () => dialog.remove());
    dialog.showModal();
  }

  function tabButton(name) {
    return el("button", { type: "button", role: "tab", dataset: { tab: name }, "aria-selected": state.tab === name ? "true" : "false", onclick: () => { state.tab = name; renderTab(); } }, name[0].toUpperCase() + name.slice(1));
  }

  const tabs = el("div", { class: "tabs", role: "tablist" }, ...TABS.map(tabButton));

  async function renderTab() {
    releaseBlobs();
    clear(body);
    for (const button of tabs.querySelectorAll("button")) button.setAttribute("aria-selected", button.dataset.tab === state.tab ? "true" : "false");
    const panels = { overview, transcript, analysis, qa, speakers, versions, jobs };
    const panel = el("div", {});
    try {
      panel.append(await panels[state.tab]());
    } catch (err) {
      panel.append(el("div", { class: "banner error", text: friendlyError(err) }));
    }
    body.append(panel);
  }
  ...
}
```

The seven panel functions are spelled out in the **appendix (A1)** at the end of this plan — copy them into the same file, above `renderMeeting`'s closing brace (they are closures over `ctx`, `state`, `meetingId`, `blobFor`). **Do not invent them**: the appendix is the specification.

Finally, the boot of this view:

```js
  const view = el("div", {}, head, tabs, body);
  window.addEventListener("hashchange", () => { state.disposed = true; clearTimeout(state.pollTimer); releaseBlobs(); }, { once: true });
  await loadHead();
  await renderTab();
  schedulePoll();
  return view;
}
```

Commit: `feat(web): meeting detail with transcript, analysis, Q&A, speakers, versions and jobs`.

---

#### T17 — `web/views/system.js` — the service tab (matrix row 21, ~6 min)

Create `backend/web/views/system.js`:

```js
import { el } from "../lib/dom.js";
import { friendlyError } from "../lib/format.js";

export async function renderSystem(ctx) {
  const view = el("div", {}, el("h1", {}, "Service"), el("p", { class: "sub" }, "Which engines this server is actually running."));
  try {
    const [info, providers] = await Promise.all([ctx.api.systemInfo(), ctx.api.providers()]);
    const facts = el("dl", { class: "facts", "data-testid": "system-facts" });
    for (const [key, value] of Object.entries(info || {})) {
      facts.append(el("dt", { text: key }), el("dd", { text: typeof value === "object" ? JSON.stringify(value) : String(value) }));
    }
    const providerRows = el("div", { class: "rows", "data-testid": "providers" });
    const list = Array.isArray(providers?.providers) ? providers.providers : Array.isArray(providers) ? providers : [];
    for (const provider of list) {
      providerRows.append(
        el("div", { class: "row" },
          el("div", {}, el("div", { class: "title", text: `${provider.capability || provider.name || "provider"} — ${provider.effective || provider.selected || "?"}` }), el("div", { class: "meta", text: provider.reason || "" })),
          provider.is_mock ? el("span", { class: "pill mock", text: "Mock" }) : el("span", { class: "pill", dataset: { kind: "ready" }, text: "Live" }),
        ),
      );
    }
    view.append(el("div", { class: "card" }, el("h2", {}, "Server"), facts), el("div", { class: "card" }, el("h2", {}, "Providers"), providerRows));
  } catch (err) {
    view.append(el("div", { class: "banner error", text: friendlyError(err) }));
  }
  return view;
}
```

**Field-name audit (required before commit):** open `backend/src/mvb/api/routers/system.py` and read what `/v1/system/info` and `/v1/system/providers` really return; adjust the property names above to match (the renderer is defensive already, but the report screenshots must show real engine data, not "?"). Note any correction in the commit message.

Commit: `feat(web): service tab (effective providers)`.

---

#### T18 — appendix functions land as part of T16 (~0 min, gate)

Confirm every panel in appendix A1 exists in `backend/web/views/meeting.js` and that `grep -c "async function " backend/web/views/meeting.js` ≥ 12. Missing panel = T16 is not done.

---

#### T19 — wire the console into the Python test suite as an asset-shape net (~6 min)

The Python tests in T2 check the mount. Add a cheap structural net so a deleted or renamed asset fails CI even without a browser.

**RED.** Append to `backend/tests/integration/test_web_app.py`:

```python
ASSETS_EVERY_PAGE_NEEDS = ["index.html", "app.css", "app.js", "lib/api.js", "lib/format.js", "lib/router.js", "lib/state.js", "lib/dom.js", "views/signin.js", "views/meetings.js", "views/meeting.js", "views/system.js"]


@pytest.mark.parametrize("relative", ASSETS_EVERY_PAGE_NEEDS)
def test_every_console_asset_is_served(client, relative: str) -> None:
    res = client.get(f"/assets/{relative}")
    assert res.status_code == 200, relative
    assert res.content, relative


def test_app_js_imports_only_local_modules(client) -> None:
    """No CDN, no third-party module: the CSP allows 'self' only."""
    body = client.get("/assets/app.js").text
    assert "http://" not in body and "https://" not in body
```

Run:

```bash
./.venv/Scripts/python.exe -m pytest tests/integration/test_web_app.py -q
```

Expected RED for the asset list **only if** Phase C is incomplete; after T17 it must be GREEN (`20 passed`), except `test_app_js_imports_only_local_modules` — which fails if any view/import pulled a URL. Fix the source, not the test.

Commit: `test(web): console asset-shape net`.

---

### Phase D — packaging and docs

#### T20 — ship the console in the image (~6 min)

Edit `backend/Dockerfile` — after the `RUN pip install --upgrade pip && pip install ".[prod]"` block, add:

```dockerfile
# The management web console (static SPA, no build step) served from MVB_WEB_ROOT.
COPY web ./web
```

and in the `ENV` block add one line:

```dockerfile
ENV MVB_ENV=prod \
    MVB_MEDIA_ROOT=/data/media \
    MVB_WEB_ROOT=/app/web \
    MVB_HOST=0.0.0.0 \
    MVB_PORT=8080
```

**Verify (no Docker on this box — do NOT claim a local docker pass):**

```bash
cd /d/workspace/MeetVault/backend
grep -n "COPY web ./web" Dockerfile && grep -n "MVB_WEB_ROOT" Dockerfile
# expect: one hit each
python - <<'PY'
import pathlib
d = pathlib.Path("Dockerfile").read_text()
assert "web/node_modules" not in d
ign = pathlib.Path(".dockerignore").read_text()
for line in ("web/node_modules/", "web/e2e/", "web/tests/"):
    assert line in ign, line
print("dockerignore ok")
PY
```

Then validate the compose file still parses (pyyaml is in the venv):

```bash
./.venv/Scripts/python.exe -c "import yaml,pathlib; yaml.safe_load(pathlib.Path('compose.yaml').read_text()); print('compose ok')"
# expect: compose ok
```

Commit: `build(docker): ship the web console in the backend image`.

---

#### T21 — document the console (~8 min)

Append a section to `backend/README.md` (read the file first; keep its style, use its existing heading depth) and a short section to `backend/docs/API.md` under the Health & system table:

```markdown
## Web console

The same image also serves a management console for a signed-in user's cloud meetings — the browser
equivalent of the desktop client's Cloud Storage screen: list/search/filter, open a meeting
(transcript, analysis, Q&A, speakers, transcript versions, jobs), re-run analysis, export Word and
remove a meeting from the cloud. Recording and uploads stay in the desktop app.

- URL: `http://<host>:8080/` (the public deployment: `https://meetvault.supersanta.io.vn/`).
- Assets come from `MVB_WEB_ROOT` (default `./web`; the image sets `/app/web`). If that directory
  has no `index.html` the service boots API-only and logs `web_ui_disabled` — a missing console is
  never a startup failure.
- The page is served with a strict CSP and no third-party script; the session token is kept in the
  browser's `localStorage` and cleared on sign-out and on any `401`.
- Browser playback/download of `/v1/media/{id}` works off a blob fetched with the bearer header
  (the token is never accepted as a query parameter).
```

**Verify:** `grep -n "Web console" backend/README.md backend/docs/API.md` → one hit in each.

Commit: `docs: the management web console (run, auth model, media rule)`.

---

#### T22 — Phase D gate (~2 min)

```bash
cd /d/workspace/MeetVault/backend
./.venv/Scripts/python.exe -m pytest -q 2>&1 | tail -1
# expect: 234 passed, 2 skipped  (222 after T4 + 12 asset tests + 1 import test = 235; recount
# from the actual run and record the number — do NOT bend the expectation, report the real one)
./.venv/Scripts/python.exe -m ruff check src tests && ./.venv/Scripts/python.exe -m ruff format --check src tests
# expect: All checks passed!  /  formatted
cd web && node --test tests/ 2>&1 | tail -3
# expect: # pass 21  (12 format + 5 router + 4 state + 5 api = 26; record the real number)
```

The point of T22 is that the numbers are **recorded**, not predicted. If `ruff` complains about anything under `web/` (it should not — `src = ["src","tests"]` excludes it), fix the config, not the code.

---

### Phase E — thorough testing, screenshots, report

#### T23 — local demo stack + deterministic seed data (~14 min)

Everything lives outside the repo. Define once:

```bash
DEMO_ROOT="C:/Users/supersanta/AppData/Local/hermes/cache/scratch/mvweb"
mkdir -p "$DEMO_ROOT"
```

Create `backend/web/e2e/seed_demo.py`:

```python
"""Seed a deterministic demo dataset against a RUNNING MeetVault backend.

Used by the Playwright suite and by the report run. Not part of the app: it only speaks the
public HTTP contract (plus one direct DB insert to pin a `running` job so the list's elapsed
timer is deterministic in screenshots).

Env: MVB_DEMO_URL (default http://127.0.0.1:8099), MVB_DEMO_EMAIL, MVB_DEMO_PASSWORD,
MVB_DATABASE_URL + MVB_MEDIA_ROOT for the direct insert.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

BASE = os.environ.get("MVB_DEMO_URL", "http://127.0.0.1:8099")
EMAIL = os.environ.get("MVB_DEMO_EMAIL", "demo@meetvault.local")
PASSWORD = os.environ.get("MVB_DEMO_PASSWORD", "demo-password-123")
MEDIA = Path(os.environ["MVB_MEDIA_ROOT"]) if os.environ.get("MVB_MEDIA_ROOT") else Path("data/media")


def http(method: str, path: str, token: str | None = None, body=None, raw: bytes | None = None, content_type: str | None = None):
    data = None
    headers = {}
    if raw is not None:
        data = raw
        headers["Content-Type"] = content_type or "application/octet-stream"
    elif body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            payload = response.read()
            return response.status, json.loads(payload) if payload else None
    except urllib.error.HTTPError as exc:
        return exc.code, json.loads(exc.read() or b"{}")


def login() -> str:
    status, body = http("POST", "/v1/auth/login", body={"email": EMAIL, "password": PASSWORD})
    if status != 200:
        raise SystemExit(f"login failed: {status} {body}")
    return body["token"]


def make_wav(path: Path, seconds: float) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        ["ffmpeg", "-y", "-f", "lavfi", "-i", f"sine=frequency=440:duration={seconds}", "-ac", "1", "-ar", "48000", str(path)],
        check=True,
        capture_output=True,
    )
    return path


TRANSCRIPT = [
    {"Speaker": "Speaker 1", "Text": "Welcome everyone. Today we review the MeetVault cloud release and the storage migration."},
    {"Speaker": "Speaker 2", "Text": "The release slips by a week because the object storage migration needs another test pass."},
    {"Speaker": "Speaker 1", "Text": "Agreed. We ship the console first, then the migration."},
    {"Speaker": "Speaker 2", "Text": "I will draft the rollout plan and share it before Friday."},
]


def main() -> int:
    token = login()
    created = []
    for index, (title, seconds) in enumerate(
        [("Cloud console review", 6.0), ("Storage migration standup", 5.0), ("Customer feedback sync", 4.0)], start=1
    ):
        external = f"demo-{int(time.time())}-{index}"
        status, meeting = http("POST", "/v1/meetings", token=token, body={"title": title, "external_id": external, "language": "en", "source": "meetvault"})
        assert status in (200, 201), (status, meeting)
        meeting_id = meeting["id"]
        wav = make_wav(MEDIA / "seed" / f"{external}.wav", seconds)
        boundary = "----mvwebseed"
        body = (
            f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{wav.name}\"\r\n"
            f"Content-Type: audio/wav\r\n\r\n"
        ).encode() + wav.read_bytes() + f"\r\n--{boundary}--\r\n".encode()
        request = urllib.request.Request(
            f"{BASE}/v1/meetings/{meeting_id}/media?kind=audio",
            data=body,
            headers={"Authorization": f"Bearer {token}", "Content-Type": f"multipart/form-data; boundary={boundary}"},
            method="POST",
        )
        with urllib.request.urlopen(request, timeout=120) as response:
            assert response.status == 201, response.status
        http("POST", f"/v1/meetings/{meeting_id}/transcripts?align=auto", token=token, body=TRANSCRIPT)
        created.append({"id": meeting_id, "title": title, "external_id": external})

    # Meeting 1: run the full pipeline to a finished, analyzed state.
    http("POST", f"/v1/meetings/{created[0]['id']}/process", token=token, body={"steps": ["transcribe", "diarize", "align", "analyze"], "language": "en"})

    # Drain the queue in-process (the same tick the worker runs).
    os.environ.setdefault("MVB_ENV", "dev")
    from mvb.jobs.worker import tick  # noqa: E402  (import late so the env is set first)

    deadline = time.time() + 180
    while time.time() < deadline:
        tick()
        status, meeting = http("GET", f"/v1/meetings/{created[0]['id']}/status", token=token)
        if meeting and meeting.get("status") == "ready" and meeting.get("fresh_kinds"):
            break
        time.sleep(0.5)

    # Ask one question so the Q&A tab has a stored answer.
    status, qa = http("POST", f"/v1/meetings/{created[0]['id']}/qa", token=token, body={"question": "What did we decide about the release date?", "top_k": 8})
    if status == 202:
        deadline = time.time() + 90
        while time.time() < deadline:
            tick()
            status, docs = http("GET", f"/v1/meetings/{created[0]['id']}/qa", token=token)
            if docs and docs.get("total"):
                break
            time.sleep(0.5)

    # Meeting 3: pin a running job so the list shows a live elapsed timer deterministically.
    from datetime import UTC, datetime, timedelta  # noqa: E402

    from mvb.db.base import get_session_factory  # noqa: E402
    from mvb.db.models import Job  # noqa: E402

    session = get_session_factory()()
    try:
        job = Job(
            meeting_id=created[2]["id"],
            type="analyze",
            state="running",
            progress=0.47,
            params={"steps": ["transcribe", "diarize", "align", "analyze"]},
            started_at=datetime.now(UTC) - timedelta(seconds=92),
        )
        session.add(job)
        session.commit()
    finally:
        session.close()

    print(json.dumps({"meetings": created, "ready": created[0]["id"], "pinned_running": created[2]["id"]}, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

Create `backend/web/run-demo-stack.sh` (git-tracked; it is the single source of truth for the E2E flow, used locally and by CI):

```sh
#!/usr/bin/env sh
# Boot a demo backend (sqlite + media in DEMO_ROOT), seed the demo dataset, print the URL.
# Usage: sh web/run-demo-stack.sh start|stop|seed
set -eu
DEMO_ROOT="${DEMO_ROOT:-${TMPDIR:-/tmp}/mvweb}"
PORT="${MVB_DEMO_PORT:-8099}"
mkdir -p "$DEMO_ROOT/media"
export MVB_ENV=dev
export MVB_DATABASE_URL="sqlite+pysqlite:///$DEMO_ROOT/demo.db"
export MVB_MEDIA_ROOT="$DEMO_ROOT/media"
export MVB_ANALYZER=mock
export MVB_TRANSCRIBER=mock
export MVB_DIARIZER=mock
export MVB_LOG_LEVEL=INFO
PY=./.venv/Scripts/python.exe
[ -x "$PY" ] || PY=./.venv/bin/python

case "${1:-start}" in
  start)
    rm -f "$DEMO_ROOT/demo.db"
    "$PY" -m alembic upgrade head
    "$PY" -m mvb.admin create-user --email demo@meetvault.local --password demo-password-123 --name "Demo User"
    MVB_DEMO_URL="http://127.0.0.1:$PORT" MVB_DEMO_EMAIL=demo@meetvault.local MVB_DEMO_PASSWORD=demo-password-123 \
      "$PY" -m uvicorn mvb.main:app --host 127.0.0.1 --port "$PORT" &
    echo "$!" > "$DEMO_ROOT/server.pid"
    ;;
  seed)
    MVB_DEMO_URL="http://127.0.0.1:$PORT" MVB_DEMO_EMAIL=demo@meetvault.local MVB_DEMO_PASSWORD=demo-password-123 "$PY" web/e2e/seed_demo.py
    ;;
  stop)
    [ -f "$DEMO_ROOT/server.pid" ] && kill "$(cat "$DEMO_ROOT/server.pid")" 2>/dev/null || true
    rm -f "$DEMO_ROOT/server.pid"
    ;;
esac
```

**This script is a plan sketch with one known flaw the implementer must fix while writing it:** a backgrounded uvicorn inside a shell script is not a *tracked* process for this harness. On this box start the server through the harness instead:

```bash
cd /d/workspace/MeetVault/backend
DEMO_ROOT="C:/Users/supersanta/AppData/Local/hermes/cache/scratch/mvweb"
rm -f "$DEMO_ROOT/demo.db"
export MVB_ENV=dev MVB_DATABASE_URL="sqlite+pysqlite:///$DEMO_ROOT/demo.db" MVB_MEDIA_ROOT="$DEMO_ROOT/media" \
       MVB_ANALYZER=mock MVB_TRANSCRIBER=mock MVB_DIARIZER=mock MVB_LOG_LEVEL=INFO
mkdir -p "$DEMO_ROOT/media"
./.venv/Scripts/python.exe -m alembic upgrade head
./.venv/Scripts/python.exe -m mvb.admin create-user --email demo@meetvault.local --password demo-password-123 --name "Demo User"
./.venv/Scripts/python.exe -m uvicorn mvb.main:app --host 127.0.0.1 --port 8099
# run this last command as a TRACKED BACKGROUND process, then health-check it:
curl -s -m 5 -o /dev/null -w 'healthz=%{http_code}\n' http://127.0.0.1:8099/healthz   # expect 200
curl -s -m 5 -o /dev/null -w 'root=%{http_code}\n' http://127.0.0.1:8099/             # expect 200
curl -s -m 5 -o /dev/null -w 'no_token=%{http_code}\n' http://127.0.0.1:8099/v1/meetings  # expect 401
```

Then seed (foreground, same env):

```bash
./.venv/Scripts/python.exe web/e2e/seed_demo.py
# expect a JSON tail: {"meetings": [...3 titles...], "ready": "<uuid>", "pinned_running": "<uuid>"}
```

Commit: `test(web): deterministic demo seed for the console E2E suite`.

---

#### T24 — Playwright harness (~10 min)

Create `backend/web/playwright.config.js`:

```js
import { defineConfig, devices } from "@playwright/test";

// The demo stack is started by web/run-demo-stack.sh (or by the caller) — the suite does not
// manage it, so a failure to start is visible instead of silently re-used.
export default defineConfig({
  testDir: "./e2e",
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [["list"], ["html", { outputFolder: "playwright-report", open: "never" }]],
  use: {
    baseURL: process.env.MVB_DEMO_URL || "http://127.0.0.1:8099",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    viewport: { width: 1440, height: 900 },
  },
  projects: [
    { name: "desktop", use: { ...devices["Desktop Chrome"] } },
    { name: "narrow", use: { ...devices["Desktop Chrome"], viewport: { width: 420, height: 900 } } },
  ],
});
```

Create `backend/web/e2e/fixtures.js`:

```js
import { test as base, expect } from "@playwright/test";

const EMAIL = process.env.MVB_DEMO_EMAIL || "demo@meetvault.local";
const PASSWORD = process.env.MVB_DEMO_PASSWORD || "demo-password-123";

export const test = base.extend({
  consoleErrors: async ({ page }, use) => {
    const errors = [];
    page.on("console", (message) => {
      if (message.type() === "error") errors.push(message.text());
    });
    page.on("pageerror", (error) => errors.push(String(error)));
    await use(errors);
  },
  signedIn: async ({ page }, use) => {
    await page.goto("/#/login");
    await page.getByLabel("Email").fill(EMAIL);
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page.getByRole("heading", { name: "Meetings in the cloud" })).toBeVisible();
    await use(page);
  },
});

export { expect };
```

Install and prove it works:

```bash
cd /d/workspace/MeetVault/backend/web
npm install
npx playwright install chromium
# expect: "downloaded Chromium" lines then a clean exit
```

Commit: `test(web): Playwright harness (config, signed-in fixture, console-error capture)`.

---

#### T25 — the E2E specs (~25 min, the bulk of "test thoroughly")

Create these files under `backend/web/e2e/`. Each spec is a scenario from the parity matrix; **every spec must be able to fail** (assert on content, not on "the page loaded").

`auth.spec.js`:

```js
import { test, expect } from "./fixtures.js";

test("an unauthenticated visitor is asked to sign in", async ({ page, consoleErrors }) => {
  await page.goto("/#/meetings");
  await expect(page.getByRole("heading", { name: "Sign in to the cloud" })).toBeVisible();
});

test("a wrong password shows the friendly message and stays put", async ({ page }) => {
  await page.goto("/#/login");
  await page.getByLabel("Email").fill("demo@meetvault.local");
  await page.getByLabel("Password").fill("definitely-wrong");
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByText("That email and password do not match.")).toBeVisible();
});

test("signing in shows the account and the list; signing out returns to sign-in", async ({ signedIn, consoleErrors }) => {
  await expect(signedIn.getByText("demo@meetvault.local")).toBeVisible();
  await signedIn.getByRole("button", { name: "Sign out" }).click();
  await expect(signedIn.getByRole("heading", { name: "Sign in to the cloud" })).toBeVisible();
  expect(consoleErrors).toEqual([]);
});
```

`list.spec.js`:

```js
import { test, expect } from "./fixtures.js";

test("the list shows the seeded meetings with status, duration and mock marking", async ({ signedIn, consoleErrors }) => {
  await expect(signedIn.locator('[data-testid="status"]').first()).toBeVisible();
  await expect(signedIn.getByText("Cloud console review")).toBeVisible();
  await expect(signedIn.getByText("Storage migration standup")).toBeVisible();
  const rows = signedIn.locator(".row");
  expect(await rows.count()).toBeGreaterThanOrEqual(3);
  await expect(signedIn.getByTestId("mock-badge").first()).toBeVisible();
  expect(consoleErrors).toEqual([]);
});

test("search and the status filter narrow the list", async ({ signedIn }) => {
  await signedIn.getByLabel("Search meetings").fill("storage");
  await signedIn.getByLabel("Search meetings").press("Enter");
  await expect(signedIn.locator(".row")).toHaveCount(1);
  await expect(signedIn.getByText("Storage migration standup")).toBeVisible();
});
```

`autorefresh.spec.js`:

```js
import { test, expect } from "./fixtures.js";

test("a processing row's elapsed time actually counts up", async ({ signedIn }) => {
  const row = signedIn.locator(`[data-row="${process.env.PINNED_RUNNING_ID}"]`);
  const status = row.locator('[data-testid="status"]');
  const first = await status.textContent();
  await expect(status).not.toHaveText(first, { timeout: 8000 });
  const second = await status.textContent();
  expect(second).not.toEqual(first);
});

test("an idle console does not poll", async ({ signedIn }) => {
  const calls = [];
  signedIn.on("request", (request) => {
    if (request.url().includes("/v1/meetings/")) calls.push(request.url());
  });
  await signedIn.waitForTimeout(6000);
  expect(calls.length).toBe(0);
});
```

`PINNED_RUNNING_ID` must be exported by the demo run (T23 prints it) and passed to Playwright via env by the report script (T30). Document this in the spec header comment.

`detail.spec.js`, `transcript.spec.js`, `analysis.spec.js`, `qa.spec.js`, `speakers.spec.js`, `versions.spec.js`, `jobs.spec.js`, `export.spec.js`, `remove.spec.js`, `responsive.spec.js` — each follows the same three-part shape. Write them with these exact assertions (they are the acceptance criteria):

- **detail**: opening a row from the list lands on `#/meetings/<id>`; the header shows title, date, duration, "recording(s)", and the status pill; `Re-run analysis` POSTs `/process` (assert the request) and the toast "Analysis queued." appears.
- **transcript**: the Transcript tab renders a table with at least 4 rows; a row shows a speaker label and an `m:ss` time; clicking a time cell sets `video.currentTime > 0` on the media element (or, for audio-only rows, `audio.currentTime > 0`); `Copy transcript text` writes to the clipboard (`context.grantPermissions(['clipboard-read','clipboard-write'])`) and the toast appears.
- **analysis**: the Analysis tab shows the summary card (with bullets), the agenda list, at least one section, a decisions list and an action-items list — all non-empty for the seeded analyzed meeting — plus the `Mock data` badge; a citation button seeks the player (`currentTime` changes).
- **qa**: asking a question shows the queued state, then the answer appears with at least one citation button (the seed drains the qa job); the previously-asked question is listed after a reload.
- **speakers**: renaming a speaker updates its segment labels (assert the new label appears in the Transcript tab); merging reduces the speaker count by one; deleting removes the row (assert the count drops).
- **versions**: the Versions tab lists at least 2 versions for the seeded meeting (the client transcript + the pipeline transcript); `Activate` on the inactive one moves the "Active" pill; deleting the active one is refused with the server's `409` message surfaced in the UI.
- **jobs**: the Jobs tab lists the pipeline jobs with state and type; a `Cancel` on a queued/succeeded job surfaces either success or the server's refusal without a page error; opening a job's event stream renders at least one event line (for the pinned running job the stream stays open — assert the panel is present and non-empty rather than waiting for a terminal state).
- **export**: clicking `Export to Word` triggers a download whose suggested filename ends in `.docx`; then independently prove the bytes are a real docx: `python -c "import docx,sys; d=docx.Document(sys.argv[1]); print(len(d.paragraphs))" <file>` → prints a number > 0.
- **remove**: the confirm dialog opens; `Cancel` keeps the row; confirming removes it, the row disappears and a later `GET /v1/meetings/<id>` (via `page.request.get` with the token) answers 404.
- **responsive** (narrow project): at 420 px the page has no horizontal overflow (`await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1)` is true) and the sign-in form plus the list are usable.

Run the whole suite:

```bash
cd /d/workspace/MeetVault/backend/web
MVB_DEMO_URL=http://127.0.0.1:8099 PINNED_RUNNING_ID=<id from the seed> npx playwright test
# expect: all tests passed (record the exact "N passed" line)
```

Add `web/e2e/README.md` with the exact three commands needed to reproduce (start stack, seed, run suite) so the next person does not have to reverse-engineer it.

Commit: `test(web): Playwright E2E suite covering the cloud-storage parity matrix`.

---

#### T26 — a11y + contract sweep on the console (~8 min)

Two cheap, high-value checks that catch the "questionable taste" failure modes:

1. `backend/web/e2e/a11y.spec.js` — assert on the analyzed meeting page: every `button` and `a` has an accessible name (`await page.evaluate` walking `document.querySelectorAll('button,a')` and checking `innerText.trim() || aria-label || title`), the tab list exposes `role="tab"`/`aria-selected`, and the toast has `role="status"`.
2. **The contract sweep** (backend skill's pitfall list, applied to this console): the console must never 500 the API. With the demo server up, drive every console action and assert no `500` appears in the browser or in the uvicorn log:

```bash
grep -c "500 Internal Server Error" "$DEMO_ROOT/server.log"   # expect 0
```

Run, fix, commit: `test(web): a11y assertions and a no-500 sweep for the console`.

---

#### T27 — ordered screenshots (~12 min)

Create `backend/web/e2e/screenshots.spec.js`. It walks the flows in order and writes PNGs into `backend/docs/web-report/screens/` (relative to the repo, resolved via `process.env.MVB_REPORT_DIR` defaulting to `../docs/web-report/screens`):

```js
import { test } from "./fixtures.js";
import fs from "node:fs";
import path from "node:path";

const DIR = path.resolve(process.env.MVB_REPORT_DIR || "../docs/web-report/screens");
fs.mkdirSync(DIR, { recursive: true });
const shot = async (page, name) => page.screenshot({ path: path.join(DIR, name), fullPage: true });

test("capture the console flow in order", async ({ page }) => {
  await page.goto("/#/login");
  await shot(page, "01-sign-in.png");
  await page.getByLabel("Email").fill(process.env.MVB_DEMO_EMAIL || "demo@meetvault.local");
  await page.getByLabel("Password").fill(process.env.MVB_DEMO_PASSWORD || "demo-password-123");
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.getByRole("heading", { name: "Meetings in the cloud" }).waitFor();
  await shot(page, "02-meetings-list.png");
  // ... then, in order: open the analyzed meeting (03-meeting-overview), Transcript (04),
  // Analysis (05), Q&A (06), Speakers (07), Versions (08), Jobs (09), the remove dialog (10),
  // the Service tab (11), and a narrow 420px list (12).
});
```

Requirements for this spec (non-negotiable, they are the deliverable):

- **One screenshot per step, in flow order, numbered** — the owner asked for screens "for report".
- Files are written **into the repo** (`backend/docs/web-report/screens/`), so they are the artifact; sizes stay small by using `fullPage` only on the list/analysis pages and a 1440×900 viewport elsewhere.
- The spec must assert something at each step (a visible heading/label) — a screenshot of a blank page is worse than no screenshot.
- Deterministic: no sleeps; always wait on a locator.

Run:

```bash
cd /d/workspace/MeetVault/backend/web
MVB_DEMO_URL=http://127.0.0.1:8099 npx playwright test e2e/screenshots.spec.js
ls -1 ../docs/web-report/screens/ | wc -l    # expect 12
```

Commit: `docs(web): ordered console screenshots for the report`.

---

#### T28 — architecture diagram (owner convention: a report ships prose + a diagram)

Author `backend/docs/web-report/console-architecture.drawio` **as XML** (never by driving the draw.io GUI while another session is at the machine), one diagram: `Browser → nginx vhost → FastAPI (/, /assets, /v1, /docs) → SQLite/Postgres + media volume + worker → providers`, plus a second row showing the SPA's own modules (`app.js → router/state/api/format → views/*`).

Export and audit headlessly:

```bash
"C:/Program Files/draw.io/draw.io.exe" --export --output backend/docs/web-report/console-architecture.png --format png backend/docs/web-report/console-architecture.drawio
ls -l backend/docs/web-report/console-architecture.png   # expect a non-zero file
```

Then load the PNG with `vision_analyze` and fix any cut-off text, overlap or dangling edge; re-export and re-check. Load the `diagram-authoring` skill before authoring — the rules that cost the most time are in it (an `html=1` label swallows angle brackets, edge labels land on whatever text sits at the midpoint, a dense panel can hide a garbled line).

Commit: `docs(web): console architecture diagram (drawio + png)`.

---

#### T29 — the report (~12 min)

Create `backend/docs/web-report/REPORT.md` with, in this order:

1. **What shipped** — one paragraph + the parity matrix from §3 with a ✅/⚠️/❌ and the test that proves each row.
2. **How to run it** — the three commands (start stack, seed, open `http://127.0.0.1:8099/`), plus the production note (`https://meetvault.supersanta.io.vn/`, same image).
3. **Screens** — the 12 screenshots inlined with captions in flow order (`![02 Meetings list](screens/02-meetings-list.png)` followed by one line of what it shows and any finding).
4. **Test evidence** — the exact command lines and their real tails: pytest count, ruff output, `node --test` count, `npx playwright test` count, the docx-paragraph proof, the `grep -c 500` result.
5. **Findings** — every real defect found while testing, with the evidence (what was clicked, what the server returned, the console error text). Findings are the valuable part: an empty list means the testing was shallow.
6. **Diagram** — `console-architecture.png`.
7. **Not done / gated** — the public-host check (T30) and every Open question from §8 with its status.

**Verify:** `wc -l backend/docs/web-report/REPORT.md` > 60 and every `screens/*.png` referenced by the report exists:

```bash
cd /d/workspace/MeetVault/backend/docs/web-report
grep -o "screens/[0-9a-z-]*\.png" REPORT.md | sort -u | while read -r f; do [ -f "$f" ] || echo "MISSING $f"; done
# expect: no output
```

Commit: `docs(web): console report with parity evidence and screenshots`.

---

#### T30 — the gated public check (~5 min, may be skipped — never faked)

```bash
curl -s -m 12 -o /dev/null -w 'healthz=%{http_code}\n' https://meetvault.supersanta.io.vn/healthz
curl -s -m 12 -o /dev/null -w 'root=%{http_code}\n' https://meetvault.supersanta.io.vn/
curl -s -m 12 -o /dev/null -w 'assets=%{http_code}\n' https://meetvault.supersanta.io.vn/assets/app.js
curl -s -m 12 -o /dev/null -w 'docs=%{http_code}\n' https://meetvault.supersanta.io.vn/docs
```

- If `healthz=200` and `root=200`: the console is live; take the public screenshots into `screens/public/` and record them in the report. The nginx vhost must also proxy `/assets` — if `assets=404` while `root=200`, the vhost routes only specific paths: report it as a finding with the exact response and stop (that is an owner/server-side fix).
- If the host still answers `000` (today's state): record in the report **"public host does not yet serve the service — verified `healthz=000` on <date>; the console is verified locally, and this row is BLOCKED on the owner running the server-side installer."** Do not soften it, do not simulate it.

Commit only if there is new evidence (the report edit): `docs(web): public-host verification state`.

---

#### T31 — final gate and progress log (~6 min)

```bash
cd /d/workspace/MeetVault/backend
./.venv/Scripts/python.exe -m pytest -q 2>&1 | tail -1
./.venv/Scripts/python.exe -m ruff check src tests && ./.venv/Scripts/python.exe -m ruff format --check src tests
cd web && node --test tests/ 2>&1 | tail -3
cd /d/workspace/MeetVault && git status --short
```

- pytest: the recorded count from T22 (no regressions), ruff clean.
- `git status --short`: only the intended files + the known untracked trio.
- Append a progress entry to `backend/docs/progress/progress_supersanta/PROGRESS_2026-10-15.md` (create it if absent; append-only, one bullet per claim with its evidence line, no secrets). Cover: the mount, the SPA, the parity matrix, the test counts, the screenshots, the public-host state, and the open questions.

Commit: `docs(progress): web console wave` (include the progress file only).

---

#### T32 — CI (recommended, ~8 min)

Add a job to `.github/workflows/backend-ci.yml` that runs exactly what T23–T27 run locally. Read the existing file first and mirror its style (checkout, setup-python, `working-directory: backend`, cache as configured).

```yaml
  web-console:
    runs-on: ubuntu-latest
    defaults:
      run:
        working-directory: backend
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-python@v5
        with:
          python-version: "3.12"
      - uses: actions/setup-node@v4
        with:
          node-version: "22"
      - run: pip install -e ".[dev]"
      - run: node --test web/tests/
      - run: npm ci --prefix web
      - run: npx --prefix web playwright install --with-deps chromium
      - name: demo stack
        run: |
          sudo apt-get update && sudo apt-get install -y ffmpeg
          DEMO_ROOT="$RUNNER_TEMP/mvweb" sh web/run-demo-stack.sh start
          for i in $(seq 1 30); do curl -fsS http://127.0.0.1:8099/healthz && break || sleep 2; done
          DEMO_ROOT="$RUNNER_TEMP/mvweb" sh web/run-demo-stack.sh seed
      - run: npx --prefix web playwright test
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: web-console-report
          path: |
            backend/docs/web-report/
            backend/web/playwright-report/
```

`run-demo-stack.sh`'s backgrounded-server approach is acceptable **in CI** (a runner has no reason for a tracked process) — note that in the script's header so nobody "fixes" it either way. If the flow needs a `PINNED_RUNNING_ID` for the autorefresh spec, make `seed_demo.py` write `$DEMO_ROOT/seed.json` and have `playwright.config.js` read it (small addition; do it if the spec is flaky without it).

Commit: `ci(web): run the console unit tests and the browser suite`.

---

## 7. Tests / validation summary

| Layer | Command (from) | Expected |
|---|---|---|
| Python unit | `./.venv/Scripts/python.exe -m pytest tests/unit/test_web_settings.py -q` (`backend/`) | 2 passed |
| Python integration | `./.venv/Scripts/python.exe -m pytest tests/integration/test_web_app.py -q` (`backend/`) | 20 passed |
| Python full suite | `./.venv/Scripts/python.exe -m pytest -q` (`backend/`) | baseline 214 → recorded count, 0 failed |
| Lint/format | `ruff check src tests` + `ruff format --check src tests` (`backend/`) | `All checks passed!` / clean |
| JS unit | `node --test tests/` (`backend/web/`) | all pass, 0 fail |
| Browser E2E | `MVB_DEMO_URL=… PINNED_RUNNING_ID=… npx playwright test` (`backend/web/`) | all pass on `desktop` + `narrow` |
| Real docx | `python -c "import docx,sys;print(len(docx.Document(sys.argv[1]).paragraphs))" <downloaded.docx>` | > 0 |
| No 500s | `grep -c "500 Internal Server Error" $DEMO_ROOT/server.log` | 0 |
| Console clean | E2E `consoleErrors` fixture | `[]` |
| Live smoke | the curl block in T23 | `healthz=200`, `root=200`, `no_token=401` |
| Public host | the curl block in T30 | 200s, or an honest BLOCKED row |

## 8. Risks, tradeoffs, open questions

**Risks**

1. **The public host does not answer today** (`healthz=000`, verified this session). Public deployment verification is BLOCKED on the owner running the server-side installer; the console is verified against local uvicorn. Reported, never simulated.
2. **The nginx vhost may proxy only some paths.** If it proxies `/healthz` and `/v1` but not `/assets`, the page will load with no CSS/JS. T30 detects it; the fix is server-side (add `/` and `/assets/` to the proxy block) and is the owner's call.
3. **`localStorage` session token.** Anyone able to run script in the page could read it. Mitigations shipped here: strict CSP with `script-src 'self'`, no third-party script, no `innerHTML`, sign-out clears it, any `401` clears it. Proper fix (httpOnly cookie session) needs backend auth changes — follow-up, not this wave.
4. **Public login endpoint with no rate limiting.** The backend's `POST /v1/auth/login` has no throttle today; exposing a browser console makes the credential surface public. Recommending a rate limit / lockout is a follow-up with a real security arg — record it in the report.
5. **Media as blobs** means a whole recording is held in browser memory for playback (fine for typical meeting sizes; a multi-GB screen recording would hurt). Alternative (a signed short-lived URL) is a backend feature — follow-up.
6. **A long real analyze job + several browser tabs polling `/status`** is the SQLite write-lock scenario the owner's earlier fix addressed (`WAL` + `busy_timeout=30000`). The console adds polling traffic, not writes, so it should be safe — but if a `database is locked` 500 shows up in T26's sweep, report it rather than papering over it.
7. **Playwright needs to download Chromium** (~150 MB). On a network-restricted box this fails; the fallback is to drive the site with the harness browser tool and capture screenshots that way, and to keep the node unit tests as the machine-checkable layer.

**Tradeoffs (deliberate)**

- No framework/bundler: zero build step, zero npm runtime deps, everything reviewable as text; the cost is manual DOM building (mitigated by `lib/dom.js`).
- Hash routing instead of history routing: no server catch-all, so `/docs` and `/v1` are untouched.
- The console ships inside the API image: one artifact, one deployment, no CORS; the cost is that a broken asset can only be fixed by shipping the image again.
- English-only UI for now (the desktop is en/ja).

**Open questions for the owner** (record the answers in the report)

1. **Q1 — Confirm the nginx vhost proxies `/` and `/assets/`** (not just `/v1`). Without it the console will not render on the public host.
2. **Q2 — Is browser-side upload wanted?** It is the only real parity gap (the desktop is the recorder). Adding it means multipart upload + progress in the browser; estimate a follow-up wave, not this one.
3. **Q3 — Japanese UI?** The desktop localizes en/ja; the console is English-only unless asked.
4. **Q4 — Should the console be reachable at `/` (this plan) or move to a `/console` path?** `/` is nicer; `/console` is safer if another app ever shares the host.
5. **Q5 — Rate limiting / lockout on the public login** — wanted now (needs a small backend feature) or recorded as a follow-up?

---

## Appendix A1 — the seven panel functions for `web/views/meeting.js`

Paste these **inside** `renderMeeting`, after the `renderTab` definition (they close over `ctx`, `state`, `meetingId`, `blobFor`, and `renderTab`). Every server string is inserted with `el(..., { text: ... })` (never `innerHTML`) — that is also what makes the CSP safe.

```js
  async function overview() {
    const [meeting, media] = await Promise.all([ctx.api.getMeeting(meetingId), ctx.api.listMedia(meetingId, { limit: 50 })]);
    const card = el("div", { class: "card", "data-testid": "overview" });
    const facts = el("dl", { class: "facts" });
    facts.append(
      el("dt", {}, "Status"), el("dd", { "data-testid": "status-text", text: meeting.status }),
      el("dt", {}, "Language"), el("dd", { text: meeting.language || "auto" }),
      el("dt", {}, "Duration"), el("dd", { text: formatDuration(meeting.duration_ms) }),
      el("dt", {}, "Created"), el("dd", { text: formatDate(meeting.created_at) }),
      el("dt", {}, "Recordings"), el("dd", { text: String(meeting.media_count) }),
      el("dt", {}, "Transcript versions"), el("dd", { text: String(meeting.transcript_count) }),
      el("dt", {}, "Speakers"), el("dd", { text: String(meeting.speaker_count) }),
      el("dt", {}, "Analysis"), el("dd", { text: meeting.has_analysis ? "current" : "none yet" }),
    );
    card.append(el("h2", {}, "Meeting"), facts);
    const mediaCard = el("div", { class: "card", "data-testid": "media-list" });
    mediaCard.append(el("h2", {}, "Recordings on the server"));
    if (!media.items.length) {
      mediaCard.append(el("div", { class: "empty", text: "No recordings on the server for this meeting." }));
    }
    for (const asset of media.items) {
      const player = el("div", { class: "player" });
      const status = el("span", { class: "meta", text: asset.is_video ? "Video" : "Audio" });
      const play = el("button", { type: "button", "data-testid": "preview", onclick: async () => {
        try {
          const { url, blob } = await blobFor(asset.id);
          clear(player);
          player.append(asset.is_video ? el("video", { controls: true, src: url }) : el("audio", { controls: true, src: url }));
          player.append(el("span", { class: "meta", text: `${formatBytes(blob.size)} · ${asset.name || asset.storage_key}` }));
        } catch (err) { ctx.toast(friendlyError(err)); }
      } }, "Load playback");
      const download = el("button", { type: "button", "data-testid": "download", onclick: async () => {
        try {
          const { url } = await blobFor(asset.id);
          const anchor = el("a", { href: url, download: asset.name || "recording" });
          document.body.append(anchor); anchor.click(); anchor.remove();
        } catch (err) { ctx.toast(friendlyError(err)); }
      } }, "Download");
      const remove = el("button", { class: "danger", type: "button", "data-testid": "delete-media", onclick: async () => {
        try { await ctx.api.deleteMedia(asset.id); ctx.toast("Recording deleted from the server."); await renderTab(); await loadHead(); }
        catch (err) { ctx.toast(friendlyError(err)); }
      } }, "Delete");
      mediaCard.append(
        el("div", { class: "row", dataset: { asset: asset.id } },
          el("div", {}, el("div", { class: "title", text: asset.name || asset.storage_key }), el("div", { class: "meta", text: `${status.textContent} · ${formatDuration(asset.duration_ms)} · ${formatBytes(asset.size_bytes)}` })),
          el("div", { class: "actions" }, play, download, remove)),
        player,
      );
    }
    return el("div", {}, card, mediaCard, el("div", { class: "card" }, el("h2", {}, "Player"), playerHost()));
  }

  // The transcript tab shares ONE player element with the seek/citation handlers; the element is
  // created once per tab render so object URLs are released with the tab.
  let sharedPlayer = null;
  function playerHost() {
    const host = el("div", { class: "player", id: "player-host" });
    sharedPlayer = el("audio", { controls: true, "data-testid": "player" });
    host.append(sharedPlayer);
    return host;
  }

  async function loadPlayerSource() {
    const media = await ctx.api.listMedia(meetingId, { limit: 50 });
    const asset = media.items.find((item) => !item.is_video) || media.items[0];
    if (!asset) return null;
    const { url } = await blobFor(asset.id);
    sharedPlayer.src = url;
    return sharedPlayer;
  }

  async function seekTo(seconds) {
    sharedPlayer.currentTime = Math.max(0, seconds);
    try { await sharedPlayer.play(); } catch { /* autoplay refusal is fine */ }
  }

  async function transcript() {
    const [detail, text] = await Promise.all([
      ctx.api.transcript(meetingId),
      ctx.api.transcriptText(meetingId).catch(() => ""),
    ]);
    const segments = detail.segments || [];
    const card = el("div", { class: "card", "data-testid": "transcript" });
    card.append(
      el("h2", {}, `Transcript — ${segments.length} segment(s)`),
      el("div", { class: "toolbar" },
        el("button", { type: "button", "data-testid": "copy-text", onclick: async () => {
          try { await navigator.clipboard.writeText(text || segments.map((s) => `${s.speaker_label || ""}: ${s.text}`).join("\n")); ctx.toast("Transcript copied."); }
          catch (err) { ctx.toast(friendlyError(err)); }
        } }, "Copy transcript text")),
      playerHost(),
    );
    const table = el("table", { class: "segments" }, el("thead", {}, el("tr", {}, el("th", {}, "Time"), el("th", {}, "Speaker"), el("th", {}, "Text"))));
    const tbody = el("tbody", {});
    for (const segment of segments) {
      const row = el("tr", { dataset: { segment: segment.id } },
        el("td", { class: "time", "data-testid": "seek", onclick: () => seekTo((segment.start_ms || 0) / 1000) }, formatDuration(segment.start_ms)),
        el("td", { class: "speaker", text: segment.speaker_label || "—" }),
        el("td", { text: segment.text }));
      tbody.append(row);
    }
    table.append(tbody);
    card.append(table);
    await loadPlayerSource();
    return card;
  }

  async function analysis() {
    const bundle = await ctx.api.analysis(meetingId);
    const card = el("div", { class: "card", "data-testid": "analysis" });
    card.append(el("h2", {}, "Summary"));
    if (bundle.mock) card.append(el("span", { class: "pill mock", "data-testid": "mock-badge", text: "Mock data" }));
    if (bundle.stale_kinds && bundle.stale_kinds.length) card.append(el("div", { class: "banner", text: `Out of date: ${bundle.stale_kinds.join(", ")}` }));
    if (bundle.run) card.append(el("p", { class: "citation", text: `Analyzed by ${bundle.run.provider}${bundle.run.model ? ` (${bundle.run.model})` : ""}${bundle.run.is_mock ? " — mock" : ""}` }));
    const summary = bundle.summary || {};
    if (summary.text) card.append(el("p", { text: summary.text }));
    if (summary.bullets && summary.bullets.length) card.append(el("ul", { class: "bullets", "data-testid": "summary-bullets" }, ...summary.bullets.map((b) => el("li", { text: b }))));
    if (!summary.text && !(summary.bullets || []).length) card.append(el("div", { class: "empty", "data-testid": "no-analysis", text: "No analysis is current for this meeting yet. Use Re-run analysis." }));

    const agenda = bundle.agenda && bundle.agenda.items ? bundle.agenda.items : [];
    if (agenda.length) {
      const agendaCard = el("div", { class: "card", "data-testid": "agenda" }, el("h2", {}, "Agenda & discussion"));
      for (const item of agenda) {
        agendaCard.append(el("div", { class: "row" },
          el("div", {}, el("div", { class: "title", text: item.title }), el("div", { class: "meta", text: item.topic || "" })),
          el("button", { type: "button", "data-testid": "seek-agenda", onclick: () => seekTo((item.start_ms || 0) / 1000) }, formatDuration(item.start_ms))));
      }
      card.append(agendaCard);
    }

    const sections = bundle.sections || [];
    if (sections.length) {
      const sectionCard = el("div", { class: "card", "data-testid": "sections" }, el("h2", {}, "Sections"));
      const summaries = new Map((bundle.section_summaries || []).map((doc) => [doc.section_id, doc.payload || {}]));
      for (const section of sections) {
        const payload = summaries.get(section.id) || {};
        sectionCard.append(el("div", { class: "row" },
          el("div", {}, el("div", { class: "title", text: `${section.ordinal}. ${section.title}` }), el("div", { class: "meta", text: payload.summary || section.summary || "" })),
          el("button", { type: "button", "data-testid": "seek-section", onclick: () => seekTo((section.start_ms || 0) / 1000) }, formatDuration(section.start_ms))));
      }
      card.append(sectionCard);
    }

    card.append(citationsCard("Decisions", bundle.decisions, "decision", "decided_by"));
    card.append(citationsCard("Action items", bundle.action_items, "action", "owner"));
    return card;
  }

  function citationsCard(title, document, testId, actorField) {
    const items = document && document.items ? document.items : [];
    const card = el("div", { class: "card", "data-testid": testId });
    card.append(el("h2", {}, title));
    if (!items.length) { card.append(el("div", { class: "empty", text: `No ${title.toLowerCase()} recorded.` })); return card; }
    for (const item of items) {
      const actors = item[actorField];
      card.append(el("div", { class: "row" },
        el("div", {},
          el("div", { class: "title", text: item.text }),
          el("div", { class: "meta", text: [Array.isArray(actors) ? actors.join(", ") : actors, item.due ? `due ${item.due}` : "", item.at_ms !== undefined ? formatDuration(item.at_ms) : ""].filter(Boolean).join(" · ") }),
          el("div", { class: "citation" }, ...(item.segment_ids || []).slice(0, 4).map((segmentId, index) =>
            el("button", { type: "button", "data-testid": `${testId}-citation`, onclick: async () => {
              const detail = await ctx.api.transcript(meetingId);
              const segment = (detail.segments || []).find((s) => s.id === segmentId);
              if (segment) await seekTo((segment.start_ms || 0) / 1000);
            } }, `source ${index + 1}`)))),
        null));
    }
    return card;
  }

  async function qa() {
    const card = el("div", { class: "card", "data-testid": "qa" });
    const question = el("input", { type: "text", "aria-label": "Ask about this meeting", placeholder: "What did we decide about the release date?" });
    const askButton = el("button", { class: "primary", type: "button", "data-testid": "ask", onclick: async () => {
      const value = question.value.trim();
      if (!value) return;
      askButton.disabled = true;
      try {
        await ctx.api.ask(meetingId, value);
        ctx.toast("Question queued — it appears when the analysis worker answers it.");
        question.value = "";
        await renderTab();
      } catch (err) { ctx.toast(friendlyError(err)); } finally { askButton.disabled = false; }
    } }, "Ask");
    card.append(el("h2", {}, "Ask about this meeting"), el("div", { class: "toolbar" }, question, askButton));
    const page = await ctx.api.qa(meetingId);
    const host = el("div", { class: "rows", "data-testid": "qa-history" });
    if (!page.items.length) host.append(el("div", { class: "empty", text: "No questions asked yet." }));
    for (const doc of page.items) {
      const payload = doc.payload || {};
      host.append(el("div", { class: "row" },
        el("div", {},
          el("div", { class: "title", text: payload.question || "(question)" }),
          el("div", { class: "meta", text: payload.answer || "" }),
          el("div", { class: "citation" }, ...(payload.citations || []).slice(0, 5).map((citation, index) =>
            el("button", { type: "button", "data-testid": "qa-citation", onclick: () => seekTo((citation.start_ms || 0) / 1000) }, `${index + 1}. ${formatDuration(citation.start_ms)}`)))),
        doc.stale ? el("span", { class: "pill stale", text: "Outdated" }) : null));
    }
    card.append(host);
    return card;
  }

  async function speakers() {
    const list = await ctx.api.speakers(meetingId);
    const card = el("div", { class: "card", "data-testid": "speakers" }, el("h2", {}, "Speakers"));
    if (!list.length) card.append(el("div", { class: "empty", text: "No speakers on this transcript." }));
    for (const speaker of list) {
      const input = el("input", { type: "text", value: speaker.label, "aria-label": `Rename ${speaker.label}` });
      const others = list.filter((other) => other.id !== speaker.id);
      const mergeInto = el("select", { "aria-label": `Merge ${speaker.label} into` }, el("option", { value: "" }, "Merge into…"), ...others.map((other) => el("option", { value: other.id }, other.label)));
      card.append(el("div", { class: "row", dataset: { speaker: speaker.id } },
        el("div", {}, el("div", { class: "title", text: speaker.label }), el("div", { class: "meta", text: `${speaker.segment_count} segment(s) · ${formatDuration(speaker.speaking_ms)}` })),
        el("div", { class: "actions" }, input,
          el("button", { type: "button", "data-testid": "rename", onclick: async () => {
            try { await ctx.api.renameSpeaker(speaker.id, input.value.trim()); ctx.toast("Speaker renamed."); await renderTab(); }
            catch (err) { ctx.toast(friendlyError(err)); }
          } }, "Rename"),
          mergeInto,
          el("button", { type: "button", "data-testid": "merge", onclick: async () => {
            if (!mergeInto.value) return;
            try { await ctx.api.mergeSpeaker(speaker.id, mergeInto.value); ctx.toast("Speakers merged."); await renderTab(); }
            catch (err) { ctx.toast(friendlyError(err)); }
          } }, "Merge"),
          el("button", { class: "danger", type: "button", "data-testid": "delete-speaker", onclick: async () => {
            try { await ctx.api.deleteSpeaker(speaker.id); ctx.toast("Speaker removed."); await renderTab(); }
            catch (err) { ctx.toast(friendlyError(err)); }
          } }, "Delete"))));
    }
    return card;
  }

  async function versions() {
    const page = await ctx.api.transcripts(meetingId, { limit: 50 });
    const card = el("div", { class: "card", "data-testid": "versions" }, el("h2", {}, "Transcript versions"));
    if (!page.items.length) card.append(el("div", { class: "empty", text: "No transcript versions yet." }));
    for (const version of page.items) {
      card.append(el("div", { class: "row", dataset: { version: version.id } },
        el("div", {}, el("div", { class: "title", text: `${version.source || "unknown"} · ${version.segment_count ?? "?"} segment(s)` }), el("div", { class: "meta", text: formatDate(version.created_at) })),
        el("div", { class: "actions" },
          version.is_active ? el("span", { class: "pill", dataset: { kind: "ready" }, "data-testid": "active-version", text: "Active" }) : null,
          version.is_active ? null : el("button", { type: "button", "data-testid": "activate", onclick: async () => {
            try { await ctx.api.activateTranscript(version.id); ctx.toast("Transcript activated."); await renderTab(); await loadHead(); }
            catch (err) { ctx.toast(friendlyError(err)); }
          } }, "Activate"),
          el("button", { class: "danger", type: "button", "data-testid": "delete-version", onclick: async () => {
            try { await ctx.api.deleteTranscript(version.id); ctx.toast("Version deleted."); await renderTab(); }
            catch (err) { ctx.toast(friendlyError(err)); }
          } }, "Delete"))));
    }
    return card;
  }

  async function jobs() {
    const page = await ctx.api.jobs(meetingId);
    const card = el("div", { class: "card", "data-testid": "jobs" }, el("h2", {}, "Jobs"));
    if (!page.items.length) card.append(el("div", { class: "empty", text: "No jobs for this meeting." }));
    const events = el("pre", { class: "meta", "data-testid": "job-events", text: "" });
    for (const job of page.items) {
      card.append(el("div", { class: "row", dataset: { job: job.id } },
        el("div", {}, el("div", { class: "title", text: `${job.type} — ${job.state}` }), el("div", { class: "meta", text: `${Math.round((job.progress || 0) * 100)}%${job.error ? ` · ${job.error}` : ""}` })),
        el("div", { class: "actions" },
          isProcessing(job.state) ? el("button", { class: "danger", type: "button", "data-testid": "cancel", onclick: async () => {
            try { await ctx.api.cancelJob(job.id); ctx.toast("Cancellation requested."); await renderTab(); }
            catch (err) { ctx.toast(friendlyError(err)); }
          } }, "Cancel") : null,
          el("button", { type: "button", "data-testid": "watch", onclick: () => watchJob(job.id, events) }, "Watch"))));
    }
    card.append(el("h3", {}, "Live events"), events);
    return card;
  }

  // SSE needs the bearer header, which EventSource cannot send: read the stream with fetch().
  async function watchJob(jobId, output) {
    output.textContent = "";
    try {
      const res = await fetch(`/v1/jobs/${jobId}/events`, { headers: { Authorization: `Bearer ${ctx.store.getToken()}` } });
      if (!res.ok || !res.body) throw new Error(String(res.status));
      const reader = res.body.getReader();
      const decoder = new TextDecoder();
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        output.textContent += decoder.decode(value, { stream: true });
      }
    } catch {
      // Fall back to polling: the stream closes on a terminal state and may end on idle proxies.
      const job = await ctx.api.job(jobId).catch(() => null);
      output.textContent += `\n(poll) ${job ? `${job.type} ${job.state} ${Math.round((job.progress || 0) * 100)}%` : "unavailable"}\n`;
    }
  }
```

**Two invariants for the appendices' code** (an implementer who breaks these will fail T25/T26):

1. `ctx.store.getToken()` is only safe because `createSessionStore` is on `ctx` — `watchJob` uses it. Never read `localStorage` directly from a view.
2. **Field names in `overview()`, `versions()` and `speakers()` were NOT all read from the Pydantic schemas** (only the meetings/auth/analysis ones were). Before committing T16, verify `MediaAssetOut`, `SpeakerWithStatsOut`, `TranscriptOut`/`TranscriptDetailOut` and `SegmentOut` in `backend/src/mvb/schemas/` and correct every name that differs (`name`, `is_video`, `storage_key`, `duration_ms`, `size_bytes`, `label`, `segment_count`, `speaking_ms`, `source`, `segment_count`, `is_active`, `created_at`, `start_ms`, `end_ms`, `speaker_label`, `text`). Correct the code, not the reader.

---

## Appendix A2 — why the console is not a separate frontend app

Considered and rejected for this wave:

- **A React/Vite app in the repo** — a second toolchain, a build step in Docker and CI, a `node_modules` in a Python repo, and an nginx `try_files` for history routing. The parity work (17 endpoints) does not need a framework.
- **A separate container/port** — a second deployment artifact for the owner, plus CORS and a second vhost.
- **Server-rendered Jinja templates** — would need a template engine dependency, a per-page Python change for every feature, and would still require JS for the polling/SSE/player behaviours, i.e. both halves.

The chosen shape keeps the project's promise ("a missing console is never a startup failure") and one deployable artifact.
