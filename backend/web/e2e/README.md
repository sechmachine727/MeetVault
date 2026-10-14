# MeetVault console — browser tests

Two layers, both run from `backend/web/`:

1. **Pure logic** (no browser): `node --test "tests/*.test.mjs"` — formatting, status vocabulary,
   hash routing, the session store, the API client's contract behaviour.
2. **Browser flows** (Playwright, Chromium): `npx playwright test` — the 21-row parity matrix from
   the plan, on a real server with real data.

## Reproduce the browser suite

```sh
cd backend
DEMO_ROOT="C:/Users/<you>/AppData/Local/hermes/cache/scratch/mvweb"   # or $TMPDIR/mvweb

# 1. throwaway stack + demo data (migrate, create-user, boot api + worker, seed)
DEMO_ROOT="$DEMO_ROOT" sh web/run-demo-stack.sh start

# 2. browser suite against it
cd web
npm install
npx playwright install chromium
MVB_DEMO_URL=http://127.0.0.1:8099 npx playwright test

# 3. stop it
cd .. && DEMO_ROOT="$DEMO_ROOT" sh web/run-demo-stack.sh stop
```

On the Windows authoring box, start `uvicorn mvb.main:app --port 8099` and
`python -m mvb.jobs.worker` as **tracked background processes** (a backgrounded process inside a
shell script is not visible to the harness), export the same `MVB_*` environment as
`run-demo-stack.sh` sets, then run only the seed step:

```sh
MVB_DEMO_URL=http://127.0.0.1:8099 ./.venv/Scripts/python.exe web/e2e/seed_demo.py
```

## Data assumptions

`seed_demo.py` is deterministic and creates exactly three meetings:

- **Cloud console review** — full mock pipeline drained to `ready`, with analysis and one stored
  Q&A answer (with citations). Everything else in the suite hangs off this meeting.
- **Storage migration standup** — media + imported transcript, never processed (the
  "no current analysis" path, e.g. the Word export 409).
- **Customer feedback sync** — a `running` analyze job pinned directly in the DB (heartbeat set, so
  the worker's reaper leaves it) so the list's elapsed timer and progress badge are deterministic
  in screenshots and in the auto-refresh test.

The remove spec creates its own throwaway meeting through the API, so the suite is repeatable
without re-seeding; everything else is read-only.
