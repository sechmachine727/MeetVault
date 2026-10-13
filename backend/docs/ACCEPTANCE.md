# Acceptance criteria and evidence

Each row is a requirement from the plan, how it is met, and the evidence that it holds.
Anything not verified on this machine is labelled as such rather than claimed.

| # | Requirement | How it is met | Evidence |
| --- | --- | --- | --- |
| 1 | Independent backend, no changes to the client's recording or live transcription | Separate repo/service; the client is untouched and only talks HTTP. MeetVault's local pipeline (Vosk, overlay, ScreenCapture) is not referenced by any backend code | `git -C ../MeetVault status --short` shows only the plan file; no import of MeetVault code exists |
| 2 | Upload finished recordings, list/update/delete meetings | `POST/GET /v1/meetings`, `PATCH/DELETE /v1/meetings/{id}`, `POST /v1/meetings/{id}/media` (multipart, streamed to disk) | `tests/e2e/test_full_flow.py::test_meeting_crud_lifecycle_and_auth`, `tests/integration/test_meetings_api.py` |
| 3 | Asynchronous processing with status tracking | DB-backed job queue + worker, one job row per step, append-only `job_events`, `GET /v1/meetings/{id}/status`, SSE `GET /v1/jobs/{id}/events` | `tests/e2e/test_full_flow.py::test_full_mock_pipeline_over_http` (asserts `status=ready`, `progress=1.0`) |
| 4 | Retrieve media, transcripts, speakers, timestamps, analysis | `GET /v1/media/{id}` (Range 206/416), `/transcript`, `/transcript/text`, `/speakers`, `/analysis`, `/sections`, `/decisions`, `/action-items`, `/documents` | e2e assertions on ranges (`content-range`, 416) and every artifact |
| 5 | Higher-accuracy multilingual transcription via a pluggable provider | `Transcriber` protocol + `faster_whisper` implementation, `language` per job (en/ja/vi/auto), lazily imported so the core install stays light | `tests/unit/test_registry_and_http_providers.py`; provider swap is one env var |
| 6 | Speaker identification / diarization | `Diarizer` protocol + `pyannote` implementation; per-meeting `Speaker` rows, segments labelled by largest overlap, rename + merge endpoints | `test_diarize_labels_segments_and_is_idempotent`; speaker stats in the e2e test |
| 7 | Timestamp alignment with the recording | Untimed client transcripts are distributed over the probed media duration; timed ones are rescaled. `align_segments` keeps segments contiguous, non-overlapping and inside `[0, duration]` | `tests/unit/test_align.py` (11 cases incl. mixed/degenerate), e2e `segments[-1].end_ms == duration_ms` |
| 8 | Agenda, section summaries, summary, decisions, action items, section Q&A | `analyze` job: agenda → sections → per-section summary → summary → decisions/action items; `qa` job supports `section_id` scoping | e2e `analysis` bundle assertions (sections, `len(section_summaries) == len(sections)`, decisions/actions) |
| 9 | Analysis results stay synchronised with the media | Every document records its transcript; a new transcript/media marks older documents `stale`; re-analysis supersedes its predecessor; `/status` exposes `stale_kinds` | `test_analyze_reruns_create_a_new_run_and_stale_the_previous_documents`, `test_status_reports_progress_and_staleness`, e2e import-then-stale |
| 10 | Local LM Studio and OpenCode Go providers, modular and configurable | `Analyzer` protocol with `mock`, `lmstudio` (OpenAI-compatible, JSON schema, hard timeout) and `opencode-go` (managed gateway + session contract); transcription/diarization/analysis are separate capabilities | `tests/unit/test_registry_and_http_providers.py` (respx) **plus** `tests/integration/test_lmstudio_pipeline.py`, which drives the real LM Studio adapter through a real analyze job over a real socket (model id, bearer header, `response_format`, non-mock documents) |
| 11 | Deterministic mocks; the workflow works with no AI engine | `mock` is the default for all three capabilities; mocks are deterministic and derive durations from the real media | `tests/unit/test_mock_providers.py`, `docs/MOCK.md` |
| 12 | Mock output is clearly distinguishable | `is_mock` on provider result → DB row → API response, plus `[mock]` / `[mock:en]` text prefixes and a 12-hex digest, plus a single `mock` flag on `/status` and the analysis bundle | asserted line-by-line in the e2e test; `docs/MOCK.md` |
| 13 | A missing provider must not prevent startup | The registry factory never raises: it logs `provider_fallback` and returns the mock; `/v1/system/providers` reports availability, selection and the fallback reason | `test_registry_and_http_providers.py::test_factory_falls_back_*`, `GET /v1/system/providers`, and `test_lmstudio_pipeline.py::test_lmstudio_analyzer_falls_back_to_mock_when_unreachable` (whole pipeline, unreachable engine, job still succeeds and is flagged mock) |
| 14 | Seamless client integration: upload, poll, fetch improved results | Documented call-by-call in `docs/INTEGRATION.md` with exact payloads, the alignment rule, staleness, and the `enqueued: 0` idempotency semantics | Docs + e2e test executing that exact flow over HTTP |
| 15 | Linux/Docker deployment with persistent storage | `Dockerfile` (python:3.12-slim + ffmpeg, non-root, healthcheck), `compose.yaml` (Postgres + api + worker, named volumes, `host.docker.internal` for LM Studio) | YAML parses; `docker compose config` **CI-only** — no Docker daemon on the dev box |
| 16 | Database migrations | Alembic, hand-written portable migrations `20261012_0001` → `20261014_0002` creating all 12 tables | `alembic upgrade head` + `downgrade base` verified locally on SQLite; Postgres round trip in CI |
| 17 | Environment configuration | `pydantic-settings`, `MVB_*` prefix, `.env.example`; `MVB_ENV=prod` refuses to start without API keys | `tests/unit/test_config.py` |
| 18 | Authentication and validation | Static bearer keys compared with `hmac.compare_digest`; 401 on every `/v1` route without it; pydantic validation everywhere | `tests/integration/test_auth.py`, e2e auth assertions |
| 19 | Structured logging, health checks, OpenAPI | JSON logs (`python-json-logger`) with request id, job id and meeting id bound; `/healthz` + `/readyz`; OpenAPI at `/openapi.json` and `/docs` | `tests/unit/test_logging.py`, `tests/integration/test_health.py`, `make openapi` → 33 paths |
| 20 | Unit, integration and end-to-end tests | 157 passing (unit + integration + e2e), 2 intentional skips (a marker-wiring placeholder and the Postgres-only migration check) | `pytest -q` on the dev box; the same suite runs against Postgres in CI via `MVB_TEST_DATABASE_URL`; `bash scripts/smoke.sh` (or `make smoke`) proves the deployed shape: real uvicorn + worker over HTTP, exit 0 |

## Known limits (stated, not hidden)

- **Docker and Postgres are not exercised on this machine** — there is no Docker daemon here.
  The image, compose file and Postgres job are validated in CI (`.github/workflows/ci.yml`).
- **Real AI engines**: the LM Studio adapter is now exercised end to end through a real job and a
  real socket (`tests/integration/test_lmstudio_pipeline.py`, including the unreachable-engine
  fallback). The remaining ones — a real `faster_whisper`/`pyannote` model and a live OpenCode
  instance — need `MVB_RUN_LIVE=1` plus the engine installed; LM Studio itself was not running on
  this box at the end of the build, so a call against a served model is still unverified.
- **OpenCode Go's client contract** is pinned in `providers/analysis/opencode_go.py`: an
  identifying `User-Agent` plus a stable `x-opencode-session` per instance (the managed
  gateway requires both; generic SDK names risk blocks).
- **Job list ordering** is `created_at DESC, id DESC`. On Postgres that is chronological; on
  SQLite, whose timestamps have one-second resolution, jobs created within the same second
  come back in a deterministic but not chronological order.
- The client (MeetVault) has no code in this repo and was deliberately not modified.
