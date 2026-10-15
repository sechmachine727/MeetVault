from __future__ import annotations

import uuid
from datetime import datetime
from typing import Any

from pydantic import Field

from mvb.schemas.common import ORMModel

DOC_KINDS = ("summary", "agenda", "section_summary", "decisions", "action_items", "qa")


class SectionOut(ORMModel):
    id: uuid.UUID
    meeting_id: uuid.UUID
    analysis_run_id: uuid.UUID
    ordinal: int
    title: str
    topic: str
    start_ms: int
    end_ms: int
    summary: str
    is_mock: bool


class AnalysisDocumentOut(ORMModel):
    id: uuid.UUID
    meeting_id: uuid.UUID
    analysis_run_id: uuid.UUID
    section_id: uuid.UUID | None
    kind: str
    payload: dict[str, Any] = Field(default_factory=dict, validation_alias="payload_json")
    transcript_id: uuid.UUID
    stale: bool
    is_mock: bool
    created_at: datetime


class AnalysisRunOut(ORMModel):
    id: uuid.UUID
    transcript_id: uuid.UUID
    provider: str
    model: str
    prompt_version: str
    is_mock: bool
    status: str
    created_at: datetime


class AnalysisBundleOut(ORMModel):
    """Everything the client needs in one round trip. `mock` is true when any part is mock."""

    meeting_id: uuid.UUID
    transcript_id: uuid.UUID | None = None
    run: AnalysisRunOut | None = None
    summary: dict[str, Any] | None = None
    agenda: dict[str, Any] | None = None
    sections: list[SectionOut] = Field(default_factory=list)
    section_summaries: list[AnalysisDocumentOut] = Field(default_factory=list)
    decisions: dict[str, Any] | None = None
    action_items: dict[str, Any] | None = None
    qa: list[AnalysisDocumentOut] = Field(default_factory=list)
    stale_kinds: list[str] = Field(default_factory=list)
    mock: bool = False


class QARequest(ORMModel):
    question: str = Field(min_length=1, max_length=2000)
    section_id: uuid.UUID | None = None
    top_k: int = Field(default=8, ge=1, le=40)


class QAResponse(ORMModel):
    meeting_id: uuid.UUID
    job_id: uuid.UUID
    question: str
    state: str = "queued"
