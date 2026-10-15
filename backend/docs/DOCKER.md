# Running MeetVault backend in Docker

One image, three containers: **Postgres**, the **API** (and the web console), and the **worker**.
Everything you configure lives in one file — `.env` — so switching models, keys or ports never
means editing a Dockerfile.

```bash
cd backend
cp docker.env.example .env      # then set POSTGRES_PASSWORD
docker compose up -d --build
docker compose exec api python -m mvb.admin create-user --email you@example.com
```

Open `http://localhost:8080` (or whatever `MVB_PORT` says) and sign in. Creating the first account
is a one-off command because `docker compose exec` runs it inside the API container, where the
schema and database URL are already correct.

Vendor images are not rebuilt by this: the API and worker share one image, tagged
`meetvault-backend:local` (change `IMAGE_TAG`).

## What each setting does

| Setting | Default | Meaning |
| --- | --- | --- |
| `POSTGRES_PASSWORD` | *(required)* | Password for the bundled Postgres. Compose refuses to start without it. |
| `MVB_PORT` | `8080` | **Host** port for the API and console. Inside the container the API always listens on 8080. |
| `MVB_ENV` | `prod` | Environment label; `dev` also allows interactive docs. |
| `MVB_LOG_LEVEL` | `INFO` | `DEBUG` logs every request and job event. |
| `MVB_ALLOW_SIGNUP` | `true` | Self-service signup. Set `false` on a public host and use `mvb.admin`. |
| `MVB_MAX_UPLOAD_MB` | `4096` | Upload ceiling (client and server both enforce it). |
| `MVB_WORKER_CONCURRENCY` | `1` | Jobs a single worker claims at once. A real ASR job is CPU-bound: scale with more **workers**, not more concurrency. |
| `MVB_JOB_STALE_SECONDS` | `900` | A job whose heartbeat stops is requeued after this. |
| `MVB_SESSION_TTL_DAYS` | `30` | Login token lifetime. |
| `IMAGE_TAG` | `local` | Tag for the built image. |
| `HF_CACHE_DIR` | `./.docker-hf` | Host folder mounted at `/data/hf`, where a real ASR/diarizer caches model weights. |

Every setting must also be listed in `compose.yaml`'s `x-app-env` anchor to reach the containers —
that mapping is what turns a `.env` value into container configuration.

## Using real models

### Analysis (summaries, agenda, decisions, actions, Q&A) — no rebuild needed

The analyzer is an HTTP call, so this is pure configuration:

```bash
# A local LM Studio server on this machine
MVB_ANALYZER=lmstudio
MVB_ANALYZER_MODEL=qwen3.8-27b-gsq-rco     # exact id from GET /v1/models; empty = whatever is loaded
MVB_LMSTUDIO_BASE_URL=http://host.docker.internal:1234/v1
MVB_LMSTUDIO_API_KEY=<your LM Studio key>  # required if the server has auth on; it answers 401 without
MVB_ANALYZER_TIMEOUT_S=600                 # see the warning below

# or the hosted gateway
MVB_ANALYZER=opencode-go
MVB_ANALYZER_MODEL=deepseek-v4.1-flash
MVB_OPENCODE_GO_API_KEY=<your key>
```

`host.docker.internal` is how the container reaches a server on **this** host — compose maps it via
`extra_hosts`, so keep `127.0.0.1` out of the URL.

> **Raise `MVB_ANALYZER_TIMEOUT_S` for reasoning-heavy local models.** A 27B model on this box
> measured ~387–425 s for a full analyze run and 24–42 s for one Q&A answer. At the 120 s default
> the call times out and the pipeline silently degrades to the labelled mock.

### Transcription (ASR) — rebuild once

`faster-whisper` is not in the default image (it would add a large dependency to every deployment),
so turning it on is a build:

```bash
WITH_TRANSCRIBE=true          # in .env
MVB_TRANSCRIBER=faster_whisper
MVB_TRANSCRIBER_MODEL=small   # tiny | base | small | medium | large-v3

docker compose up -d --build
```

