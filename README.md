# MeetVault

MeetVault records meetings on Windows, transcribes them on-device in real time, and — optionally —
sends a finished meeting to its own local backend service for better transcription, speaker labels,
summaries and Q&A. Everything runs on your machines: no cloud account, no subscription.

This repository contains both halves:

- [client/](client/README.md) — the WinUI 3 desktop app (.NET 8). Records audio + screen, live
  Vosk transcription, transcript editing/export/playback, optional Gemini refinement, and the
  backend integration UI (Settings ▸ Backend service; a meeting's Details ▸ Send to backend; the
  Backend page).
- [backend/](backend/README.md) — a FastAPI service that ingests a finished recording (audio, screen
  video, exported transcript), re-processes it asynchronously with pluggable providers
  (mock / faster-whisper / pyannote / LM Studio / OpenCode), and serves meetings, transcripts,
  speakers, analysis and Q&A over HTTP. See [backend/docs/INTEGRATION.md](backend/docs/INTEGRATION.md)
  for the client contract.

## Quick start — client

```powershell
cd client
dotnet build MeetVault.sln -c Release
dotnet test tests\MeetVault.Core.Tests\MeetVault.Core.Tests.csproj -c Release --nologo -v q
dotnet publish src\MeetVault\MeetVault.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
```

The portable app is `client/publish/win-x64/MeetVault.exe` (stage `client/models/` into the publish
folder first — see [client/RUN-GUIDE.md](client/RUN-GUIDE.md)).

## Quick start — backend (no Docker needed for dev)

```bash
cd backend
python -m venv .venv
.venv/Scripts/python.exe -m pip install -e ".[dev]"     # Linux/macOS: .venv/bin/python
.venv/Scripts/python.exe -m alembic upgrade head
.venv/Scripts/python.exe -m uvicorn mvb.main:app --port 8080
# second shell: the job worker
.venv/Scripts/python.exe -m mvb.jobs.worker
```

Full deployment (Docker + Postgres) and configuration: [backend/README.md](backend/README.md),
[backend/HANDOVER.md](backend/HANDOVER.md).

## Agent/contributor rules

- [AGENTS.md](AGENTS.md) — repo-wide (combined) rules.
- [client/AGENTS.md](client/AGENTS.md) · [backend/AGENTS.md](backend/AGENTS.md) — per-tree rules.
- Work logs: [client/docs/progress/](client/docs/progress/) · [backend/docs/progress/](backend/docs/progress/).
