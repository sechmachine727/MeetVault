from __future__ import annotations

import uuid

from sqlalchemy import select

from mvb.db.models import AnalysisDocument, AnalysisRun, Section, Transcript, TranscriptSegment
from mvb.jobs.handlers import JobContext, handler
from mvb.providers.analysis.prompts import QA_PROMPT, SYSTEM_ANALYST, meeting_qa_schema
from mvb.providers.registry import build_analyzer

DEFAULT_TOP_K = 8
MAX_TOP_K = 40


def rank_segments(question: str, segments: list[TranscriptSegment], top_k: int) -> list[TranscriptSegment]:
    """Deterministic lexical retrieval: term overlap with the segment text, tie-broken by
    recency. Always keeps the first segment so an unanswerable question still gets context."""
    terms = {
        token
        for token in "".join(ch.lower() if ch.isalnum() else " " for ch in question).split()
        if len(token) > 2
    }
    if not segments:
        return []
    if not terms:
        return segments[-top_k:]
    scored = sorted(
        segments,
        key=lambda segment: (
            -len(terms & {t for t in segment.text.lower().replace(".", " ").split() if len(t) > 2}),
            -segment.start_ms,
        ),
    )
    picked = scored[:top_k]
    if segments[0] not in picked:
        picked.append(segments[0])
    return sorted(picked, key=lambda segment: segment.start_ms)


@handler("qa")
def qa_job(context: JobContext) -> dict:
    """Answer a question over the active transcript (optionally inside one section)."""
    meeting_id = context.job.meeting_id
    if meeting_id is None:
        raise RuntimeError("qa requires a meeting")
    question = str(context.params.get("question", "")).strip()
    if not question:
        raise RuntimeError("qa requires a question")
    section_id = context.params.get("section_id")
    top_k = max(1, min(int(context.params.get("top_k") or DEFAULT_TOP_K), MAX_TOP_K))

    transcript = context.session.scalar(
        select(Transcript).where(Transcript.meeting_id == meeting_id, Transcript.is_active.is_(True))
    )
    if transcript is None:
        raise RuntimeError("no active transcript")

    segments = list(
        context.session.scalars(
            select(TranscriptSegment)
            .where(TranscriptSegment.transcript_id == transcript.id)
            .order_by(TranscriptSegment.start_ms)
        ).all()
    )
    section_uuid: uuid.UUID | None = None
    if section_id:
        try:
            section_uuid = uuid.UUID(str(section_id))
        except ValueError as exc:
            raise RuntimeError(f"invalid section id: {section_id}") from exc
        section = context.session.get(Section, section_uuid)
        if section is None or section.meeting_id != meeting_id:
            raise RuntimeError("section not found")
        segments = [s for s in segments if s.end_ms > section.start_ms and s.start_ms < section.end_ms]

    context.progress(0.3)
    ranked = rank_segments(question, segments, top_k)
    excerpt = "\n".join(
        f"{segment.id} | {segment.start_ms}-{segment.end_ms} | {segment.speaker_label or '?'}: {segment.text}"
        for segment in ranked
    )

    analyzer = build_analyzer(context.settings)
    run = AnalysisRun(
        id=uuid.uuid4(),
        meeting_id=meeting_id,
        transcript_id=transcript.id,
        provider=str(getattr(analyzer, "id", "mock")),
        model=str(getattr(analyzer, "model", "")),
        prompt_version="v1",
        is_mock=bool(getattr(analyzer, "is_mock", True)),
        status="running",
    )
    context.session.add(run)
    context.session.flush()
    result = analyzer.complete_json(
        system=SYSTEM_ANALYST,
        user=f"{QA_PROMPT}QUESTION: {question}\n\nEXCERPT:\n{excerpt}",
        schema=meeting_qa_schema(),
        timeout_s=context.settings.analyzer_timeout_s,
    )

    valid = {str(segment.id): segment for segment in ranked}
    citations: list[dict] = []
    dropped = 0
    for raw_id in result.payload.get("citation_segment_ids") or []:
        segment = valid.get(str(raw_id))
        if segment is None:
            dropped += 1
            continue
        citations.append(
            {
                "segment_id": str(segment.id),
                "start_ms": segment.start_ms,
                "end_ms": segment.end_ms,
                "text": segment.text,
            }
        )

    document = AnalysisDocument(
        id=uuid.uuid4(),
        meeting_id=meeting_id,
        analysis_run_id=run.id,
        section_id=section_uuid,
        kind="qa",
        payload_json={
            "question": question,
            "answer": str(result.payload.get("answer", ""))[:8000],
            "section_id": str(section_uuid) if section_uuid else None,
            "citations": citations,
            "dropped_citations": dropped,
        },
        transcript_id=transcript.id,
        stale=False,
        is_mock=run.is_mock,
    )
    context.session.add(document)
    run.status = "succeeded"
    context.session.flush()
    context.progress(1.0)
    return {
        "qa_id": str(document.id),
        "citations": len(citations),
        "dropped_citations": dropped,
        "is_mock": run.is_mock,
        "provider": run.provider,
    }
