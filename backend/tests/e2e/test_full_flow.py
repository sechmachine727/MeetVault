from __future__ import annotations

from pathlib import Path

import pytest
from tests.helpers import ffmpeg_available, make_wav

from mvb.jobs.worker import tick

pytestmark = [
    pytest.mark.e2e,
    pytest.mark.skipif(not ffmpeg_available(), reason="ffmpeg required for real media fixtures"),
]


def drain(max_ticks: int = 20) -> int:
    """Run the worker in-process until the queue is empty (the API and the worker share the DB)."""
    ticks = 0
    while ticks < max_ticks and tick():
        ticks += 1
    return ticks


@pytest.fixture
def uploaded_meeting(client, media_root: Path, auth_headers) -> dict:
    """Create a meeting through the API and upload a real 30-second WAV."""
    created = client.post("/v1/meetings", json={"title": "Weekly sync", "external_id": "mv-42"}, headers=auth_headers)
    assert created.status_code == 201, created.text
    meeting = created.json()
    wav = make_wav(Path(media_root) / "seed.wav", 30.0)
    with wav.open("rb") as handle:
        uploaded = client.post(
            f"/v1/meetings/{meeting['id']}/media",
            files={"file": ("weekly-sync.wav", handle, "audio/wav")},
            headers=auth_headers,
        )
    assert uploaded.status_code == 201, uploaded.text
    return {"meeting": meeting, "asset": uploaded.json()}


def test_full_mock_pipeline_over_http(client, uploaded_meeting: dict, auth_headers) -> None:
    meeting_id = uploaded_meeting["meeting"]["id"]
    asset = uploaded_meeting["asset"]
    assert asset["duration_ms"] >= 29_000  # probed from the real file

    started = client.post(f"/v1/meetings/{meeting_id}/process", json={}, headers=auth_headers)
    assert started.status_code == 202, started.text
    assert started.json()["enqueued"] == 1
    assert started.json()["job_ids"], "the pipeline starts with media_probe"

    # A second POST while running is idempotent: no duplicate chain.
    again = client.post(f"/v1/meetings/{meeting_id}/process", json={"steps": ["transcribe"]}, headers=auth_headers)
    assert again.status_code == 202
    assert again.json()["enqueued"] == 0

    assert drain() >= 4, "transcribe, diarize, align, analyze"

    status = client.get(f"/v1/meetings/{meeting_id}/status", headers=auth_headers).json()
    assert status["status"] == "ready"
    assert status["progress"] == 1.0
    assert status["mock"] is True
    assert status["active_job_id"] is None
    assert status["stale_kinds"] == []
    assert set(status["fresh_kinds"]) >= {"summary", "agenda", "decisions", "action_items"}
    assert status["duration_ms"] >= 29_000

    transcript = client.get(f"/v1/meetings/{meeting_id}/transcript", headers=auth_headers)
    assert transcript.status_code == 200, transcript.text
    body = transcript.json()
    assert body["is_mock"] is True
    assert body["provider"] == "mock-transcriber"
    assert body["segment_count"] >= 2
    assert len(body["segments"]) == body["segment_count"]
    for segment in body["segments"]:
        assert 0 <= segment["start_ms"] < segment["end_ms"] <= status["duration_ms"]
        assert segment["text"].startswith("[mock:")
    assert all(segment["speaker_label"].startswith("Speaker ") for segment in body["segments"]), (
        "diarization labels every segment"
    )

    speaker_rows = client.get(f"/v1/meetings/{meeting_id}/speakers", headers=auth_headers).json()
    assert len(speaker_rows) == 2
    assert sum(row["segment_count"] for row in speaker_rows) == body["segment_count"]
    assert all(row["speaking_ms"] >= 0 for row in speaker_rows)

    bundle = client.get(f"/v1/meetings/{meeting_id}/analysis", headers=auth_headers).json()
    assert bundle["mock"] is True
    assert bundle["run"]["provider"] == "mock-analyzer"
    assert bundle["summary"]["text"]
    assert bundle["decisions"]["items"]
    assert bundle["action_items"]["items"]
    assert bundle["sections"], "the agenda must produce sections"
    assert len(bundle["section_summaries"]) == len(bundle["sections"])
    assert all(section["summary"] for section in bundle["sections"])
    assert bundle["stale_kinds"] == []

    sections = client.get(f"/v1/meetings/{meeting_id}/sections", headers=auth_headers).json()
    assert [s["ordinal"] for s in sections] == list(range(1, len(sections) + 1))
    assert sections[0]["start_ms"] == 0
    assert sections[-1]["end_ms"] == status["duration_ms"]

    summary_doc = client.get(f"/v1/meetings/{meeting_id}/sections/{sections[0]['id']}/summary", headers=auth_headers)
    assert summary_doc.status_code == 200
    assert summary_doc.json()["kind"] == "section_summary"

    decisions = client.get(f"/v1/meetings/{meeting_id}/decisions", headers=auth_headers).json()
    assert decisions["kind"] == "decisions"
    assert decisions["payload"]["items"]

    text = client.get(f"/v1/meetings/{meeting_id}/transcript/text", headers=auth_headers)
    assert text.status_code == 200
    assert "[mock:" in text.text and "\n" in text.text

    jobs = client.get(f"/v1/meetings/{meeting_id}/jobs", headers=auth_headers).json()
    # Exact ordering needs sub-second timestamps (Postgres); SQLite ties on the second, so
    # the list is deterministic but not chronological — assert the chain, not the order.
    assert sorted(job["type"] for job in jobs["items"]) == ["align", "analyze", "diarize", "transcribe"]
    assert all(job["state"] == "succeeded" for job in jobs["items"])


