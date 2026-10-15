# Deploying MeetVault backend

Three containers — **Postgres**, the **API** (and the web console), and the **worker** — from one
image. Everything you configure lives in `.env`, so switching models, keys or ports never means
editing a Dockerfile.

```sh
cp .env.example .env      # then set POSTGRES_PASSWORD
docker compose up -d --build
docker compose exec api python -m mvb.admin create-user --email you@example.com
```

Then open `http://localhost:8080` (or whatever `MVB_PORT` says) and sign in. The first account is
created with one command because `docker compose exec` runs it inside the API container, where the
schema and database URL are already correct.

Vendor images are not touched: the API and the worker share one image, `meetvault-backend:local`
(change `IMAGE_TAG`).

## What is in this folder

| File | Purpose |
| --- | --- |
| `Dockerfile` | One image, two commands (the API's default `CMD`, the worker's `command`). Same definition as `backend/Dockerfile`, re-rooted so this folder is self-contained; a unit test pins the two together. |
| `docker-compose.yml` | Postgres + api + worker. Every knob comes from `.env`; the build context is the repository root, so relative paths resolve against this file. |
| `.env.example` | The settings template — the one file a deployer edits. |
| `build.sh` | Builds the image (optionally with real ASR/diarization) and can export a loadable tarball. |
| `docs/DEPLOY.md` | This guide. |

`backend/docs/DOCKER.md` covers the same stack for local development; this folder is the one to
hand to whoever deploys it.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `POSTGRES_PASSWORD` | *(required)* | Password for the bundled Postgres. Compose refuses to start without it. |
| `MVB_PORT` | `8080` | **Host** port for the API and console. Inside the container the API always listens on 8080. |
| `MVB_ENV` | `prod` | Environment label; `dev` also allows interactive docs. |
| `MVB_LOG_LEVEL` | `INFO` | `DEBUG` logs every request and job event. |
| `MVB_ALLOW_SIGNUP` | `true` | Self-service signup. Set `false` on a public host and use `mvb.admin`. |
| `MVB_MAX_UPLOAD_MB` | `4096` | Upload ceiling (client and server both enforce it). |
| `MVB_WORKER_CONCURRENCY` | `1` | Jobs one worker claims at once. A real ASR job is CPU-bound: scale with more **workers**, not more concurrency. |
| `MVB_JOB_STALE_SECONDS` | `900` | A job whose heartbeat stops is requeued after this. |
| `MVB_SESSION_TTL_DAYS` | `30` | Login token lifetime. |
| `IMAGE_TAG` | `local` | Tag for the built image. |
| `HF_CACHE_DIR` | `./.docker-hf` | Host folder mounted at `/data/hf`, where a real ASR/diarizer caches weights. |

`WITH_TRANSCRIBE` and `WITH_DIARIZE` are **build** settings: changing them needs
`docker compose up -d --build`. Everything else applies with `docker compose up -d`.

Every setting must also appear in `docker-compose.yml`'s `x-app-env` anchor to reach the
containers — that mapping is what turns a `.env` value into container configuration.

## Choosing engines

### Analysis (summary, agenda, decisions, actions, Q&A) — configuration only

The analyzer is an HTTP call, so nothing is rebuilt:

```sh
# a local LM Studio server on this machine
MVB_ANALYZER=lmstudio
MVB_ANALYZER_MODEL=qwen3.8-27b-gsq-rco     # exact id from GET /v1/models; empty = whatever is loaded
MVB_LMSTUDIO_BASE_URL=http://host.docker.internal:1234/v1
MVB_LMSTUDIO_API_KEY=<your key>            # required when the server has auth on (it 401s without)
MVB_ANALYZER_TIMEOUT_S=600                 # see the note below

# or the hosted gateway
MVB_ANALYZER=opencode-go
MVB_ANALYZER_MODEL=deepseek-v4.1-flash
MVB_OPENCODE_GO_API_KEY=<your key>
```

`host.docker.internal` is how a container reaches a server on **this** host; compose maps it, so
do not put `127.0.0.1` in the URL.

> **Raise `MVB_ANALYZER_TIMEOUT_S` for reasoning-heavy local models.** A 27B model measured
> ~387–425 s for a full analyze run and 24–42 s for a single Q&A answer. At the 120 s default the
> call times out mid-run and that step silently degrades to the labelled mock.

### Transcription (ASR) — one build arg

```sh
WITH_TRANSCRIBE=true          # in .env
MVB_TRANSCRIBER=faster_whisper
MVB_TRANSCRIBER_MODEL=small   # tiny | base | small | medium | large-v3
docker compose up -d --build
```

The image installs `av<19`; faster-whisper 1.2.x calls `av.open(..., metadata_errors=...)`, which
PyAV removed in 19, so without the pin every transcription fails with
`TypeError: open() got an unexpected keyword argument 'metadata_errors'`.

Weights download into `HF_CACHE_DIR` on first use, so a rebuild does not re-download them.
Decoding is CPU-only: start at `small`, and note that a long meeting blocks the single worker.

### Speaker turns (diarization) — one build arg plus a token

```sh
WITH_DIARIZE=true             # in .env
MVB_DIARIZER=pyannote
MVB_HF_TOKEN=hf_...           # gated model: accept its terms on huggingface.co first
docker compose up -d --build
```

Adds ~2–3 GB (torch). The token is passed through as `HF_TOKEN` inside the containers. Meetings
from the current desktop client are single-speaker by construction, so this mostly produces one
cluster — enable it only if you need it.

