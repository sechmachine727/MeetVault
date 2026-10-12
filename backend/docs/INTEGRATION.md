# MeetVault client → Meetvault_Backend integration

The client keeps doing what it already does: record audio (and optionally screen video) and
run live Vosk transcription locally. Nothing in the recording path changes. After a meeting
ends, the client *uploads* the finished recording and its exported transcript, asks the
backend to process it, and later fetches a better transcript, speaker labels and analysis.

This document is the contract. Every payload below is copy-pasteable.

## 0. Conventions

- Base URL: `http://<host>:8080`, all endpoints under `/v1` (health checks are at `/healthz`,
  `/readyz`).
- Auth: `Authorization: Bearer <key>` on every `/v1` call. Keys live in `MVB_API_KEYS`
  (comma-separated, server side only). `401 unauthorized` means a missing/wrong key.
- Errors always have this shape, whatever the failure:

```json
{"error": {"code": "not_found", "message": "Meeting not found", "detail": {"meeting_id": "…"}},
 "request_id": "9f0c…"}
```

- Timings are **integer milliseconds** from the start of the recording.
- Anything produced by a mock provider is marked: `is_mock: true` on the object, and text
  fields carry a `[mock]` / `[mock:en]` prefix. Never display mock output as a real result.

## 1. Upload a finished meeting (6 calls)

### 1.1 Create the meeting

```bash
curl -X POST http://localhost:8080/v1/meetings \
  -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"title": "Weekly sync", "external_id": "mv-2026-10-12-0930",
       "started_at": "2026-10-12T02:30:00Z", "language": "en"}'
```

`external_id` is the client's own id (the meeting row id from `meetings.db`). Posting the
same `source`+`external_id` again returns the **existing** meeting with `200` instead of
`201`, so a retry after a network error cannot create duplicates.

Response (`201`): the meeting, including `id` (server UUID — store it against the local
meeting), `status` (`uploaded` → `processing` → `ready`), `duration_ms`, `media_count`, …

### 1.2 Upload the recording

```bash
curl -X POST "http://localhost:8080/v1/meetings/$MEETING_ID/media?kind=audio" \
  -H "Authorization: Bearer $KEY" \
  -F "file=@weekly-sync.wav;type=audio/wav"
```

- `kind=audio|video`. Send the recorded audio track as `audio`; a screen-recording MP4 goes
  as `video` (with `start_offset_ms` if it starts later than the audio).
- The response carries `duration_ms` **probed from the file**, plus `sample_rate`,
  `channels`, `sha256`. This duration is the authority for every timestamp the backend
  produces; the client should store it.
- Uploads above `MVB_MAX_UPLOAD_MB` (default 4 GiB) are rejected with `413 payload_too_large`.
- Repeat the call for additional assets (e.g. audio + video) — they all belong to the same
  meeting and are returned together by `GET /v1/meetings/{id}/media`.
- Play the recording back through the backend with
  `GET /v1/media/{asset_id}` (supports `Range`, so seeking works).

### 1.3 Upload the local transcript (optional but recommended)

The client's exported transcript (the `List<TranscriptLine>` JSON from `DetailsPage`) is
either an object with a `segments` array or a bare array of `{Speaker, Text}` rows:

```bash
curl -X POST http://localhost:8080/v1/meetings/$MEETING_ID/transcripts \
  -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '[{"Speaker":"Speaker 1","Text":"Welcome everyone."},
       {"Speaker":"Speaker 2","Text":"The release slips by a week."}]'
```

**Alignment rule.** Those rows carry no timings, so the backend distributes them across the
*probed media duration* (by text length), contiguous and non-overlapping. That is why the
media upload must come first: rescaling to the real duration is what keeps a transcript from
drifting out of sync with the audio during playback. If the payload *does* carry
`start_ms`/`end_ms`: `?align=scale` rescales them onto the real duration (factor
`duration_ms / max(end_ms)`); `?align=auto` (the default) rescales the same way when every
row is timed, and only with a *mix* of timed and untimed rows are the timed rows kept and
the untimed gaps filled.

### 1.4 Start processing

```bash
curl -X POST http://localhost:8080/v1/meetings/$MEETING_ID/process \
  -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"steps": ["transcribe","diarize","align","analyze"], "language": "en"}'
```

| field | meaning |
| --- | --- |
| `steps` | subset/order of `media_probe`, `transcribe`, `diarize`, `align`, `analyze` |
| `language` | `en`, `ja`, `vi`, … or `auto`/omitted to let the backend decide |
| `analyze_kinds` | subset of `summary`, `agenda`, `section_summary`, `decisions`, `action_items` |
| `force` | `true` re-runs even if the pipeline is already running |

Response (`202 Accepted`): `{"meeting_id": …, "steps": ["transcribe"], "job_ids": ["…"],
"enqueued": 1}`. Each step runs as its own job; the next step is created only after the
previous one succeeds, so a failure never leaves half a pipeline queued. `enqueued: 0` means
the pipeline was already running — that is the idempotent answer to a double-click, not an
error.

### 1.5 Track progress

```bash
curl -H "Authorization: Bearer $KEY" http://localhost:8080/v1/meetings/$MEETING_ID/status
```

