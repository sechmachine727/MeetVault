# Meetvault_Backend — API + worker image.
# One image, two commands: the HTTP API (default CMD) and the job worker
# (see compose.yaml). ffmpeg/ffprobe are required for probing and audio extraction.
#
# Build-time options (compose passes them from .env):
#   WITH_TRANSCRIBE=true   install faster-whisper (real ASR; ~0.2 GB)
#   WITH_DIARIZE=true      install pyannote.audio + torch (real speaker turns; ~2-3 GB)
#   APP_EXTRAS             extra pip extras, default "prod" (psycopg, for Postgres)
# The defaults build the small mock-ready image; a real engine is one env var away.
FROM python:3.12-slim AS base

ARG APP_EXTRAS=prod
ARG WITH_TRANSCRIBE=false
ARG WITH_DIARIZE=false

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    # Where a real transcribe/diarize engine caches its model weights. Mount a volume
    # here (compose caches.) so a rebuild does not re-download gigabytes.
    HF_HOME=/data/hf

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Dependencies first so the layers cache; the project itself is installed after.
COPY pyproject.toml README.md ./
COPY src ./src
COPY alembic ./alembic
COPY alembic.ini ./
# One pip run, so the extras compose into a single resolution. The optional heavy
# engines are chosen at build time — the default image stays small and needs no
# torch, and the provider registry falls back to the labelled mock if a requested
# engine is missing from this image.
RUN pip install --upgrade pip \
    && extras="${APP_EXTRAS}" \
    && if [ "${WITH_TRANSCRIBE}" = "true" ]; then extras="${extras},transcribe"; fi \
    && if [ "${WITH_DIARIZE}" = "true" ]; then extras="${extras},diarize"; fi \
    && echo "installing extras: ${extras}" \
    && pip install ".[${extras}]"

# The management web console (static SPA, no build step) served from MVB_WEB_ROOT.
COPY web ./web

# The entrypoint applies migrations before any command (api or worker).
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

# Non-root runtime. /data holds media and the model cache, and must be writable by
# the api and the worker (a real engine downloads weights into HF_HOME on first run).
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

# The entrypoint runs `alembic upgrade head` (idempotent) then execs this CMD.
# Single-container runs are therefore zero-config: a fresh volume boots with a
# complete schema. Compose no longer needs its own migration command.
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["uvicorn", "mvb.main:app", "--host", "0.0.0.0", "--port", "8080"]
