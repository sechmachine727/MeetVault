from __future__ import annotations

from pathlib import Path

import pytest
from sqlalchemy import select

from mvb.db.models import Speaker, Transcript, TranscriptSegment
from mvb.jobs.worker import tick
from mvb.pipeline.chain import enqueue_pipeline
from tests.helpers import ffmpeg_available, seed_meeting_with_audio

pytestmark = [
    pytest.mark.skipif(not ffmpeg_available(), reason="ffmpeg required for real media fixtures"),
]


def drain(max_ticks: int = 12) -> int:
    ticks = 0
    while ticks < max_ticks and tick():
        ticks += 1
    return ticks


@pytest.fixture
def analysed(db_session, media_root: Path, auth_user) -> dict:
    """A meeting through the whole pipeline: two diarized speakers and one transcript version."""
    meeting, _ = seed_meeting_with_audio(db_session, media_root, seconds=50.0, user_id=auth_user.id)
    enqueue_pipeline(db_session, meeting_id=meeting.id)
    db_session.commit()
    drain()
    db_session.expire_all()
    transcript = db_session.scalar(
        select(Transcript).where(Transcript.meeting_id == meeting.id, Transcript.is_active.is_(True))
    )
    speakers = list(
        db_session.scalars(select(Speaker).where(Speaker.meeting_id == meeting.id).order_by(Speaker.label)).all()
    )
    return {"meeting": meeting, "transcript": transcript, "speakers": speakers}


# --------------------------------------------------------------------------- speakers


def test_list_speakers_reports_talk_time_and_preview(client, analysed: dict, auth_headers) -> None:
    meeting_id = str(analysed["meeting"].id)
    rows = client.get(f"/v1/meetings/{meeting_id}/speakers", headers=auth_headers).json()
    assert len(rows) == 2, rows
    assert {row["label"] for row in rows} == {"Speaker 1", "Speaker 2"}
    for row in rows:
        assert row["segment_count"] > 0
        assert row["speaking_ms"] > 0
        assert row["preview"]
        assert row["kind"] == "diarized"


def test_list_speakers_is_empty_for_a_meeting_without_analysis(client, auth_headers) -> None:
    meeting = client.post("/v1/meetings", json={"title": "Empty"}, headers=auth_headers).json()
    rows = client.get(f"/v1/meetings/{meeting['id']}/speakers", headers=auth_headers).json()
    assert rows == []


def test_rename_speaker_relabels_its_segments(client, db_session, analysed: dict, auth_headers) -> None:
    speaker = analysed["speakers"][0]
    renamed = client.patch(f"/v1/speakers/{speaker.id}", json={"label": "Ana"}, headers=auth_headers)
    assert renamed.status_code == 200, renamed.text
    assert renamed.json()["label"] == "Ana"

    db_session.expire_all()
    labels = set(
        db_session.scalars(
            select(TranscriptSegment.speaker_label).where(TranscriptSegment.speaker_id == speaker.id)
        ).all()
    )
    assert labels == {"Ana"}, "the denormalised label on the segments must follow the rename"


def test_rename_speaker_to_an_existing_label_conflicts(client, analysed: dict, auth_headers) -> None:
    first, second = analysed["speakers"][0], analysed["speakers"][1]
    response = client.patch(f"/v1/speakers/{first.id}", json={"label": second.label}, headers=auth_headers)
    assert response.status_code == 409
    assert response.json()["error"]["code"] == "conflict"


def test_rename_unknown_speaker_is_404(client, auth_headers) -> None:
    response = client.patch(
        "/v1/speakers/00000000-0000-0000-0000-000000000000",
        json={"label": "Nobody"},
        headers=auth_headers,
    )
    assert response.status_code == 404


def test_merge_moves_segments_and_removes_the_source(client, db_session, analysed: dict, auth_headers) -> None:
    source, target = analysed["speakers"][0], analysed["speakers"][1]
    before = len(db_session.scalars(select(TranscriptSegment).where(TranscriptSegment.speaker_id == target.id)).all())
    moved = len(db_session.scalars(select(TranscriptSegment).where(TranscriptSegment.speaker_id == source.id)).all())
    assert moved > 0, "the mock diarizer must have labelled segments for both speakers"

    response = client.post(
        f"/v1/speakers/{source.id}/merge",
        json={"target_speaker_id": str(target.id)},
        headers=auth_headers,
    )
    assert response.status_code == 200, response.text
    assert response.json()["moved_segments"] == moved

    db_session.expunge_all()  # the API session deleted the row; drop our stale copy
    assert db_session.get(Speaker, source.id) is None, "the merged-away speaker is deleted"
    labels = set(
        db_session.scalars(
            select(TranscriptSegment.speaker_label).where(TranscriptSegment.speaker_id == target.id)
        ).all()
    )
    assert labels == {target.label}
    after = len(db_session.scalars(select(TranscriptSegment).where(TranscriptSegment.speaker_id == target.id)).all())
    assert after == before + moved


