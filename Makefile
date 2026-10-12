PY := .venv/Scripts/python.exe
PY_LINUX := .venv/bin/python

.PHONY: install test lint fmt migrate run worker e2e cov openapi smoke docker-build docker-up docker-down docker-logs dev

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

docker-build:
	docker build -t meetvault-backend:local .

docker-up:
	docker compose up -d --build

docker-down:
	docker compose down

docker-logs:
	docker compose logs -f api worker
