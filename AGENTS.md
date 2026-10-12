# AGENTS.md — Meetvault_Backend

## What this is

An independent REST backend for the MeetVault Windows app. It never modifies the client and
never runs during a live meeting. Post-meeting processing only.

## Hard rules

- Python 3.11+ (dev box has 3.14; CI runs 3.12 and 3.13). Type hints on every public function.
- Portable SQLAlchemy types only: `Uuid`, `String`, `Text`, `Integer`, `BigInteger`, `Boolean`,
  `Float`, `DateTime(timezone=True)`, `JSON`. No `JSONB`/`ARRAY`/`INET`, no PG-only server
  defaults. Postgres-specific SQL is allowed ONLY in `src/mvb/jobs/queue.py`.
- Every AI capability goes through its protocol in `src/mvb/providers/base.py`. The factory in
  `registry.py` NEVER raises: on any failure it returns the mock and logs a warning.
- Mock output must be labelled: set `is_mock=True` on the result dataclass, propagate it to the
  DB row, and include it in every API response that carries mock-derived content. Mock text
  fields start with `[mock]`.
- No secrets in code, tests, logs or docs. Auth is env-based bearer keys (`MVB_API_KEYS`).
- Uploads stream to disk; never `await file.read()`. Storage keys are server-generated, so a
  client filename can never influence a path.
- Tests: TDD (write the test, watch it fail, then implement). Every new endpoint needs an
  integration test asserting the status code AND the response body shape.
- Commit per task. Never `git push`. Never `git add -A` while untracked payload dirs exist.

## Layout

```
src/mvb            application package (config, db, api, storage, jobs, media, providers, pipeline)
tests/unit         pure logic (no DB, no HTTP)
tests/integration  API + DB through the in-process TestClient
tests/e2e          full pipeline end-to-end with mock providers
alembic/versions   migrations (hand-written, portable)
docs/              ARCHITECTURE.md, API.md, PROVIDERS.md, MOCK.md, INTEGRATION-MEETVAULT.md
```

## Commands

    .venv/Scripts/python.exe -m pytest -q                 # full suite
    .venv/Scripts/python.exe -m ruff check src tests      # lint
    .venv/Scripts/python.exe -m ruff format --check src tests
    .venv/Scripts/python.exe -m alembic upgrade head      # migrate

On Linux/macOS the interpreter path is `.venv/bin/python`.

## A new provider = three steps

1. New module under `src/mvb/providers/<capability>/` exposing `id`, `is_mock` and the
   protocol's method.
2. One line in `src/mvb/providers/registry.py` that constructs it, wrapped in the
   try/except fallback.
3. One test pinning the happy path and the fallback reason.
