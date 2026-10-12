from __future__ import annotations

import uuid

from sqlalchemy import select
from sqlalchemy.orm import Session

from mvb.db.models import MediaAsset, Meeting, Transcript, TranscriptSegment
from mvb.pipeline.align import align_segments
from mvb.pipeline.parse_transcript import ParsedSegment
from mvb.staleness import mark_documents_stale


def meeting_duration_ms(session: Session, meeting_id: uuid.UUID) -> int:
    """The authoritative duration: the meeting row, else its first audio asset."""
    meeting = session.get(Meeting, meeting_id)
    if meeting is not None and meeting.duration_ms:
        return meeting.duration_ms
    asset = session.scalar(
        select(MediaAsset).where(MediaAsset.meeting_id == meeting_id).order_by(MediaAsset.created_at.asc())
    )
    return asset.duration_ms if asset is not None else 0


def active_transcript(session: Session, meeting_id: uuid.UUID) -> Transcript | None:
    return session.scalar(
        select(Transcript).where(Transcript.meeting_id == meeting_id, Transcript.is_active.is_(True))
    )


def realign_active_transcript(session: Session, meeting_id: uuid.UUID) -> int:
    """Rescale the active transcript's segments onto the media duration. Idempotent."""
    transcript = active_transcript(session, meeting_id)
    if transcript is None:
        return 0
    rows = session.scalars(
        select(TranscriptSegment)
        .where(TranscriptSegment.transcript_id == transcript.id)
        .order_by(TranscriptSegment.start_ms)
    ).all()
    if not rows:
        return 0
    parsed = [
        ParsedSegment(
            text=row.text,
            start_ms=row.start_ms,
            end_ms=row.end_ms,
            speaker_label=row.speaker_label,
            confidence=row.confidence,
        )
        for row in rows
    ]
    aligned = align_segments(parsed, meeting_duration_ms(session, meeting_id), mode="scale")
    changed = 0
    for row, fixed in zip(rows, aligned, strict=True):
        if (row.start_ms, row.end_ms) != (fixed.start_ms, fixed.end_ms):
            row.start_ms, row.end_ms = fixed.start_ms, fixed.end_ms
            changed += 1
    session.flush()
    mark_documents_stale(session, meeting_id, active_transcript_id=transcript.id)
    return changed
