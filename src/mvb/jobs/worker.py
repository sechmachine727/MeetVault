from __future__ import annotations

import logging
import signal
import time
import uuid

from sqlalchemy.orm import Session, sessionmaker

from mvb.config import Settings, get_settings
from mvb.db.base import get_session_factory
from mvb.db.models import Job
from mvb.jobs import handlers as handlers_module
from mvb.jobs.handlers import JobContext, run_handler
from mvb.jobs.queue import claim_one, reap_stale
from mvb.logging_setup import configure_logging, job_id_var, meeting_id_var
from mvb.storage.local import LocalStorage

log = logging.getLogger(__name__)


def cancelled(session: Session, job_id: uuid.UUID) -> bool:
    """Re-read state so /cancel takes effect on a running job."""
    session.expire_all()
    job = session.get(Job, job_id)
    return job is None or job.state == "cancelled"


def tick(
    *,
    session_factory: sessionmaker[Session] | None = None,
    storage: LocalStorage | None = None,
    settings: Settings | None = None,
) -> bool:
    """Claim and run at most one job. Returns True when a job ran."""
    settings = settings or get_settings()
    factory = session_factory or get_session_factory()
    storage = storage or LocalStorage(settings.media_root)
    session = factory()
    try:
        reap_stale(session, stale_after_s=settings.job_stale_seconds)
        session.commit()
        job = claim_one(session)
        session.commit()
        if job is None:
            return False
        job_id_var.set(str(job.id))
        if job.meeting_id:
            meeting_id_var.set(str(job.meeting_id))
        context = JobContext(
            session=session,
            job=job,
            settings=settings,
            storage=storage,
            cancellable=lambda: cancelled(session, job.id),
        )
        log.info("job_claimed", extra={"job_type": job.type})
        started = time.perf_counter()
        run_handler(context)
        log.info(
            "job_finished",
            extra={
                "job_type": job.type,
                "state": job.state,
                "duration_ms": int((time.perf_counter() - started) * 1000),
            },
        )
        return True
    finally:
        job_id_var.set(None)
        meeting_id_var.set(None)
        session.close()


def main() -> None:
    settings = get_settings()
    configure_logging(settings.log_level)
    log.info(
        "worker_start poll=%ss concurrency=%s handlers=%s",
        settings.worker_poll_seconds,
        settings.worker_concurrency,
        ",".join(handlers_module.registered_types()),
    )
    stopping = False

    def _stop(_signum: int, _frame: object) -> None:
        nonlocal stopping
        stopping = True

    signal.signal(signal.SIGINT, _stop)
    signal.signal(signal.SIGTERM, _stop)
    while not stopping:
        if not tick():
            time.sleep(settings.worker_poll_seconds)
    log.info("worker_stop")


if __name__ == "__main__":
    main()
