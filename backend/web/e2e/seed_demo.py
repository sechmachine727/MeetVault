"""Seed a deterministic demo dataset against a RUNNING MeetVault backend.

Used by the Playwright suite and by the report run. It is NOT part of the app: it speaks the
public HTTP contract (plus one direct DB insert to pin a `running` job so the list's elapsed
timer is deterministic in screenshots).

Env:
  MVB_DEMO_URL       base URL of the running API (default http://127.0.0.1:8099)
  MVB_DEMO_EMAIL     demo account (default demo@meetvault.local)
  MVB_DEMO_PASSWORD  demo password (default demo-password-123)
  MVB_MEDIA_ROOT     where the generated WAVs are staged before upload
  MVB_DATABASE_URL / MVB_MEDIA_ROOT are also used by the in-process worker ticks.

Prints a JSON summary on stdout, including the pinned-running meeting id.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path

BASE = os.environ.get("MVB_DEMO_URL", "http://127.0.0.1:8099")
EMAIL = os.environ.get("MVB_DEMO_EMAIL", "demo@meetvault.local")
PASSWORD = os.environ.get("MVB_DEMO_PASSWORD", "demo-password-123")
MEDIA = Path(os.environ.get("MVB_MEDIA_ROOT", "data/media"))

TRANSCRIPT = [
    {
        "Speaker": "Speaker 1",
        "Text": "Welcome everyone. Today we review the MeetVault cloud release and the storage migration.",
    },
    {
        "Speaker": "Speaker 2",
        "Text": "The release slips by a week because the object storage migration needs another test pass.",
    },
    {"Speaker": "Speaker 1", "Text": "Agreed. We ship the console first, then the migration."},
    {"Speaker": "Speaker 2", "Text": "I will draft the rollout plan and share it before Friday."},
]


def http(method: str, path: str, token: str | None = None, body=None, raw: bytes | None = None, content_type: str | None = None):
    data = None
    headers = {}
    if raw is not None:
        data = raw
        headers["Content-Type"] = content_type or "application/octet-stream"
    elif body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            payload = response.read()
            return response.status, (json.loads(payload) if payload else None)
    except urllib.error.HTTPError as exc:
        raw_body = exc.read()
        try:
            parsed = json.loads(raw_body) if raw_body else None
        except json.JSONDecodeError:
            parsed = {"raw": raw_body.decode(errors="replace")}
        return exc.code, parsed


def login() -> str:
    status, body = http("POST", "/v1/auth/login", body={"email": EMAIL, "password": PASSWORD})
    if status != 200:
        raise SystemExit(f"login failed: {status} {body}")
    return body["token"]


def make_wav(path: Path, seconds: float) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        [
            "ffmpeg",
            "-y",
            "-f",
            "lavfi",
            "-i",
            f"sine=frequency=440:duration={seconds}",
            "-ac",
            "1",
            "-ar",
            "48000",
            str(path),
        ],
        check=True,
        capture_output=True,
    )
    return path


def make_video(path: Path, seconds: float, size: str = "640x360", rate: int = 15) -> Path:
    """A real H.264 MP4 — the desktop client's screen-recording track."""
    path.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        [
            "ffmpeg",
            "-y",
            "-f",
            "lavfi",
            "-i",
            f"testsrc=size={size}:rate={rate}:duration={seconds}",
            "-pix_fmt",
            "yuv420p",
            "-c:v",
            "libx264",
            "-preset",
            "ultrafast",
            str(path),
        ],
        check=True,
        capture_output=True,
    )
    return path


def upload_media(
    token: str, meeting_id: str, path: Path, kind: str, content_type: str, start_offset_ms: int = 0
) -> int:
    boundary = "----mvwebseed"
    body = (
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="file"; filename="{path.name}"\r\n'
        f"Content-Type: {content_type}\r\n\r\n"
    ).encode() + path.read_bytes() + f"\r\n--{boundary}--\r\n".encode()
    request = urllib.request.Request(
        f"{BASE}/v1/meetings/{meeting_id}/media?kind={kind}&start_offset_ms={start_offset_ms}",
        data=body,
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": f"multipart/form-data; boundary={boundary}",
        },
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=300) as response:
        return response.status


def upload_audio(token: str, meeting_id: str, wav: Path) -> int:
    return upload_media(token, meeting_id, wav, "audio", "audio/wav")