def test_qa_and_media_ranges_and_client_transcript(client, uploaded_meeting: dict, auth_headers) -> None:
    meeting_id = uploaded_meeting["meeting"]["id"]
    asset_id = uploaded_meeting["asset"]["asset_id"]
    drain()
    client.post(f"/v1/meetings/{meeting_id}/process", json={}, headers=auth_headers)
    drain()

    # --- Q&A over the analysed transcript -------------------------------------------------
    asked = client.post(
        f"/v1/meetings/{meeting_id}/qa",
        json={"question": "What did we agree about the release date?", "top_k": 4},
        headers=auth_headers,
    )
    assert asked.status_code == 202, asked.text
    job_id = asked.json()["job_id"]
    drain()
    job = client.get(f"/v1/jobs/{job_id}", headers=auth_headers).json()
    assert job["state"] == "succeeded", job
    qa = client.get(f"/v1/meetings/{meeting_id}/qa", headers=auth_headers).json()
    assert qa["total"] == 1
    payload = qa["items"][0]["payload"]
    assert payload["question"].startswith("What did we agree")
    assert payload["answer"]
    assert payload["dropped_citations"] == 0
    assert payload["citations"], "the answer cites the segments it came from"
    for citation in payload["citations"]:
        assert 0 <= citation["start_ms"] < citation["end_ms"]

    # --- media streaming with Range -------------------------------------------------------
    full = client.get(f"/v1/media/{asset_id}", headers=auth_headers)
    assert full.status_code == 200
    assert full.headers["accept-ranges"] == "bytes"
    assert len(full.content) == uploaded_meeting["asset"]["size_bytes"]

    partial = client.get(f"/v1/media/{asset_id}", headers={**auth_headers, "Range": "bytes=0-99"})
    assert partial.status_code == 206
    assert partial.headers["content-range"] == f"bytes 0-99/{uploaded_meeting['asset']['size_bytes']}"
    assert len(partial.content) == 100
    assert partial.content == full.content[:100]

    suffix = client.get(f"/v1/media/{asset_id}", headers={**auth_headers, "Range": "bytes=-50"})
    assert suffix.status_code == 206
    assert len(suffix.content) == 50

    bad = client.get(f"/v1/media/{asset_id}", headers={**auth_headers, "Range": "bytes=999999999-"})
    assert bad.status_code == 416

    info = client.get(f"/v1/media/{asset_id}/info", headers=auth_headers).json()
    assert info["kind"] == "audio" and info["sha256"] == uploaded_meeting["asset"]["sha256"]

    # --- importing the client's own transcript -------------------------------------------
    imported = client.post(
        f"/v1/meetings/{meeting_id}/transcripts",
        json=[
            {"Speaker": "Speaker 1", "Text": "Welcome everyone, let us start with the agenda."},
            {"Speaker": "Speaker 2", "Text": "The release date slips by a week because of testing."},
            {"Speaker": "Speaker 1", "Text": "Agreed, I will update the plan document."},
        ],
        headers=auth_headers,
    )
    assert imported.status_code == 201, imported.text
    body = imported.json()
    assert body["version"] == 2, "the ASR pipeline already created version 1"
    assert body["is_active"] is True
    assert body["source"] == "uploaded"
    assert body["is_mock"] is False
    assert body["segment_count"] == 3
    assert [segment["start_ms"] for segment in body["segments"]] == sorted(
        segment["start_ms"] for segment in body["segments"]
    )
    assert body["segments"][0]["start_ms"] == 0
    assert body["segments"][-1]["end_ms"] == body["duration_ms"], "untimed input is spread over the media"


def test_meeting_crud_lifecycle_and_auth(client, uploaded_meeting: dict, auth_headers) -> None:
    meeting_id = uploaded_meeting["meeting"]["id"]

    assert client.get("/v1/meetings").status_code == 401
    assert client.get("/v1/meetings", headers={"Authorization": "Bearer wrong"}).status_code == 401

    # Idempotent create: same source+external_id returns the same meeting with 200.
    repeat = client.post("/v1/meetings", json={"title": "Weekly sync", "external_id": "mv-42"}, headers=auth_headers)
    assert repeat.status_code == 200
    assert repeat.json()["id"] == meeting_id

    listing = client.get("/v1/meetings?q=weekly", headers=auth_headers).json()
    assert listing["total"] == 1
    assert listing["items"][0]["id"] == meeting_id
    assert listing["limit"] == 50 and listing["offset"] == 0

    patched = client.patch(f"/v1/meetings/{meeting_id}", json={"title": "Renamed sync"}, headers=auth_headers)
    assert patched.status_code == 200
    assert patched.json()["title"] == "Renamed sync"

    detail = client.get(f"/v1/meetings/{meeting_id}", headers=auth_headers).json()
    assert detail["media_count"] == 1
    assert detail["transcript_count"] == 0
    assert detail["has_analysis"] is False

    assert client.get("/v1/meetings/00000000-0000-0000-0000-000000000000", headers=auth_headers).status_code == 404

    deleted = client.delete(f"/v1/meetings/{meeting_id}", headers=auth_headers)
    assert deleted.status_code == 200
    assert deleted.json()["detail"]["purged_files"] is True
    assert client.get(f"/v1/meetings/{meeting_id}", headers=auth_headers).status_code == 404
    assert client.get("/v1/meetings", headers=auth_headers).json()["total"] == 0
    assert client.get("/v1/meetings?include_deleted=true", headers=auth_headers).json()["total"] == 1
