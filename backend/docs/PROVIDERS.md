# Providers

Three capabilities, each behind a protocol in `src/mvb/providers/base.py`:

| Capability | Protocol | Implementations |
| --- | --- | --- |
| Speech → text | `Transcriber.transcribe(path, language, duration_ms, progress)` | `providers/transcription/mock.py`, `providers/transcription/faster_whisper.py` |
| Speaker turns | `Diarizer.diarize(path, duration_ms, num_speakers, progress)` | `providers/diarization/mock.py`, `providers/diarization/pyannote.py` |
| Text → JSON | `Analyzer.complete_json(system, user, schema, timeout_s)` | `providers/analysis/mock.py`, `providers/analysis/lmstudio.py`, `providers/analysis/opencode.py`, `providers/analysis/opencode_go.py` |

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
 "reason": "LM Studio unreachable: ConnectError", "fallback": "mock-analyzer"}
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
    MVB_LMSTUDIO_BASE_URL=http://host.docker.internal:1234/v1
    MVB_LMSTUDIO_API_KEY=      # only if the server requires one

`host.docker.internal` is how a container reaches LM Studio on the host (compose sets
`extra_hosts` for it). The client posts to `/chat/completions` with
`response_format: {"type": "json_schema"}` and a hard timeout
(`MVB_ANALYZER_TIMEOUT_S`, default 120 s); an invalid or unreachable response degrades to the
mock with the reason recorded. The model name is passed through verbatim, so
`GET /v1/system/providers` shows exactly which model the analysis used.

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

## OpenCode

    MVB_ANALYZER=opencode
    MVB_OPENCODE_BASE_URL=http://host.docker.internal:4096
    MVB_OPENCODE_API_KEY=

The client is deliberately thin: it posts `{system, user, schema}` to
`POST {base}/session/{id}/message`-style JSON endpoints and parses a JSON object out of the
reply. Because OpenCode's HTTP surface is not stable across versions, the adapter is written
so the endpoint shape lives in one place (`_post_json`); pin the real route for your version
there when enabling it. It is exercised with `respx` in the unit tests, and a live call is
skipped unless `MVB_RUN_LIVE=1`.

## Adding a provider

1. New module under `src/mvb/providers/<capability>/` with `id`, `is_mock`, `model` and the
   protocol method.
2. One constructor line in `providers/registry.py`, inside the try/except that falls back.
3. One test for the happy path and one for the fallback reason.
