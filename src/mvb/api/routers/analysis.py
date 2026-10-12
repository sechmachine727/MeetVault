from __future__ import annotations

import uuid
from typing import Annotated

from fastapi import APIRouter, Body, Query, status
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from mvb.api.deps import ApiKeyId, DbSession, Pagination
from mvb.api.errors import ApiError
from mvb.api.routers.meetings import get_meeting
from mvb.db.models import AnalysisDocument, AnalysisRun, Job, Section
from mvb.jobs.queue import enqueue
from mvb.pipeline.status import meeting_status
from mvb.schemas.analysis import (
    AnalysisBundleOut,
    AnalysisDocumentOut,
    AnalysisRunOut,
    QAResponse,
    SectionOut,
)
from mvb.schemas.common import Page

router = APIRouter(tags=["analysis"])

BUNDLE_KINDS = ("summary", "agenda", "decisions", "action_items")


def latest_documents(db: Session, meeting_id: uuid.UUID, *, fresh_only: bool = True) -> dict[str, AnalysisDocument]:
    """Newest document per kind (ties broken by id) — the current answer for that kind."""
    conditions = [AnalysisDocument.meeting_id == meeting_id, AnalysisDocument.section_id.is_(None)]
    if fresh_only:
        conditions.append(AnalysisDocument.stale.is_(False))
    rows = db.scalars(
        select(AnalysisDocument)
        .where(*conditions)
        .order_by(AnalysisDocument.created_at.asc(), AnalysisDocument.id.asc())
    ).all()
    latest: dict[str, AnalysisDocument] = {}
    for row in rows:  # ascending, so the last write per kind wins deterministically
        latest[row.kind] = row
    return latest


def latest_sections(db: Session, meeting_id: uuid.UUID, run_id: uuid.UUID | None) -> list[Section]:
    conditions = [Section.meeting_id == meeting_id]
    if run_id is not None:
        conditions.append(Section.analysis_run_id == run_id)
    return list(db.scalars(select(Section).where(*conditions).order_by(Section.ordinal)).all())


@router.get("/meetings/{meeting_id}/analysis", response_model=AnalysisBundleOut)
def get_analysis_bundle(meeting_id: uuid.UUID, _: ApiKeyId, db: DbSession) -> AnalysisBundleOut:
    """Everything the client renders after processing, in one round trip: summary, agenda
    sections with their summaries, decisions, action items, previous Q&A, and staleness."""
    get_meeting(db, meeting_id)
    status_payload = meeting_status(db, meeting_id)
    latest_run = db.scalar(
        select(AnalysisRun)
        .where(AnalysisRun.meeting_id == meeting_id)
        .order_by(AnalysisRun.created_at.desc(), AnalysisRun.id.desc())
        .limit(1)
    )
    documents = latest_documents(db, meeting_id)
    sections = latest_sections(db, meeting_id, latest_run.id if latest_run else None)
    section_summaries = list(
        db.scalars(
            select(AnalysisDocument)
            .where(
                AnalysisDocument.meeting_id == meeting_id,
                AnalysisDocument.kind == "section_summary",
                AnalysisDocument.section_id.is_not(None),
            )
            .order_by(AnalysisDocument.created_at.asc(), AnalysisDocument.id.asc())
        ).all()
    )
    section_ids = {section.id for section in sections}
    section_summaries = [doc for doc in section_summaries if doc.section_id in section_ids]
    seen: set[uuid.UUID] = set()
    deduped: list[AnalysisDocument] = []
    for doc in reversed(section_summaries):  # newest first
        if doc.section_id is not None and doc.section_id not in seen:
            seen.add(doc.section_id)
            deduped.append(doc)
    qa_documents = list(
        db.scalars(
            select(AnalysisDocument)
            .where(AnalysisDocument.meeting_id == meeting_id, AnalysisDocument.kind == "qa")
            .order_by(AnalysisDocument.created_at.desc(), AnalysisDocument.id.desc())
            .limit(20)
        ).all()
    )
    mock = bool(latest_run is not None and latest_run.is_mock) or any(
        doc.is_mock for doc in documents.values()
    )
    return AnalysisBundleOut(
        meeting_id=meeting_id,
        transcript_id=uuid.UUID(status_payload["active_transcript_id"])
        if status_payload["active_transcript_id"]
        else None,
        run=AnalysisRunOut.model_validate(latest_run) if latest_run else None,
        summary=documents["summary"].payload_json if "summary" in documents else None,
        agenda=documents["agenda"].payload_json if "agenda" in documents else None,
        sections=[SectionOut.model_validate(section) for section in sections],
        section_summaries=[AnalysisDocumentOut.model_validate(doc) for doc in reversed(deduped)],
        decisions=documents["decisions"].payload_json if "decisions" in documents else None,
        action_items=documents["action_items"].payload_json if "action_items" in documents else None,
        qa=[AnalysisDocumentOut.model_validate(doc) for doc in qa_documents],
        stale_kinds=list(status_payload["stale_kinds"]),
        mock=mock,
    )


