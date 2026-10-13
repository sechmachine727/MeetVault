#!/usr/bin/env bash
# Standalone acceptance smoke test (plan T69).
#
# Boots the real API against a throwaway SQLite database and media root, drives it over HTTP
# exactly like the Windows client will (create -> upload a real recording -> process -> run the
# worker -> read the results), asserts every step, then shuts down and cleans up.
#
#   scripts/smoke.sh
#
# Exits 0 only when the whole flow worked, so it is usable as a deployment gate. No Docker, no
# AI engine and no network access are required — the default providers are the labelled mocks.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

if [[ -x .venv/bin/python ]]; then
  PY=".venv/bin/python"
elif [[ -x .venv/Scripts/python.exe ]]; then
  PY=".venv/Scripts/python.exe"
else
  echo "no virtualenv found (run: python -m venv .venv && .venv/bin/python -m pip install -e '.[dev]')" >&2
  exit 1
fi

command -v curl >/dev/null || { echo "curl is required" >&2; exit 1; }
command -v ffmpeg >/dev/null || { echo "ffmpeg is required (media fixture + audio extraction)" >&2; exit 1; }

PORT="${SMOKE_PORT:-8099}"
WORK="$(mktemp -d)"
# Native tools (python, curl, ffmpeg) cannot open git-bash's /tmp paths, and bash/MSYS tools
# accept the native form fine — so keep ONE native-form scratch path for everything.
# No-op on Linux, where cygpath does not exist.
if command -v cygpath >/dev/null 2>&1; then
  WORK="$(cygpath -w "$WORK" | sed 's|\\|/|g')"
fi
BASE="http://127.0.0.1:${PORT}"

export MVB_ENV=dev
export MVB_DATABASE_URL="sqlite+pysqlite:///${WORK}/smoke.db"
export MVB_MEDIA_ROOT="${WORK}/media"
export MVB_LOG_LEVEL=WARNING

SERVER_PID=""
cleanup() {
  if [[ -n "$SERVER_PID" ]]; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  rm -rf "$WORK"
}
trap cleanup EXIT

fail() { echo "FAIL: $*" >&2; if [[ -f "$WORK/server.log" ]]; then echo "--- server log ---" >&2; tail -20 "$WORK/server.log" >&2; fi; exit 1; }
ok() { echo "ok   $*"; }

api() { # api METHOD PATH [curl args…]
  local method="$1" path="$2"; shift 2
  curl -fsS -X "$method" "${BASE}${path}" -H "Authorization: Bearer $TOKEN" "$@"
}

echo "== Meetvault_Backend smoke: $BASE =="

"$PY" -m alembic upgrade head >/dev/null
"$PY" -m uvicorn mvb.main:app --host 127.0.0.1 --port "$PORT" >"$WORK/server.log" 2>&1 &
SERVER_PID=$!

for i in $(seq 1 40); do
  if curl -fsS "${BASE}/healthz" >"$WORK/health.txt" 2>/dev/null; then break; fi
  sleep 0.5
  [[ "$i" -eq 40 ]] && fail "server never became ready on port $PORT"
done
grep -q '"status":"ok"' "$WORK/health.txt" || fail "unexpected /healthz body: $(cat "$WORK/health.txt")"
ok "server ready on port $PORT"

"$PY" -m mvb.admin create-user --email smoke@example.com --password smoke-pass-1
TOKEN="$(curl -fsS -X POST "$BASE/v1/auth/login" -H 'Content-Type: application/json' \
  -d '{"email":"smoke@example.com","password":"smoke-pass-1"}' \
  | "$PY" -c 'import json,sys; print(json.load(sys.stdin)["token"])')"

api GET /readyz -o "$WORK/readyz.json"
grep -q '"status":"ready"' "$WORK/readyz.json" || fail "readiness is not ok: $(cat "$WORK/readyz.json")"
grep -q '"is_mock":true' "$WORK/readyz.json" || fail "mock providers are not reported on /readyz"
ok "readyz: ready, providers reported (mock by default)"

CODE="$(curl -s -o "$WORK/unauth.json" -w '%{http_code}' "${BASE}/v1/meetings")"
[[ "$CODE" == "401" ]] || fail "expected 401 without an API key, got $CODE"
ok "unauthenticated request rejected (401)"

MEETING="$(api POST /v1/meetings -H 'Content-Type: application/json' \
  -d '{"title":"Smoke meeting","external_id":"smoke-1"}')"
MID="$("$PY" -c 'import json,sys; print(json.load(sys.stdin)["id"])' <<<"$MEETING")"
[[ -n "$MID" ]] || fail "no meeting id returned"
ok "meeting created: $MID"