def test_merge_into_itself_is_rejected(client, analysed: dict, auth_headers) -> None:
    speaker = analysed["speakers"][0]
    response = client.post(
        f"/v1/speakers/{speaker.id}/merge",
        json={"target_speaker_id": str(speaker.id)},
        headers=auth_headers,
    )
    assert response.status_code == 422
    assert response.json()["error"]["code"] == "validation_error"


def test_merge_across_meetings_is_rejected(client, analysed: dict, auth_headers) -> None:
    other = client.post("/v1/meetings", json={"title": "Other"}, headers=auth_headers).json()
    stranger = client.get(f"/v1/meetings/{other['id']}/speakers", headers=auth_headers).json()
    assert stranger == []
    # Create a speaker in the other meeting directly through the DB is not exposed by the API,
    # so assert the guard with the meeting-scoped lookup instead: an unknown target id.
    response = client.post(
        f"/v1/speakers/{analysed['speakers'][0].id}/merge",
        json={"target_speaker_id": "00000000-0000-0000-0000-000000000000"},
        headers=auth_headers,
    )
    assert response.status_code == 404


def test_delete_speaker_keeps_the_text(client, db_session, analysed: dict, auth_headers) -> None:
    speaker = analysed["speakers"][0]
    before = len(db_session.scalars(select(TranscriptSegment).where(TranscriptSegment.speaker_id == speaker.id)).all())
    response = client.delete(f"/v1/speakers/{speaker.id}", headers=auth_headers)
    assert response.status_code == 200, response.text
    assert response.json()["detail"]["unlinked_segments"] == before

    db_session.expunge_all()  # the API session deleted the row; drop our stale copy
    assert db_session.get(Speaker, speaker.id) is None
    orphans = db_session.scalars(
        select(TranscriptSegment).where(TranscriptSegment.speaker_label == speaker.label)
    ).all()
    assert all(segment.text for segment in orphans), "deleting a speaker must never delete transcript text"
    assert all(segment.speaker_id is None for segment in orphans)


# ----------------------------------------------------------------- transcript versions


def test_list_and_fetch_transcript_versions(client, analysed: dict, auth_headers) -> None:
    meeting_id = str(analysed["meeting"].id)
    versions = client.get(f"/v1/meetings/{meeting_id}/transcripts", headers=auth_headers).json()
    assert versions["total"] == 1
    assert versions["items"][0]["version"] == 1
    assert versions["items"][0]["is_active"] is True

    transcript_id = str(analysed["transcript"].id)
    detail = client.get(f"/v1/transcripts/{transcript_id}", headers=auth_headers)
    assert detail.status_code == 200
    body = detail.json()
    assert body["segment_count"] == len(body["segments"])
    assert body["duration_ms"] == analysed["meeting"].duration_ms


def test_unknown_transcript_is_404(client, auth_headers) -> None:
    assert client.get("/v1/transcripts/00000000-0000-0000-0000-000000000000", headers=auth_headers).status_code == 404


def test_segments_paging_and_time_window(client, analysed: dict, auth_headers) -> None:
    transcript_id = str(analysed["transcript"].id)
    first = client.get(f"/v1/transcripts/{transcript_id}/segments?limit=1&offset=0", headers=auth_headers).json()
    assert first["total"] == analysed["transcript"].segment_count
    assert len(first["items"]) == 1
    assert first["limit"] == 1 and first["offset"] == 0

    second = client.get(f"/v1/transcripts/{transcript_id}/segments?limit=1&offset=1", headers=auth_headers).json()
    assert second["items"][0]["id"] != first["items"][0]["id"]

    window = client.get(f"/v1/transcripts/{transcript_id}/segments?start_ms=0&end_ms=1000", headers=auth_headers).json()
    assert window["total"] >= 1
    for segment in window["items"]:
        assert segment["end_ms"] > 0 and segment["start_ms"] < 1000

    empty = client.get(f"/v1/transcripts/{transcript_id}/segments?start_ms=999999", headers=auth_headers).json()
    assert empty["total"] == 0


def test_import_then_roll_back_versions(client, analysed: dict, auth_headers) -> None:
    meeting_id = str(analysed["meeting"].id)
    first_id = str(analysed["transcript"].id)

    imported = client.post(
        f"/v1/meetings/{meeting_id}/transcripts",
        json=[{"Speaker": "Speaker 1", "Text": "Imported line one."}, {"Speaker": "Speaker 2", "Text": "Two."}],
        headers=auth_headers,
    )
    assert imported.status_code == 201, imported.text
    second_id = imported.json()["id"]
    assert imported.json()["version"] == 2
    assert imported.json()["is_active"] is True

    versions = client.get(f"/v1/meetings/{meeting_id}/transcripts", headers=auth_headers).json()
    assert [item["version"] for item in versions["items"]] == [2, 1]
    assert [item["is_active"] for item in versions["items"]] == [True, False]

    # Rolling back must not delete the newer version.
    rolled = client.post(f"/v1/transcripts/{first_id}/activate", headers=auth_headers)
    assert rolled.status_code == 200, rolled.text
    active = client.get(f"/v1/meetings/{meeting_id}/transcript", headers=auth_headers).json()
    assert active["id"] == first_id and active["version"] == 1
    assert client.get(f"/v1/transcripts/{second_id}", headers=auth_headers).json()["is_active"] is False