@router.get("/meetings/{meeting_id}/sections", response_model=list[SectionOut])
def list_sections(meeting_id: uuid.UUID, _: ApiKeyId, db: DbSession) -> list[SectionOut]:
    """Agenda sections of the newest analysis run, in meeting order."""
    get_meeting(db, meeting_id)
    latest_run = db.scalar(
        select(AnalysisRun)
        .where(AnalysisRun.meeting_id == meeting_id)
        .order_by(AnalysisRun.created_at.desc(), AnalysisRun.id.desc())
        .limit(1)
    )
    sections = latest_sections(db, meeting_id, latest_run.id if latest_run else None)
    return [SectionOut.model_validate(section) for section in sections]


@router.get("/meetings/{meeting_id}/sections/{section_id}/summary", response_model=AnalysisDocumentOut)
def get_section_summary(meeting_id: uuid.UUID, section_id: uuid.UUID, _: ApiKeyId, db: DbSession) -> AnalysisDocumentOut:
    """The newest summary document for one section."""
    get_meeting(db, meeting_id)
    section = db.get(Section, section_id)
    if section is None or section.meeting_id != meeting_id:
        raise ApiError(404, "not_found", "Section not found", {"section_id": str(section_id)})
    document = db.scalar(
        select(AnalysisDocument)
        .where(
            AnalysisDocument.section_id == section_id,
            AnalysisDocument.kind == "section_summary",
            AnalysisDocument.stale.is_(False),
        )
        .order_by(AnalysisDocument.created_at.desc(), AnalysisDocument.id.desc())
        .limit(1)
    )
    if document is None:
        raise ApiError(404, "not_found", "This section has no summary yet", {"section_id": str(section_id)})
    return AnalysisDocumentOut.model_validate(document)


@router.get("/meetings/{meeting_id}/documents", response_model=Page[AnalysisDocumentOut])
def list_documents(
    meeting_id: uuid.UUID,
    _: ApiKeyId,
    db: DbSession,
    page: Pagination,
    kind: Annotated[str | None, Query(max_length=32)] = None,
    include_stale: Annotated[bool, Query()] = False,
) -> Page[AnalysisDocumentOut]:
    """Analysis documents (newest first), optionally filtered by kind."""
    limit, offset = page
    get_meeting(db, meeting_id)
    conditions = [AnalysisDocument.meeting_id == meeting_id]
    if kind:
        conditions.append(AnalysisDocument.kind == kind)
    if not include_stale:
        conditions.append(AnalysisDocument.stale.is_(False))
    total = db.scalar(select(func.count()).select_from(AnalysisDocument).where(*conditions)) or 0
    rows = db.scalars(
        select(AnalysisDocument)
        .where(*conditions)
        .order_by(AnalysisDocument.created_at.desc(), AnalysisDocument.id.desc())
        .limit(limit)
        .offset(offset)
    ).all()
    return Page[AnalysisDocumentOut](
        items=[AnalysisDocumentOut.model_validate(row) for row in rows],
        total=int(total),
        limit=limit,
        offset=offset,
    )


@router.get("/meetings/{meeting_id}/decisions", response_model=AnalysisDocumentOut)
def get_decisions(meeting_id: uuid.UUID, _: ApiKeyId, db: DbSession) -> AnalysisDocumentOut:
    """The newest decisions document."""
    return _one_kind(db, meeting_id, "decisions")


@router.get("/meetings/{meeting_id}/action-items", response_model=AnalysisDocumentOut)
def get_action_items(meeting_id: uuid.UUID, _: ApiKeyId, db: DbSession) -> AnalysisDocumentOut:
    """The newest action-items document."""
    return _one_kind(db, meeting_id, "action_items")


