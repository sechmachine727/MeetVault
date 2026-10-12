from __future__ import annotations

import uuid

from sqlalchemy import select
from sqlalchemy.orm import Session

from mvb.db.models import AnalysisDocument, AnalysisRun, Job, Meeting, Section, Transcript

TERMINAL_STATES = ("succeeded", "failed", "cancelled")


def meeting_status(session: Session, meeting_id: uuid.UUID) -> dict:
    """The aggregate view the client polls: per-step states, progress, staleness, mockness."""
    meeting = session.get(Meeting, meeting_id)
    if meeting is None:
        raise LookupError("meeting not found")
    jobs = session.scalars(select(Job).where(Job.meeting_id == meeting_id).order_by(Job.created_at)).all()
    steps: dict[str, str] = {}
    for job in jobs:
        steps[job.type] = job.state
    running = next((job for job in jobs if job.state == "running"), None)
    succeeded = [job.progress for job in jobs if job.state == "succeeded"]
    transcript = session.scalar(
        select(Transcript).where(Transcript.meeting_id == meeting_id, Transcript.is_active.is_(True))
    )
    fresh = sorted(
        set(
            session.scalars(
                select(AnalysisDocument.kind).where(
                    AnalysisDocument.meeting_id == meeting_id, AnalysisDocument.stale.is_(False)
                )
            ).all()
        )
    )
    stale = sorted(
        set(
            session.scalars(
                select(AnalysisDocument.kind).where(
                    AnalysisDocument.meeting_id == meeting_id, AnalysisDocument.stale.is_(True)
                )
            ).all()
        )
    )
    is_mock = any(job.is_mock for job in jobs) or bool(transcript and transcript.is_mock)
    return {
        "status": meeting.status,
        "steps": steps,
        "progress": (sum(succeeded) / len(succeeded)) if succeeded else 0.0,
        "active_job_id": str(running.id) if running else None,
        "active_transcript_id": str(transcript.id) if transcript else None,
        "fresh_kinds": fresh,
        "stale_kinds": stale,
        "mock": is_mock,
        "duration_ms": meeting.duration_ms,
    }


def all_jobs_terminal(session: Session, meeting_id: uuid.UUID) -> bool:
    jobs = session.scalars(select(Job).where(Job.meeting_id == meeting_id)).all()
    return bool(jobs) and all(job.state in TERMINAL_STATES for job in jobs)


def latest_run(session: Session, meeting_id: uuid.UUID) -> AnalysisRun | None:
    return session.scalar(
        select(AnalysisRun).where(AnalysisRun.meeting_id == meeting_id).order_by(AnalysisRun.created_at.desc()).limit(1)
    )


def section_ids(session: Session, meeting_id: uuid.UUID) -> list[uuid.UUID]:
    return list(
        session.scalars(select(Section.id).where(Section.meeting_id == meeting_id).order_by(Section.ordinal)).all()
    )
