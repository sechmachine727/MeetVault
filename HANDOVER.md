# HANDOVER — Meetvault_Backend

**Status: complete and verified to the limit of the authoring box.** Post-meeting
transcription, diarization and LLM analysis for the MeetVault Windows app, as a standalone
service. Last updated 2026-10-12, at commit `09b0855` (branch `main`, working tree clean).

---

## 1. TL;DR for whoever picks this up

- **What it is:** a FastAPI + SQLAlchemy + Alembic service that ingests a *finished* MeetVault
  recording, re-processes it asynchronously (ASR → diarization → alignment → LLM analysis) and
  serves meetings, media, transcripts, speakers, jobs and analysis over HTTP.
- **Where:** `D:/workspace/Meetvault_Backend` (its own git repo). The client is a different repo,
  `D:/workspace/MeetVault`, and is **untouched** by this work.
- **Runs with zero AI engines installed:** every capability has a deterministic mock, and a
  configured-but-missing engine degrades to the labelled mock instead of blocking startup.
- **Verified:** 177 tests pass / 2 intentional skips, 92% coverage, `ruff` clean, 33 OpenAPI
  paths, plus `scripts/smoke.sh` (real uvicorn + worker over HTTP, 12/12 checks).
- **Not verified here:** Docker/Postgres (no Docker daemon on this box → CI), and live
  `faster-whisper` / `pyannote` / OpenCode / an LM Studio-served model.
- **Next step for the product:** wire the MeetVault client to the API (plan tasks T72–T74). The
  contract is written up in `docs/INTEGRATION.md`; no client code has been written yet.

---

## 2. What was delivered

Scope came from the plan `D:/workspace/MeetVault/.hermes/plans/2026-10-12_125820-meetvault-backend.md`
(tasks T01–T71, plus three *optional* client tasks T72–T74 that are deliberately not done).

| Phase | Tasks | Delivered |
| --- | --- | --- |
| Bootstrap | T01–T06 | repo skeleton, `pyproject.toml`, hermetic test harness, settings |
| Database | T07–T14 | 10 ORM models, hand-written portable migration `20261012_0001`, parity test |
| API shell | T15–T22 | error envelope, JSON logging + request id, bearer auth, health/readyz, system router |
| Storage + media | T23–T27 | traversal-guarded local storage, HTTP Range, ffprobe wrapper, 16 kHz extraction |
| Jobs | T28–T32 | DB queue (portable), handler registry, worker loop, Jobs API, SSE events |
| Providers | T33–T43 | 3 capability protocols, never-raise registry, mock + LM Studio + OpenCode + faster-whisper + pyannote |
| Pipeline | T44–T52 | tolerant transcript parsing, alignment, transcribe/diarize/align/analyze/qa handlers, job chain, status |
| Routers | T53–T60 | meetings CRUD + upload/process/status, media streaming, transcripts, speakers, analysis, Q&A |
| Deployment | T61–T67 | Dockerfile, compose (Postgres + api + worker), CI, docs set, Makefile |
| E2E + acceptance | T68–T71 | hermetic e2e over HTTP, standalone smoke script, acceptance evidence |
| Client hooks | T72–T74 | **not done — optional, awaits the owner's go-ahead** |

---

## 3. Current state (evidence, not claims)

```
177 passed, 2 skipped          pytest -q                     (suite ~40 s)
92% statement coverage         pytest --cov=mvb --cov-report=term-missing
All checks passed!             ruff check src tests && ruff format --check src tests
bridge 33 paths                make openapi
PASS 12/12 checks              bash scripts/smoke.sh         (real uvicorn + worker, exit 0)
alembic upgrade/downgrade      verified on SQLite; Postgres round trip in CI
```
15 commits on `main`. 65 source files, 25 test modules (113 unit / 63 integration / 3 e2e).
The two skips are intentional: a marker-wiring placeholder and the Postgres-only migration check.

The MeetVault client repo is unchanged: `git -C D:/workspace/MeetVault status --short` shows only
the untracked plan directory.

---

## 4. How to run it

### 4.1 Local development (no Docker — the authoring box has none)

```bash
cd /d/workspace/Meetvault_Backend
python -m venv .venv
.venv/Scripts/python.exe -m pip install -U pip
.venv/Scripts/python.exe -m pip install -e ".[dev]"      # Linux/macOS: .venv/bin/python
cp .env.example .env                                     # set MVB_API_KEYS (and POSTGRES_PASSWORD for compose)
.venv/Scripts/python.exe -m alembic upgrade head
.venv/Scripts/python.exe -m uvicorn mvb.main:app --port 8080
# in a second shell:
.venv/Scripts/python.exe -m mvb.jobs.worker
```
Or: `make install`, `make migrate`, `make run`, `make worker`, `make test`, `make lint`,
`make smoke`, `bash scripts/dev.sh [api|worker|migrate|test]`.

