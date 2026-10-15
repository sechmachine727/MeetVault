from __future__ import annotations

import logging
import shutil
import subprocess
from pathlib import Path

from mvb.config import get_settings
from mvb.media.probe import probe_media

log = logging.getLogger(__name__)
TARGET_RATE = 16000


class AudioExtractionError(RuntimeError):
    pass


def extract_audio_16k(source: Path, destination: Path) -> Path:
    """Decode any ffmpeg-readable media to 16 kHz mono PCM WAV for ASR."""
    settings = get_settings()
    if not shutil.which(settings.ffmpeg):
        raise AudioExtractionError("ffmpeg not found")
    destination.parent.mkdir(parents=True, exist_ok=True)
    command = [
        settings.ffmpeg,
        "-y",
        "-i",
        str(source),
        "-vn",
        "-ac",
        "1",
        "-ar",
        str(TARGET_RATE),
        "-acodec",
        "pcm_s16le",
        str(destination),
    ]
    try:
        completed = subprocess.run(command, capture_output=True, text=True, timeout=3600, check=False)
    except OSError as exc:
        raise AudioExtractionError(f"ffmpeg failed to start: {exc}") from exc
    if completed.returncode != 0 or not destination.exists():
        raise AudioExtractionError((completed.stderr or "ffmpeg failed").strip()[-500:])
    info = probe_media(destination)
    if info.sample_rate != TARGET_RATE or info.channels != 1:
        raise AudioExtractionError(f"unexpected output format: rate={info.sample_rate} channels={info.channels}")
    return destination


def duration_ms_of(path: Path) -> int:
    return probe_media(path).duration_ms
