# Progress — 2026-10-15

Contributor: supersanta (Hermes Agent). Scope: the Docker deployment — a configurable image and
compose stack, verified end to end on this box for the first time.

## Docker deployment, verified live (the docs claimed it was CI-only)

- **Owner ask:** "I want it to be deployed through a docker image, with a dockerfile yml, which I can
  set up things like model, api key, port."
- **Context:** every earlier doc says the docker path cannot run here ("no Docker daemon on the
  authoring box" — `HANDOVER.md`, `docs/ARCHITECTURE.md`, the `docker` CI job). Docker Desktop IS
  installed now (engine 29.8.2, Compose v5.6.0), so the path could be — and was — executed.

### Shipped

- **`Dockerfile`**: build-time `ARG`s so the heavy engines are opt-in and the default image stays
  small — `WITH_TRANSCRIBE` (faster-whisper), `WITH_DIARIZE` (pyannote+torch), `APP_EXTRAS`. One pip
  run composes them (`pip install ".[prod,transcribe]"`). `HF_HOME=/data/hf` and `/data/hf` created
  for the non-root user, so a real engine caches weights in a volume instead of re-downloading.
- **`compose.yaml`**: one `x-app-env` anchor carries every runtime knob from `.env` (models, keys,
  timeouts, signup, port, TTL, concurrency); `x-app-build` carries the build args; API publishes
  `${MVB_PORT}:8080`; `HF_CACHE_DIR` bind-mounts a host model cache at `/data/hf`; the worker's
  inherited API healthcheck is disabled.
- **`docker.env.example`**: a commented template — the one file a deployer edits.
- **`docs/DOCKER.md`**: quick start, the full setting table, per-engine recipes (analyzer = config
  only, ASR/diarizer = one build arg), verification via `/readyz` + `/v1/system/providers`, volumes
  and backups, operations, and the single-container caveat.
- **`Makefile`**: `docker-env`, `docker-build-image` (with `EXTRA_ARGS`), `docker-up`,
  `docker-user`, `docker-ps`, `docker-providers`, `docker-down`, `docker-reset`, `docker-logs`.

### Two real defects found by running it

1. **The worker reported "unhealthy" forever.** It inherits the image `HEALTHCHECK`, which curls the
   API's `/healthz` on 8080 — a port the worker does not serve. Fixed with
   `healthcheck: disable: true` on the worker (measured before/after: "Up (unhealthy)" -> "Up").
2. **Real transcription was broken in the image**, and only there: faster-whisper 1.2.1 decodes via
   `av.open(..., metadata_errors="ignore")`, an argument PyAV removed in 19.0, while faster-whisper
   only constrains `av>=11` — so a fresh resolve picked av 19.0.1 and every real transcription died
   with `TypeError: open() got an unexpected keyword argument 'metadata_errors'`. The job retried to
   `max_attempts` and failed. Fixed by pinning `av<19` in the `transcribe` extra (resolves 18.1.0),
   with the reason in a comment. The mock never touches av, so no test could have caught it.

### Evidence (all run this session)

| Check | Result |
| --- | --- |
| `docker build` (default) | exit 0; image 985 MB |
| `docker compose up -d --build` | db -> api -> worker in dependency order, all healthy |
| Container smoke (CI's own flow) | `/healthz` ok, `/readyz` ready, `/v1/meetings` 401, `/` 200, `/assets/app.js` 200, `/openapi.json` 200 |
| **Full pipeline on Postgres** | probe 6000 ms -> `transcribe/diarize/align/analyze` all `succeeded` -> `ready`; `psql` showed 1 meeting / 4 jobs |
| **Real analyzer from the container** | `.env` with `MVB_ANALYZER=lmstudio`, `MVB_ANALYZER_MODEL=qwen3.8-27b-gsq-rco`, `MVB_ANALYZER_TIMEOUT_S=600`, key read from the Hermes `.env` by path -> `/readyz` analyzer `is_mock:false`; a full run stored `run.provider=lmstudio`, `run.is_mock=False`, real prose summary, no `[mock]` text |
| **Real ASR** (`WITH_TRANSCRIBE=true`) | `/readyz` transcriber `is_mock:false model=tiny`; the 17.4 s two-speaker fixture decoded to 3 segments matching ground truth ("The quick brown fox jumped over the lazy dog…", "We should order more coffee…"), `transcript.is_mock=False` |
| Host port override | `MVB_PORT=8097` published 8097 -> container 8080 (verified with `docker compose config` and by serving on 8097) |
| Model-cache bind | `HF_CACHE_DIR=./.docker-hf` resolved to an absolute host path mounted at `/data/hf` |
| Default image still mock | fresh container: all three capabilities `is_mock=True` |

### Files

`backend/Dockerfile`, `backend/compose.yaml`, `backend/docker.env.example` (new),
`backend/docs/DOCKER.md` (new), `backend/Makefile`, `backend/pyproject.toml`, `backend/.gitignore`.

### Decisions

- **The analyzer needs no image change**, so `lmstudio`/`opencode-go` are pure `.env` settings; only
  the in-process engines (faster-whisper, pyannote) are build args. That split is what keeps the
  default image small and the common ask ("use my model + key") a config-only change.
- **Explicit `x-app-env` mapping over `env_file`**: `env_file` would make a missing `.env` fatal and
  hides the resolved values from `docker compose config`; the anchor keeps sane defaults with no
  `.env` at all. Cost: a new server setting must be added to the anchor to be reachable.
- **Migrations stay in the entrypoint** (idempotent) rather than a compose command, so a
  single-container run also boots a complete schema.
- **Not written into the repo:** the live verification `.env` (it held the real LM Studio key) was
  deleted after use; the key is never printed or committed. The ASR verification containers and
  their volumes were removed; the throwaway images were left for the owner to reuse.

### Open threads

- The docs that still claim the Docker path is CI-only (`HANDOVER.md`, `docs/ARCHITECTURE.md`,
  `docs/ACCEPTANCE.md`'s "CI only" row) are NOT yet corrected for this deployment work; the CI
  `docker` job could now be reproduced locally as well.
- The `docker.env.example` list and the `x-app-env` anchor are two places to keep in step; no test
  pins that they agree.

### Blockers needing the user

- None.