Docs UI: `http://localhost:8080/docs` · liveness `/healthz` · readiness `/readyz`.

### 4.2 Docker / compose (Linux target)

```bash
cp .env.example .env      # MVB_API_KEYS and POSTGRES_PASSWORD are REQUIRED
docker compose up -d --build
docker compose logs -f api worker
```
The `api` container applies migrations before serving; `worker` waits for `api` to be healthy,
so migrations are never applied concurrently. `host.docker.internal` is mapped in, which is how
a container reaches LM Studio (or OpenCode) running on the host.

### 4.3 Configuration

All settings are env-only, prefix `MVB_`, defined in `src/mvb/config.py`, documented in
`.env.example`. The ones that matter in practice:

- `MVB_ENV` (`dev`|`prod`|`test`) — `prod` **refuses to start** without `MVB_API_KEYS`.
- `MVB_API_KEYS` — comma-separated bearer keys; compared with `hmac.compare_digest`.
- `MVB_DATABASE_URL` — `sqlite+pysqlite:///./_local.db` by default; compose sets Postgres.
- `MVB_MEDIA_ROOT` — where recordings live (must be writable by api *and* worker).
- `MVB_TRANSCRIBER` / `MVB_DIARIZER` / `MVB_ANALYZER` — `mock` (default) or a real engine.
- `MVB_MAX_UPLOAD_MB`, `MVB_JOB_STALE_SECONDS`, `MVB_WORKER_POLL_SECONDS`,
  `MVB_ANALYZER_TIMEOUT_S`, `MVB_LMSTUDIO_BASE_URL`, `MVB_LMSTUDIO_API_KEY`,
  `MVB_OPENCODE_BASE_URL`, `MVB_OPENCODE_API_KEY`, `MVB_FFMPEG`, `MVB_FFPROBE`, `MVB_LOG_LEVEL`.
- `HF_TOKEN` — only for the real pyannote diarizer (compose passes `MVB_HF_TOKEN` through).

Optional extras (not installed by default): `.[transcribe]` faster-whisper, `.[diarize]`
pyannote+torch, `.[prod]` psycopg.

### 4.4 First-run checklist for a new environment

1. `ffmpeg` and `ffprobe` on PATH (probing and audio extraction depend on them).
2. `.env`: strong `MVB_API_KEYS`, and Postgres password if using compose.
3. `alembic upgrade head` (or let the api container do it).
4. Start **both** the api and the worker — the api never runs jobs itself.
5. `curl -X POST .../v1/meetings` then `/media` then `/process`; poll `/status` until `ready`.
6. Confirm `GET /v1/system/providers` names the provider you think you configured, and whether
   each capability is mock.

---

## 5. Architecture in brief

```
client ──POST /v1/meetings─────────────────────────► meetings row (status=uploaded)
       ──POST /v1/meetings/{id}/media (multipart)──► file on disk + probed duration
       ──POST /v1/meetings/{id}/transcripts────────► transcript v1, aligned to that duration
       ──POST /v1/meetings/{id}/process────────────► job[transcribe] queued, status=processing
worker ──claim──run──succeeded──► enqueue next step ──► … last step ──► meeting status=ready
client ──GET /status  /transcript  /analysis ──────► progress, segments+speakers, summary/agenda/
                                                     decisions/action items (+ Q&A on demand)
```

Five design rules carry most of the weight; keep them when extending:

1. **The API validates and enqueues; the worker executes.** No provider runs inside a request.
2. **The job chain is data, not control flow.** `enqueue_pipeline` creates exactly **one** job;
   the worker asks `next_steps_after(current, params["steps"])` for the next one after a success.
   (Pre-creating the chain *and* chaining in the worker ran steps 2–5 twice — see §8.)
3. **Providers never break startup.** `providers/registry.py` catches every construction failure,
   logs `provider_fallback`, and returns the mock.
4. **Mocks are labelled at three layers** — result dataclass → DB row → API response — plus
   `[mock]`/`[mock:en]` text prefixes and mock timings derived from the **real** probed duration.
5. **Staleness over silent drift.** A document records the transcript it came from; a new
   transcript or media asset marks dependents `stale=True` and `/status` reports `stale_kinds`.

Storage: `$MVB_MEDIA_ROOT/<meeting_id>/<uuid><ext>` (server-generated keys; a client filename
never reaches a path) and `$MVB_MEDIA_ROOT/_work/<meeting_id>/audio16k.wav` for decoded audio.

