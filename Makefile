PY := .venv/Scripts/python.exe
PY_LINUX := .venv/bin/python

.PHONY: install test lint fmt migrate run worker e2e cov

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