## Building the image on a machine that cannot build it

`build.sh` is the reproducible route, and `--save` produces one file to move:

```sh
./build.sh --tag 1.0.0 --save            # -> dist/meetvault-backend-1.0.0.tar.gz
./build.sh --tag 1.0.0 --with-transcribe --save
```

It prints the **image ID** and the **archive sha256** when it finishes; quote the image ID when you
need to prove which image was built, since a tag can be moved.

The export is ~248 MB compressed for the default image (~1 GB loaded), so it is **not committed to
git** — GitHub rejects any file over 100 MB. A prebuilt export ships beside the other release
artifacts in `../delivery/meetvault-backend-1.0.0.tar.gz`
(sha256 `63F93274A57370C265F3E3B06FB4E3FF08F9C03E3E018F466035A8BDDB092383`, matching the
`verify-build` image); build your own with the command above when you need a different engine set.

On the target machine:

```sh
gunzip -c meetvault-backend-1.0.0.tar.gz | docker load
```

Then point `IMAGE_TAG` at the loaded tag in `.env` and start the stack.

On a server you can also build from source instead of loading an archive:

```sh
git clone <repo> && cd <repo>/deploy
cp .env.example .env && $EDITOR .env
docker compose up -d --build
```

## Verifying what is actually running

```sh
docker compose ps                     # service health
curl -s localhost:8080/readyz | python -m json.tool          # effective provider per capability
docker compose logs -f worker         # job-by-job progress
```

`/readyz` needs no token and is the honest answer to "is it using my model": each capability
reports `requested`, `effective`, `is_mock`, `model` and, when it fell back, `reason`.

```json
{"transcriber":{"requested":"faster_whisper","effective":"faster_whisper","is_mock":false,"model":"small","reason":"ok"},
 "analyzer":{"requested":"lmstudio","effective":"lmstudio","is_mock":false,"model":"qwen3.8-27b-gsq-rco","reason":"ok"}}
```

A requested engine that cannot start **never blocks the service**: the registry logs
`provider_fallback` and substitutes the labelled mock. Anything with `is_mock: true` (and `[mock]`
text prefixes) came from that mock — the console and the desktop app both show that badge.

The same view per authenticated user: `GET /v1/system/providers` with a bearer token.

## Data, backups and ports

| Volume | Holds | Back up with |
| --- | --- | --- |
| `meetvault-backend_pgdata` | Postgres: meetings, transcripts, analysis, accounts | `docker compose exec -T db pg_dump -U mvb mvb > backup.sql` |
| `meetvault-backend_media` | Uploaded recordings (`/data/media`) | copy the volume, or `docker cp` from the container |
| `HF_CACHE_DIR` (host folder) | Model weights | not needed — re-downloadable |

The API publishes `MVB_PORT` on the host; Postgres is **not** published, so it is reachable only
from the compose network. Behind a reverse proxy, forward `/` (the console) as well as `/v1`.

## Operating

```sh
docker compose up -d --build     # build + start (after changing build args or the code)
docker compose up -d             # apply .env changes without rebuilding
docker compose logs -f api worker
docker compose restart worker    # after changing the worker's environment
docker compose down              # stop, keep data
docker compose down -v           # stop and DELETE the database and media volumes
```

Adding worker replicas or raising concurrency is safe: jobs are claimed with
`FOR UPDATE SKIP LOCKED` on Postgres, so two workers never take the same job.

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| Compose refuses to start: `set POSTGRES_PASSWORD` | `.env` is missing or empty. `cp .env.example .env` and set it. |
| A capability reports `is_mock: true` while you set a real engine | Read the `reason` in `/readyz` — `package not installed` means the build arg is off (rebuild), `unreachable or no model served` means the URL/key/model id is wrong. |
| Meetings sit at `processing` forever | No worker is running, or it cannot reach the database. Check `docker compose ps` and `docker compose logs worker`. |
| Transcription fails with `metadata_errors` | The image was built without the `av<19` pin (an older checkout). Rebuild. |
| Analysis times out and degrades to mock | Raise `MVB_ANALYZER_TIMEOUT_S` (600 for a 27B local model). |
| Port already in use | Change `MVB_PORT` in `.env`; the container's 8080 is fixed. |
| Signup returns `403 signup_disabled` | `MVB_ALLOW_SIGNUP=false`. Create accounts with `mvb.admin`. |
| Postgres logs `password authentication failed for user "mvb"`, or `docker compose down -v` in another folder wiped this stack | Two compose stacks are sharing a project name, so they share the `pgdata`/`media` volumes. Give each a distinct `COMPOSE_PROJECT_NAME` in its `.env` (this folder also needs the matching `POSTGRES_PASSWORD` for a volume it already created). |

## Single-container runs (not for real work)

One container is enough for a look around — it applies its own migrations:

```sh
docker run -d --name mvb -p 8080:8080 \
  -e MVB_ENV=dev \
  -e MVB_DATABASE_URL=sqlite+pysqlite:////data/mvb.db \
  -e MVB_MEDIA_ROOT=/data/media \
  -v mvb-data:/data \
  meetvault-backend:local
```

This runs **only the API**. There is no worker in that container, so `POST /process` enqueues jobs
that nothing executes and the meeting stays at `processing` indefinitely. Use compose — or add a
second container running `python -m mvb.jobs.worker` against the same `/data` volume — for real
work.
