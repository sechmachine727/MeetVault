# Meetvault_Backend — API + worker image.
# One image, two commands: the HTTP API (default CMD) and the job worker
# (see compose.yaml). ffmpeg/ffprobe are required for probing and audio extraction.
FROM python:3.12-slim AS base

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Dependencies first so the layers cache; the project itself is installed after.
COPY pyproject.toml README.md ./
COPY src ./src
COPY alembic ./alembic
COPY alembic.ini ./
RUN pip install --upgrade pip \
    && pip install ".[prod]"

# The entrypoint applies migrations before any command (api or worker).
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

# Non-root runtime. /data holds media and must be writable by the api and worker.
RUN useradd --create-home --uid 10001 app \
    && mkdir -p /data/media \
    && chown -R app:app /app /data
USER app

ENV MVB_ENV=prod \
    MVB_MEDIA_ROOT=/data/media \
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
