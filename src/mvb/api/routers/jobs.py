from __future__ import annotations

import json
import time
import uuid
from collections.abc import Iterator

from fastapi import APIRouter
from fastapi.responses import StreamingResponse
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from mvb.api.deps import ApiKeyId, DbSession, Pagination
from mvb.api.errors import ApiError
from mvb.db.base import get_session_factory
from mvb.db.models import Job, JobEvent
from mvb.jobs.queue import TERMINAL_STATES, cancel
from mvb.schemas.common import Page
from mvb.schemas.jobs import JobDetailOut, JobEventOut, JobOut

router = APIRouter(tags=["jobs"])

DETAIL_EVENT_LIMIT = 50
EVENTS_POLL_SECONDS = 1.0
EVENTS_MAX_SECONDS = 15 * 60


def _get_job(db: Session, job_id: uuid.UUID) -> Job:
    job = db.get(Job, job_id)
    if job is None:
        raise ApiError(404, "not_found", "Job not found", {"job_id": str(job_id)})
    return job


@router.get("/jobs/{job_id}", response_model=JobDetailOut)
def get_job(job_id: uuid.UUID, _: ApiKeyId, db: DbSession) -> JobDetailOut:
    """One job plus its most recent events, oldest first."""
    job = _get_job(db, job_id)
    rows = db.scalars(
        select(JobEvent).where(JobEvent.job_id == job_id).order_by(JobEvent.id.desc()).limit(DETAIL_EVENT_LIMIT)
    ).all()
    detail = JobDetailOut.model_validate(job)
    detail.events = [JobEventOut.model_validate(row) for row in reversed(rows)]
    return detail


@router.get("/meetings/{meeting_id}/jobs", response_model=Page[JobOut])
def list_meeting_jobs(meeting_id: uuid.UUID, _: ApiKeyId, db: DbSession, page: Pagination) -> Page[JobOut]:
    """Job history for one meeting, newest first."""
    limit, offset = page
    total = db.scalar(select(func.count()).select_from(Job).where(Job.meeting_id == meeting_id)) or 0
    rows = db.scalars(
        select(Job)
        .where(Job.meeting_id == meeting_id)
        .order_by(Job.created_at.desc(), Job.id.desc())
        .limit(limit)
        .offset(offset)
    ).all()
    return Page[JobOut](
        items=[JobOut.model_validate(row) for row in rows],
        total=int(total),
        limit=limit,
        offset=offset,
    )


@router.post("/jobs/{job_id}/cancel", response_model=JobOut)
def cancel_job(job_id: uuid.UUID, _: ApiKeyId, db: DbSession) -> Job:
    """Cancel a queued/running job. Terminal jobs are returned unchanged (idempotent)."""
    job = _get_job(db, job_id)
    cancel(db, job)
    db.commit()
    return job


@router.get("/jobs/{job_id}/events")
def job_events(job_id: uuid.UUID, _: ApiKeyId, db: DbSession) -> StreamingResponse:
    """text/event-stream over job_events; closes when the job reaches a terminal state (cap 15 min)."""
    _get_job(db, job_id)
    return StreamingResponse(
        _event_stream(job_id),
        media_type="text/event-stream",
        headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
    )


def _event_stream(job_id: uuid.UUID) -> Iterator[str]:
    factory = get_session_factory()
    deadline = time.monotonic() + EVENTS_MAX_SECONDS
    last_id = 0
    while True:
        session = factory()
        try:
            rows = session.scalars(
                select(JobEvent).where(JobEvent.job_id == job_id, JobEvent.id > last_id).order_by(JobEvent.id.asc())
            ).all()
            state = session.scalar(select(Job.state).where(Job.id == job_id))
            payloads = [(row.id, json.dumps(JobEventOut.model_validate(row).model_dump(mode="json"))) for row in rows]
        finally:
            session.close()
        for event_id, body in payloads:
            last_id = event_id
            yield f"data: {body}\n\n"
        if state is None or state in TERMINAL_STATES or time.monotonic() >= deadline:
            break
        time.sleep(EVENTS_POLL_SECONDS)

