# Providers

Three capabilities, each behind a protocol in `src/mvb/providers/base.py`:

| Capability | Protocol | Implementations |
| --- | --- | --- |
| Speech → text | `Transcriber.transcribe(path, language, duration_ms, progress)` | `providers/transcription/mock.py`, `providers/transcription/faster_whisper.py` |
| Speaker turns | `Diarizer.diarize(path, duration_ms, num_speakers, progress)` | `providers/diarization/mock.py`, `providers/diarization/pyannote.py` |
| Text → JSON | `Analyzer.complete_json(system, user, schema, timeout_s)` | `providers/analysis/mock.py`, `providers/analysis/lmstudio.py`, `providers/analysis/opencode_go.py` |

Selection is by env: `MVB_TRANSCRIBER`, `MVB_DIARIZER`, `MVB_ANALYZER`.

## Never-raise factory

```python
build_transcriber(settings)   # -> Transcriber (never raises)
build_diarizer(settings)      # -> Diarizer   (never raises)
build_analyzer(settings)      # -> Analyzer   (never raises)
```

A selected-but-unusable provider (package missing, host down, bad config) is replaced by the
labelled mock and logged:

```json
{"event": "provider_fallback", "capability": "analyzer", "requested": "lmstudio",
 "reason": "lm studio unreachable or no model served", "fallback": "mock-analyzer"}
```

The service therefore always starts, `GET /v1/system/providers` reports what is available and
what is selected, and every result says whether it is mock (`is_mock`). A deployment with no
AI engine at all is a fully working (mock-labelled) deployment — that is the default.

Optional extras (not installed by default, so the core install stays small):

    pip install -e ".[transcribe]"   # faster-whisper
    pip install -e ".[diarize]"      # pyannote.audio + torch
    pip install -e ".[prod]"         # psycopg (Postgres)

## LM Studio

LM Studio exposes an OpenAI-compatible server. Load a model in the UI (or `lms load`), then:

    MVB_ANALYZER=lmstudio
    MVB_ANALYZER_MODEL=        # empty -> whatever instance the server has loaded
    MVB_LMSTUDIO_BASE_URL=http://127.0.0.1:1234/v1
    MVB_LMSTUDIO_API_KEY=      # required when the server has auth enabled (see below)

The client posts to `/chat/completions` with a schema-ENFORCED output contract —
`response_format: {"type": "json_schema", "json_schema": {"name": "mvb_analysis", "schema": <the
kind's JSON schema>, "strict": true}}` (the schema travels in `response_format`, not appended to the
system message), `temperature 0`, `stream false` — under a hard timeout
(`MVB_ANALYZER_TIMEOUT_S`, default 120 s). An invalid or unreachable response degrades to the mock
with the reason recorded. The model name is passed through verbatim, so
`GET /v1/system/providers` shows exactly which model the analysis used.

### The local server on the authoring box (verified 2026-10-14)

- **Bind:** `http://127.0.0.1:1234/v1` — the server listens on **IPv4 loopback only** (`http://[::1]:1234`
  is refused). A container reaches the host copy at `http://host.docker.internal:1234/v1` (compose
  sets `extra_hosts` for it); the Tailscale proxy (`http://binblows:1234`) is for OTHER machines on the
  tailnet, so the backend stays on `127.0.0.1`. The server runs only while the LM Studio app is up.
- **Auth is mandatory:** no key or a wrong key answers `401`; the valid key with `GET /v1/models`
  answers `200`. The key is the Hermes-held `LM_API_KEY` — read it by path into
  `MVB_LMSTUDIO_API_KEY` at launch and never commit or echo it.
- **Models this box serves:** `lmstudio-community/qwen3.8-27b`, `unsloth/qwen3.8-27b`,
  `qwen3.8-27b-gsq-rco` (the owner's custom build, used as `MVB_ANALYZER_MODEL`), plus
  `text-embedding-nomic-embed-text-v1.5` (an embedding model — irrelevant to the analyzer).
- **Measured timings (2026-10-14, RX 7900 XTX / Vulkan):** ~42 tok/s, but the model emits heavy
  reasoning — a trivial call is ~100–130 s wall and the no-think soft tag does not disable it. A full
  `analyze` job (agenda + per-section summaries + summary + decisions/actions = 4–8 calls) measured
  **387 s** and **425 s** on two meetings; a `/qa` answer took **24–42 s**. Launch with
  `MVB_ANALYZER_TIMEOUT_S=600`.
- **The SQLite write-lock hold across model calls is FIXED (2026-10-14, commit `2b3857a`):** the
  analyze job used to keep its transaction open across every model call, so a concurrent API *write*
  (the auth-token `last_used_at` touch is one) answered `500 database is locked` for the whole
  multi-minute job — reproduced live (login `500` while a job ran, `200` the moment it finished). The
  pipeline now commits *before* each model call (`pipeline/analyze.py`, so no model call runs inside an
  open write transaction) and the SQLite engine sets `PRAGMA journal_mode=WAL` plus
  `PRAGMA busy_timeout=30000` on connect (`db/base.py`); a concurrent write now commits while the model
  call is still parked, pinned by
  `tests/integration/test_analysis_jobs.py::test_analyze_does_not_pin_the_write_lock_across_a_model_call`.
  Honest residual nuance: a *single* SQLite statement still holds its own statement lock for as long as
  it runs, so one very long write can still block another writer until `busy_timeout` expires — no
  statement in the pipeline runs for minutes. Postgres was never affected (MVCC).

## OpenCode Go

    MVB_ANALYZER=opencode-go
    MVB_ANALYZER_MODEL=deepseek-v4.1-flash
    MVB_OPENCODE_GO_BASE_URL=https://opencode.ai/zen/go/v1
    MVB_OPENCODE_GO_API_KEY=<the Hermes-held OPENCODE_GO_API_KEY, exported at runtime; never committed>

OpenCode Go is the managed gateway Hermes itself uses. The `OpenCodeGoAnalyzer` speaks
plain OpenAI (`GET /models` ping with a model-served check, `POST /chat/completions` with
`response_format: {"type": "json_object"}`) plus Go's client contract: an identifying
`User-Agent` (generic SDK names risk blocks) and a stable `x-opencode-session` id per
analyzer instance (one instance serves one analysis run, aiding routing and caching).
The model id is the bare id (`deepseek-v4.1-flash`, confirmed in the live `/models`
listing on 2026-10-13). The API key is the same one Hermes runs on — export it into the
backend environment; the registry still degrades to labelled mock when the gateway is
unreachable, and per-call spend follows the Go plan limits.

> Removed 2026-10-13: the self-hosted `opencode serve` analyzer (`MVB_ANALYZER=opencode`)
> is gone — its routes were never verified against a live server. Use `opencode-go` above.

## Adding a provider

1. New module under `src/mvb/providers/<capability>/` with `id`, `is_mock`, `model` and the
   protocol method.
2. One constructor line in `providers/registry.py`, inside the try/except that falls back.
3. One test for the happy path and one for the fallback reason.
