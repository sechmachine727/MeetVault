from __future__ import annotations

import uuid
from datetime import UTC, datetime
from typing import Annotated, Any

from fastapi import APIRouter, Body, File, Query, Response, UploadFile, status
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from mvb.api.deps import CurrentUser, DbSession, Pagination, SettingsDep
from mvb.api.errors import ApiError
from mvb.db.models import (
    AnalysisDocument,
    Job,
    MediaAsset,
    Meeting,
    Speaker,
    Transcript,
    TranscriptSegment,
    User,
)
from mvb.media.probe import probe_media
from mvb.pipeline.align import align_segments
from mvb.pipeline.chain import STEP_ORDER, enqueue_pipeline
from mvb.pipeline.parse_transcript import parse_transcript_payload
from mvb.pipeline.status import meeting_status
from mvb.schemas.common import OkResponse, Page
from mvb.schemas.jobs import ProcessRequest
from mvb.schemas.media import MediaAssetOut
from mvb.schemas.meetings import (
    MediaUploadOut,
    MeetingCreate,
    MeetingDetailOut,
    MeetingOut,
    MeetingPatch,
    MeetingStatusOut,
    ProcessResponse,
)
from mvb.schemas.transcripts import SegmentOut, TranscriptDetailOut
from mvb.storage.local import LocalStorage

router = APIRouter(tags=["meetings"])

VIDEO_SUFFIXES = (".mp4", ".mkv", ".webm", ".mov")


def get_meeting(db: Session, meeting_id: uuid.UUID, user: User, *, include_deleted: bool = False) -> Meeting:
    """Load a meeting the USER owns. Missing AND foreign rows both raise the same 404 so a
    foreign id is indistinguishable from a nonexistent one."""
    meeting = db.get(Meeting, meeting_id)
    if meeting is None or meeting.user_id != user.id or (meeting.deleted_at is not None and not include_deleted):
        raise ApiError(404, "not_found", "Meeting not found", {"meeting_id": str(meeting_id)})
    return meeting


def get_owned_transcript(db: Session, transcript_id: uuid.UUID, user: User) -> Transcript:
    transcript = db.get(Transcript, transcript_id)
    if transcript is None:
        raise ApiError(404, "not_found", "Transcript not found", {"transcript_id": str(transcript_id)})
    get_meeting(db, transcript.meeting_id, user)
    return transcript


def get_owned_speaker(db: Session, speaker_id: uuid.UUID, user: User) -> Speaker:
    speaker = db.get(Speaker, speaker_id)
    if speaker is None:
        raise ApiError(404, "not_found", "Speaker not found", {"speaker_id": str(speaker_id)})
    get_meeting(db, speaker.meeting_id, user)
    return speaker


def get_owned_asset(db: Session, asset_id: uuid.UUID, user: User) -> MediaAsset:
    asset = db.get(MediaAsset, asset_id)
    if asset is None:
        raise ApiError(404, "not_found", "Media asset not found", {"asset_id": str(asset_id)})
    get_meeting(db, asset.meeting_id, user)
    return asset


def get_owned_job(db: Session, job_id: uuid.UUID, user: User) -> Job:
    job = db.get(Job, job_id)
    if job is None or job.meeting_id is None:
        raise ApiError(404, "not_found", "Job not found", {"job_id": str(job_id)})
    get_meeting(db, job.meeting_id, user)
    return job


def segments_of(db: Session, transcript_id: uuid.UUID) -> list[TranscriptSegment]:
    return list(
        db.scalars(
            select(TranscriptSegment)
            .where(TranscriptSegment.transcript_id == transcript_id)
            .order_by(TranscriptSegment.start_ms)
        ).all()
    )


def active_transcript_of(db: Session, meeting_id: uuid.UUID) -> Transcript | None:
    return db.scalar(select(Transcript).where(Transcript.meeting_id == meeting_id, Transcript.is_active.is_(True)))


def meeting_detail(db: Session, meeting: Meeting) -> MeetingDetailOut:
    detail = MeetingDetailOut.model_validate(meeting)
    detail.media_count = int(
        db.scalar(select(func.count()).select_from(MediaAsset).where(MediaAsset.meeting_id == meeting.id)) or 0
    )
    detail.transcript_count = int(
        db.scalar(select(func.count()).select_from(Transcript).where(Transcript.meeting_id == meeting.id)) or 0
    )
    active = active_transcript_of(db, meeting.id)
    detail.active_transcript_id = active.id if active else None
    detail.speaker_count = int(
        db.scalar(select(func.count()).select_from(Speaker).where(Speaker.meeting_id == meeting.id)) or 0
    )
    detail.has_analysis = bool(
        db.scalar(
            select(AnalysisDocument.id)
            .where(AnalysisDocument.meeting_id == meeting.id, AnalysisDocument.stale.is_(False))
            .limit(1)
        )
    )
    detail.is_mock = bool(active is not None and active.is_mock)
    return detail


