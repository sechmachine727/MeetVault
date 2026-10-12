from __future__ import annotations

import uuid
from datetime import datetime

from pydantic import Field

from mvb.schemas.common import ORMModel


class SpeakerOut(ORMModel):
    id: uuid.UUID
    meeting_id: uuid.UUID
    label: str
    kind: str
    color: str
    created_at: datetime


class SpeakerWithStatsOut(SpeakerOut):
    segment_count: int = 0
    speaking_ms: int = 0
    preview: str = ""


class SpeakerPatch(ORMModel):
    label: str = Field(min_length=1, max_length=200)
    color: str | None = Field(default=None, max_length=16)


class SpeakerMerge(ORMModel):
    target_speaker_id: uuid.UUID


class SpeakerMergeOut(ORMModel):
    merged_speaker_id: uuid.UUID
    target_speaker_id: uuid.UUID
    moved_segments: int
