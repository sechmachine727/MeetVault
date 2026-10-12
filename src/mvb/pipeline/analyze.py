from __future__ import annotations

import uuid

from sqlalchemy import select
from sqlalchemy.orm import Session

from mvb.api.errors import ApiError
from mvb.db.models import (
    AnalysisDocument,
    AnalysisRun,
    MediaAsset,
    Meeting,
    Section,
    Transcript,
    TranscriptSegment,
)
from mvb.jobs.handlers import JobContext, handler
from mvb.pipeline.transcribe import next_transcript_version  # noqa: F401  (kept for symmetry)
from mvb.providers.analysis.prompts import (
    AGENDA_PROMPT,
    EXTRACT_PROMPT,
    EXTRACT_SCHEMA,
    PROMPT_VERSION,
    SECTION_PROMPT,
    SUMMARY_PROMPT,
    SYSTEM_ANALYST,
    duration_note,
    kind_schema,
    transcript_block,
)
from mvb.providers.registry import build_analyzer
from mvb.staleness import mark_documents_stale

ANALYZE_KINDS = ("summary", "agenda", "section_summary", "decisions", "action_items")
MAX_ITEMS = 100


def rows_for(session: Session, transcript: Transcript) -> list[tuple[str, int, int, str, str]]:
    segments = session.scalars(
        select(TranscriptSegment)
        .where(TranscriptSegment.transcript_id == transcript.id)
        .order_by(TranscriptSegment.start_ms)
    ).all()
    return [
        (str(segment.id), segment.start_ms, segment.end_ms, segment.speaker_label or "?", segment.text)
        for segment in segments
    ]


def meeting_duration(session: Session, meeting_id: uuid.UUID) -> int:
    meeting = session.get(Meeting, meeting_id)
    if meeting is not None and meeting.duration_ms:
        return meeting.duration_ms
    asset = session.scalar(
        select(MediaAsset).where(MediaAsset.meeting_id == meeting_id).order_by(MediaAsset.created_at.asc())
    )
    return asset.duration_ms if asset is not None else 0


def _new_run(context: JobContext, transcript: Transcript, analyzer) -> AnalysisRun:  # type: ignore[no-untyped-def]
    run = AnalysisRun(
        id=uuid.uuid4(),
        meeting_id=transcript.meeting_id,
        transcript_id=transcript.id,
        provider=str(getattr(analyzer, "id", "mock")),
        model=str(getattr(analyzer, "model", "")),
        prompt_version=PROMPT_VERSION,
        is_mock=bool(getattr(analyzer, "is_mock", True)),
        status="running",
    )
    context.session.add(run)
    context.session.flush()
    return run


def _store(
    context: JobContext,
    run: AnalysisRun,
    kind: str,
    payload: dict,
    *,
    section_id: uuid.UUID | None = None,
) -> AnalysisDocument:
    """Write one analysis document; earlier documents of the same kind/section are superseded."""
    previous = context.session.scalars(
        select(AnalysisDocument).where(
            AnalysisDocument.meeting_id == run.meeting_id,
            AnalysisDocument.kind == kind,
            AnalysisDocument.stale.is_(False),
        )
    ).all()
    for document in previous:
        if document.section_id == section_id:
            document.stale = True
    document = AnalysisDocument(
        id=uuid.uuid4(),
        meeting_id=run.meeting_id,
        analysis_run_id=run.id,
        section_id=section_id,
        kind=kind,
        payload_json=payload,
        transcript_id=run.transcript_id,
        stale=False,
        is_mock=run.is_mock,
    )
    context.session.add(document)
    context.session.flush()
    return document


def sanitize_agenda(payload: dict, duration: int) -> dict:
    items: list[dict] = []
    for item in list(payload.get("items") or [])[:50]:
        try:
            start = int(item.get("start_ms", 0))
            end = int(item.get("end_ms", start + 1))
        except (TypeError, ValueError):
            start, end = 0, 1
        start = max(0, min(start, max(duration - 1, 0)))
        end = max(start + 1, min(end, max(duration, start + 1)))
        items.append(
            {
                "title": str(item.get("title", ""))[:300],
                "topic": str(item.get("topic", ""))[:200],
                "start_ms": start,
                "end_ms": end,
            }
        )
    return {"items": items}


def sanitize_summary(payload: dict) -> dict:
    return {
        "text": str(payload.get("text", ""))[:8000],
        "bullets": [str(bullet)[:500] for bullet in (payload.get("bullets") or [])][:20],
    }


def sanitize_section(payload: dict) -> dict:
    return {
        "summary": str(payload.get("summary", ""))[:4000],
        "bullets": [str(b)[:500] for b in (payload.get("bullets") or [])][:20],
        "decisions": [str(d)[:1000] for d in (payload.get("decisions") or [])][:20],
        "action_items": [str(a)[:1000] for a in (payload.get("action_items") or [])][:20],
    }


