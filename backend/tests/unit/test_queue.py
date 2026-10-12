from __future__ import annotations

from datetime import timedelta

import pytest
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from mvb.db.base import Base, get_engine, get_session_factory
from mvb.db.models import Job
from mvb.jobs.queue import claim_one, complete, enqueue, fail, heartbeat, reap_stale


@pytest.fixture
def session() -> Session:
    Base.metadata.create_all(get_engine())
    db = get_session_factory()()
    try:
        yield db
    finally:
        db.close()


def test_enqueue_creates_queued_job(session: Session) -> None:
    job = enqueue(session, job_type="media_probe", meeting_id=None, params={"a": 1}, is_mock=True)
    session.commit()
    assert job.state == "queued"
    assert job.progress == 0.0
    assert job.attempts == 0
    assert job.run_after is not None


def test_enqueue_is_idempotent_on_key(session: Session) -> None:
    first = enqueue(session, job_type="transcribe", meeting_id=None, idempotency_key="k1")
    session.commit()
    second = enqueue(session, job_type="transcribe", meeting_id=None, idempotency_key="k1")
    session.commit()
    assert second.id == first.id
    assert session.scalar(select(func.count()).select_from(Job).where(Job.idempotency_key == "k1")) == 1


def test_claim_one_returns_oldest_ready_and_marks_running(session: Session) -> None:
    older = enqueue(session, job_type="a", meeting_id=None, priority=100)
    newer = enqueue(session, job_type="b", meeting_id=None, priority=10)
    session.commit()
    claimed = claim_one(session)
    session.commit()
    assert claimed is not None
    assert claimed.id == newer.id  # priority wins
    assert claimed.state == "running"
    assert claimed.attempts == 1
    assert claimed.started_at is not None
    assert claim_one(session) is not None  # the other job is claimable too
    assert older.id != newer.id


def test_claim_respects_run_after(session: Session) -> None:
    job = enqueue(session, job_type="later", meeting_id=None)
    job.run_after = job.run_after + timedelta(hours=1)
    session.commit()
    assert claim_one(session) is None


def test_complete_sets_progress_and_result(session: Session) -> None:
    job = enqueue(session, job_type="x", meeting_id=None)
    session.commit()
    claim_one(session)
    session.commit()
    complete(session, job, result={"ok": True}, progress=1.0)
    session.commit()
    assert job.state == "succeeded"
    assert job.progress == 1.0
    assert job.result_json == {"ok": True}
    assert job.finished_at is not None


def test_fail_requeues_then_exhausts(session: Session) -> None:
    job = enqueue(session, job_type="x", meeting_id=None, max_attempts=2)
    session.commit()
    claim_one(session)
    session.commit()
    fail(session, job, "boom", retry_in_s=0)
    session.commit()
    assert job.state == "queued"
    assert job.error == "boom"
    claim_one(session)
    session.commit()
    fail(session, job, "boom again", retry_in_s=0)
    session.commit()
    assert job.state == "failed"
    assert job.attempts == 2


def test_heartbeat_and_reap(session: Session) -> None:
    job = enqueue(session, job_type="x", meeting_id=None)
    session.commit()
    claim_one(session)
    session.commit()
    heartbeat(session, job)
    session.commit()
    assert reap_stale(session, stale_after_s=900) == 0
    job.heartbeat_at = job.heartbeat_at - timedelta(seconds=5000)
    session.commit()
    assert reap_stale(session, stale_after_s=900) == 1
    session.commit()
    assert job.state == "queued"
    assert "stale" in (job.error or "")


def test_progress_is_monotonic_helper(session: Session) -> None:
    from mvb.jobs.queue import set_progress

    job = enqueue(session, job_type="x", meeting_id=None)
    session.commit()
    set_progress(session, job, 0.5)
    assert job.progress == 0.5
    set_progress(session, job, 0.2)  # never rewinds
    assert job.progress == 0.5
    set_progress(session, job, 1.0)
    assert job.progress == 1.0


def test_event_appended_with_meeting_context(session: Session) -> None:
    from mvb.jobs.queue import add_event

    job = enqueue(session, job_type="x", meeting_id=None)
    session.commit()
    event = add_event(session, job, "hello", data={"n": 1})
    session.commit()
    assert event.job_id == job.id
    assert event.message == "hello"
    assert event.data_json == {"n": 1}
    assert event.meeting_id == job.meeting_id


def test_cancel_is_noop_for_terminal_state(session: Session) -> None:
    from mvb.jobs.queue import cancel

    job = enqueue(session, job_type="x", meeting_id=None)
    session.commit()
    claim_one(session)
    session.commit()
    complete(session, job, result={}, progress=1.0)
    session.commit()
    cancel(session, job)
    session.commit()
    assert job.state == "succeeded"