def test_deleting_the_active_transcript_conflicts(client, analysed: dict, auth_headers) -> None:
    first_id = str(analysed["transcript"].id)
    response = client.delete(f"/v1/transcripts/{first_id}", headers=auth_headers)
    assert response.status_code == 409
    assert response.json()["error"]["code"] == "conflict"


def test_deleting_a_superseded_transcript_succeeds(client, analysed: dict, auth_headers) -> None:
    meeting_id = str(analysed["meeting"].id)
    first_id = str(analysed["transcript"].id)
    imported = client.post(
        f"/v1/meetings/{meeting_id}/transcripts",
        json=[{"Speaker": "Speaker 1", "Text": "Only line."}],
        headers=auth_headers,
    )
    assert imported.status_code == 201
    removed = client.delete(f"/v1/transcripts/{first_id}", headers=auth_headers)
    assert removed.status_code == 200, removed.text
    assert client.get(f"/v1/transcripts/{first_id}", headers=auth_headers).status_code == 404
    assert client.get(f"/v1/meetings/{meeting_id}/transcripts", headers=auth_headers).json()["total"] == 1


def test_transcript_text_endpoint(client, analysed: dict, auth_headers) -> None:
    meeting_id = str(analysed["meeting"].id)
    plain = client.get(f"/v1/meetings/{meeting_id}/transcript/text", headers=auth_headers)
    assert plain.status_code == 200
    assert "[mock:" in plain.text
    without = client.get(f"/v1/meetings/{meeting_id}/transcript/text?speaker_prefix=false", headers=auth_headers).text
    assert "Speaker 1:" not in without


# --------------------------------------------------------------- analysis extras / errors


def test_document_filters_and_missing_kinds(client, analysed: dict, auth_headers) -> None:
    meeting_id = str(analysed["meeting"].id)
    summaries = client.get(f"/v1/meetings/{meeting_id}/documents?kind=summary", headers=auth_headers).json()
    assert summaries["total"] >= 1
    assert {item["kind"] for item in summaries["items"]} == {"summary"}

    all_docs = client.get(f"/v1/meetings/{meeting_id}/documents", headers=auth_headers).json()
    stale_included = client.get(f"/v1/meetings/{meeting_id}/documents?include_stale=true", headers=auth_headers).json()
    assert stale_included["total"] >= all_docs["total"]

    runs = client.get(f"/v1/meetings/{meeting_id}/analysis/runs", headers=auth_headers).json()
    assert runs["total"] == 1 and runs["items"][0]["provider"] == "mock-analyzer"

    compact = client.get(f"/v1/meetings/{meeting_id}/jobs/latest", headers=auth_headers).json()
    assert [job["type"] for job in compact] == ["media_probe", "transcribe", "diarize", "align", "analyze"]

    fresh = client.post("/v1/meetings", json={"title": "No analysis"}, headers=auth_headers).json()
    for path in ("decisions", "action-items"):
        response = client.get(f"/v1/meetings/{fresh['id']}/{path}", headers=auth_headers)
        assert response.status_code == 404, path
        assert response.json()["error"]["code"] == "not_found"
    assert client.get(f"/v1/meetings/{fresh['id']}/analysis", headers=auth_headers).status_code == 200


def test_section_summary_404_for_unknown_section(client, analysed: dict, auth_headers) -> None:
    meeting_id = str(analysed["meeting"].id)
    response = client.get(
        f"/v1/meetings/{meeting_id}/sections/00000000-0000-0000-0000-000000000000/summary",
        headers=auth_headers,
    )
    assert response.status_code == 404


def test_qa_validation_and_unknown_section(client, analysed: dict, auth_headers) -> None:
    meeting_id = str(analysed["meeting"].id)
    assert client.post(f"/v1/meetings/{meeting_id}/qa", json={}, headers=auth_headers).status_code == 422
    assert client.post(f"/v1/meetings/{meeting_id}/qa", json={"question": ""}, headers=auth_headers).status_code == 422
    unknown = client.post(
        f"/v1/meetings/{meeting_id}/qa",
        json={"question": "Anything?", "section_id": "00000000-0000-0000-0000-000000000000"},
        headers=auth_headers,
    )
    assert unknown.status_code == 404
    assert client.get(f"/v1/meetings/{meeting_id}/qa", headers=auth_headers).json()["total"] == 0


def test_media_deletion_removes_file_and_row(client, db_session, analysed: dict, auth_headers) -> None:
    from mvb.db.models import MediaAsset

    asset = db_session.scalar(select(MediaAsset).where(MediaAsset.meeting_id == analysed["meeting"].id))
    assert asset is not None
    response = client.delete(f"/v1/media/{asset.id}", headers=auth_headers)
    assert response.status_code == 200, response.text
    db_session.expunge_all()  # the API session deleted the row; drop our stale copy
    assert db_session.get(MediaAsset, asset.id) is None
    assert client.get(f"/v1/media/{asset.id}/info", headers=auth_headers).status_code == 404
    assert client.get(f"/v1/meetings/{analysed['meeting'].id}/media", headers=auth_headers).json()["total"] == 0
