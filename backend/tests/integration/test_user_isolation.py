"""Cross-user isolation: every foreign resource must look exactly like a missing one (404)."""

from __future__ import annotations

import uuid
from pathlib import Path

import pytest

from mvb.db.models import Speaker
from tests.helpers import create_user_with_token, ffmpeg_available, make_wav


def _user_b_headers(db_session, email: str = "user-b@example.com") -> dict[str, str]:
    """Create a SECOND user (B) inside the test; return its bearer headers."""
    _, raw = create_user_with_token(db_session, email=email, password="test-password-b")
    return {"Authorization": f"Bearer {raw}"}


def _create_meeting(client, headers, title: str = "A meeting", external_id: str | None = None) -> str:
    body: dict[str, object] = {"title": title}
    if external_id is not None:
        body["external_id"] = external_id
    response = client.post("/v1/meetings", json=body, headers=headers)
    assert response.status_code == 201, response.text
    return response.json()["id"]


def test_list_is_scoped(client, auth_headers, db_session) -> None:
    b_headers = _user_b_headers(db_session)
    _create_meeting(client, auth_headers, "A one")
    _create_meeting(client, auth_headers, "A two")

    assert client.get("/v1/meetings", headers=b_headers).json()["total"] == 0
    assert client.get("/v1/meetings", headers=auth_headers).json()["total"] == 2


def test_get_foreign_meeting_404(client, auth_headers, db_session) -> None:
    b_headers = _user_b_headers(db_session)
    a_id = _create_meeting(client, auth_headers)

    assert client.get(f"/v1/meetings/{a_id}", headers=b_headers).status_code == 404
    assert client.get(f"/v1/meetings/{a_id}", headers=auth_headers).status_code == 200


def test_patch_delete_foreign_meeting_404(client, auth_headers, db_session) -> None:
    b_headers = _user_b_headers(db_session)
    a_id = _create_meeting(client, auth_headers, "Original title")

    patched = client.patch(f"/v1/meetings/{a_id}", json={"title": "Hijacked"}, headers=b_headers)
    assert patched.status_code == 404
    assert client.get(f"/v1/meetings/{a_id}", headers=auth_headers).json()["title"] == "Original title"

    deleted = client.delete(f"/v1/meetings/{a_id}", headers=b_headers)
    assert deleted.status_code == 404
    assert client.get(f"/v1/meetings/{a_id}", headers=auth_headers).status_code == 200


def test_status_and_process_foreign_404(client, auth_headers, db_session) -> None:
    b_headers = _user_b_headers(db_session)
    a_id = _create_meeting(client, auth_headers)

    assert client.get(f"/v1/meetings/{a_id}/status", headers=b_headers).status_code == 404
    assert client.post(f"/v1/meetings/{a_id}/process", json={}, headers=b_headers).status_code == 404

    status = client.get(f"/v1/meetings/{a_id}/status", headers=auth_headers)
    assert status.status_code == 200
    assert status.json()["status"] == "uploaded"


@pytest.mark.skipif(not ffmpeg_available(), reason="ffmpeg required for real media fixtures")
def test_media_list_upload_foreign_404(client, auth_headers, db_session, media_root: Path) -> None:
    b_headers = _user_b_headers(db_session)
    a_id = _create_meeting(client, auth_headers)

    assert client.get(f"/v1/meetings/{a_id}/media", headers=b_headers).status_code == 404

    wav = make_wav(Path(media_root) / "b-upload.wav", 2.0)
    with wav.open("rb") as handle:
        uploaded = client.post(
            f"/v1/meetings/{a_id}/media",
            files={"file": ("b-upload.wav", handle, "audio/wav")},
            headers=b_headers,
        )
    assert uploaded.status_code == 404
    assert client.get(f"/v1/meetings/{a_id}/media", headers=auth_headers).json()["total"] == 0


@pytest.mark.skipif(not ffmpeg_available(), reason="ffmpeg required for real media fixtures")
def test_media_asset_endpoints_foreign_404(client, auth_headers, db_session, media_root: Path) -> None:
    b_headers = _user_b_headers(db_session)
    a_id = _create_meeting(client, auth_headers)

    wav = make_wav(Path(media_root) / "a-upload.wav", 2.0)
    with wav.open("rb") as handle:
        uploaded = client.post(
            f"/v1/meetings/{a_id}/media",
            files={"file": ("a-upload.wav", handle, "audio/wav")},
            headers=auth_headers,
        )
    assert uploaded.status_code == 201, uploaded.text
    asset_id = uploaded.json()["asset_id"]

    assert client.get(f"/v1/media/{asset_id}/info", headers=b_headers).status_code == 404
    assert client.get(f"/v1/media/{asset_id}", headers=b_headers).status_code == 404
    assert client.delete(f"/v1/media/{asset_id}", headers=b_headers).status_code == 404
    assert client.get(f"/v1/media/{asset_id}/info", headers=auth_headers).status_code == 200


def test_transcript_endpoints_foreign_404(client, auth_headers, db_session) -> None:
    b_headers = _user_b_headers(db_session)
    a_id = _create_meeting(client, auth_headers)

    uploaded = client.post(
        f"/v1/meetings/{a_id}/transcripts",
        json={
            "source": "live_export",
            "segments": [{"start_ms": 0, "end_ms": 1000, "text": "hello", "speaker_label": ""}],
        },
        headers=auth_headers,
    )
    assert uploaded.status_code == 201, uploaded.text
    transcript_id = uploaded.json()["id"]

    assert client.get(f"/v1/meetings/{a_id}/transcript", headers=b_headers).status_code == 404
    assert client.get(f"/v1/meetings/{a_id}/transcript/text", headers=b_headers).status_code == 404
    assert client.get(f"/v1/transcripts/{transcript_id}", headers=b_headers).status_code == 404
    assert client.get(f"/v1/transcripts/{transcript_id}/segments", headers=b_headers).status_code == 404
    assert client.post(f"/v1/transcripts/{transcript_id}/activate", headers=b_headers).status_code == 404
    assert client.delete(f"/v1/transcripts/{transcript_id}", headers=b_headers).status_code == 404
    assert client.get(f"/v1/transcripts/{transcript_id}", headers=auth_headers).status_code == 200


