#!/usr/bin/env sh
# MeetVault web-console demo stack: a throwaway SQLite backend + worker + the demo dataset.
#
#   sh web/run-demo-stack.sh start   # migrate, create the demo user, boot api + worker, seed
#   sh web/run-demo-stack.sh stop    # stop both processes
#
# Everything lives under DEMO_ROOT (default: $TMPDIR/mvweb) — never in the repo.
# In CI this script owns the background processes (a runner has no other supervisor); on the
# authoring box start the api/worker as *tracked* background processes instead and call
# `seed` — see web/e2e/README.md.
set -eu

DEMO_ROOT="${DEMO_ROOT:-${TMPDIR:-/tmp}/mvweb}"
PORT="${MVB_DEMO_PORT:-8099}"
EMAIL="demo@meetvault.local"
PASSWORD="demo-password-123"

export MVB_ENV=dev
export MVB_DATABASE_URL="sqlite+pysqlite:///$DEMO_ROOT/demo.db"
export MVB_MEDIA_ROOT="$DEMO_ROOT/media"
export MVB_ANALYZER=mock
export MVB_TRANSCRIBER=mock
export MVB_DIARIZER=mock
export MVB_LOG_LEVEL=INFO
export MVB_WEB_ROOT="$PWD/web"

PY=./.venv/Scripts/python.exe
[ -x "$PY" ] || PY=./.venv/bin/python
mkdir -p "$DEMO_ROOT/media"

start() {
  rm -f "$DEMO_ROOT/demo.db"
  "$PY" -m alembic upgrade head
  "$PY" -m mvb.admin create-user --email "$EMAIL" --password "$PASSWORD" --name "Demo User"
  "$PY" -m uvicorn mvb.main:app --host 127.0.0.1 --port "$PORT" > "$DEMO_ROOT/api.log" 2>&1 &
  echo $! > "$DEMO_ROOT/api.pid"
  # Wait for readiness instead of sleeping blindly.
  i=0
  while [ "$i" -lt 40 ]; do
    if curl -fsS "http://127.0.0.1:$PORT/healthz" > /dev/null 2>&1; then break; fi
    i=$((i + 1))
    sleep 1
  done
  "$PY" -m mvb.jobs.worker > "$DEMO_ROOT/worker.log" 2>&1 &
  echo $! > "$DEMO_ROOT/worker.pid"
  seed
}

seed() {
  MVB_DEMO_URL="http://127.0.0.1:$PORT" MVB_DEMO_EMAIL="$EMAIL" MVB_DEMO_PASSWORD="$PASSWORD" \
    "$PY" web/e2e/seed_demo.py | tee "$DEMO_ROOT/seed.json"
}

stop() {
  for name in api worker; do
    if [ -f "$DEMO_ROOT/$name.pid" ]; then
      kill "$(cat "$DEMO_ROOT/$name.pid")" 2>/dev/null || true
      rm -f "$DEMO_ROOT/$name.pid"
    fi
  done
}

case "${1:-start}" in
  start) start ;;
  seed) seed ;;
  stop) stop ;;
  *) echo "usage: $0 start|seed|stop" >&2; exit 2 ;;
esac
