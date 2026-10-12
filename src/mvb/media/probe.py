from __future__ import annotations

import json
import logging
import shutil
import subprocess
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from mvb.config import get_settings

log = logging.getLogger(__name__)


@dataclass(slots=True, frozen=True)
class ProbeResult:
    """Normalized ffprobe summary. Built once, never mutated."""

    duration_ms: int = 0
    sample_rate: int | None = None
    channels: int | None = None
    codec: str = ""
    has_audio: bool = False
    has_video: bool = False
    raw: dict[str, Any] = field(default_factory=dict)
    error: str | None = None


def ffmpeg_available() -> bool:
    settings = get_settings()
    return bool(shutil.which(settings.ffmpeg)) and bool(shutil.which(settings.ffprobe))


def probe_media(path: Path) -> ProbeResult:
    """Run ffprobe. A missing/failing ffprobe returns an empty result with `error` set (never raises)."""
    settings = get_settings()
    if not shutil.which(settings.ffprobe):
        return ProbeResult(error="ffprobe not found")
    command = [
        settings.ffprobe,
        "-v",
        "error",
        "-print_format",
        "json",
        "-show_format",
        "-show_streams",
        str(path),
    ]
    try:
        completed = subprocess.run(command, capture_output=True, text=True, timeout=120, check=False)
    except OSError as exc:
        return ProbeResult(error=f"ffprobe failed to start: {exc}")
    if completed.returncode != 0:
        return ProbeResult(error=(completed.stderr or "ffprobe failed").strip()[:500])
    try:
        payload = json.loads(completed.stdout or "{}")
    except json.JSONDecodeError as exc:
        return ProbeResult(error=f"ffprobe output not json: {exc}")

    duration_ms = 0
    sample_rate: int | None = None
    channels: int | None = None
    codec = ""
    has_audio = False
    has_video = False

    duration = (payload.get("format") or {}).get("duration")
    if duration:
        try:
            duration_ms = int(float(duration) * 1000)
        except ValueError:
            duration_ms = 0
    for stream in payload.get("streams", []):
        kind = stream.get("codec_type")
        if kind == "audio":
            has_audio = True
            codec = stream.get("codec_name", codec)
            sample_rate = int(stream["sample_rate"]) if stream.get("sample_rate") else None
            channels = stream.get("channels")
            if not duration_ms and stream.get("duration"):
                duration_ms = int(float(stream["duration"]) * 1000)
        elif kind == "video":
            has_video = True
    return ProbeResult(
        duration_ms=duration_ms,
        sample_rate=sample_rate,
        channels=channels,
        codec=codec,
        has_audio=has_audio,
        has_video=has_video,
        raw=payload,
    )