@router.post("/meetings", response_model=MeetingDetailOut, status_code=status.HTTP_201_CREATED)
def create_meeting(payload: MeetingCreate, user: CurrentUser, db: DbSession, response: Response) -> MeetingDetailOut:
    """Create a meeting. Re-posting the same `source`+`external_id` returns the existing
    meeting with 200 instead of 201, so client retries are safe."""
    if payload.external_id:
        existing = db.scalar(
            select(Meeting).where(
                Meeting.source == payload.source,
                Meeting.external_id == payload.external_id,
                Meeting.user_id == user.id,
                Meeting.deleted_at.is_(None),
            )
        )
        if existing is not None:
            response.status_code = status.HTTP_200_OK
            return meeting_detail(db, existing)
        # A soft-deleted row still holds the (source, external_id) unique key but is
        # invisible to the lookup above, so re-creating it fell through to INSERT and
        # hit the constraint (500). The user removed that meeting, so drop the
        # tombstone (children cascade at the DB level) and insert fresh below.
        tombstone = db.scalar(
            select(Meeting).where(
                Meeting.source == payload.source,
                Meeting.external_id == payload.external_id,
                Meeting.user_id == user.id,
                Meeting.deleted_at.is_not(None),
            )
        )
        if tombstone is not None:
            db.delete(tombstone)
            db.flush()
    meeting = Meeting(
        id=uuid.uuid4(),
        title=payload.title,
        source=payload.source,
        external_id=payload.external_id,
        language=payload.language or "",
        started_at=payload.started_at,
        ended_at=payload.ended_at,
        duration_ms=payload.duration_ms,
        status="uploaded",
        user_id=user.id,
        metadata_json=payload.metadata,
    )
    db.add(meeting)
    db.commit()
    return meeting_detail(db, meeting)


@router.get("/meetings", response_model=Page[MeetingOut])
def list_meetings(
    user: CurrentUser,
    db: DbSession,
    page: Pagination,
    q: Annotated[str | None, Query(max_length=200)] = None,
    pipeline_status: Annotated[str | None, Query(alias="status", max_length=32)] = None,
    include_deleted: Annotated[bool, Query()] = False,
) -> Page[MeetingOut]:
    """Newest first; `q` matches the title, `status` filters the pipeline state."""
    limit, offset = page
    conditions = [Meeting.user_id == user.id]
    if not include_deleted:
        conditions.append(Meeting.deleted_at.is_(None))
    if q:
        conditions.append(Meeting.title.ilike(f"%{q}%"))
    if pipeline_status:
        conditions.append(Meeting.status == pipeline_status)
    total = db.scalar(select(func.count()).select_from(Meeting).where(*conditions)) or 0
    rows = db.scalars(
        select(Meeting)
        .where(*conditions)
        .order_by(Meeting.created_at.desc(), Meeting.id.desc())
        .limit(limit)
        .offset(offset)
    ).all()
    return Page[MeetingOut](
        items=[MeetingOut.model_validate(row) for row in rows],
        total=int(total),
        limit=limit,
        offset=offset,
    )


@router.get("/meetings/{meeting_id}", response_model=MeetingDetailOut)
def get_meeting_detail(meeting_id: uuid.UUID, user: CurrentUser, db: DbSession) -> MeetingDetailOut:
    return meeting_detail(db, get_meeting(db, meeting_id, user))


@router.patch("/meetings/{meeting_id}", response_model=MeetingDetailOut)
def patch_meeting(meeting_id: uuid.UUID, payload: MeetingPatch, user: CurrentUser, db: DbSession) -> MeetingDetailOut:
    meeting = get_meeting(db, meeting_id, user)
    data = payload.model_dump(exclude_unset=True)
    metadata = data.pop("metadata", None)
    if metadata is not None:
        meeting.metadata_json = metadata
    for field, value in data.items():
        if value is not None:
            setattr(meeting, field, value)
    db.commit()
    return meeting_detail(db, meeting)


