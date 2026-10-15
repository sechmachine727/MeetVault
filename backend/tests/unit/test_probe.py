from __future__ import annotations

import shutil
import subprocess
from pathlib import Path

import pytest

from mvb.media.probe import ffmpeg_available, probe_media

pytestmark = pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="ffmpeg not installed on this machine")


def _make_wav(path: Path, seconds: float = 1.0) -> None:
    subprocess.run(
        [
            "ffmpeg",
            "-y",
            "-f",
            "lavfi",
            "-i",
            f"sine=frequency=440:duration={seconds}",
            "-ac",
            "1",
            "-ar",
            "48000",
            str(path),
        ],
        capture_output=True,
        check=True,
    )


def test_probe_reports_duration_and_channels(tmp_path: Path) -> None:
    wav = tmp_path / "tone.wav"
    _make_wav(wav, 0.5)
    result = probe_media(wav)
    assert result.error is None
    assert result.has_audio is True
    assert 400 <= result.duration_ms <= 600
    assert result.channels == 1
    assert result.sample_rate == 48000


def test_probe_on_missing_file_reports_error(tmp_path: Path) -> None:
    result = probe_media(tmp_path / "nope.wav")
    assert result.error is not None
    assert result.duration_ms == 0


def test_ffmpeg_available_true_here() -> None:
    assert ffmpeg_available() is True
