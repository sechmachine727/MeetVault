from __future__ import annotations

import uuid
from datetime import datetime
from typing import Any

from pydantic import Field

from mvb.schemas.common import ORMModel

PIPELINE_STEPS = ["transcribe", "diarize", "align", "analyze"]
ANALYSIS_KINDS = ["summary", "agenda", "section_summary", "decisions", "action_items"]


class JobEventOut(ORMModel):
    id: int
    at: datetime
    level: str
    message: str
    data: dict[str, Any] = Field(default_factory=dict, validation_alias="data_json")


class JobOut(ORMModel):
    id: uuid.UUID
    meeting_id: uuid.UUID | None
    type: str
    state: str
    progress: float
    attempts: int
    max_attempts: int
    error: str | None
    result: dict[str, Any] = Field(default_factory=dict, validation_alias="result_json")
    is_mock: bool
    run_after: datetime
    started_at: datetime | None
    finished_at: datetime | None
    created_at: datetime


class JobDetailOut(JobOut):
    events: list[JobEventOut] = Field(default_factory=list)


class ProcessRequest(ORMModel):
    steps: list[str] = Field(default_factory=lambda: list(PIPELINE_STEPS))
    language: str | None = None
    force: bool = False
    analyze_kinds: list[str] = Field(default_factory=lambda: list(ANALYSIS_KINDS))