Database: 10 tables, head `20261012_0001` — `meetings`, `media_assets`, `transcripts`,
`transcript_segments`, `speakers`, `jobs`, `job_events`, `analysis_runs`, `sections`,
`analysis_documents`. Portable types only; the one Postgres-specific statement
(`FOR UPDATE SKIP LOCKED`) is isolated in `src/mvb/jobs/queue.py`.

## 6. API surface (33 paths, all `/v1` behind a bearer key)

- **Meetings:** `POST/GET /meetings`, `GET/PATCH/DELETE /meetings/{id}`,
  `POST/GET /meetings/{id}/media`, `POST /meetings/{id}/process`, `GET /meetings/{id}/status`,
  `GET /meetings/{id}/transcript[/text]`, `GET|POST /meetings/{id}/transcripts`,
  `POST /meetings/{id}/qa`, `GET /meetings/{id}/qa`.
- **Transcripts:** `GET /transcripts/{id}`, `GET /transcripts/{id}/segments`,
  `POST /transcripts/{id}/activate`, `DELETE /transcripts/{id}`.
- **Speakers:** `GET /meetings/{id}/speakers`, `PATCH /speakers/{id}`,
  `POST /speakers/{id}/merge`, `DELETE /speakers/{id}`.
- **Analysis:** `GET /meetings/{id}/analysis`, `/sections`, `/sections/{sid}/summary`,
  `/documents`, `/decisions`, `/action-items`, `/analysis/runs`, `/jobs/latest`.
- **Media:** `GET /media/{id}` (Range-aware), `GET /media/{id}/info`, `DELETE /media/{id}`.
- **Jobs:** `GET /jobs/{id}`, `GET /meetings/{id}/jobs`, `POST /jobs/{id}/cancel`,
  `GET /jobs/{id}/events` (SSE).
- **Ops:** `/healthz`, `/readyz`, `/v1/system/info`, `/v1/system/providers`, `/openapi.json`, `/docs`.

Full detail in `docs/API.md`; the client-facing walkthrough is `docs/INTEGRATION.md`.

---

## 7. Verification evidence

| Check | Command | Observed |
| --- | --- | --- |
| Suite | `.venv/Scripts/python.exe -m pytest -q` | `177 passed, 2 skipped` |
| Coverage | `... --cov=mvb --cov-report=term-missing` | 2702 stmts, 216 missed, **92%** |
| Lint/format | `ruff check src tests` / `ruff format --check` | `All checks passed!` / already formatted |
| OpenAPI | `make openapi` | 33 paths, schema builds |
| Migrations | `alembic upgrade head` / `downgrade base` | 10 tables, round trip OK (SQLite) |
| Live smoke | `bash scripts/smoke.sh` | 12/12 `ok`, `PASS: full flow verified against the live server` |
| Client untouched | `git -C ../MeetVault status --short` | only `?? .hermes/` |
| Real provider path | `pytest tests/integration/test_lmstudio_pipeline.py` | adapter driven through a real job over a real socket; non-mock documents; unreachable engine → job succeeds, flagged mock |

Per-criterion mapping (20 acceptance criteria → evidence) is in `docs/ACCEPTANCE.md`.

---

## 8. Defects found during the build (context for the next person)

1. **Every pipeline step ran twice** — the API pre-created the full chain *and* the worker chained
   the next step. Fixed to "worker creates one step at a time"; the e2e test now asserts each step
   runs exactly once. Invisible to unit tests.
2. **`start_ms: 0` was coerced to `None`** by `or`-chaining while parsing transcripts (plus a gap
   bug in mixed-mode alignment and missing duration propagation). Fixed with explicit `None`
   checks; 11 alignment cases cover it.
3. **`tick()` had no job handlers registered** unless `main()` imported them, so any other caller
   claimed jobs it could not run and failed each with `no handler registered for job type …`
   (retried to the max). Fixed with an idempotent `_ensure_handlers()` inside `tick()`;
   regression test is mutation-proven in a fresh interpreter.
4. **`POST /qa` returned 500 on a missing/blank question** (an unguarded `model_validate`) and its
   request body was undocumented. Now a declared `QARequest` → 422 in the standard envelope, plus
   a real OpenAPI body schema.
5. **`POST /process` body was `Any`** — undocumented; now `ProcessRequest | None`.
6. **`/jobs/latest` ordering tied** on SQLite's one-second timestamps; now ordered by pipeline
   priority, which is exact on any engine.

4–6 were found by the coverage-gap test module added at the end; 1–3 by running the real service.

---

## 9. Known gaps, risks and constraints

**Not verified on this machine (external prerequisites):**
- **Docker image / compose stack** — no Docker daemon on the authoring box. Written and
  YAML-validated; verification is the CI `docker` job.
- **Postgres** — the same suite runs against it in CI via `MVB_TEST_DATABASE_URL`; locally only
  SQLite. The only dialect-specific code is in `jobs/queue.py`.
