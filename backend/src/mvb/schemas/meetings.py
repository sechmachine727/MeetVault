from __future__ import annotations

import uuid
from datetime import datetime
from typing import Any

from pydantic import Field

from mvb.schemas.common import ORMModel


class MeetingCreate(ORMModel):
    """Create a meeting. `source`+`external_id` is the client's idempotency pair."""

    title: str = Field(min_length=1, max_length=500)
    external_id: str | None = Field(default=None, max_length=200)
    source: str = Field(default="meetvault", max_length=32)
    language: str | None = Field(default=None, max_length=8)
    started_at: datetime | None = None
    ended_at: datetime | None = None
    duration_ms: int = Field(default=0, ge=0)
    metadata: dict[str, Any] = Field(default_factory=dict)


class MeetingPatch(ORMModel):
    title: str | None = Field(default=None, min_length=1, max_length=500)
    language: str | None = Field(default=None, max_length=8)
    started_at: datetime | None = None
    ended_at: datetime | None = None
    duration_ms: int | None = Field(default=None, ge=0)
    metadata: dict[str, Any] | None = None


class MeetingOut(ORMModel):
    id: uuid.UUID
    title: str
    status: str
    language: str
    source: str
    external_id: str | None
    started_at: datetime | None
    ended_at: datetime | None
    duration_ms: int
    metadata: dict[str, Any] = Field(default_factory=dict, validation_alias="metadata_json")
    deleted_at: datetime | None
    created_at: datetime
    updated_at: datetime


class MeetingDetailOut(MeetingOut):
    media_count: int = 0
    transcript_count: int = 0
    active_transcript_id: uuid.UUID | None = None
    speaker_count: int = 0
    has_analysis: bool = False
    is_mock: bool = False


class MeetingStatusOut(ORMModel):
    """The aggregate the client polls after POST /process."""

    status: str
    steps: dict[str, str] = Field(default_factory=dict)
    progress: float = 0.0
    active_job_id: str | None = None
    active_transcript_id: str | None = None
    fresh_kinds: list[str] = Field(default_factory=list)
    stale_kinds: list[str] = Field(default_factory=list)
    mock: bool = False
    duration_ms: int = 0


class MediaUploadOut(ORMModel):
    asset_id: uuid.UUID
    storage_key: str
    size_bytes: int
    sha256: str
    duration_ms: int
    sample_rate: int | None = None
    channels: int | None = None
    is_video: bool = False
    probe_error: str | None = None


class ProcessResponse(ORMModel):
    meeting_id: uuid.UUID
    steps: list[str]
    job_ids: list[uuid.UUID]
    enqueued: int