ffmpeg -y -f lavfi -i "sine=frequency=440:duration=20" -ac 1 -ar 48000 "$WORK/smoke.wav" >/dev/null 2>&1
api POST "/v1/meetings/${MID}/media?kind=audio" -F "file=@${WORK}/smoke.wav;type=audio/wav" -o "$WORK/asset.json"
UPLOADED="$("$PY" -c 'import json,sys; d=json.load(open(sys.argv[1])); assert d["duration_ms"] >= 19000, d; print(d["duration_ms"])' "$WORK/asset.json")"
ASSET_ID="$("$PY" -c 'import json,sys; print(json.load(open(sys.argv[1]))["asset_id"])' "$WORK/asset.json")"
ok "recording uploaded and probed: ${UPLOADED} ms"

api POST "/v1/meetings/${MID}/process" -H 'Content-Type: application/json' -d '{}' -o "$WORK/process.json"
"$PY" -c 'import json,sys; d=json.load(open(sys.argv[1])); assert d["enqueued"] == 1 and d["job_ids"], d' "$WORK/process.json"
ok "pipeline started (one job, chained by the worker)"

# Run the worker until the queue is empty. A bare tick() loop also proves the worker entry
# point registers its handlers on its own (no import of main()).
RAN="$("$PY" -c '
from mvb.jobs.worker import tick
n = 0
while n < 12 and tick():
    n += 1
print(n)')"
[[ "$RAN" -ge 4 ]] || fail "expected at least 4 pipeline jobs to run, ran $RAN"
ok "worker ran $RAN jobs"

api GET "/v1/meetings/${MID}/status" -o "$WORK/status.json"
"$PY" -c '
import json, sys
d = json.load(open(sys.argv[1]))
assert d["status"] == "ready", d
assert d["progress"] == 1.0, d
assert d["mock"] is True, d
assert d["stale_kinds"] == [], d
missing = {"summary", "agenda", "decisions", "action_items"} - set(d["fresh_kinds"])
assert not missing, f"missing fresh kinds: {sorted(missing)}"
' "$WORK/status.json"
ok "status: ready, progress 1.0, analysis fresh"

api GET "/v1/meetings/${MID}/transcript" -o "$WORK/transcript.json"
"$PY" -c '
import json, sys
d = json.load(open(sys.argv[1]))
assert d["is_mock"] is True and d["segment_count"] >= 2, d
assert len(d["segments"]) == d["segment_count"], d
for s in d["segments"]:
    assert 0 <= s["start_ms"] < s["end_ms"] <= d["duration_ms"], s
    assert s["text"].startswith("[mock:"), s
    assert s["speaker_label"], s
' "$WORK/transcript.json"
ok "transcript: labelled segments with times inside the media duration"

api GET "/v1/meetings/${MID}/analysis" -o "$WORK/analysis.json"
"$PY" -c '
import json, sys
d = json.load(open(sys.argv[1]))
assert d["mock"] is True and d["run"] is not None, d
assert d["sections"], "no agenda sections"
assert len(d["section_summaries"]) == len(d["sections"]), d
assert d["sections"][0]["start_ms"] == 0, d
assert (d["decisions"] or {}).get("items"), d
assert (d["action_items"] or {}).get("items"), d
' "$WORK/analysis.json"
ok "analysis: summary, sections + section summaries, decisions, action items"

api GET "/v1/media/${ASSET_ID}" -o "$WORK/media.bin"
api GET "/v1/media/${ASSET_ID}" -H 'Range: bytes=0-99' -o "$WORK/part.bin" -D "$WORK/part.headers"
grep -qi "content-range: bytes 0-99/" "$WORK/part.headers" || fail "range request did not return a Content-Range header"
[[ "$(wc -c <"$WORK/part.bin")" -eq 100 ]] || fail "range request returned the wrong number of bytes"
ok "media streaming supports Range (206, Content-Range)"

api POST "/v1/meetings/${MID}/qa" -H 'Content-Type: application/json' \
  -d '{"question":"What did we decide about the release date?","top_k":4}' -o "$WORK/qa.json"
QJOB="$("$PY" -c 'import json,sys; print(json.load(open(sys.argv[1]))["job_id"])' "$WORK/qa.json")"
"$PY" -c '
from mvb.jobs.worker import tick
while tick():
    pass' >/dev/null
api GET "/v1/jobs/${QJOB}" -o "$WORK/qajob.json"
api GET "/v1/meetings/${MID}/qa" -o "$WORK/qaanswer.json"
"$PY" -c '
import json, sys
job = json.load(open(sys.argv[1]))
assert job["state"] == "succeeded", job
docs = json.load(open(sys.argv[2]))
assert docs["total"] == 1, docs
payload = docs["items"][0]["payload"]
assert payload["answer"], payload
assert payload["citations"], payload
for c in payload["citations"]:
    assert 0 <= c["start_ms"] < c["end_ms"], c
' "$WORK/qajob.json" "$WORK/qaanswer.json"
ok "Q&A answered with grounded citations"

echo "== PASS: full flow verified against the live server =="
