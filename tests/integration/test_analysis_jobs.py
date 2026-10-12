from __future__ import annotations

import uuid
from pathlib import Path

import pytest
from sqlalchemy import select

from mvb.db.models import (
    AnalysisDocument,
    AnalysisRun,
    Job,
    Section,
    Transcript,
)
from mvb.jobs.worker import tick
from mvb.pipeline.chain import enqueue_pipeline
from mvb.pipeline.status import meeting_status
from mvb.providers.analysis.mock import MockAnalyzer
from mvb.providers.base import LLMResult
from tests.helpers import seed_meeting_with_audio

pytestmark = pytest.mark.skipif(
    not __import__("tests.helpers", fromlist=["ffmpeg_available"]).ffmpeg_available(),
    reason="ffmpeg required for real media fixtures",
)


class HostileAnalyzer(MockAnalyzer):
    """Returns schema-valid but ungrounded payloads: every citation is invented."""

    id = "hostile-analyzer"
    is_mock = False

    def complete_json(self, *, system: str, user: str, schema: dict, timeout_s: float) -> LLMResult:
        base = super().complete_json(system=system, user=user, schema=schema, timeout_s=timeout_s)
        payload = dict(base.payload)
        kind = schema.get("x-kind")
        if kind == "decisions":
            payload["decisions"] = [{"text": "invented decision", "segment_ids": ["invented-1"]}]
            payload["action_items"] = [{"text": "invented action", "segment_ids": ["invented-2"]}]
        if kind == "qa":
            payload["citation_segment_ids"] = ["invented-1", "invented-2"]
        return LLMResult(payload=payload, provider=self.id, model="hostile", is_mock=False)


def _run_chain(db_session, media_root: Path, seconds: float = 40.0):  # type: ignore[no-untyped-def]
    meeting, _ = seed_meeting_with_audio(db_session, media_root, seconds=seconds)
    enqueue_pipeline(db_session, meeting_id=meeting.id)
    db_session.commit()
    for _ in range(20):
        if not tick():
            break
    db_session.expire_all()
    return meeting


def _doc(db_session, meeting_id: uuid.UUID, kind: str) -> AnalysisDocument | None:
    return db_session.scalar(
        select(AnalysisDocument)
        .where(AnalysisDocument.meeting_id == meeting_id, AnalysisDocument.kind == kind)
        .order_by(AnalysisDocument.created_at.desc())
        .limit(1)
    )


def test_analyze_produces_sections_and_documents(db_session, media_root: Path) -> None:
    meeting = _run_chain(db_session, media_root)
    transcript = db_session.scalar(
        select(Transcript).where(Transcript.meeting_id == meeting.id, Transcript.is_active.is_(True))
    )
    assert transcript is not None

    run = db_session.scalar(select(AnalysisRun).where(AnalysisRun.meeting_id == meeting.id))
    assert run is not None
    assert run.prompt_version == "v1"
    assert run.is_mock is True
    assert run.provider == "mock-analyzer"

    for kind in ("summary", "agenda", "decisions", "action_items", "section_summary"):
        document = _doc(db_session, meeting.id, kind)
        assert document is not None, kind
        assert document.is_mock is True
        assert document.stale is False
        assert document.transcript_id == transcript.id
        assert document.payload_json

    sections = list(
        db_session.scalars(select(Section).where(Section.meeting_id == meeting.id).order_by(Section.ordinal)).all()
    )
    assert sections, "agenda must create sections"
    assert [s.ordinal for s in sections] == list(range(1, len(sections) + 1))
    assert sections[0].start_ms == 0
    assert sections[-1].end_ms == meeting.duration_ms
    assert all(s.summary for s in sections), "every section gets a summary"

    section_docs = list(
        db_session.scalars(
            select(AnalysisDocument).where(
                AnalysisDocument.meeting_id == meeting.id, AnalysisDocument.kind == "section_summary"
            )
        ).all()
    )
    assert len(section_docs) == len(sections)
    assert all(doc.section_id is not None for doc in section_docs)


