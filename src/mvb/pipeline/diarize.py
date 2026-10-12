from __future__ import annotations

import uuid

from sqlalchemy import select

from mvb.db.models import Speaker, Transcript, TranscriptSegment
from mvb.jobs.handlers import JobContext, handler
from mvb.providers.registry import build_diarizer


def speaker_at(turns, start_ms: int, end_ms: int) -> int | None:  # type: ignore[no-untyped-def]
    """The diarization cluster with the largest overlap with this segment."""
    best_index: int | None = None
    best_overlap = 0
    for turn in turns:
        overlap = min(end_ms, turn.end_ms) - max(start_ms, turn.start_ms)
        if overlap > best_overlap:
            best_overlap = overlap
            best_index = turn.index
    return best_index


@handler("diarize")
def diarize_job(context: JobContext) -> dict:
    """Label the active transcript's segments with per-meeting speaker identities."""
    meeting_id = context.job.meeting_id
    if meeting_id is None:
        raise RuntimeError("diarize requires a meeting")
    transcript = context.session.scalar(
        select(Transcript).where(Transcript.meeting_id == meeting_id, Transcript.is_active.is_(True))
    )
    if transcript is None:
        raise RuntimeError("no active transcript to label (run transcribe first)")
    segments = context.session.scalars(
        select(TranscriptSegment)
        .where(TranscriptSegment.transcript_id == transcript.id)
        .order_by(TranscriptSegment.start_ms)
    ).all()
    if not segments:
        return {"speakers": 0, "labeled": 0, "is_mock": True}

    duration_ms = max(segment.end_ms for segment in segments)
    diarizer = build_diarizer(context.settings)
    context.event("diarizing", provider=diarizer.id)
    wav = context.storage.root / "_work" / str(meeting_id) / "audio16k.wav"
    result = diarizer.diarize(
        wav,
        duration_ms=duration_ms,
        num_speakers=context.params.get("num_speakers"),
        progress=lambda value: context.progress(0.2 + 0.7 * value),
    )

    labels: dict[int, str] = {index: f"Speaker {index + 1}" for index in sorted({t.index for t in result.turns})}
    for label in labels.values():
        existing = context.session.scalar(
            select(Speaker).where(Speaker.meeting_id == meeting_id, Speaker.label == label)
        )
        if existing is None:
            context.session.add(
                Speaker(id=uuid.uuid4(), meeting_id=meeting_id, label=label, kind="diarized")
            )
    context.session.flush()
    speaker_ids = {
        speaker.label: speaker.id
        for speaker in context.session.scalars(select(Speaker).where(Speaker.meeting_id == meeting_id)).all()
    }

    labeled = 0
    for segment in segments:
        index = speaker_at(result.turns, segment.start_ms, segment.end_ms)
        if index is None:
            continue
        label = labels.get(index, f"Speaker {index + 1}")
        segment.speaker_label = label
        segment.speaker_id = speaker_ids.get(label)
        segment.source = "diarized"
        labeled += 1
    transcript.source = "diarized"
    context.session.flush()
    context.progress(1.0)
    return {
        "speakers": len(labels),
        "labeled": labeled,
        "is_mock": result.is_mock,
        "provider": result.provider,
        "model": result.model,
    }