- **Live engines** — `faster-whisper`, `pyannote` (needs torch + `HF_TOKEN`), OpenCode, and a real
  LM Studio-served model. The LM Studio *adapter* is socket-verified; LM Studio was not running at
  the end of the build, so a call against an actual loaded model remains unverified. Run with
  `MVB_RUN_LIVE=1` to include the opt-in live tests.
- **OpenCode HTTP routes** — deliberately pinned in one function
  (`providers/analysis/opencode.py::_post_json`) because they move between releases; re-pin
  against the target version.

**Design constraints worth knowing:**
- **Job list ordering** (`GET /meetings/{id}/jobs`) is `created_at DESC, id DESC`: chronological
  and exact on Postgres, but on SQLite several jobs of one run share a timestamp and come back in
  a deterministic yet non-chronological order. `/jobs/latest` avoids this by ordering on priority.
- **Single worker by default** (`MVB_WORKER_CONCURRENCY=1`). The queue is safe for several
  workers on Postgres (`SKIP LOCKED`), but concurrency has not been load-tested.
- **No auth rotation, rate limiting, or per-key scoping** — static shared bearer keys only.
- **Deleting a meeting is a soft delete** (rows kept for auditability) and `purge_files=true`
  removes only `<media_root>/<meeting_id>/`; there is no general media-retention GC.
- **`_work/audio16k.wav` is kept** between runs (it is reused to avoid re-decoding); it lives under
  the meeting's own directory and goes away with `purge_files`.
- **Blocking providers inside the worker thread** — fine for mock and HTTP analyzers; a real
  CPU-heavy ASR/Diarizer will block that worker, so scale workers out rather than up.
- **No structured metrics/tracing** — JSON logs with request/job/meeting ids only.

---

## 10. Recommended next steps, in order

1. **Client hooks (T72–T74)** — the only remaining product work: in MeetVault, after a meeting
   ends, create the meeting (`source=meetvault`, `external_id` = the local meeting id), upload the
   WAV/screen MP4, upload the exported live transcript, POST `/process`, poll `/status`, then fetch
   `/transcript` + `/analysis`. Show a badge when `mock` is true. Contract: `docs/INTEGRATION.md`.
2. **Make CI green once** (`.github/workflows/ci.yml`): sqlite matrix, Postgres job, Docker build +
   `docker compose config` + a container smoke. That converts the two remaining "CI-only" rows into
   verified ones.
3. **Pin OpenCode's routes** against the installed version and run one live analysis.
4. **Run a real LM Studio analysis** — start LM Studio, load a model, set
   `MVB_ANALYZER=lmstudio` + `MVB_LMSTUDIO_BASE_URL=http://127.0.0.1:1234/v1`, then re-run
   `scripts/smoke.sh` with the analyzer switched on.
5. **Optional hardening:** key rotation/rate limiting, media retention GC, worker concurrency load
   test, Prometheus-style metrics, and a `/v1/meetings/{id}/export` (docx/markdown) if the client
   wants an exportable report.

---

## 11. Where everything lives

| Thing | Location |
| --- | --- |
| Service repo | `D:/workspace/Meetvault_Backend` (branch `main`, 15 commits) |
| Build plan (work queue, T01–T71) | `D:/workspace/MeetVault/.hermes/plans/2026-10-12_125820-meetvault-backend.md` |
| Progress / source of truth | `docs/progress/progress_supersanta/PROGRESS_2026-10-12.md` |
| Client integration contract | `docs/INTEGRATION.md` |
| Endpoint map | `docs/API.md` · Architecture: `docs/ARCHITECTURE.md` |
| Providers (LM Studio / OpenCode) | `docs/PROVIDERS.md` · Mock rules: `docs/MOCK.md` |
| Acceptance criteria → evidence | `docs/ACCEPTANCE.md` |
| Agent operating rules | `AGENTS.md` (this repo) |
| Client repo (untouched) | `D:/workspace/MeetVault` (WinUI 3 / .NET 8, PR #4 open) |

**Secrets:** none are stored in this repo (`.env` is gitignored; `.env.example` has placeholders
only). A live `MVB_LMSTUDIO_API_KEY` exists in the MeetVault repo's `.env`; GitHub access for the
client repo is via `git credential fill` (do not print either value).

**Authoring environment quirks (they will bite again):**
- Hermes's file tools redact `token=`/`secret=` patterns and can corrupt code containing them.
- Native tools (python, curl, ffmpeg) on Windows **cannot open git-bash `/tmp` paths** — convert
  with `cygpath -w` (see `scripts/smoke.sh`); Windows also locks a SQLite file until the process
  using it exits.
- The repo's `AGENTS.md` is a protected agent-instruction file: writing it needs explicit
  approval, and a timed-out prompt is not consent.