```json
{"status": "processing", "steps": {"transcribe": "succeeded", "diarize": "running"},
 "progress": 0.5, "active_job_id": "…", "active_transcript_id": "…",
 "fresh_kinds": [], "stale_kinds": ["summary"], "mock": true, "duration_ms": 1834000}
```

- `status`: `uploaded` → `processing` → `ready` (or `failed`/`deleted`).
- `mock: true` means at least one part came from a mock provider. Surface it in the UI.
- `stale_kinds`: analysis kinds that no longer match the active transcript/media and should
  be re-run or shown as outdated.
- Per-step detail: `GET /v1/jobs/{job_id}` (with recent events) or stream them with
  `GET /v1/jobs/{job_id}/events` (`text/event-stream`, closes on a terminal state).

### 1.6 Fetch the results

```bash
curl -H "Authorization: Bearer $KEY" http://localhost:8080/v1/meetings/$MEETING_ID/transcript
curl -H "Authorization: Bearer $KEY" http://localhost:8080/v1/meetings/$MEETING_ID/analysis
```

`…/transcript` returns the **active** version with all segments (`id`, `start_ms`, `end_ms`,
`speaker_label`, `text`, `confidence`, `source`), each one clickable against the audio.
`…/transcript/text` returns the same as plain text.

`…/analysis` returns everything else in one round trip:

```json
{"meeting_id": "…", "transcript_id": "…", "run": {"provider": "mock-analyzer", "is_mock": true},
 "summary": {"text": "…", "bullets": ["…"]},
 "agenda": {"items": [{"title": "…", "topic": "…", "start_ms": 0, "end_ms": 300000}]},
 "sections": [{"id": "…", "ordinal": 1, "title": "…", "start_ms": 0, "end_ms": 300000, "summary": "…"}],
 "section_summaries": [{"section_id": "…", "payload": {"summary": "…", "bullets": [], "decisions": [], "action_items": []}}],
 "decisions": {"items": [{"text": "…", "decided_by": ["Speaker 1"], "at_ms": 120000, "segment_ids": ["…"]}]},
 "action_items": {"items": [{"text": "…", "owner": "Speaker 2", "due": null, "segment_ids": ["…"]}]},
 "qa": [], "stale_kinds": [], "mock": true}
```

`segment_ids` in decisions/action items are **verified against the stored segments**: the
backend drops any id the model invented, so the client can always resolve a citation. The
same applies to Q&A citations.

### 1.7 Ask questions (optional)

```bash
curl -X POST http://localhost:8080/v1/meetings/$MEETING_ID/qa \
  -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"question": "What did we decide about the release date?", "top_k": 8}'
```

Returns `202` with a `job_id`. When that job succeeds, the newest `qa` document (with
`answer`, `citations[]` carrying `segment_id`/`start_ms`/`end_ms`/`text`, and
`dropped_citations`) appears in `GET /v1/meetings/{id}/qa` and in the analysis bundle.
Add `section_id` to scope a question to one agenda section.

## 2. Re-running and staleness

- Uploading a new media asset or activating a new transcript marks existing analysis
  `stale` (`stale_kinds` in `/status`), because it was derived from older material. Old
  documents are **never silently overwritten**: each kind keeps its history, the newest
  fresh document is what the API returns, and a re-analysis marks its predecessor stale.
- `GET /v1/meetings/{id}/transcripts` lists every version; `POST /v1/transcripts/{id}/activate`
  rolls back to an earlier one (e.g. a bad ASR run) without losing the newer one.
- Speaker cleanup: `PATCH /v1/speakers/{id}` renames (and relabels that speaker's segments),
  `POST /v1/speakers/{id}/merge` folds a duplicate diarization cluster into another.

## 3. Suggested client flow

1. Meeting ends → `POST /v1/meetings` (store `id`), `POST …/media` (audio, then video).
2. `POST …/transcripts` with the exported live transcript, if the user wants to keep it.
3. `POST …/process` → poll `GET …/status` every 2 s (or stream job events), show
   `steps`/`progress`, and show a `mock` badge when `mock` is true.
4. On `status == "ready"` → `GET …/transcript` + `GET …/analysis`, render the improved
   transcript with speaker labels next to the recording (`GET /v1/media/{asset_id}` for
   playback, with `Range`).
5. Offer "ask about this meeting" → `POST …/qa`.

The client must treat the local recording and live transcription as the source of truth for
the live experience and the backend as an *additional* layer. If the backend is unreachable,
recording and live captions must keep working exactly as before — that is the whole point of
keeping this a separate service.

## 4. Provider configuration (server side)

`MVB_TRANSCRIBER`, `MVB_DIARIZER`, `MVB_ANALYZER` select the implementation
(`mock` default, `faster_whisper`, `pyannote`, `lmstudio`, `opencode`). A provider that is
selected but unavailable degrades to the labelled mock and never blocks startup; the API
reports what it actually used in `GET /v1/system/providers`. Point the container at LM Studio
on the host with `MVB_LMSTUDIO_BASE_URL=http://host.docker.internal:1234/v1`.
