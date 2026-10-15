# MeetVault backend — deployment image.
#
# Build from THIS folder (the deployment folder is the build context):
#   ./build.sh                      # default: mock engines, small image
#   ./build.sh --with-transcribe    # + faster-whisper (real ASR)
#   ./build.sh --with-diarize       # + pyannote/torch (real speaker turns)
#   ./build.sh --tag 1.0.0 --save   # also export a loadable image tarball
#
# It is the same image definition as backend/Dockerfile; the only difference is that the
# COPY paths are rooted here so this folder is self-contained. tests/unit/test_docker_assets.py
# pins the two together (build args, entrypoint, healthcheck, port) so they cannot drift.
#
# Build-time options, also settable through docker-compose.yml's `args`:
#   WITH_TRANSCRIBE=true   install faster-whisper (real ASR; ~0.2 GB)
#   WITH_DIARIZE=true      install pyannote.audio + torch (real speaker turns; ~2-3 GB)
#   APP_EXTRAS             extra pip extras, default "prod" (psycopg, for Postgres)
FROM python:3.12-slim AS base

ARG APP_EXTRAS=prod
ARG WITH_TRANSCRIBE=false
ARG WITH_DIARIZE=false

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    # Where a real transcribe/diarize engine caches its model weights. Mount a volume
    # here (compose does) so a rebuild never re-downloads gigabytes.
    HF_HOME=/data/hf

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Dependencies first so the layers cache; the project itself is installed after.
COPY backend/pyproject.toml backend/README.md ./
COPY backend/src ./src
COPY backend/alembic ./alembic
COPY backend/alembic.ini ./
# One pip run, so the extras compose into a single resolution. An engine that is not built
# in is not fatal: the provider registry falls back to the labelled mock and says so.
RUN pip install --upgrade pip \
    && extras="${APP_EXTRAS}" \
    && if [ "${WITH_TRANSCRIBE}" = "true" ]; then extras="${extras},transcribe"; fi \
    && if [ "${WITH_DIARIZE}" = "true" ]; then extras="${extras},diarize"; fi \
    && echo "installing extras: ${extras}" \
    && pip install ".[${extras}]"

# The management web console (static SPA, no build step) served from MVB_WEB_ROOT.
COPY backend/web ./web

# The entrypoint applies migrations before any command (api or worker).
COPY backend/docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

# Non-root runtime. /data holds media and the model cache, and must be writable by the
# api and the worker (a real engine downloads weights into HF_HOME on first run).
RUN useradd --create-home --uid 10001 app \
    && mkdir -p /data/media /data/hf \
    && chown -R app:app /app /data
USER app

ENV MVB_ENV=prod \
    MVB_MEDIA_ROOT=/data/media \
    MVB_WEB_ROOT=/app/web \
    MVB_HOST=0.0.0.0 \
    MVB_PORT=8080

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/healthz || exit 1

# The entrypoint runs `alembic upgrade head` (idempotent) then execs this CMD, so a fresh
# volume boots with a complete schema with no separate migration step.
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["uvicorn", "mvb.main:app", "--host", "0.0.0.0", "--port", "8080"]
