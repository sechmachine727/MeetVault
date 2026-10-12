from __future__ import annotations

import uuid
from pathlib import Path

import pytest
from sqlalchemy import select

from mvb.db.models import Job, MediaAsset, Meeting, Speaker, Transcript, TranscriptSegment
from mvb.jobs.worker import tick
from mvb.pipeline.chain import STEP_ORDER, enqueue_pipeline
from mvb.pipeline.status import all_jobs_terminal
from tests.helpers import seed_meeting_with_audio

pytestmark = pytest.mark.skipif(
    not __import__("tests.helpers", fromlist=["ffmpeg_available"]).ffmpeg_available(),
    reason="ffmpeg required for real media fixtures",
)


def _active(session, meeting_id: uuid.UUID) -> Transcript | None:
    return session.scalar(
        select(Transcript).where(Transcript.meeting_id == meeting_id, Transcript.is_active.is_(True))
    )


def _segments(session, transcript: Transcript) -> list[TranscriptSegment]:
    return list(
        session.scalars(
            select(TranscriptSegment)
            .where(TranscriptSegment.transcript_id == transcript.id)
            .order_by(TranscriptSegment.start_ms)
        ).all()
    )


def _drain(max_ticks: int = 12) -> int:
    ticks = 0
    while ticks < max_ticks and tick():
        ticks += 1
    return ticks


def test_media_probe_sets_duration_and_probes_assets(db_session, media_root: Path) -> None:
    meeting, asset = seed_meeting_with_audio(db_session, media_root, seconds=4.0)
    asset.duration_ms = 0  # pretend the upload did not probe it
    db_session.commit()
    enqueue_pipeline(db_session, meeting_id=meeting.id, steps=["media_probe"])
    db_session.commit()
    assert tick() is True
    db_session.expire_all()
    refreshed = db_session.get(Meeting, meeting.id)
    stored = db_session.get(MediaAsset, asset.id)
    assert refreshed is not None and 3500 <= refreshed.duration_ms <= 4500
    assert stored is not None and stored.probe_json["has_audio"] is True


def test_transcribe_creates_a_version_with_bounded_segments(db_session, media_root: Path) -> None:
    meeting, _ = seed_meeting_with_audio(db_session, media_root, seconds=20.0)
    enqueue_pipeline(db_session, meeting_id=meeting.id, steps=["transcribe"])
    db_session.commit()
    assert tick() is True
    db_session.expire_all()
    transcript = _active(db_session, meeting.id)
    assert transcript is not None
    assert transcript.source == "asr"
    assert transcript.is_mock is True
    assert transcript.provider == "mock-transcriber"
    assert transcript.segment_count >= 2
    segments = _segments(db_session, transcript)
    assert len(segments) == transcript.segment_count
    for segment in segments:
        assert 0 <= segment.start_ms < segment.end_ms <= transcript.meeting.duration_ms
        assert segment.text.startswith("[mock:")
    refreshed = db_session.get(Meeting, meeting.id)
    assert refreshed is not None and refreshed.status == "ready"


def test_transcribe_again_creates_version_two_and_deactivates_version_one(
    db_session, media_root: Path
) -> None:
    meeting, _ = seed_meeting_with_audio(db_session, media_root, seconds=12.0)
    enqueue_pipeline(db_session, meeting_id=meeting.id, steps=["transcribe"])
    db_session.commit()
    tick()
    db_session.expire_all()
    first = _active(db_session, meeting.id)
    assert first is not None and first.version == 1
    enqueue_pipeline(db_session, meeting_id=meeting.id, steps=["transcribe"], force=True)
    db_session.commit()
    tick()
    db_session.expire_all()
    second = _active(db_session, meeting.id)
    assert second is not None and second.version == 2
    db_session.refresh(first)
    assert first.is_active is False
    assert db_session.scalar(select(Transcript).where(Transcript.meeting_id == meeting.id).order_by(Transcript.version.desc())) is not None


def test_diarize_labels_segments_and_is_idempotent(db_session, media_root: Path) -> None:
    meeting, _ = seed_meeting_with_audio(db_session, media_root, seconds=50.0)
    enqueue_pipeline(db_session, meeting_id=meeting.id, steps=["transcribe"])
    db_session.commit()
    tick()
    enqueue_pipeline(db_session, meeting_id=meeting.id, steps=["diarize"], force=True)
    db_session.commit()
    assert tick() is True
    db_session.expire_all()
    transcript = _active(db_session, meeting.id)
    assert transcript is not None and transcript.source == "diarized"
    segments = _segments(db_session, transcript)
    labeled = [s for s in segments if s.speaker_label]
    assert labeled, "the mock diarizer must label at least one segment"
    assert all(s.speaker_label.startswith("Speaker ") for s in labeled)
    assert all(s.speaker_id is not None for s in labeled)
    speakers = list(db_session.scalars(select(Speaker).where(Speaker.meeting_id == meeting.id)).all())
    assert {s.label for s in speakers} == {"Speaker 1", "Speaker 2"}

    # Re-running must not duplicate speakers (unique constraint + lookup).
    enqueue_pipeline(db_session, meeting_id=meeting.id, steps=["diarize"], force=True)
    db_session.commit()
    tick()
    db_session.expire_all()
    again = list(db_session.scalars(select(Speaker).where(Speaker.meeting_id == meeting.id)).all())
    assert len(again) == len(speakers)


def test_align_is_idempotent_after_transcribe(db_session, media_root: Path) -> None:
    meeting, _ = seed_meeting_with_audio(db_session, media_root, seconds=20.0)
    enqueue_pipeline(db_session, meeting_id=meeting.id, steps=["transcribe"])
    db_session.commit()
    tick()
    enqueue_pipeline(db_session, meeting_id=meeting.id, steps=["align"], force=True)
    db_session.commit()
    assert tick() is True
    db_session.expire_all()
    job = db_session.scalar(
        select(Job).where(Job.meeting_id == meeting.id, Job.type == "align").order_by(Job.created_at.desc())
    )
    assert job is not None and job.state == "succeeded"
    assert job.result_json["rescaled_segments"] == 0


def test_full_chain_runs_in_order_and_ends_ready(db_session, media_root: Path) -> None:
    meeting, _ = seed_meeting_with_audio(db_session, media_root, seconds=30.0)
    enqueue_pipeline(db_session, meeting_id=meeting.id)  # full STEP_ORDER
    db_session.commit()
    ticks = _drain()
    db_session.expire_all()
    jobs = list(
        db_session.scalars(select(Job).where(Job.meeting_id == meeting.id).order_by(Job.created_at)).all()
    )
    kinds = [job.type for job in jobs]
    assert kinds[: len(STEP_ORDER)] == list(STEP_ORDER), kinds
    assert len(kinds) == len(STEP_ORDER), f"each step must run exactly once: {kinds}"
    assert all(job.state == "succeeded" for job in jobs), [(j.type, j.state, j.error) for j in jobs]
    assert ticks >= len(STEP_ORDER)
    refreshed = db_session.get(Meeting, meeting.id)
    assert refreshed is not None and refreshed.status == "ready"
    assert all_jobs_terminal(db_session, meeting.id) is True