def test_analysis_endpoints_foreign_404(client, auth_headers, db_session) -> None:
    b_headers = _user_b_headers(db_session)
    a_id = _create_meeting(client, auth_headers)

    assert client.get(f"/v1/meetings/{a_id}/analysis", headers=auth_headers).status_code == 200
    assert client.get(f"/v1/meetings/{a_id}/analysis", headers=b_headers).status_code == 404
    assert client.get(f"/v1/meetings/{a_id}/documents", headers=b_headers).status_code == 404
    assert client.get(f"/v1/meetings/{a_id}/decisions", headers=b_headers).status_code == 404
    assert client.get(f"/v1/meetings/{a_id}/qa", headers=b_headers).status_code == 404
    assert client.post(f"/v1/meetings/{a_id}/qa", json={"question": "anything?"}, headers=b_headers).status_code == 404


def test_jobs_endpoints_foreign_404(client, auth_headers, db_session) -> None:
    b_headers = _user_b_headers(db_session)
    a_id = _create_meeting(client, auth_headers)

    queued = client.post(f"/v1/meetings/{a_id}/process", json={}, headers=auth_headers)
    assert queued.status_code == 202, queued.text
    job_id = queued.json()["job_ids"][0]

    assert client.get(f"/v1/jobs/{job_id}", headers=b_headers).status_code == 404
    assert client.post(f"/v1/jobs/{job_id}/cancel", headers=b_headers).status_code == 404
    assert client.get(f"/v1/jobs/{job_id}/events", headers=b_headers).status_code == 404
    assert client.get(f"/v1/jobs/{job_id}", headers=auth_headers).status_code == 200


def test_speakers_endpoints_foreign_404(client, auth_headers, db_session) -> None:
    b_headers = _user_b_headers(db_session)
    a_id = _create_meeting(client, auth_headers)

    speaker = Speaker(id=uuid.uuid4(), meeting_id=uuid.UUID(a_id), label="Speaker 1")
    db_session.add(speaker)
    db_session.commit()
    speaker_id = str(speaker.id)

    assert client.patch(f"/v1/speakers/{speaker_id}", json={"label": "Renamed"}, headers=b_headers).status_code == 404
    assert (
        client.post(
            f"/v1/speakers/{speaker_id}/merge",
            json={"target_speaker_id": str(uuid.uuid4())},
            headers=b_headers,
        ).status_code
        == 404
    )
    assert client.delete(f"/v1/speakers/{speaker_id}", headers=b_headers).status_code == 404
    assert (
        client.patch(f"/v1/speakers/{speaker_id}", json={"label": "Renamed"}, headers=auth_headers).status_code == 200
    )


def test_export_docx_foreign_404(client, auth_headers, db_session) -> None:
    b_headers = _user_b_headers(db_session)
    a_id = _create_meeting(client, auth_headers)

    # Ownership is checked before the 409 "no analysis" branch: B sees 404, never 409.
    assert client.get(f"/v1/meetings/{a_id}/export.docx", headers=b_headers).status_code == 404


def test_foreign_meeting_id_and_missing_id_are_indistinguishable(client, auth_headers, db_session) -> None:
    b_headers = _user_b_headers(db_session)
    a_id = _create_meeting(client, auth_headers)

    foreign = client.get(f"/v1/meetings/{a_id}", headers=b_headers)
    missing = client.get(f"/v1/meetings/{uuid.uuid4()}", headers=b_headers)

    assert foreign.status_code == 404 and missing.status_code == 404
    assert foreign.json()["error"]["code"] == "not_found"
    assert missing.json()["error"]["code"] == "not_found"
    assert foreign.json()["error"]["message"] == missing.json()["error"]["message"]


def test_recreate_after_delete_is_scoped(client, auth_headers, db_session) -> None:
    """A's own tombstone is dropped on re-create; the recreate stays in A's namespace.

    NOTE (spec defect): requirement 13 also asks that user B re-post the SAME external_id and get
    201 with a different id. That is impossible with the frozen schema: `meetings` keeps the
    GLOBAL unique constraint `(source, external_id)` (the plan's A3/A4 never changed it), so a
    foreign live row makes B's INSERT fail before any per-user lookup matters. What IS guaranteed
    (and asserted here) is that the recreate does not cross users: B can neither see nor touch it.
    """
    b_headers = _user_b_headers(db_session)
    first_id = _create_meeting(client, auth_headers, "Recreate", external_id="iso-1")

    assert client.delete(f"/v1/meetings/{first_id}", headers=auth_headers).status_code == 200

    recreated = client.post("/v1/meetings", json={"title": "Recreate", "external_id": "iso-1"}, headers=auth_headers)
    assert recreated.status_code == 201, recreated.text
    new_id = recreated.json()["id"]
    assert new_id != first_id

    # The tombstone drop and the fresh row stayed inside A's namespace.
    assert client.get(f"/v1/meetings/{new_id}", headers=b_headers).status_code == 404
    assert client.get("/v1/meetings", headers=b_headers).json()["total"] == 0
    assert client.get("/v1/meetings", headers=auth_headers).json()["total"] == 1
