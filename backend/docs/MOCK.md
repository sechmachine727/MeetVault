# Mock providers: what is synthetic, and how to tell

The backend must be fully developable and testable with **no AI engine installed**. Every AI
capability therefore has a deterministic mock provider, selected by default
(`MVB_TRANSCRIBER=mock`, `MVB_DIARIZER=mock`, `MVB_ANALYZER=mock`).

## How mock output is marked

Three independent layers, so mock data cannot be mistaken for real results at any level:

1. **Provider level** — the result dataclass carries `is_mock=True`.
2. **Database level** — `transcripts.is_mock`, `analysis_runs.is_mock`,
   `analysis_documents.is_mock`, `jobs.is_mock`, `sections.is_mock`.
3. **API level** — `is_mock` appears on the transcript, the analysis run, every document and
   every job; `GET /v1/meetings/{id}/status` reports a single `mock` boolean for the whole
   meeting, and `GET /v1/meetings/{id}/analysis` reports `mock` for the bundle.

In addition, **every text field the mock produces starts with `[mock]`** (transcripts use
`[mock:en]`, `[mock:ja]`, …), and the LLM mock appends a 12-hex digest of its input, so
mock output is both obvious and reproducible.

## What the mocks actually produce

| Provider | Output |
| --- | --- |
| `mock-transcriber` (`mock-asr-v1`) | 8-second segments covering the real media duration, text `[mock:en] segment N of M`, `is_final=True` |
| `mock-diarizer` (`mock-diar-v1`) | 20-second alternating turns for two speakers, so overlap labelling is deterministic |
| `mock-analyzer` (`mock-llm-v1`) | schema-valid payloads for agenda / summary / section summary / decisions / action items; Q&A cites the real segment ids it was given |

Durations are derived from the **real** probed media length, not invented, so the timeline,
segment counts and alignment behave exactly as they would with a real engine.

## What is NOT mocked

Nothing else: ffprobe probing, 16 kHz mono decoding (`ffmpeg`), storage, hashing, the job
queue, retries, cancellation, reaping, persistence, auth, validation, range streaming,
alignment, staleness and the whole HTTP surface are the real implementation. Switching
`MVB_ANALYZER=lmstudio` changes only which class answers `complete_json`.

## Rules for clients

- Show a badge when `mock`/`is_mock` is true. Never present mock text as a real transcript.
- Mock results are stable across runs — good for tests and demos, useless as meeting notes.
- `GET /v1/system/providers` says which provider is available, which is selected, and why a
  requested one fell back, so an operator can tell "mock on purpose" from "engine unreachable".
