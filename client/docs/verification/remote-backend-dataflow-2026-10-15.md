# Remote backend dataflow verification — 2026-10-15

**Question asked:** does the desktop frontend correctly use the *remote* backend?
**Answer: yes — verified on a full dataflow, every cloud function, with the app's own UI driven.**
Also found: the account password is committed in this repo (finding 1, act on it).

Verified from this workstation against `https://meetvault.supersanta.io.vn`. Evidence below is
what commands and the app itself actually printed; nothing here is inferred from configuration
alone.

---

## 0. Environment (measured)

| Check | Result |
| --- | --- |
| DNS | `meetvault.supersanta.io.vn` → `1.53.35.146` (public) |
| `GET /healthz` / `/readyz` | `200` / `200` |
| `GET /v1/meetings` with no token | `401` (auth enforced) |
| TLS | SAN includes `DNS:meetvault.supersanta.io.vn`; valid 2026-10-15 → 2027-01-13 |
| Server providers | transcriber / diarizer / analyzer all `is_mock: true` (server-side config) |
| App binary | `client/publish/win-x64/MeetVault.exe`, built 11:00 today, running since 11:01 (PID 10552) |
| App settings (its own SQLite DB) | `backend.url = https://meetvault.supersanta.io.vn`, `backend.email = demo@example.com` |

## 1. The frontend's own surfaces (driven via UIA; evidence = the app's rendered text)

| Surface | What the app rendered | Where it came from |
| --- | --- | --- |
| Settings ▸ Cloud service URL | `BackendUrlBox = https://meetvault.supersanta.io.vn` | the stored setting, not a default fallback |
| Settings ▸ Test backend connection | `BackendStatus = Ready · transcribe mock-transcriber [mock] · diarize mock-diarizer [mock] · analyze mock-analyzer [mock]` | a live call to the remote's provider endpoint |
| Account | `Signed in as demo@example.com.` / `Session expires 2026-11-14T05:47:07.574274Z.` | the remote's login response |
| Cloud Storage (list) | `API smoke test 2026-10-15` · `2026-10-15T05:45:26.456361Z · 3s` · `Analyzed` + Open / Remove from cloud | `GET /v1/meetings` on the remote |
| Cloud Storage ▸ Refresh | re-fetched live and additionally showed `frontend dataflow sweep …` — a meeting the API sweep had created seconds earlier | proves the list is server state, not a cache |
| Details ▸ player | `00:00` → `DurationText = 00:03` | the remote's probed media duration |
| Details ▸ Analyze meeting | `Analyzed · mock data` + `mock data` badge + `Analyze again` | the remote's analysis run |
| Details ▸ Meeting summary | `[mock] Summary f29fc813a2b4`, `[mock] point 1/2`, `Agenda & discussion` with `[00:00-00:03] intro`, `Decisions`, `Action items`, Q&A box | the remote's analysis bundle, citations included |
| Transcript panel | `Backend-enhanced transcript`, row `00:00 · Speaker 1 · [mock:en] Welcome everyone to the meeting.` | the remote's transcript |
| Export summary to Word | `C:\Users\supersanta\Documents\MeetVault\api-smoke-test-2026-10-15.summary.docx` (36 974 B; extracted text = the remote summary/agenda/decisions) | the remote's `export.docx` |

## 2. Every cloud function against the remote — 31/31 PASS

