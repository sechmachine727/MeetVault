from __future__ import annotations

import json
import re
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from typing import Any

import pytest
from sqlalchemy import select

from mvb.db.models import AnalysisDocument, Job, Meeting
from mvb.jobs.worker import tick
from mvb.pipeline.chain import enqueue_pipeline
from tests.helpers import ffmpeg_available, seed_meeting_with_audio

pytestmark = pytest.mark.skipif(not ffmpeg_available(), reason="ffmpeg required for media fixtures")

MODEL_ID = "stub-model"
CAPTURED: list[dict[str, Any]] = []

# The adapter sends the JSON schema in response_format.json_schema, so the stub recovers the
# kind from there — exactly like a schema-constrained server would be expected to.
KIND = re.compile(r'"x-kind":\s*"(\w+)"')


def _payload_for(kind: str) -> dict:
    """A schema-shaped reply, mimicking a local model that honours the requested schema."""
    if kind == "agenda":
        return {"items": [{"title": "Real agenda", "topic": "main", "start_ms": 0, "end_ms": 12_000}]}
    if kind == "summary":
        return {"text": "real summary from the stub server", "bullets": ["one", "two"]}
    if kind == "section_summary":
        return {"summary": "real section summary", "bullets": [], "decisions": [], "action_items": []}
    if kind == "decisions":
        return {
            "decisions": [{"text": "real decision", "decided_by": ["Speaker 1"], "at_ms": 1000, "segment_ids": []}],
            "action_items": [{"text": "real action", "owner": "Speaker 2", "due": None, "segment_ids": []}],
        }
    if kind == "qa":
        return {"answer": "real answer", "citation_segment_ids": []}
    raise AssertionError(f"the pipeline asked for an unknown kind: {kind!r}")


class _Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _send(self, payload: dict) -> None:
        encoded = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def do_GET(self) -> None:
        # The provider registry pings GET /models before it accepts the provider.
        CAPTURED.append({"method": "GET", "path": self.path})
        assert self.path.endswith("/models"), self.path
        self._send({"object": "list", "data": [{"id": MODEL_ID, "object": "model"}]})

    def do_POST(self) -> None:
        length = int(self.headers.get("content-length", "0"))
        body = json.loads(self.rfile.read(length) or b"{}")
        CAPTURED.append(
            {"method": "POST", "path": self.path, "auth": self.headers.get("authorization", ""), "body": body}
        )
        assert self.path.endswith("/chat/completions"), self.path
        schema = body["response_format"]["json_schema"]["schema"]
        match = KIND.search(json.dumps(schema))
        assert match, f"the adapter must send the JSON schema; response_format was {body.get('response_format')!r}"
        self._send(
            {
                "id": "chatcmpl-stub",
                "object": "chat.completion",
                "model": body.get("model") or MODEL_ID,
                "choices": [
                    {
                        "index": 0,
                        "message": {"role": "assistant", "content": json.dumps(_payload_for(match.group(1)))},
                        "finish_reason": "stop",
                    }
                ],
                "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
            }
        )

    def log_message(self, *args: object) -> None:  # keep pytest output clean
        return


@pytest.fixture
def lmstudio_stub():  # type: ignore[no-untyped-def]
    """A real HTTP server on a real socket standing in for LM Studio's OpenAI-compatible API."""
    server = HTTPServer(("127.0.0.1", 0), _Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    CAPTURED.clear()
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_port}/v1"
    finally:
        server.shutdown()
        server.server_close()


def _configure(monkeypatch: pytest.MonkeyPatch, base_url: str, **extra: str) -> None:
    monkeypatch.setenv("MVB_ANALYZER", "lmstudio")
    monkeypatch.setenv("MVB_ANALYZER_MODEL", MODEL_ID)
    monkeypatch.setenv("MVB_LMSTUDIO_BASE_URL", base_url)
    monkeypatch.setenv("MVB_LMSTUDIO_API_KEY", "stub-key")
    for key, value in extra.items():
        monkeypatch.setenv(key, value)
    from mvb.config import get_settings

    get_settings.cache_clear()


def _run_pipeline(db_session, media_root: Path) -> Meeting:  # type: ignore[no-untyped-def]
    meeting, _ = seed_meeting_with_audio(db_session, media_root, seconds=12.0)
    enqueue_pipeline(db_session, meeting_id=meeting.id, steps=["transcribe", "diarize", "align", "analyze"])
    db_session.commit()
    for _ in range(8):
        if not tick():
            break
    db_session.expire_all()
    return meeting


def _jobs(db_session, meeting_id, job_type: str) -> Job | None:  # type: ignore[no-untyped-def]
    return db_session.scalar(
        select(Job).where(Job.meeting_id == meeting_id, Job.type == job_type).order_by(Job.created_at.desc())
    )


def test_lmstudio_analyzer_runs_through_the_pipeline(
    db_session, media_root: Path, lmstudio_stub: str, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The REAL LM Studio provider driven through a real analyze job over a real socket.

    The unit tests stub httpx; this exercises the actual request the adapter builds (model id,
    bearer header, the schema in the response_format, response parsing) and the rows it produces.
    """
    _configure(monkeypatch, lmstudio_stub)
    meeting = _run_pipeline(db_session, media_root)

    posts = [item for item in CAPTURED if item["method"] == "POST"]
    assert posts, "the analyzer never called /chat/completions"
    assert any(item["path"].endswith("/models") for item in CAPTURED), "the registry must ping /models first"
    for item in posts:
        assert item["auth"] == "Bearer stub-key", "the configured api key must be sent"
        assert item["body"]["model"] == MODEL_ID
        rf = item["body"]["response_format"]
        assert rf["type"] == "json_schema"
        assert rf["json_schema"]["strict"] is True
        assert isinstance(rf["json_schema"]["schema"], dict) and rf["json_schema"]["schema"]
        assert item["body"]["temperature"] == 0.0
        assert item["body"]["stream"] is False
        assert item["body"]["messages"][1]["role"] == "user"

    documents = list(
        db_session.scalars(select(AnalysisDocument).where(AnalysisDocument.meeting_id == meeting.id)).all()
    )
    assert documents, "analyse produced no documents"
    assert all(document.is_mock is False for document in documents), "a real provider must not be flagged mock"
    assert next(d for d in documents if d.kind == "summary").payload_json["text"] == "real summary from the stub server"
    assert next(d for d in documents if d.kind == "decisions").payload_json["items"][0]["text"] == "real decision"
    assert next(d for d in documents if d.kind == "agenda").payload_json["items"][0]["title"] == "Real agenda"

    job = _jobs(db_session, meeting.id, "analyze")
    assert job is not None and job.state == "succeeded", job.error if job else "no analyze job"
    assert job.result_json["provider"] == "lmstudio"
    assert job.result_json["is_mock"] is False


def test_lmstudio_analyzer_falls_back_to_mock_when_unreachable(
    db_session, media_root: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """A configured-but-unreachable engine must not fail the job: it degrades to the labelled
    mock and reports why (a missing provider must never block the pipeline)."""
    _configure(monkeypatch, "http://127.0.0.1:1/v1", MVB_ANALYZER_TIMEOUT_S="2")
    meeting = _run_pipeline(db_session, media_root)

    job = _jobs(db_session, meeting.id, "analyze")
    assert job is not None
    assert job.state == "succeeded", f"an unreachable engine must not fail the job: {job.error}"
    documents = list(
        db_session.scalars(select(AnalysisDocument).where(AnalysisDocument.meeting_id == meeting.id)).all()
    )
    assert documents and all(document.is_mock is True for document in documents), "falling back must be visible as mock"
