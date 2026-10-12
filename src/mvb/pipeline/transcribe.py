from __future__ import annotations

import uuid

from sqlalchemy import select, update

from mvb.db.models import MediaAsset, Meeting, Transcript, TranscriptSegment
from mvb.jobs.handlers import JobContext, handler
from mvb.media.audio import extract_audio_16k
from mvb.providers.registry import build_transcriber
from mvb.staleness import mark_documents_stale

MOCK_SOURCES = ("mock",)


def primary_audio_asset(context: JobContext) -> MediaAsset | None:
    return context.session.scalar(
        select(MediaAsset)
        .where(MediaAsset.meeting_id == context.job.meeting_id, MediaAsset.kind == "audio")
        .order_by(MediaAsset.created_at.asc())
    )


def next_transcript_version(context: JobContext, meeting_id: uuid.UUID) -> int:
    latest = context.session.scalar(
        select(Transcript.version)
        .where(Transcript.meeting_id == meeting_id)
        .order_by(Transcript.version.desc())
        .limit(1)
    )
    return int(latest or 0) + 1


@handler("transcribe")
def transcribe_job(context: JobContext) -> dict:
    """Decode the audio to 16 kHz mono and run the configured transcriber into a NEW version."""
    meeting = context.session.get(Meeting, context.job.meeting_id)
    if meeting is None:
        raise RuntimeError("meeting not found for transcribe job")
    asset = primary_audio_asset(context)
    if asset is None:
        raise RuntimeError("no audio asset on this meeting")
    duration_ms = asset.duration_ms
    if not duration_ms:
        from mvb.media.probe import probe_media

        duration_ms = probe_media(context.storage.path_for(asset.storage_key)).duration_ms
    if duration_ms and not meeting.duration_ms:
        meeting.duration_ms = duration_ms

    work_dir = context.storage.root / "_work" / str(meeting.id)
    wav = work_dir / "audio16k.wav"
    if not wav.exists():
        context.progress(0.05)
        extract_audio_16k(context.storage.path_for(asset.storage_key), wav)
    context.progress(0.15)
    context.check_cancelled()

    language = str(context.params.get("language") or meeting.language or "").strip() or None
    if language == "auto":
        language = None
    transcriber = build_transcriber(context.settings)
    context.event("transcribing", provider=transcriber.id, model=getattr(transcriber, "model", ""))
    result = transcriber.transcribe(
        wav,
        language=language,
        duration_ms=duration_ms,
        progress=lambda value: context.progress(0.15 + 0.7 * value),
    )

    context.session.execute(update(Transcript).where(Transcript.meeting_id == meeting.id).values(is_active=False))
    transcript = Transcript(
        id=uuid.uuid4(),
        meeting_id=meeting.id,
        version=next_transcript_version(context, meeting.id),
        source="asr",
        provider=result.provider,
        model=result.model,
        language=result.language,
        is_mock=result.is_mock,
        is_active=True,
        segment_count=len(result.segments),
    )
    context.session.add(transcript)
    context.session.flush()
    for draft in result.segments:
        context.session.add(
            TranscriptSegment(
                id=uuid.uuid4(),
                transcript_id=transcript.id,
                meeting_id=meeting.id,
                speaker_label="",
                start_ms=draft.start_ms,
                end_ms=draft.end_ms,
                text=draft.text,
                confidence=draft.confidence,
                is_final=True,
                source="asr",
            )
        )
    if result.language:
        meeting.language = result.language
    meeting.status = "processing"
    context.session.flush()
    mark_documents_stale(context.session, meeting.id, active_transcript_id=transcript.id)
    context.progress(0.95)
    return {
        "transcript_id": str(transcript.id),
        "segment_count": len(result.segments),
        "is_mock": result.is_mock,
        "provider": result.provider,
        "model": result.model,
    }


@handler("align")
def align_job(context: JobContext) -> dict:
    """Rescale the active transcript onto the audio duration (idempotent)."""
    from mvb.pipeline.alignment_service import realign_active_transcript

    meeting_id = context.job.meeting_id
    if meeting_id is None:
        raise RuntimeError("align requires a meeting")
    changed = realign_active_transcript(context.session, meeting_id)
    context.progress(1.0)
    return {"rescaled_segments": changed, "is_mock": False}
