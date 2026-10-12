# API map

All `/v1` routes require `Authorization: Bearer <key>`.

## Health & system

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/healthz` | liveness (process is up) |
| GET | `/readyz` | readiness (DB + media root reachable) |
| GET | `/v1/system/info` | env, migration head, configured providers |
| GET | `/v1/system/providers` | which providers are available, which is selected, mock state |
| GET | `/openapi.json`, `/docs` | OpenAPI schema and Swagger UI |

## Meetings

| Method | Path | Purpose |
| --- | --- | --- |
| POST | `/v1/meetings` | create (`source`+`external_id` dedupe, 200 on repeat) |
| GET | `/v1/meetings` | list (`q`, `status`, `include_deleted`, `limit`, `offset`) |
| GET | `/v1/meetings/{id}` | detail (counts, active transcript, has_analysis) |
| PATCH | `/v1/meetings/{id}` | title/language/times/metadata |
| DELETE | `/v1/meetings/{id}` | soft delete (+ purge media files) |
| POST | `/v1/meetings/{id}/media` | upload a recording (multipart) |
| GET | `/v1/meetings/{id}/media` | list recordings |
| POST | `/v1/meetings/{id}/transcripts` | import a client transcript (aligned to the media) |
| GET | `/v1/meetings/{id}/transcript` | active transcript + segments |
| GET | `/v1/meetings/{id}/transcript/text` | active transcript as plain text |
| GET | `/v1/meetings/{id}/transcripts` | transcript versions |
| POST | `/v1/meetings/{id}/process` | start the pipeline (202) |
| GET | `/v1/meetings/{id}/status` | progress, per-step state, staleness, mock flag |

## Media

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/v1/media/{asset_id}` | stream the file, `Range` aware (206/416) |
| GET | `/v1/media/{asset_id}/info` | asset metadata |
| DELETE | `/v1/media/{asset_id}` | delete file + row |

## Transcripts & speakers

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/v1/transcripts/{id}` | one version with segments |
| GET | `/v1/transcripts/{id}/segments` | paged segments (`start_ms`, `end_ms`) |
| POST | `/v1/transcripts/{id}/activate` | make a version active |
| DELETE | `/v1/transcripts/{id}` | delete a non-active version (409 if active) |
| GET | `/v1/meetings/{id}/speakers` | speakers with segment count / speaking time |
| PATCH | `/v1/speakers/{id}` | rename (relabels its segments) |
| POST | `/v1/speakers/{id}/merge` | fold a duplicate speaker into another |
| DELETE | `/v1/speakers/{id}` | remove a speaker (segments keep their text) |

## Analysis

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/v1/meetings/{id}/analysis` | bundle: summary, agenda, sections, decisions, actions, Q&A |
| GET | `/v1/meetings/{id}/sections` | agenda sections of the newest run |
| GET | `/v1/meetings/{id}/sections/{sid}/summary` | newest summary document for a section |
| GET | `/v1/meetings/{id}/documents` | documents (`kind`, `include_stale`) |
| GET | `/v1/meetings/{id}/decisions` | newest decisions document |
| GET | `/v1/meetings/{id}/action-items` | newest action-items document |
| GET | `/v1/meetings/{id}/analysis/runs` | analysis history |
| POST | `/v1/meetings/{id}/qa` | ask a question (202 + job) |
| GET | `/v1/meetings/{id}/qa` | previously asked questions |

## Jobs

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/v1/jobs/{job_id}` | job + recent events |
| GET | `/v1/meetings/{id}/jobs` | job history for a meeting |
| POST | `/v1/jobs/{job_id}/cancel` | cooperative cancel (idempotent) |
| GET | `/v1/jobs/{job_id}/events` | SSE stream, closes on a terminal state |

## Pipeline

`media_probe → transcribe → diarize → align → analyze`, then `qa` on demand.

- `media_probe`: ffprobe every asset, set the meeting duration from the audio.
- `transcribe`: decode to 16 kHz mono, run the transcriber, store a **new transcript version**
  and deactivate the previous one.
- `diarize`: label the active transcript's segments with per-meeting speakers.
- `align`: rescale segment times onto the media duration (idempotent).
- `analyze`: agenda → sections → per-section summaries → summary → decisions/action items.
- `qa`: retrieve the top-k segments, answer with citations, store a `qa` document.

Every job row carries `is_mock`, `progress`, `error` and an append-only `job_events` trail.