def _grounded_items(items: list[dict], valid_ids: set[str], *, owner: bool) -> list[dict]:
    out: list[dict] = []
    for item in list(items or [])[:MAX_ITEMS]:
        text = str(item.get("text", "")).strip()
        if not text:
            continue
        if owner:
            out.append(
                {
                    "text": text[:2000],
                    "owner": str(item.get("owner", ""))[:200],
                    "due": item.get("due"),
                    "segment_ids": [sid for sid in (item.get("segment_ids") or []) if sid in valid_ids],
                }
            )
        else:
            out.append(
                {
                    "text": text[:2000],
                    "decided_by": [str(x)[:100] for x in (item.get("decided_by") or [])][:10],
                    "at_ms": item.get("at_ms"),
                    "segment_ids": [sid for sid in (item.get("segment_ids") or []) if sid in valid_ids],
                }
            )
    return out


def sanitize_extract(payload: dict, valid_ids: set[str]) -> tuple[list[dict], list[dict]]:
    """Never trust model output: unknown segment ids are dropped, times are not."""
    decisions = _grounded_items(payload.get("decisions") or payload.get("items") or [], valid_ids, owner=False)
    actions = _grounded_items(payload.get("action_items") or [], valid_ids, owner=True)
    return decisions, actions


@handler("analyze")
def analyze_job(context: JobContext) -> dict:
    """Agenda -> sections -> per-section summaries -> summary -> decisions/actions."""
    meeting_id = context.job.meeting_id
    if meeting_id is None:
        raise RuntimeError("analyze requires a meeting")
    transcript = context.session.scalar(
        select(Transcript).where(Transcript.meeting_id == meeting_id, Transcript.is_active.is_(True))
    )
    if transcript is None:
        raise RuntimeError("no active transcript to analyze")
    requested = [kind for kind in (context.params.get("analyze_kinds") or list(ANALYZE_KINDS)) if kind in ANALYZE_KINDS]
    if not requested:
        raise ApiError(422, "validation_error", "No analysable kinds requested")

    analyzer = build_analyzer(context.settings)
    run = _new_run(context, transcript, analyzer)
    rows = rows_for(context.session, transcript)
    duration = meeting_duration(context.session, meeting_id) or (rows[-1][2] if rows else 0)
    prefix = duration_note(duration) + transcript_block(rows)
    valid_ids = {row[0] for row in rows}
    produced: dict[str, int] = {}
    steps = 1.0 / max(1, len(requested) + (1 if "section_summary" in requested else 0))
    done = 0.0

    def call(kind: str, user: str, schema: dict | None = None) -> dict:
        nonlocal done
        context.check_cancelled()
        result = analyzer.complete_json(
            system=SYSTEM_ANALYST,
            user=user,
            schema=schema or kind_schema(kind),
            timeout_s=context.settings.analyzer_timeout_s,
        )
        done += steps
        context.progress(min(0.95, done))
        context.event(f"{kind} done", provider=result.provider, model=result.model)
        return result.payload

    if "agenda" in requested:
        agenda = sanitize_agenda(call("agenda", AGENDA_PROMPT + prefix), duration)
        for index, item in enumerate(agenda["items"]):
            context.session.add(
                Section(
                    id=uuid.uuid4(),
                    meeting_id=meeting_id,
                    analysis_run_id=run.id,
                    ordinal=index + 1,
                    title=str(item["title"])[:300] or f"Section {index + 1}",
                    topic=str(item["topic"]),
                    start_ms=int(item["start_ms"]),
                    end_ms=int(item["end_ms"]),
                    summary="",
                    is_mock=run.is_mock,
                )
            )
        context.session.flush()
        _store(context, run, "agenda", agenda)
        produced["agenda"] = len(agenda["items"])

    if "section_summary" in requested:
        sections = context.session.scalars(
            select(Section).where(Section.analysis_run_id == run.id).order_by(Section.ordinal)
        ).all()
        for section in sections:
            excerpt = [row for row in rows if row[2] > section.start_ms and row[1] < section.end_ms]
            payload = sanitize_section(
                call(
                    "section_summary",
                    SECTION_PROMPT + duration_note(duration) + transcript_block(excerpt or rows),
                )
            )
            section.summary = payload["summary"][:4000]
            payload["section_id"] = str(section.id)
            _store(context, run, "section_summary", payload, section_id=section.id)
        produced["section_summary"] = len(sections)

    if "summary" in requested:
        _store(context, run, "summary", sanitize_summary(call("summary", SUMMARY_PROMPT + prefix)))
        produced["summary"] = 1

    if "decisions" in requested or "action_items" in requested:
        extract = call("decisions", EXTRACT_PROMPT + prefix, schema=EXTRACT_SCHEMA)
        decisions, actions = sanitize_extract(extract, valid_ids)
        if "decisions" in requested:
            _store(context, run, "decisions", {"items": decisions})
            produced["decisions"] = len(decisions)
        if "action_items" in requested:
            _store(context, run, "action_items", {"items": actions})
            produced["action_items"] = len(actions)

    run.status = "succeeded"
    context.session.flush()
    mark_documents_stale(context.session, meeting_id, active_transcript_id=transcript.id)
    context.progress(1.0)
    return {
        "run_id": str(run.id),
        "produced": produced,
        "is_mock": run.is_mock,
        "provider": run.provider,
        "model": run.model,
    }