@router.delete("/meetings/{meeting_id}", response_model=OkResponse)
def delete_meeting(
    meeting_id: uuid.UUID,
    user: CurrentUser,
    db: DbSession,
    settings: SettingsDep,
    purge_files: Annotated[bool, Query()] = True,
) -> OkResponse:
    """Soft-delete the meeting (rows are kept so transcripts stay auditable);
    `purge_files` also removes its media files from disk."""
    meeting = get_meeting(db, meeting_id, user)
    meeting.deleted_at = datetime.now(UTC)
    meeting.status = "deleted"
    db.commit()
    if purge_files:
        LocalStorage(settings.media_root).delete_meeting_dir(str(meeting.id))
    return OkResponse(detail={"meeting_id": str(meeting.id), "purged_files": purge_files})


@router.get("/meetings/{meeting_id}/media", response_model=Page[MediaAssetOut])
def list_media(meeting_id: uuid.UUID, user: CurrentUser, db: DbSession, page: Pagination) -> Page[MediaAssetOut]:
    limit, offset = page
    get_meeting(db, meeting_id, user)
    total = db.scalar(select(func.count()).select_from(MediaAsset).where(MediaAsset.meeting_id == meeting_id)) or 0
    rows = db.scalars(
        select(MediaAsset)
        .where(MediaAsset.meeting_id == meeting_id)
        .order_by(MediaAsset.created_at.asc(), MediaAsset.id.asc())
        .limit(limit)
        .offset(offset)
    ).all()
    return Page[MediaAssetOut](
        items=[MediaAssetOut.model_validate(row) for row in rows],
        total=int(total),
        limit=limit,
        offset=offset,
    )


@router.post("/meetings/{meeting_id}/media", response_model=MediaUploadOut, status_code=status.HTTP_201_CREATED)
async def upload_media(
    meeting_id: uuid.UUID,
    user: CurrentUser,
    db: DbSession,
    settings: SettingsDep,
    file: Annotated[UploadFile, File()],
    kind: Annotated[str, Query(pattern="^(audio|video)$")] = "audio",
    role: Annotated[str, Query(max_length=32)] = "primary",
    start_offset_ms: Annotated[int, Query(ge=0)] = 0,
) -> MediaUploadOut:
    """Store one recording (audio or video) and probe it. An audio upload also sets the
    meeting's duration, which transcript alignment depends on."""
    meeting = get_meeting(db, meeting_id, user)
    original_name = file.filename or "upload.bin"
    suffix = "." + original_name.rsplit(".", 1)[-1].lower() if "." in original_name else ""
    if kind == "audio" and suffix in VIDEO_SUFFIXES:
        kind = "video"
    storage = LocalStorage(settings.media_root)
    key = f"{meeting.id}/{uuid.uuid4().hex}{suffix}"
    written, digest = storage.put_stream(key, file.file, max_bytes=settings.max_upload_mb * 1024 * 1024)
    info = probe_media(storage.path_for(key))
    asset = MediaAsset(
        id=uuid.uuid4(),
        meeting_id=meeting.id,
        kind=kind,
        role=role,
        original_name=original_name,
        content_type=file.content_type or "application/octet-stream",
        size_bytes=written,
        sha256=digest,
        storage_key=key,
        duration_ms=info.duration_ms,
        start_offset_ms=start_offset_ms,
        sample_rate=info.sample_rate,
        channels=info.channels,
        probe_json={
            "error": info.error,
            "codec": info.codec,
            "has_audio": info.has_audio,
            "has_video": info.has_video,
        },
    )
    db.add(asset)
    if kind == "audio" and info.duration_ms and not meeting.duration_ms:
        meeting.duration_ms = info.duration_ms
    db.commit()
    return MediaUploadOut(
        asset_id=asset.id,
        storage_key=key,
        size_bytes=written,
        sha256=digest,
        duration_ms=info.duration_ms,
        sample_rate=info.sample_rate,
        channels=info.channels,
        is_video=kind == "video",
        probe_error=info.error,
    )


