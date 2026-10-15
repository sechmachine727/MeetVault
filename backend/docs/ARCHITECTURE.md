# Architecture

## Two processes, one image

| Process | Entry point | Role |
| --- | --- | --- |
| API | `uvicorn mvb.main:app` | HTTP only. Validates, writes DB rows, enqueues jobs. Never runs a provider. |
| Worker | `python -m mvb.jobs.worker` | Claims jobs from the DB queue and runs the pipeline. No HTTP. |

The API is stateless apart from the DB and the media root, so it can be scaled horizontally.
Jobs are claimed with `FOR UPDATE SKIP LOCKED` on Postgres (a plain UPDATE … on SQLite), so
several workers can run without stepping on each other. `job_stale_seconds` (default 900)
reaps jobs whose heartbeat stopped, and `attempts`/`max_attempts` bound retries.

`docker compose` starts Postgres + api + worker; the api applies migrations before serving and
the worker waits for the api to be healthy, so migrations are never applied twice at once.

## Request → job flow

```
client ──POST /v1/meetings──────────────────────────────► meetings row (status=uploaded)
       ──POST /v1/meetings/{id}/media (multipart)───────► file on disk + probed metadata
       ──POST /v1/meetings/{id}/transcripts────────────► transcript v1 (aligned to duration)
       ──POST /v1/meetings/{id}/process────────────────► job[media_probe|transcribe] queued
                                                          status=processing
worker ──claim ──run─────────────succeeded──► enqueue next step ──► …
                                                last step ──► status=ready
client ──GET  /v1/meetings/{id}/status──────────────────► per-step state + progress
       ──GET  /v1/meetings/{id}/transcript──────────────► segments (times, speakers)
       ──GET  /v1/meetings/{id}/analysis────────────────► summary, agenda, decisions, actions
```

The chain is **data, not control flow**: each job carries `params["steps"]`, the requested
step list, and the worker asks `next_steps_after(current_type, steps)` which step to create
next. One job is created at a time, so a failed step never leaves a queue of jobs that will
never run, and `enqueue_pipeline` creating exactly one job is what makes `POST /process`
idempotent (a second call returns `enqueued: 0`).

## Pipeline

| Step | Handler | What it does |
| --- | --- | --- |
| `media_probe` | `pipeline/media.py` | ffprobe each asset, set `meeting.duration_ms` from the audio, mark analysis stale |
| `transcribe` | `pipeline/transcribe.py` | decode to 16 kHz mono, run the transcriber, store a NEW transcript version, deactivate the old one |
| `diarize` | `pipeline/diarize.py` | run the diarizer, create per-meeting `Speaker` rows, label segments by largest overlap |
| `align` | `pipeline/alignment_service.py` | rescale segment times onto the media duration (idempotent) |
| `analyze` | `pipeline/analyze.py` | agenda → sections → per-section summaries → summary → decisions + action items |
| `qa` | `pipeline/qa.py` | lexical top-k retrieval, answer with cited segment ids, store a `qa` document |

## Storage layout

```
$MVB_MEDIA_ROOT/<meeting_id>/<uuid><ext>   uploaded recordings (never the client filename)
$MVB_MEDIA_ROOT/_work/<meeting_id>/audio16k.wav   decoded audio (deleted with the meeting dir)
```

Storage keys are server-generated UUIDs, and `LocalStorage._resolve` refuses any key that
would escape the root, so a hostile filename cannot influence a path. Uploads stream to a
`.part` file and are renamed only after the hash is complete.

## Database

12 tables: `users`, `auth_tokens`, `meetings`, `media_assets`, `transcripts`,
`transcript_segments`, `speakers`, `jobs`, `job_events`, `analysis_runs`, `sections`,
`analysis_documents`. Migration head is `20261014_0002`, hand-written and portable (SQLite +
Postgres). Hard deletes cascade at the
DB level (`ON DELETE CASCADE`), except `transcript_segments.speaker_id` which is `SET NULL`
so deleting a speaker never deletes transcript text.

## Timestamps and drift

MeetVault's exported transcript carries only `Speaker` + `Text`. The backend therefore
distributes untimed segments across the **probed media duration** (`align` mode
`proportional`), rescales existing times onto that duration (`scale`), or keeps times and
fills only the gaps (`auto`, the default for imports). Everything the client reads is
expressed on the same timeline as the recording, so playback can never drift.

## Staleness over silent drift

An analysis document records the transcript it was derived from. When that transcript is
replaced — a new ASR run, or the client activating another version — the old documents are
marked `stale` rather than deleted, and `GET /status` reports `stale_kinds`. Re-running an
analysis marks its predecessor stale so "newest fresh document per kind" is unambiguous.

## Verification status

| Area | Verified how |
| --- | --- |
| Unit + integration + e2e suite (153 tests) | `pytest -q` on the dev box (SQLite), 2 intentional skips |
| Migrations up/down | `alembic upgrade head` / `downgrade base` locally; Postgres round trip in CI |
| OpenAPI | `make openapi` → 33 paths, schema builds with no errors |
| Job chain end to end | `tests/e2e/test_full_flow.py` drives upload → process → worker → results over HTTP |
| Docker image, compose, Postgres | CI only (`docker` and `postgres` jobs) — **not** runnable on the dev box (no Docker daemon) |
| Real providers (faster-whisper, pyannote, LM Studio, OpenCode) | Interfaces + HTTP clients unit-tested with `respx`; a live call is skipped unless `MVB_RUN_LIVE=1` |
