"""Meeting lifecycle: idempotent create and delete-then-recreate (D1 regression)."""

from __future__ import annotations

from pathlib import Path

import pytest

from tests.helpers import ffmpeg_available, make_wav


def test_repost_returns_existing_meeting_with_200(client, auth_headers) -> None:
    first = client.post(
        "/v1/meetings",
        json={"title": "Idempotent", "external_id": "idem-1"},
        headers=auth_headers,
    )
    assert first.status_code == 201, first.text
    second = client.post(
        "/v1/meetings",
        json={"title": "Idempotent", "external_id": "idem-1"},
        headers=auth_headers,
    )
    assert second.status_code == 200, second.text
    assert second.json()["id"] == first.json()["id"]


def test_delete_then_recreate_same_external_id_creates_fresh_meeting(client, auth_headers) -> None:
    """D1: remove-then-resend used to 500 (UNIQUE source/external_id vs the soft-deleted row)."""
    created = client.post(
        "/v1/meetings",
        json={"title": "Repro", "external_id": "repro-1"},
        headers=auth_headers,
    )
    assert created.status_code == 201, created.text
    old_id = created.json()["id"]

    deleted = client.delete(f"/v1/meetings/{old_id}", headers=auth_headers)
    assert deleted.status_code == 200, deleted.text

    recreated = client.post(
        "/v1/meetings",
        json={"title": "Repro", "external_id": "repro-1"},
        headers=auth_headers,
    )
    assert recreated.status_code == 201, recreated.text
    new_id = recreated.json()["id"]
    assert new_id != old_id

    detail = client.get(f"/v1/meetings/{new_id}", headers=auth_headers)
    assert detail.status_code == 200, detail.text
    assert detail.json()["status"] == "uploaded"

    listed = client.get("/v1/meetings", headers=auth_headers).json()
    assert any(item["id"] == new_id for item in listed["items"]), listed


@pytest.mark.skipif(not ffmpeg_available(), reason="ffmpeg required for real media fixtures")
def test_delete_then_recreate_meeting_with_content(client, media_root: Path, auth_headers) -> None:
    """D1 with teeth: the removed meeting owns a media asset and queued jobs, so the
    tombstone drop must cascade instead of tripping a foreign key."""
    created = client.post(
        "/v1/meetings",
        json={"title": "Content", "external_id": "repro-content"},
        headers=auth_headers,
    )
    assert created.status_code == 201, created.text
    old_id = created.json()["id"]

    wav = make_wav(Path(media_root) / "seed.wav", 6.0)
    with wav.open("rb") as handle:
        uploaded = client.post(
            f"/v1/meetings/{old_id}/media",
            files={"file": ("seed.wav", handle, "audio/wav")},
            headers=auth_headers,
        )
    assert uploaded.status_code == 201, uploaded.text

    started = client.post(f"/v1/meetings/{old_id}/process", json={}, headers=auth_headers)
    assert started.status_code == 202, started.text

    assert client.delete(f"/v1/meetings/{old_id}", headers=auth_headers).status_code == 200

    recreated = client.post(
        "/v1/meetings",
        json={"title": "Content", "external_id": "repro-content"},
        headers=auth_headers,
    )
    assert recreated.status_code == 201, recreated.text
    assert recreated.json()["id"] != old_id


def test_meeting_list_reports_job_and_analysis_fields(client, auth_headers) -> None:
    created = client.post("/v1/meetings", json={"title": "Fields"}, headers=auth_headers).json()
    row = client.get("/v1/meetings", headers=auth_headers).json()["items"][0]
    assert row["id"] == created["id"]
    assert row["has_analysis"] is False
    assert row["latest_job_type"] is None
    assert row["latest_job_state"] is None
    # queue a pipeline; the list must surface the newest job
    client.post(f"/v1/meetings/{created['id']}/process", json={}, headers=auth_headers)
    row = client.get("/v1/meetings", headers=auth_headers).json()["items"][0]
    assert row["latest_job_type"] == "transcribe"
    assert row["latest_job_state"] == "queued"


def test_force_process_on_processing_meeting_persists_the_job(client, auth_headers) -> None:
    """Live-verified defect (E5 wave): a force re-run on a meeting whose aggregate
    status is already `processing` (a worker died mid-pipeline) answered
    `enqueued: 1` with a job id while the row was never committed, so the worker
    could never claim it and the UI's Retry analysis was a silent no-op."""
    created = client.post("/v1/meetings", json={"title": "Force re-run"}, headers=auth_headers).json()
    meeting_id = created["id"]

    # No worker runs in the test app: the first call leaves the aggregate `processing`.
    first = client.post(f"/v1/meetings/{meeting_id}/process", json={}, headers=auth_headers)
    assert first.status_code == 202, first.text
    assert first.json()["enqueued"] == 1
    status = client.get(f"/v1/meetings/{meeting_id}/status", headers=auth_headers).json()
    assert status["status"] == "processing"

    # A force re-run while already processing must persist the new job.
    re_run = client.post(
        f"/v1/meetings/{meeting_id}/process",
        json={"steps": ["analyze"], "force": True},
        headers=auth_headers,
    )
    assert re_run.status_code == 202, re_run.text
    assert re_run.json()["enqueued"] == 1
    job_id = re_run.json()["job_ids"][0]

    # The row must survive the request: a fresh read answers 200 (not 404) and is queued.
    persisted = client.get(f"/v1/jobs/{job_id}", headers=auth_headers)
    assert persisted.status_code == 200, persisted.text
    assert persisted.json()["state"] == "queued"