@router.post(
    "/meetings/{meeting_id}/transcripts",
    response_model=TranscriptDetailOut,
    status_code=status.HTTP_201_CREATED,
)
def upload_transcript(
    meeting_id: uuid.UUID,
    user: CurrentUser,
    db: DbSession,
    payload: Annotated[Any, Body()],
    align: Annotated[str, Query(pattern="^(auto|scale|proportional)$")] = "auto",
    language: Annotated[str | None, Query(max_length=8)] = None,
    activate: Annotated[bool, Query()] = True,
) -> TranscriptDetailOut:
    """Import a transcript exported by the MeetVault client (Format A: segments with optional
    times; Format B: a bare list of {Speaker, Text} rows). Untimed segments are distributed
    over the real media duration so the timeline cannot drift."""
    meeting = get_meeting(db, meeting_id, user)
    if isinstance(payload, dict) and not payload.get("segments") and payload.get("text"):
        payload = [{"text": chunk.strip()} for chunk in str(payload["text"]).split("\n\n") if chunk.strip()]
    parsed = parse_transcript_payload(payload)
    aligned = align_segments(parsed.segments, meeting.duration_ms, mode=align)
    latest = db.scalar(
        select(Transcript.version)
        .where(Transcript.meeting_id == meeting.id)
        .order_by(Transcript.version.desc())
        .limit(1)
    )
    transcript = Transcript(
        id=uuid.uuid4(),
        meeting_id=meeting.id,
        version=int(latest or 0) + 1,
        source=parsed.source or "uploaded",
        provider="client",
        model="",
        language=language or parsed.language or meeting.language,
        is_mock=False,
        is_active=activate,
        segment_count=len(aligned),
    )
    if activate:
        for row in db.scalars(select(Transcript).where(Transcript.meeting_id == meeting.id)).all():
            row.is_active = False
    db.add(transcript)
    db.flush()
    for fixed in aligned:
        db.add(
            TranscriptSegment(
                id=uuid.uuid4(),
                transcript_id=transcript.id,
                meeting_id=meeting.id,
                speaker_label=fixed.speaker_label,
                start_ms=fixed.start_ms,
                end_ms=fixed.end_ms,
                text=fixed.text,
                confidence=fixed.confidence,
                is_final=True,
                source="uploaded",
            )
        )
    db.commit()
    if activate:
        # A new active transcript supersedes any existing analysis of the old one.
        from mvb.staleness import mark_documents_stale

        mark_documents_stale(db, meeting.id, active_transcript_id=transcript.id)
        db.commit()
    detail = TranscriptDetailOut.model_validate(transcript)
    detail.duration_ms = meeting.duration_ms
    detail.segments = [SegmentOut.model_validate(row) for row in segments_of(db, transcript.id)]
    return detail


@router.post("/meetings/{meeting_id}/process", response_model=ProcessResponse, status_code=status.HTTP_202_ACCEPTED)
def process_meeting(
    meeting_id: uuid.UUID,
    user: CurrentUser,
    db: DbSession,
    payload: ProcessRequest | None = None,
    steps: Annotated[list[str] | None, Query()] = None,
    force: Annotated[bool, Query()] = False,
) -> ProcessResponse:
    """Start the post-meeting pipeline. Steps run in this order:
    media_probe → transcribe → diarize → align → analyze. Each step is a job: poll
    GET /meetings/{id}/status or stream GET /jobs/{id}/events.

    `job_ids: []` with 202 means the pipeline was already running (idempotent). The body is the
    declared `ProcessRequest` model, so it is documented in OpenAPI and malformed input is a 422
    in the standard error envelope."""
    meeting = get_meeting(db, meeting_id, user)
    options = payload or ProcessRequest()
    wanted = steps or options.steps
    unknown = [step for step in wanted if step not in STEP_ORDER]
    if unknown:
        raise ApiError(422, "validation_error", "Unknown pipeline step", {"steps": unknown, "known": list(STEP_ORDER)})
    created = enqueue_pipeline(
        db,
        meeting_id=meeting.id,
        steps=wanted,
        language=options.language,
        analyze_kinds=options.analyze_kinds,
        force=force or options.force,
    )
    if created and meeting.status != "processing":
        meeting.status = "processing"
        db.commit()
    return ProcessResponse(
        meeting_id=meeting.id,
        steps=[job.type for job in created] or wanted,
        job_ids=[job.id for job in created],
        enqueued=len(created),
    )


@router.get("/meetings/{meeting_id}/status", response_model=MeetingStatusOut)
def get_status(meeting_id: uuid.UUID, user: CurrentUser, db: DbSession) -> MeetingStatusOut:
    """Aggregate progress: per-step state, progress fraction, active job and transcript,
    which analysis kinds are fresh or stale, and whether any result is mock."""
    meeting = get_meeting(db, meeting_id, user)
    return MeetingStatusOut.model_validate(meeting_status(db, meeting.id))
