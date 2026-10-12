# Meetvault_Backend

Standalone REST backend for [MeetVault](../MeetVault): ingests finished meeting recordings,
re-processes them asynchronously (transcription, diarization, LLM analysis) and serves
meetings, media, transcripts, speakers, jobs and analysis over HTTP.

Runs on Linux/Docker. **No AI engine is required to run it**: every AI capability has a
deterministic mock provider, and a missing provider never stops the service (see
`docs/MOCK.md`).

## Quick start (dev, no Docker)

    python -m venv .venv
    .venv/Scripts/python.exe -m pip install -U pip
    .venv/Scripts/python.exe -m pip install -e ".[dev]"      # Linux/macOS: .venv/bin/python
    cp .env.example .env
    .venv/Scripts/python.exe -m alembic upgrade head
    .venv/Scripts/python.exe -m uvicorn mvb.main:app --port 8080
    # API docs: http://localhost:8080/docs   Health: /healthz   Readiness: /readyz

## Quick start (Docker)

    cp .env.example .env      # set MVB_API_KEYS and POSTGRES_PASSWORD
    docker compose up --build

## Provider configuration

| Capability | Config key | Values | Notes |
|---|---|---|---|
| Transcription | `MVB_TRANSCRIBER` | `mock` (default), `faster_whisper` | needs the `transcribe` extra |
| Diarization | `MVB_DIARIZER` | `mock` (default), `pyannote` | needs the `diarize` extra + `HF_TOKEN` |
| LLM analysis | `MVB_ANALYZER` | `mock` (default), `lmstudio`, `opencode` | HTTP providers; see `docs/PROVIDERS.md` |

A requested provider that cannot be constructed (missing package, unreachable host)
**falls back to the mock** and the reason is reported by `GET /v1/system/providers`
and `/readyz`. Mock output is always labelled `is_mock: true` and its text fields carry
a `[mock]` marker, so it can never be mistaken for real analysis.

## Documentation

- `docs/ARCHITECTURE.md` — processes, job chain, storage layout, verification status
- `docs/API.md` — every endpoint, grouped
- `docs/INTEGRATION.md` — the client flow, call by call, with exact payloads
- `docs/PROVIDERS.md` — capability interfaces, LM Studio and OpenCode setup
- `docs/MOCK.md` — what exactly is synthetic and how to tell
- `docs/ACCEPTANCE.md` — acceptance criteria with the evidence for each

## Architecture in one paragraph

The API only validates and enqueues; the worker claims jobs from the database queue and runs
`media_probe → transcribe → diarize → align → analyze`, creating each step only after the
previous one succeeds. Results are versioned (a new transcript version deactivates the old
one) and analysis documents are marked `stale` instead of being silently overwritten, so the
client can always tell whether what it shows still matches the recording.

## Status

155 tests pass on the dev box (SQLite); `ruff` is clean; the OpenAPI schema builds (33 paths);
a live `uvicorn` + worker run was exercised end to end over HTTP on 2026-10-12. Docker/Postgres
and the real AI engines are CI- or opt-in-verified only — see `docs/ACCEPTANCE.md` for exactly
what was and was not verified where.
