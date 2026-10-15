# Progress — 2026-10-15

Contributor: supersanta (Hermes Agent). Scope: the `deploy/` folder — a shippable image, stack and
guide — plus a deployment bug found by running two stacks at once.

## deploy/ : the deployment folder

- **Owner ask:** "Add that to deploy folder. both the image, the example compose yaml and the guide."
- **Shipped:** `deploy/` is self-contained and tracked —
  - `Dockerfile` — the same image definition as `backend/Dockerfile` with the `COPY` paths
    re-rooted, so the folder builds from the repository root with no dependency on the dev tree's
    layout.
  - `docker-compose.yml` — Postgres + api + worker, every runtime knob from `.env`, build context
    `..` with `dockerfile: deploy/Dockerfile`.
  - `.env.example` — models, API keys, port, timeouts, signup, project name.
  - `build.sh` — builds the image (optionally `--with-transcribe` / `--with-diarize`) and
    `--save` exports a loadable tarball, printing the image ID and archive sha256.
  - `docs/DEPLOY.md` — settings table, engine recipes, verification, volumes/backups, operations,
    troubleshooting.
  - `README.md` — the folder's entry point.
  - `.gitignore` — keeps `dist/`, `.docker-hf/` and the real `.env` out of git.
- **The image itself:** exported and verified, but deliberately **not committed**. Measured:
  `docker save | gzip -1` of the default image is **248.1 MB** (image ID
  `sha256:50b6e7ce…`, archive sha256 `63F93274A57370C265F3E3B06FB4E3FF08F9C03E3E018F466035A8BDDB092383`),
  and GitHub refuses any file over 100 MB. A 250 MB binary in git would also be permanent — every
  clone would carry it forever, and no later commit could remove it. The reproducible route is
  `./build.sh --tag 1.0.0 --save`; the built artifact was copied to
  `delivery/meetvault-backend-1.0.0.tar.gz` beside the other release files (`delivery/` is
  untracked, 452.9 MB portable exe already lives there). `gunzip -c … | docker load` was run and
  printed `Loaded image: meetvault-backend:verify-build`, so the handover path is proven.
- **Guard against drift:** `backend/tests/unit/test_docker_assets.py` (10 tests, no Docker needed)
  pins the two Dockerfiles (build args + defaults, entrypoint, CMD, EXPOSE, HF_HOME, USER), the two
  compose files (build context/dockerfile pair, `POSTGRES_PASSWORD` required, no pinned project
  name, worker healthcheck disabled, host-port mapping), and that each `.env.example` documents
  every setting its stack passes through (container-internal paths and the derived database URL
  exempted by name).

### A real deployment bug, found by running two stacks

Both compose files declared `name: meetvault-backend`, so the dev stack (`backend/compose.yaml`)
and the deployment stack were **the same compose project**: same `pgdata` and `media` volumes. Two
symptoms, one cause:

1. starting `deploy/` reused the dev stack's Postgres volume, and because the two `.env` files had
   different `POSTGRES_PASSWORD` values the API died with
   `FATAL: password authentication failed for user "mvb"` (from the earlier `initdb`);
2. `docker compose down -v` in `deploy/` then deleted the **dev** stack's database and media —
   observed: the dev stack's containers disappeared and its volumes were removed.

**Fix:** neither compose file pins a project name; each `.env`/`.env.example` sets its own
`COMPOSE_PROJECT_NAME` (`meetvault-deploy` vs `meetvault-backend`). Verified by running both stacks
side by side — backend on 8093, deploy on 8094, four distinct volumes
(`meetvault-backend_{pgdata,media}`, `meetvault-deploy_{pgdata,media}`) — then `down -v` on the
backend stack: 8093 stopped, its two volumes were removed, and the deploy stack still answered
`readyz: ready` with both its volumes intact. The test above fails if a project name is pinned
again.

### Evidence

| Check | Result |
| --- | --- |
| `deploy` compose config | valid; `context: D:\workspace\MeetVault`, `dockerfile: deploy/Dockerfile`, project `meetvault-deploy`, published 8094 |
| `docker compose up -d --build` in `deploy/` | db → api → worker, api/db healthy, worker up (healthcheck disabled by design) |
| Endpoints | `/healthz` ok, `/readyz` ready (all mock by config), `/` 200, `/v1/meetings` 401, `POST /v1/auth/register` **403** with `MVB_ALLOW_SIGNUP=false` |
| `build.sh --tag verify-build` | exit 0; `meetvault-backend:verify-build`, 985 MB, image ID `sha256:e703e141…` |
| `build.sh --save` | 260,132,946 bytes (248.1 MB) archive; image ID `sha256:50b6e7ce…` |
| Archive load | `gunzip -c … \| docker load` -> `Loaded image: meetvault-backend:verify-build` |
| Isolation | both stacks up, `down -v` on one leaves the other `ready` with its volumes |
| Backend suite | `275 passed, 2 skipped`; `ruff check` clean, `ruff format --check` clean |

### Files

`deploy/Dockerfile`, `deploy/docker-compose.yml`, `deploy/.env.example`, `deploy/build.sh`,
`deploy/README.md`, `deploy/docs/DEPLOY.md`, `deploy/.gitignore`,
`backend/tests/unit/test_docker_assets.py`, `backend/compose.yaml`,
`backend/docker.env.example`.

### Decisions

- **The image is not committed**, for the two reasons above (100 MB limit, permanence). `build.sh`
  plus the `delivery/` artifact is the handover path, and both carry the image ID/sha256 so a
  shipped image is identifiable.
- **`deploy/Dockerfile` duplicates `backend/Dockerfile` on purpose** (a deploy folder that cannot
  build from its own contents is not a deploy folder). The duplication is bounded by the new test
  rather than by discipline.
- **`COMPOSE_PROJECT_NAME` lives in `.env`, not `name:` in the compose file**: a hardcoded name is
  what caused the shared-project bug, and the value belongs with the deployment's other settings.

### Open threads

- `deploy/dist/` is gitignored; the shipped copy lives in `delivery/` (untracked). Nothing in the
  repo records the built image's digest as a released artifact.
- `HANDOVER.md`, `docs/ARCHITECTURE.md` and `docs/ACCEPTANCE.md` still describe the Docker path as
  CI-only; that correction was not part of this change.