Scratch meeting created by the sweep, exercised, then removed (the owner's data untouched).
Full log: `%LOCALAPPDATA%/hermes/cache/scratch/mvremote/sweep-final.txt`.

```text
PASS  auth.login            200 token_len=43 user=demo@example.com expires=2026-11-14
PASS  auth.me               200 demo@example.com
PASS  meetings.list         200 total=1
PASS  system.info           200 env=prod migration_head=20261014_0002
PASS  system.providers      200 transcriber=effective mock-transcriber is_mock=True
PASS  meetings.create       201
PASS  media.upload          201 (multipart wav, server-side probe)
PASS  media.list            200 count=1
PASS  media.info            200
PASS  media.bytes           200 bytes=96078 (local 96078)
PASS  media.range           206 got=100            (Range requests)
PASS  meetings.process      202 steps=['transcribe'] → server worker chained analyze
PASS  meetings.status->ready 200 status=ready
PASS  analysis.after-process 200 keys=[action_items, agenda, decisions, meeting_id, mock, qa]
PASS  jobs.list             200 count=2 (transcribe + analyze)
PASS  jobs.detail           200 type=analyze state=succeeded
PASS  transcript.get        200 version=1 segments=1
PASS  transcript.text       200 chars=42
PASS  transcripts.list      200 count=1
PASS  transcripts.activate  200 ok=True
PASS  analysis.get          200 (summary/agenda/decisions/action_items/qa)
PASS  speakers.list         200 count=0   (no diarize step requested)
PASS  qa.list               200 items=0
PASS  qa.ask                202 queued
PASS  qa.answer_landed      200 stored=1 answer='[mock] answer b69cdaf10349'   (async worker)
PASS  export.docx           200 bytes=36912 zip_magic=True                     (a real Word file)
PASS  media.delete          200
PASS  meetings.remove       200
PASS  meetings.removed->404 404 code=not_found
PASS  auth.logout           200
PASS  auth.token_revoked    401 code=session_expired
```

## 3. The client's own tests

```text
$ cd client/tests/MeetVault.Core.Tests && dotnet test -c Release
Passed!  - Failed: 0, Passed: 355, Skipped: 0, Total: 355
```

`BackendDefaultsTests` / `BackendSettingsTests` pin the rules this verification relies on: the
shipped default is absolute HTTPS with no trailing slash, the transport builds
`https://meetvault.supersanta.io.vn/healthz`, `Resolve` falls back to the shipped default for an
empty setting, the legacy `http://127.0.0.1:8081` is migrated exactly once, and a cosmetic
difference (case / trailing slash) never triggers the server-change reset.

## 4. Findings

1. **[High — act on this] The account password is committed and pushed.** `client/docs/progress/progress_supersanta/PROGRESS_2026-10-15.md`
   contains `demo@example.com password…` in plain text on a public GitHub branch. That account holds
   the only data on the deployed host, and `MVB_ALLOW_SIGNUP` defaults to true. Anyone with the repo
   can sign in and delete it. Recommend: change that password, delete the literal from the doc, and
   set `MVB_ALLOW_SIGNUP=false` on the server (see the backend report, finding 17).
2. **[Medium] "Export transcript to Documents" does not work for a cloud-only meeting.** With a
   backend transcript visibly rendered (`Backend-enhanced transcript`, one segment), clicking it
   shows **"The transcript is empty."** — reproduced twice. The export reads the local store while
   the panel displays the server transcript, so the button promises something it cannot do. The same
   meeting's "Export summary to Word" works correctly.
3. **[Low] The player renders an empty viewport for an audio-only asset** (a 3 s WAV): no waveform or
   placeholder, just a black box. Cosmetic, but it reads as "broken video".
4. **Not a defect, worth knowing:** `GET /v1/meetings/{id}/export.docx` answers `409 no_analysis`
   until an analysis run exists. The first sweep exported before analyzing and got that 409 —
   correct behaviour, and the app only offers the export once a summary exists.
5. **Speakers are 0 on a transcribe-only run** — diarize is a separate step, so this is expected.

## 5. Scope and hygiene

- **Not driven through the app UI on purpose:** "Remove from cloud", "Analyze again" and "Ask" would
  mutate the owner's only cloud meeting. They were exercised at the contract level (31/31 above) on
  the sweep's own scratch meeting, which the sweep removed at the end — the remote now holds only
  `API smoke test 2026-10-15`.
- The sweep signed in and out; its token answered `401 session_expired` afterwards.
- No password, token or session value appears in this report or in the sweep log.
- The app was not restarted or reconfigured for this test: it had been running since 11:01 against
  the current publish, and it already pointed at the remote.
