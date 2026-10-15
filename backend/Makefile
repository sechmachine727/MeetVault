PY := .venv/Scripts/python.exe
PY_LINUX := .venv/bin/python

.PHONY: install test lint fmt migrate run worker e2e cov openapi smoke dev \
        docker-env docker-build-image docker-up docker-user docker-ps docker-providers \
        docker-down docker-reset docker-logs

install:
	python -m venv .venv && $(PY) -m pip install -U pip && $(PY) -m pip install -e ".[dev]"

test:
	$(PY) -m pytest -q

cov:
	$(PY) -m pytest --cov=mvb --cov-report=term-missing -q

lint:
	$(PY) -m ruff check src tests

fmt:
	$(PY) -m ruff format src tests

migrate:
	$(PY) -m alembic upgrade head

run:
	$(PY) -m uvicorn mvb.main:app --reload --port 8080

worker:
	$(PY) -m mvb.jobs.worker

e2e:
	$(PY) -m pytest tests/e2e -q

openapi:
	$(PY) -c "import json; from mvb.main import create_app; s = create_app().openapi(); print(len(s['paths']), 'paths'); print('\n'.join(sorted(s['paths'])))"

# Standalone acceptance smoke test: boots the real API + worker against a throwaway DB.
smoke:
	bash scripts/smoke.sh

# Dev helper: 'make dev' = api, 'make dev A=worker' etc.
dev:
	bash scripts/dev.sh $(A)

# --- Docker (see docs/DOCKER.md) ---------------------------------------------------------
# Three containers (Postgres + api + worker) driven entirely by .env. --env-file is explicit so
# these targets behave the same from the repo root (make -C backend ...).
#
#   cp docker.env.example .env     # set POSTGRES_PASSWORD, models, keys, MVB_PORT
#   make docker-up                 # build + start
#   make docker-user EMAIL=you@example.com
#
# An engine that must live IN the image (faster-whisper, pyannote) is a build arg, not a setting:
#   make docker-build-image EXTRA_ARGS="--build-arg WITH_TRANSCRIBE=true"
DC := docker compose --env-file .env

# A fresh copy of the settings template (refuses to clobber an existing .env).
docker-env:
	@test -f .env && echo ".env already exists - edit it, or delete it first" || \
		( cp docker.env.example .env && echo "wrote .env - set POSTGRES_PASSWORD before 'make docker-up'" )

docker-build-image:
	docker build -t meetvault-backend:local $(EXTRA_ARGS) .

docker-up:
	$(DC) up -d --build

# Create an account without signup: make docker-user EMAIL=you@example.com [PASSWORD=...]
docker-user:
	$(DC) exec api python -m mvb.admin create-user --email $(EMAIL) $(if $(PASSWORD),--password $(PASSWORD),)

docker-ps:
	$(DC) ps

# What is really running: effective provider per capability (requested vs effective, is_mock).
docker-providers:
	@$(DC) exec -T api curl -fsS http://127.0.0.1:8080/readyz

docker-down:
	$(DC) down

# Also deletes the database and media volumes.
docker-reset:
	$(DC) down -v

docker-logs:
	$(DC) logs -f api worker
