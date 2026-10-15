# deploy — ship the MeetVault backend

Everything needed to run the backend as containers: the image definition, the stack, the settings
template, and the guide. Start with **[docs/DEPLOY.md](docs/DEPLOY.md)**.

```sh
cp .env.example .env      # set POSTGRES_PASSWORD; models/keys/port as needed
docker compose up -d --build
docker compose exec api python -m mvb.admin create-user --email you@example.com
```

| File | What it is |
| --- | --- |
| `Dockerfile` | One image, two commands — the API (default `CMD`) and the worker (`command:`) — with optional real ASR/diarization built in via `--build-arg`. |
| `docker-compose.yml` | Postgres + api + worker. Every runtime setting comes from `.env`; the build context is the repository root. |
| `.env.example` | The settings template: models, API keys, port, timeouts, signup, project name. |
| `build.sh` | Builds the image (optionally `--with-transcribe` / `--with-diarize`) and can export it with `--save`. |
| `docs/DEPLOY.md` | The full guide: settings table, engine recipes, verification, backups, troubleshooting. |
| `.gitignore` | Keeps `dist/`, the model cache and your real `.env` out of git. |

## The image

The image is ~1 GB (~1.6 GB with ASR), so it is **not** stored in this folder or in git — GitHub
rejects any file over 100 MB, and the compressed export is ~250 MB:

```sh
./build.sh --tag 1.0.0 --save      # -> dist/meetvault-backend-1.0.0.tar.gz
```

`build.sh` prints the image ID and the archive sha256, so a shipped image can be identified
exactly (a tag can be moved; an image ID cannot). One prebuilt export is kept beside the other
release artifacts in [`../delivery/`](../delivery/) (`meetvault-backend-1.0.0.tar.gz`,
sha256 `63F93274…`); load it on the target with:

```sh
gunzip -c meetvault-backend-1.0.0.tar.gz | docker load
```

**Why the image is not committed:** a 250 MB binary in git would be permanent — git cannot drop it
from history without rewriting, and every clone would carry it. A checkout plus `./build.sh`
reproduces the image exactly, and the file is handed over with the release artifacts instead.

## Two things that will bite you

- **Keep `COMPOSE_PROJECT_NAME` distinct** from any other stack in this repository
  (`backend/compose.yaml` uses `meetvault-backend`). Two compose files sharing a project name share
  volumes, so `docker compose down -v` in one folder deletes the other's database and recordings —
  observed while this folder was built.
- **A real analyzer needs no rebuild**, but the local model server does need the right base URL:
  inside a container "this host" is `host.docker.internal`, not `127.0.0.1`. And raise
  `MVB_ANALYZER_TIMEOUT_S` — a 27B local model measured 387–425 s for one analyze run against the
  120 s default, which would time out and silently fall back to the mock.

`backend/Dockerfile` and `backend/compose.yaml` are the same stack for local development;
`backend/tests/unit/test_docker_assets.py` pins the two definitions together so they cannot drift.