def _one_kind(db: Session, meeting_id: uuid.UUID, kind: str) -> AnalysisDocumentOut:
    get_meeting(db, meeting_id)
    document = db.scalar(
        select(AnalysisDocument)
        .where(AnalysisDocument.meeting_id == meeting_id, AnalysisDocument.kind == kind)
        .order_by(AnalysisDocument.created_at.desc(), AnalysisDocument.id.desc())
        .limit(1)
    )
    if document is None:
        raise ApiError(404, "not_found", f"No {kind} document for this meeting", {"kind": kind})
    return AnalysisDocumentOut.model_validate(document)


@router.post("/meetings/{meeting_id}/qa", response_model=QAResponse, status_code=status.HTTP_202_ACCEPTED)
def ask_question(
    meeting_id: uuid.UUID,
    _: ApiKeyId,
    db: DbSession,
    payload: Annotated[dict, Body()],
) -> QAResponse:
    """Queue a question about the active transcript (optionally scoped to a section).

    The answer is a job: poll GET /jobs/{job_id} or stream its events, then read the newest
    `qa` document from GET /meetings/{id}/analysis. Answers carry citations with the segment
    ids they were grounded in."""
    from mvb.schemas.analysis import QARequest

    get_meeting(db, meeting_id)
    request = QARequest.model_validate(payload)
    if request.section_id is not None:
        section = db.get(Section, request.section_id)
        if section is None or section.meeting_id != meeting_id:
            raise ApiError(404, "not_found", "Section not found", {"section_id": str(request.section_id)})
    job = enqueue(
        db,
        job_type="qa",
        meeting_id=meeting_id,
        params={
            "question": request.question,
            "section_id": str(request.section_id) if request.section_id else None,
            "top_k": request.top_k,
        },
        priority=200,
    )
    db.commit()
    return QAResponse(meeting_id=meeting_id, job_id=job.id, question=request.question, state=job.state)


@router.get("/meetings/{meeting_id}/qa", response_model=Page[AnalysisDocumentOut])
def list_qa(meeting_id: uuid.UUID, _: ApiKeyId, db: DbSession, page: Pagination) -> Page[AnalysisDocumentOut]:
    """Previously asked questions, newest first."""
    limit, offset = page
    get_meeting(db, meeting_id)
    conditions = [AnalysisDocument.meeting_id == meeting_id, AnalysisDocument.kind == "qa"]
    total = db.scalar(select(func.count()).select_from(AnalysisDocument).where(*conditions)) or 0
    rows = db.scalars(
        select(AnalysisDocument)
        .where(*conditions)
        .order_by(AnalysisDocument.created_at.desc(), AnalysisDocument.id.desc())
        .limit(limit)
        .offset(offset)
    ).all()
    return Page[AnalysisDocumentOut](
        items=[AnalysisDocumentOut.model_validate(row) for row in rows],
        total=int(total),
        limit=limit,
        offset=offset,
    )


@router.get("/meetings/{meeting_id}/analysis/runs", response_model=Page[AnalysisRunOut])
def list_runs(meeting_id: uuid.UUID, _: ApiKeyId, db: DbSession, page: Pagination) -> Page[AnalysisRunOut]:
    """Analysis history, newest first (a stale document points at the run that produced it)."""
    limit, offset = page
    get_meeting(db, meeting_id)
    total = db.scalar(select(func.count()).select_from(AnalysisRun).where(AnalysisRun.meeting_id == meeting_id)) or 0
    rows = db.scalars(
        select(AnalysisRun)
        .where(AnalysisRun.meeting_id == meeting_id)
        .order_by(AnalysisRun.created_at.desc(), AnalysisRun.id.desc())
        .limit(limit)
        .offset(offset)
    ).all()
    return Page[AnalysisRunOut](
        items=[AnalysisRunOut.model_validate(row) for row in rows],
        total=int(total),
        limit=limit,
        offset=offset,
    )


@router.get("/meetings/{meeting_id}/jobs/latest", response_model=list[dict])
def latest_jobs(meeting_id: uuid.UUID, _: ApiKeyId, db: DbSession) -> list[dict]:
    """Compact per-step job state — a lighter alternative to /meetings/{id}/jobs for polling."""
    get_meeting(db, meeting_id)
    rows = db.scalars(
        select(Job).where(Job.meeting_id == meeting_id).order_by(Job.created_at.asc(), Job.id.asc())
    ).all()
    return [
        {"id": str(job.id), "type": job.type, "state": job.state, "progress": job.progress, "error": job.error}
        for job in rows
    ]
