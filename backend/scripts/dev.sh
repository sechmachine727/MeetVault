#!/usr/bin/env bash
# Dev helper for Meetvault_Backend.
#
#   scripts/dev.sh            migrate, then run the API with reload on $MVB_PORT (default 8080)
#   scripts/dev.sh worker     run the job worker in the foreground
#   scripts/dev.sh migrate    apply migrations only
#   scripts/dev.sh test       run the full test suite
#
# Works on Linux/macOS and on Windows git-bash (the interpreter path differs).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

if [[ -x .venv/bin/python ]]; then
  PY=".venv/bin/python"
elif [[ -x .venv/Scripts/python.exe ]]; then
  PY=".venv/Scripts/python.exe"
else
  echo "no virtualenv found: run 'python -m venv .venv && $PY -m pip install -e \".[dev]\"'" >&2
  echo "(install the venv first, then re-run this script)" >&2
  exit 1
fi

# Load .env if present, without clobbering already-exported variables.
if [[ -f .env ]]; then
  set -a
  # shellcheck disable=SC1091
  source .env
  set +a
fi

case "${1:-api}" in
  api)
    "$PY" -m alembic upgrade head
    exec "$PY" -m uvicorn mvb.main:app --reload --port "${MVB_PORT:-8080}"
    ;;
  worker)
    exec "$PY" -m mvb.jobs.worker
    ;;
  migrate)
    exec "$PY" -m alembic upgrade head
    ;;
  test)
    exec "$PY" -m pytest -q
    ;;
  *)
    echo "usage: scripts/dev.sh [api|worker|migrate|test]" >&2
    exit 2
    ;;
esac