def create_meeting(token: str, title: str, suffix: str) -> dict:
    status, meeting = http(
        "POST",
        "/v1/meetings",
        token=token,
        body={"title": title, "external_id": f"demo-{suffix}", "language": "en", "source": "meetvault"},
    )
    assert status in (200, 201), (status, meeting)
    return meeting


def main() -> int:
    token = login()
    # A second seed against a live database would DOUBLE every recording (the media POST is
    # append-only), so refuse instead of silently producing a different dataset.
    status, page = http("GET", "/v1/meetings?limit=200", token=token)
    existing = {m.get("external_id") for m in (page or {}).get("items", [])}
    if "demo-1" in existing:
        raise SystemExit(
            "demo-1 already exists: start from a fresh database "
            "(web/run-demo-stack.sh start) — the media upload is append-only, so re-seeding "
            "would double the recordings and break the screenshots."
        )
    created: list[dict] = []
    # Deterministic ids: re-seeding against a fresh DB always yields the same external_ids.
    for index, (title, seconds) in enumerate(
        [("Cloud console review", 30.0), ("Storage migration standup", 20.0), ("Customer feedback sync", 12.0)],
        start=1,
    ):
        meeting = create_meeting(token, title, f"{index}")
        meeting_id = meeting["id"]
        wav = make_wav(MEDIA / "seed" / f"demo-{index}.wav", seconds)
        assert upload_audio(token, meeting_id, wav) == 201
        # Meetings 1 and 3 also carry a screen-recording track, exactly like the desktop client's
        # video asset (kind=video, starting 2 s into the meeting) so the console's video path,
        # start offset and per-kind rendering are exercised, not just audio.
        if index in (1, 3):
            video = make_video(MEDIA / "seed" / f"demo-{index}-screen.mp4", seconds)
            assert upload_media(token, meeting_id, video, "video", "video/mp4", start_offset_ms=2000) == 201
        status, imported = http(
            "POST", f"/v1/meetings/{meeting_id}/transcripts?align=auto", token=token, body=TRANSCRIPT
        )
        assert status == 201, (status, imported)
        created.append({"id": meeting_id, "title": title, "media": 2 if index in (1, 3) else 1})

    # Meeting 1: run the full pipeline to a finished, analyzed state.
    status, started = http(
        "POST",
        f"/v1/meetings/{created[0]['id']}/process",
        token=token,
        body={"steps": ["transcribe", "diarize", "align", "analyze"], "language": "en"},
    )
    assert status == 202, (status, started)

    # Drain the queue in-process (the same tick the worker runs).
    from mvb.jobs.worker import tick  # imported late so the env is already in place

    deadline = time.time() + 180
    while time.time() < deadline:
        tick()
        status, meeting_status = http("GET", f"/v1/meetings/{created[0]['id']}/status", token=token)
        if meeting_status and meeting_status.get("status") == "ready" and meeting_status.get("fresh_kinds"):
            break
        time.sleep(0.2)

    # Ask one question so the Q&A tab has a stored answer with citations.
    status, asked = http(
        "POST",
        f"/v1/meetings/{created[0]['id']}/qa",
        token=token,
        body={"question": "What did we decide about the release date?", "top_k": 8},
    )
    if status == 202:
        deadline = time.time() + 90
        while time.time() < deadline:
            tick()
            status, docs = http("GET", f"/v1/meetings/{created[0]['id']}/qa", token=token)
            if docs and docs.get("total"):
                break
            time.sleep(0.2)

    # Meeting 3: pin a running job so the list shows a live elapsed timer deterministically.
    # heartbeat_at is set so the worker's stale-job reaper leaves it alone.
    from mvb.db.base import get_session_factory
    from mvb.db.models import Job

    session = get_session_factory()()
    try:
        now = datetime.now(UTC)
        session.add(
            Job(
                meeting_id=uuid.UUID(created[2]["id"]),
                type="analyze",
                state="running",
                progress=0.47,
                attempts=1,
                params_json={"steps": ["transcribe", "diarize", "align", "analyze"]},
                result_json={},
                started_at=now - timedelta(seconds=92),
                heartbeat_at=now,
                is_mock=True,
            )
        )
        session.commit()
    finally:
        session.close()

    summary = {
        "base_url": BASE,
        "email": EMAIL,
        "meetings": created,
        "ready": created[0]["id"],
        "pinned_running": created[2]["id"],
    }
    print(json.dumps(summary, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