def test_analyze_reruns_create_a_new_run_and_stale_the_previous_documents(db_session, media_root: Path) -> None:
    meeting = _run_chain(db_session, media_root, seconds=25.0)
    first = _doc(db_session, meeting.id, "summary")
    assert first is not None and first.stale is False

    enqueue_pipeline(db_session, meeting_id=meeting.id, steps=["analyze"], force=True)
    db_session.commit()
    for _ in range(5):
        if not tick():
            break
    db_session.expire_all()
    db_session.refresh(first)
    assert first.stale is True, "the superseded document must be marked stale"
    fresh = list(
        db_session.scalars(
            select(AnalysisDocument).where(
                AnalysisDocument.meeting_id == meeting.id,
                AnalysisDocument.kind == "summary",
                AnalysisDocument.stale.is_(False),
            )
        ).all()
    )
    assert len(fresh) == 1
    assert (
        db_session.scalar(
            select(AnalysisRun).where(AnalysisRun.meeting_id == meeting.id).order_by(AnalysisRun.created_at.desc())
        )
        is not None
    )


def test_analyze_drops_invented_segment_ids(db_session, media_root: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    import mvb.pipeline.analyze as analyze_module

    monkeypatch.setattr(analyze_module, "build_analyzer", lambda *_args, **_kwargs: HostileAnalyzer())
    meeting = _run_chain(db_session, media_root, seconds=25.0)
    decisions = _doc(db_session, meeting.id, "decisions")
    actions = _doc(db_session, meeting.id, "action_items")
    assert decisions is not None and actions is not None
    assert decisions.payload_json["items"][0]["segment_ids"] == []
    assert actions.payload_json["items"][0]["segment_ids"] == []
    # The hostile provider wrote no reportable grounding, but the text survived.
    assert decisions.payload_json["items"][0]["text"] == "invented decision"


def test_qa_produces_a_grounded_answer(db_session, media_root: Path) -> None:
    meeting = _run_chain(db_session, media_root, seconds=25.0)
    from mvb.jobs.queue import enqueue

    enqueue(
        db_session,
        job_type="qa",
        meeting_id=meeting.id,
        params={"question": "What did we decide about the release schedule?", "top_k": 4},
    )
    db_session.commit()
    for _ in range(5):
        if not tick():
            break
    db_session.expire_all()
    document = _doc(db_session, meeting.id, "qa")
    assert document is not None
    payload = document.payload_json
    assert payload["question"].startswith("What did we decide")
    assert payload["answer"]
    assert isinstance(payload["citations"], list)
    assert payload["dropped_citations"] == 0
    assert document.is_mock is True


def test_qa_drops_invented_citations(db_session, media_root: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    import mvb.pipeline.qa as qa_module

    monkeypatch.setattr(qa_module, "build_analyzer", lambda *_args, **_kwargs: HostileAnalyzer())
    meeting = _run_chain(db_session, media_root, seconds=25.0)
    from mvb.jobs.queue import enqueue

    enqueue(db_session, job_type="qa", meeting_id=meeting.id, params={"question": "anything at all"})
    db_session.commit()
    for _ in range(5):
        if not tick():
            break
    db_session.expire_all()
    document = _doc(db_session, meeting.id, "qa")
    assert document is not None
    assert document.payload_json["citations"] == []
    assert document.payload_json["dropped_citations"] == 2
    assert document.is_mock is False


def test_qa_without_a_question_fails_the_job(db_session, media_root: Path) -> None:
    meeting = _run_chain(db_session, media_root, seconds=10.0)
    from mvb.jobs.queue import enqueue

    job = enqueue(db_session, job_type="qa", meeting_id=meeting.id, params={}, max_attempts=1)
    db_session.commit()
    for _ in range(3):
        if not tick():
            break
    db_session.expire_all()
    refreshed = db_session.get(Job, job.id)
    assert refreshed is not None
    assert refreshed.state == "failed"
    assert "question" in (refreshed.error or "")


def test_status_reports_progress_and_staleness(db_session, media_root: Path) -> None:
    meeting = _run_chain(db_session, media_root, seconds=20.0)
    status = meeting_status(db_session, meeting.id)
    assert status["progress"] == 1.0
    assert status["active_job_id"] is None
    assert status["mock"] is True
    assert status["stale_kinds"] == []
    assert set(status["fresh_kinds"]) >= {"summary", "agenda", "decisions", "action_items"}

    # A new media asset invalidates the analysis until it is re-run.
    from mvb.staleness import mark_all_stale

    changed = mark_all_stale(db_session, meeting.id)
    db_session.commit()
    assert changed >= 1
    after = meeting_status(db_session, meeting.id)
    assert after["fresh_kinds"] == []
    assert after["stale_kinds"]
