from __future__ import annotations

import uuid

from sqlalchemy import select
from sqlalchemy.orm import Session

from mvb.db.models import Job
from mvb.jobs.queue import enqueue

STEP_ORDER = ("media_probe", "transcribe", "diarize", "align", "analyze")
ACTIVE_STATES = ("queued", "running")


def default_steps() -> list[str]:
    return ["transcribe", "diarize", "align", "analyze"]


def enqueue_pipeline(
    session: Session,
    *,
    meeting_id: uuid.UUID,
    steps: list[str] | None = None,
    start_at: str | None = None,
    language: str | None = None,
    analyze_kinds: list[str] | None = None,
    force: bool = False,
) -> list[Job]:
    """Start (or advance) the job chain for a meeting: create exactly ONE job.

    `steps` is the requested chain (stored in `params["steps"]` so every job of the chain
    knows what the whole run is); `start_at` picks which step to create now (default: the
    first). The worker creates each following step after the current one succeeds
    (`next_steps_after`), so a failed step never leaves orphaned `queued` jobs.

    Without `force`, a first step that already has a queued or running job for this
    meeting is not created again (so a double-click cannot start the pipeline twice).
    """
    wanted = [step for step in (steps or list(STEP_ORDER)) if step in STEP_ORDER]
    if not wanted:
        return []
    first = start_at if start_at in wanted else wanted[0]
    params: dict = {"steps": wanted}
    if language:
        params["language"] = language
    if analyze_kinds:
        params["analyze_kinds"] = list(analyze_kinds)
    if not force:
        pending = session.scalar(
            select(Job).where(
                Job.meeting_id == meeting_id,
                Job.type == first,
                Job.state.in_(ACTIVE_STATES),
            )
        )
        if pending is not None:
            return []
    job = enqueue(
        session,
        job_type=first,
        meeting_id=meeting_id,
        params=params,
        priority=STEP_ORDER.index(first),
    )
    session.flush()
    return [job]


def next_steps_after(job_type: str, requested: list[str]) -> list[str]:
    """The chain is data, not control flow: the worker asks this after each success."""
    if job_type not in STEP_ORDER:
        return []
    index = STEP_ORDER.index(job_type)
    following = [step for step in requested if step in STEP_ORDER and STEP_ORDER.index(step) > index]
    return following[:1]
