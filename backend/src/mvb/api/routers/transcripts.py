from __future__ import annotations

import uuid
from typing import Annotated

from fastapi import APIRouter, Query
from fastapi.responses import PlainTextResponse
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from mvb.api.deps import ApiKeyId, DbSession, Pagination
from mvb.api.errors import ApiError
from mvb.api.routers.meetings import active_transcript_of, segments_of
from mvb.db.models import Meeting, Transcript, TranscriptSegment
from mvb.schemas.common import OkResponse, Page
from mvb.schemas.transcripts import SegmentOut, TranscriptDetailOut, TranscriptOut

router = APIRouter(tags=["transcripts"])


def get_transcript(db: Session, transcript_id: uuid.UUID) -> Transcript:
    transcript = db.get(Transcript, transcript_id)
    if transcript is None:
        raise ApiError(404, "not_found", "Transcript not found", {"transcript_id": str(transcript_id)})
    return transcript


def _detail(db: Session, transcript: Transcript, duration_ms: int = 0) -> TranscriptDetailOut:
    detail = TranscriptDetailOut.model_validate(transcript)
    detail.duration_ms = duration_ms
    detail.segments = [SegmentOut.model_validate(row) for row in segments_of(db, transcript.id)]
    return detail


def _meeting_duration(db: Session, meeting_id: uuid.UUID) -> int:
    meeting = db.get(Meeting, meeting_id)
    return meeting.duration_ms if meeting else 0


@router.get("/meetings/{meeting_id}/transcript", response_model=TranscriptDetailOut)
def get_active_transcript(meeting_id: uuid.UUID, _: ApiKeyId, db: DbSession) -> TranscriptDetailOut:
    """The active transcript with every segment, oldest first — what the client displays."""
    transcript = active_transcript_of(db, meeting_id)
    if transcript is None:
        raise ApiError(404, "not_found", "This meeting has no active transcript", {"meeting_id": str(meeting_id)})
    return _detail(db, transcript, _meeting_duration(db, meeting_id))


@router.get("/meetings/{meeting_id}/transcript/text", response_class=PlainTextResponse)
def get_active_transcript_text(
    meeting_id: uuid.UUID,
    _: ApiKeyId,
    db: DbSession,
    speaker_prefix: Annotated[bool, Query()] = True,
) -> PlainTextResponse:
    """The active transcript as plain text (one segment per line) for copy/paste or export."""
    transcript = active_transcript_of(db, meeting_id)
    if transcript is None:
        raise ApiError(404, "not_found", "This meeting has no active transcript", {"meeting_id": str(meeting_id)})
    lines = []
    for segment in segments_of(db, transcript.id):
        prefix = f"{segment.speaker_label}: " if speaker_prefix and segment.speaker_label else ""
        lines.append(f"{prefix}{segment.text}")
    return PlainTextResponse("\n".join(lines))


@router.get("/meetings/{meeting_id}/transcripts", response_model=Page[TranscriptOut])
def list_transcripts(meeting_id: uuid.UUID, _: ApiKeyId, db: DbSession, page: Pagination) -> Page[TranscriptOut]:
    """Every version of this meeting's transcript, newest version first."""
    limit, offset = page
    total = db.scalar(select(func.count()).select_from(Transcript).where(Transcript.meeting_id == meeting_id)) or 0
    rows = db.scalars(
        select(Transcript)
        .where(Transcript.meeting_id == meeting_id)
        .order_by(Transcript.version.desc())
        .limit(limit)
        .offset(offset)
    ).all()
    return Page[TranscriptOut](
        items=[TranscriptOut.model_validate(row) for row in rows],
        total=int(total),
        limit=limit,
        offset=offset,
    )


@router.get("/transcripts/{transcript_id}", response_model=TranscriptDetailOut)
def get_transcript_detail(transcript_id: uuid.UUID, _: ApiKeyId, db: DbSession) -> TranscriptDetailOut:
    transcript = get_transcript(db, transcript_id)
    return _detail(db, transcript, _meeting_duration(db, transcript.meeting_id))


@router.get("/transcripts/{transcript_id}/segments", response_model=Page[SegmentOut])
def list_segments(
    transcript_id: uuid.UUID,
    _: ApiKeyId,
    db: DbSession,
    page: Pagination,
    start_ms: Annotated[int | None, Query(ge=0)] = None,
    end_ms: Annotated[int | None, Query(ge=0)] = None,
) -> Page[SegmentOut]:
    """Paged segments; `start_ms`/`end_ms` clip to a time window (segments that overlap it)."""
    limit, offset = page
    get_transcript(db, transcript_id)
    conditions = [TranscriptSegment.transcript_id == transcript_id]
    if start_ms is not None:
        conditions.append(TranscriptSegment.end_ms > start_ms)
    if end_ms is not None:
        conditions.append(TranscriptSegment.start_ms < end_ms)
    total = db.scalar(select(func.count()).select_from(TranscriptSegment).where(*conditions)) or 0
    rows = db.scalars(
        select(TranscriptSegment).where(*conditions).order_by(TranscriptSegment.start_ms).limit(limit).offset(offset)
    ).all()
    return Page[SegmentOut](
        items=[SegmentOut.model_validate(row) for row in rows],
        total=int(total),
        limit=limit,
        offset=offset,
    )


@router.post("/transcripts/{transcript_id}/activate", response_model=OkResponse)
def activate_transcript(transcript_id: uuid.UUID, _: ApiKeyId, db: DbSession) -> OkResponse:
    """Make this version the active one (e.g. roll back from a bad ASR run)."""
    transcript = get_transcript(db, transcript_id)
    for row in db.scalars(select(Transcript).where(Transcript.meeting_id == transcript.meeting_id)).all():
        row.is_active = row.id == transcript.id
    db.commit()
    return OkResponse(detail={"transcript_id": str(transcript.id), "version": transcript.version})


@router.delete("/transcripts/{transcript_id}", response_model=OkResponse)
def delete_transcript(transcript_id: uuid.UUID, _: ApiKeyId, db: DbSession) -> OkResponse:
    """Delete one non-active transcript version."""
    transcript = get_transcript(db, transcript_id)
    if transcript.is_active:
        raise ApiError(
            409,
            "conflict",
            "Cannot delete the active transcript; activate another version first",
            {"transcript_id": str(transcript.id)},
        )
    db.delete(transcript)
    db.commit()
    return OkResponse(detail={"transcript_id": str(transcript_id), "version": transcript.version})
