"""Meeting lifecycle: idempotent create and delete-then-recreate (D1 regression)."""

from __future__ import annotations

from pathlib import Path

import pytest

from tests.helpers import ffmpeg_available, make_wav

HEADERS = {"Authorization": "Bearer test-key"}


def test_repost_returns_existing_meeting_with_200(client) -> None:
    first = client.post(
        "/v1/meetings",
        json={"title": "Idempotent", "external_id": "idem-1"},
        headers=HEADERS,
    )
    assert first.status_code == 201, first.text
    second = client.post(
        "/v1/meetings",
        json={"title": "Idempotent", "external_id": "idem-1"},
        headers=HEADERS,
    )
    assert second.status_code == 200, second.text
    assert second.json()["id"] == first.json()["id"]


def test_delete_then_recreate_same_external_id_creates_fresh_meeting(client) -> None:
    """D1: remove-then-resend used to 500 (UNIQUE source/external_id vs the soft-deleted row)."""
    created = client.post(
        "/v1/meetings",
        json={"title": "Repro", "external_id": "repro-1"},
        headers=HEADERS,
    )
    assert created.status_code == 201, created.text
    old_id = created.json()["id"]

    deleted = client.delete(f"/v1/meetings/{old_id}", headers=HEADERS)
    assert deleted.status_code == 200, deleted.text

    recreated = client.post(
        "/v1/meetings",
        json={"title": "Repro", "external_id": "repro-1"},
        headers=HEADERS,
    )
    assert recreated.status_code == 201, recreated.text
    new_id = recreated.json()["id"]
    assert new_id != old_id

    detail = client.get(f"/v1/meetings/{new_id}", headers=HEADERS)
    assert detail.status_code == 200, detail.text
    assert detail.json()["status"] == "uploaded"

    listed = client.get("/v1/meetings", headers=HEADERS).json()
    assert any(item["id"] == new_id for item in listed["items"]), listed


@pytest.mark.skipif(not ffmpeg_available(), reason="ffmpeg required for real media fixtures")
def test_delete_then_recreate_meeting_with_content(client, media_root: Path) -> None:
    """D1 with teeth: the removed meeting owns a media asset and queued jobs, so the
    tombstone drop must cascade instead of tripping a foreign key."""
    created = client.post(
        "/v1/meetings",
        json={"title": "Content", "external_id": "repro-content"},
        headers=HEADERS,
    )
    assert created.status_code == 201, created.text
    old_id = created.json()["id"]

    wav = make_wav(Path(media_root) / "seed.wav", 6.0)
    with wav.open("rb") as handle:
        uploaded = client.post(
            f"/v1/meetings/{old_id}/media",
            files={"file": ("seed.wav", handle, "audio/wav")},
            headers=HEADERS,
        )
    assert uploaded.status_code == 201, uploaded.text

    started = client.post(f"/v1/meetings/{old_id}/process", json={}, headers=HEADERS)
    assert started.status_code == 202, started.text

    assert client.delete(f"/v1/meetings/{old_id}", headers=HEADERS).status_code == 200

    recreated = client.post(
        "/v1/meetings",
        json={"title": "Content", "external_id": "repro-content"},
        headers=HEADERS,
    )
    assert recreated.status_code == 201, recreated.text
    assert recreated.json()["id"] != old_id
