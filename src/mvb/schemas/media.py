from __future__ import annotations

import uuid
from datetime import datetime
from typing import Any

from pydantic import Field

from mvb.schemas.common import ORMModel


class MediaAssetOut(ORMModel):
    id: uuid.UUID
    meeting_id: uuid.UUID
    kind: str
    role: str
    original_name: str
    content_type: str
    size_bytes: int
    sha256: str
    storage_key: str
    duration_ms: int
    start_offset_ms: int
    sample_rate: int | None
    channels: int | None
    probe: dict[str, Any] = Field(default_factory=dict, validation_alias="probe_json")
    created_at: datetime
