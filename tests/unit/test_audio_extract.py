from __future__ import annotations

import shutil
import subprocess
from pathlib import Path

import pytest

from mvb.media.audio import AudioExtractionError, extract_audio_16k
from mvb.media.probe import probe_media

pytestmark = pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="ffmpeg not installed")


def _make_stereo_wav(path: Path, seconds: float = 1.0) -> None:
    subprocess.run(
        [
            "ffmpeg",
            "-y",
            "-f",
            "lavfi",
            "-i",
            f"sine=frequency=440:duration={seconds}",
            "-ac",
            "2",
            "-ar",
            "48000",
            str(path),
        ],
        capture_output=True,
        check=True,
    )


def test_extract_produces_16k_mono(tmp_path: Path) -> None:
    source = tmp_path / "in.wav"
    _make_stereo_wav(source, 0.6)
    out = extract_audio_16k(source, tmp_path / "out.wav")
    info = probe_media(out)
    assert info.sample_rate == 16000
    assert info.channels == 1
    assert 400 <= info.duration_ms <= 800


def test_extract_failure_raises(tmp_path: Path) -> None:
    with pytest.raises(AudioExtractionError):
        extract_audio_16k(tmp_path / "missing.wav", tmp_path / "out.wav")
