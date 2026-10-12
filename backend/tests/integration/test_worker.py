from __future__ import annotations

from sqlalchemy.orm import Session

from mvb.db.base import Base, get_engine, get_session_factory
from mvb.jobs import handlers
from mvb.jobs.handlers import JobCancelled, JobContext, handler
from mvb.jobs.queue import enqueue
from mvb.jobs.worker import tick


def _session() -> Session:
    Base.metadata.create_all(get_engine())
    return get_session_factory()()


def test_worker_runs_registered_handler() -> None:
    calls: list[str] = []

    @handler("test_ok")
    def _ok(context: JobContext) -> dict[str, bool]:
        calls.append(str(context.job.id))
        context.progress(0.5)
        context.event("halfway")
        return {"is_mock": True, "done": True}

    session = _session()
    try:
        job = enqueue(session, job_type="test_ok", meeting_id=None, params={"x": 1})
        session.commit()
        assert "test_ok" in handlers.registered_types()
        assert tick() is True
        session.expire_all()
        stored = session.get(type(job), job.id)
        assert calls == [str(job.id)]
        assert stored is not None
        assert stored.state == "succeeded"
        assert stored.result_json == {"is_mock": True, "done": True}
        assert stored.is_mock is True
        assert stored.progress == 1.0
    finally:
        session.close()


def test_worker_marks_failed_handler() -> None:
    @handler("test_boom")
    def _boom(context: JobContext) -> dict[str, bool]:
        raise ValueError("nope")

    session = _session()
    try:
        job = enqueue(session, job_type="test_boom", meeting_id=None, max_attempts=1)
        session.commit()
        tick()
        session.expire_all()
        stored = session.get(type(job), job.id)
        assert stored is not None
        assert stored.state == "failed"
        assert "ValueError" in (stored.error or "")
    finally:
        session.close()


def test_unknown_job_type_fails_cleanly() -> None:
    session = _session()
    try:
        job = enqueue(session, job_type="does_not_exist", meeting_id=None, max_attempts=1)
        session.commit()
        tick()
        session.expire_all()
        stored = session.get(type(job), job.id)
        assert stored is not None
        assert stored.state == "failed"
        assert "no handler" in (stored.error or "")
    finally:
        session.close()


def test_cancelled_job_stops() -> None:
    @handler("test_cancel_me")
    def _cancel_me(context: JobContext) -> dict[str, bool]:
        raise JobCancelled()

    session = _session()
    try:
        job = enqueue(session, job_type="test_cancel_me", meeting_id=None)
        session.commit()
        tick()
        session.expire_all()
        stored = session.get(type(job), job.id)
        assert stored is not None
        assert stored.state == "cancelled"
    finally:
        session.close()