The image installs `av<19`: faster-whisper 1.2.x calls `av.open(..., metadata_errors=...)`, which
PyAV removed in 19 — without the pin, every transcription fails with
`TypeError: open() got an unexpected keyword argument 'metadata_errors'`.

Weights download into `HF_CACHE_DIR` on first use, so a rebuild does not re-download them. Expect
CPU-only decoding: start at `small`, and note a long meeting blocks the single worker for minutes.

### Speaker turns (diarization) — rebuild once plus a token

```bash
WITH_DIARIZE=true             # in .env
MVB_DIARIZER=pyannote
MVB_HF_TOKEN=hf_...           # gated model: accept its terms on huggingface.co first

docker compose up -d --build
```

This adds ~2–3 GB (torch). The `MVB_HF_TOKEN` value is passed through as `HF_TOKEN` inside the
containers. Meetings from the current desktop client are single-speaker by construction, so
diarization mostly produces one cluster — enable it only if you need it.

## Checking what is actually running

```bash
docker compose ps                     # service health
curl -s localhost:8080/readyz | python -m json.tool         # effective provider per capability
curl -s -H "Authorization: Bearer $TOKEN" \
     localhost:8080/v1/system/providers | python -m json.tool # same view, with requested vs effective
docker compose logs -f worker         # job-by-job progress
```

`/readyz` needs no token; `/v1/system/providers` is behind the bearer auth like every `/v1` route.

`/readyz` is the honest answer to "is it using my model": each capability reports
`requested`, `effective`, `is_mock`, `model` and, when it fell back, `reason`.

```json
{"transcriber":{"requested":"faster_whisper","effective":"faster_whisper","is_mock":false,"model":"small","reason":"ok"},
 "analyzer":{"requested":"lmstudio","effective":"lmstudio","is_mock":false,"model":"qwen3.8-27b-gsq-rco","reason":"ok"}}
```

A requested engine that cannot start **never blocks the service** — the registry logs
`provider_fallback` and substitutes the labelled mock. Any result carrying `is_mock: true` (and
`[mock]` text prefixes) came from that mock.

## Data, backups and ports

| Volume | Holds | Back up with |
| --- | --- | --- |
| `meetvault-backend_pgdata` | Postgres: meetings, transcripts, analysis, accounts | `docker compose exec -T db pg_dump -U mvb mvb > backup.sql` |
| `meetvault-backend_media` | Uploaded recordings under `/data/media` | copy the volume, or `docker cp` from the container |
| `HF_CACHE_DIR` (host folder) | Model weights | not needed — re-downloadable |

The API publishes `MVB_PORT` on the host; Postgres is **not** published, so it is only reachable
from the compose network. To expose the API behind a reverse proxy, point the proxy at
`http://<host>:<MVB_PORT>` and forward `/` (the console) as well as `/v1`.

## Operating

```bash
docker compose up -d --build     # build + start (after changing engine build args or code)
docker compose up -d             # apply .env changes without rebuilding
docker compose logs -f api worker
docker compose restart worker    # after changing the worker's environment
docker compose down              # stop, keep data
docker compose down -v           # stop and DELETE the database and media volumes
```

Raising `MVB_WORKER_CONCURRENCY` or adding worker replicas is safe: jobs are claimed with
`FOR UPDATE SKIP LOCKED` on Postgres, so two workers never take the same job.

## Single-container runs

For a quick look without Postgres, one container is enough — it applies migrations itself:

```bash
docker run -d --name mvb -p 8080:8080 \
  -e MVB_ENV=dev \
  -e MVB_DATABASE_URL=sqlite+pysqlite:////data/mvb.db \
  -e MVB_MEDIA_ROOT=/data/media \
  -v mvb-data:/data \
  meetvault-backend:local
```

This runs **only the API** — there is no worker in this container, so `POST /process` enqueues jobs
that nothing executes and a meeting sits at `processing` forever. Use compose (or add a second
container with `python -m mvb.jobs.worker` sharing the same `/data` volume) for real work.
