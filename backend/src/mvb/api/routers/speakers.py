from __future__ import annotations

import uuid

from fastapi import APIRouter
from sqlalchemy import func, select

from mvb.api.deps import CurrentUser, DbSession
from mvb.api.errors import ApiError
from mvb.api.routers.meetings import get_meeting, get_owned_speaker
from mvb.db.models import Speaker, TranscriptSegment
from mvb.schemas.common import OkResponse
from mvb.schemas.speakers import SpeakerMerge, SpeakerMergeOut, SpeakerOut, SpeakerPatch, SpeakerWithStatsOut

router = APIRouter(tags=["speakers"])


@router.get("/meetings/{meeting_id}/speakers", response_model=list[SpeakerWithStatsOut])
def list_speakers(meeting_id: uuid.UUID, user: CurrentUser, db: DbSession) -> list[SpeakerWithStatsOut]:
    """Speakers of a meeting with how much each one talks — the client uses this for the
    speaker legend."""
    get_meeting(db, meeting_id, user)
    rows = db.scalars(select(Speaker).where(Speaker.meeting_id == meeting_id).order_by(Speaker.label)).all()
    out: list[SpeakerWithStatsOut] = []
    for speaker in rows:
        segments = db.scalars(
            select(TranscriptSegment)
            .where(TranscriptSegment.speaker_id == speaker.id)
            .order_by(TranscriptSegment.start_ms)
        ).all()
        stats = SpeakerWithStatsOut.model_validate(speaker)
        stats.segment_count = len(segments)
        stats.speaking_ms = sum(max(0, s.end_ms - s.start_ms) for s in segments)
        stats.preview = segments[0].text[:120] if segments else ""
        out.append(stats)
    return out


@router.patch("/speakers/{speaker_id}", response_model=SpeakerOut)
def rename_speaker(speaker_id: uuid.UUID, payload: SpeakerPatch, user: CurrentUser, db: DbSession) -> SpeakerOut:
    """Rename a speaker ("Speaker 2" -> "Ana"). The denormalised label on every segment of
    the meeting is updated with it so transcripts stay consistent."""
    speaker = get_owned_speaker(db, speaker_id, user)
    conflict = db.scalar(
        select(Speaker).where(
            Speaker.meeting_id == speaker.meeting_id,
            Speaker.label == payload.label,
            Speaker.id != speaker.id,
        )
    )
    if conflict is not None:
        raise ApiError(409, "conflict", "Another speaker already uses that label", {"label": payload.label})
    old_label = speaker.label
    speaker.label = payload.label
    if payload.color is not None:
        speaker.color = payload.color
    db.flush()
    for segment in db.scalars(
        select(TranscriptSegment).where(
            TranscriptSegment.speaker_label == old_label, TranscriptSegment.meeting_id == speaker.meeting_id
        )
    ).all():
        segment.speaker_label = payload.label
    db.commit()
    return SpeakerOut.model_validate(speaker)


@router.post("/speakers/{speaker_id}/merge", response_model=SpeakerMergeOut)
def merge_speaker(speaker_id: uuid.UUID, payload: SpeakerMerge, user: CurrentUser, db: DbSession) -> SpeakerMergeOut:
    """Fold one speaker into another (diarization split one person in two). The source
    speaker row is deleted and its segments are relabelled."""
    source = get_owned_speaker(db, speaker_id, user)
    target = get_owned_speaker(db, payload.target_speaker_id, user)
    if source.meeting_id != target.meeting_id:
        raise ApiError(
            422,
            "validation_error",
            "Speakers belong to different meetings",
            {"source": str(source.id), "target": str(target.id)},
        )
    if source.id == target.id:
        raise ApiError(422, "validation_error", "Cannot merge a speaker into itself")
    moved = 0
    for segment in db.scalars(select(TranscriptSegment).where(TranscriptSegment.speaker_id == source.id)).all():
        segment.speaker_id = target.id
        segment.speaker_label = target.label
        moved += 1
    db.delete(source)
    db.commit()
    return SpeakerMergeOut(
        merged_speaker_id=speaker_id,
        target_speaker_id=target.id,
        moved_segments=moved,
    )


@router.delete("/speakers/{speaker_id}", response_model=OkResponse)
def delete_speaker(speaker_id: uuid.UUID, user: CurrentUser, db: DbSession) -> OkResponse:
    """Delete a speaker; its segments keep their text and just lose the link."""
    speaker = get_owned_speaker(db, speaker_id, user)
    cleared = db.scalar(
        select(func.count()).select_from(TranscriptSegment).where(TranscriptSegment.speaker_id == speaker.id)
    )
    for segment in db.scalars(select(TranscriptSegment).where(TranscriptSegment.speaker_id == speaker.id)).all():
        segment.speaker_id = None
    db.delete(speaker)
    db.commit()
    return OkResponse(detail={"speaker_id": str(speaker_id), "unlinked_segments": int(cleared or 0)})
