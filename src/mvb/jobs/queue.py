from __future__ import annotations

import logging
import uuid
from datetime import UTC, datetime, timedelta
from typing import Any

from sqlalchemy import select
from sqlalchemy.orm import Session

from mvb.db.models import Job, JobEvent

log = logging.getLogger(__name__)

QUEUED = "queued"
RUNNING = "running"
SUCCEEDED = "succeeded"
FAILED = "failed"
CANCELLED = "cancelled"
TERMINAL_STATES = (SUCCEEDED, FAILED, CANCELLED)
ACTIVE_STATES = (QUEUED, RUNNING)


def _now() -> datetime:
    return datetime.now(UTC)


def enqueue(
    session: Session,
    *,
    job_type: str,
    meeting_id: uuid.UUID | None,
    params: dict[str, Any] | None = None,
    priority: int = 100,
    max_attempts: int = 3,
    idempotency_key: str | None = None,
    is_mock: bool = False,
    run_after: datetime | None = None,
) -> Job:
    """Insert a queued job. With an idempotency_key, an existing job is returned unchanged."""
    if idempotency_key:
        existing = session.scalar(select(Job).where(Job.idempotency_key == idempotency_key))
        if existing is not None:
            return existing
    job = Job(
        id=uuid.uuid4(),
        meeting_id=meeting_id,
        type=job_type,
        state=QUEUED,
        progress=0.0,
        attempts=0,
        max_attempts=max_attempts,
        priority=priority,
        idempotency_key=idempotency_key,
        params_json=params or {},
        result_json={},
        is_mock=is_mock,
        run_after=run_after or _now(),
    )
    session.add(job)
    session.flush()
    add_event(session, job, f"queued {job_type}", data={"priority": priority})
    return job


def claim_one(session: Session, *, now: datetime | None = None) -> Job | None:
    """Claim the highest-priority ready job. Uses SKIP LOCKED on Postgres; a plain select elsewhere."""
    moment = now or _now()
    statement = (
        select(Job)
        .where(Job.state == QUEUED, Job.run_after <= moment)
        .order_by(Job.priority.asc(), Job.run_after.asc(), Job.created_at.asc())
        .limit(1)
    )
    if session.bind is not None and session.bind.dialect.name == "postgresql":
        statement = statement.with_for_update(skip_locked=True)
    job = session.scalar(statement)
    if job is None:
        return None
    job.state = RUNNING
    job.attempts += 1
    job.started_at = moment
    job.heartbeat_at = moment
    job.error = None
    add_event(session, job, "started", data={"attempt": job.attempts})
    session.flush()
    return job


def set_progress(session: Session, job: Job, progress: float) -> None:
    """Progress never rewinds (a retry must not look like a rollback)."""
    clamped = max(0.0, min(1.0, float(progress)))
    if clamped <= job.progress:
        return
    job.progress = clamped
    session.flush()


def complete(
    session: Session,
    job: Job,
    *,
    result: dict[str, Any] | None = None,
    progress: float = 1.0,
    is_mock: bool | None = None,
) -> None:
    job.state = SUCCEEDED
    job.progress = max(progress, job.progress)
    job.result_json = result or {}
    job.finished_at = _now()
    job.heartbeat_at = job.finished_at
    if is_mock is not None:
        job.is_mock = is_mock
    add_event(session, job, "succeeded", data={"progress": job.progress})
    session.flush()


def fail(session: Session, job: Job, error: str, *, retry_in_s: int = 15) -> None:
    job.error = error[:2000]
    if job.attempts >= job.max_attempts:
        job.state = FAILED
        job.finished_at = _now()
        add_event(session, job, "failed", level="error", data={"attempts": job.attempts})
    else:
        job.state = QUEUED
        job.run_after = _now() + timedelta(seconds=retry_in_s)
        add_event(session, job, "retry_scheduled", level="warning", data={"in_s": retry_in_s, "attempts": job.attempts})
    session.flush()


def cancel(session: Session, job: Job) -> Job:
    if job.state in TERMINAL_STATES:
        return job
    job.state = CANCELLED
    job.finished_at = _now()
    add_event(session, job, "cancelled", level="warning")
    session.flush()
    return job


def heartbeat(session: Session, job: Job) -> None:
    job.heartbeat_at = _now()
    session.flush()


def reap_stale(session: Session, *, stale_after_s: int) -> int:
    """Requeue running jobs whose worker died. Returns how many were reaped."""
    cutoff = _now() - timedelta(seconds=stale_after_s)
    stale = session.scalars(select(Job).where(Job.state == RUNNING, Job.heartbeat_at < cutoff)).all()
    for job in stale:
        if job.attempts >= job.max_attempts:
            job.state = FAILED
            job.error = "stale heartbeat; attempts exhausted"
            job.finished_at = _now()
        else:
            job.state = QUEUED
            job.error = f"stale heartbeat after {stale_after_s}s; requeued"
            job.run_after = _now()
        add_event(session, job, f"reaped: {job.state}", level="warning")
    session.flush()
    return len(stale)


def add_event(
    session: Session,
    job: Job,
    message: str,
    *,
    level: str = "info",
    data: dict[str, Any] | None = None,
) -> JobEvent:
    event = JobEvent(
        job_id=job.id,
        meeting_id=job.meeting_id,
        at=_now(),
        level=level,
        message=message[:1000],
        data_json=data or {},
    )
    session.add(event)
    session.flush()
    return event
