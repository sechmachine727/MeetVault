from __future__ import annotations

import uuid
from datetime import datetime
from typing import Any

from pydantic import Field

from mvb.schemas.common import ORMModel

TRANSCRIPT_SOURCES = ("uploaded", "asr", "diarized")


class SegmentOut(ORMModel):
    id: uuid.UUID
    speaker_id: uuid.UUID | None
    speaker_label: str
    start_ms: int
    end_ms: int
    text: str
    confidence: float | None
    source: str


class TranscriptOut(ORMModel):
    id: uuid.UUID
    meeting_id: uuid.UUID
    version: int
    source: str
    provider: str
    model: str
    language: str
    is_mock: bool
    is_active: bool
    segment_count: int
    created_at: datetime


class TranscriptDetailOut(TranscriptOut):
    segments: list[SegmentOut] = Field(default_factory=list)
    duration_ms: int = 0


class TranscriptUpload(ORMModel):
    """A transcript exported by the MeetVault client: either Format A (segments, optionally
    timed) or Format B (a bare list of {Speaker, Text} rows). Send the array itself, or an
    object with `segments` plus optional `language`/`source`."""

    language: str | None = Field(default=None, max_length=8)
    source: str = Field(default="uploaded", max_length=32)
    segments: list[dict[str, Any]] = Field(default_factory=list)
    align: str = Field(default="auto", pattern="^(auto|scale|proportional)$")
    activate: bool = True
