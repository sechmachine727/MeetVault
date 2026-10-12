from __future__ import annotations

import importlib.util
import os
from collections.abc import Callable
from pathlib import Path

from mvb.providers.base import DiarizationResult, SpeakerTurn


def is_available() -> bool:
    """True only when both the package and a HF token are present. Must not import torch."""
    return importlib.util.find_spec("pyannote.audio") is not None and bool(os.environ.get("HF_TOKEN"))


class PyannoteDiarizer:
    """Real offline diarization. Constructed lazily; the registry falls back to the mock on
    any failure (missing package, missing token, model download error)."""

    id = "pyannote"
    is_mock = False

    def __init__(self, model_id: str = "pyannote/speaker-diarization-3.1", device: str = "cpu") -> None:
        self.model = model_id
        self.device = device
        self._pipeline = None

    def _ensure_pipeline(self):  # type: ignore[no-untyped-def]
        if self._pipeline is None:
            from pyannote.audio import Pipeline

            token = os.environ.get("HF_TOKEN")
            if not token:
                raise RuntimeError("HF_TOKEN is required for pyannote diarization")
            self._pipeline = Pipeline.from_pretrained(self.model, use_auth_token=token)
        return self._pipeline

    def diarize(
        self,
        wav_path: Path,
        *,
        duration_ms: int,
        num_speakers: int | None = None,
        progress: Callable[[float], None] = lambda _p: None,
    ) -> DiarizationResult:
        pipeline = self._ensure_pipeline()
        annotation = pipeline(str(wav_path), num_speakers=num_speakers) if num_speakers else pipeline(str(wav_path))
        index_of: dict[str, int] = {}
        turns: list[SpeakerTurn] = []
        for turn, _, speaker in annotation.itertracks(yield_label=True):
            if speaker not in index_of:
                index_of[speaker] = len(index_of)
            start_ms = int(turn.start * 1000)
            end_ms = max(start_ms + 1, int(turn.end * 1000))
            if turns and turns[-1].index == index_of[speaker] and turns[-1].end_ms >= start_ms:
                previous = turns[-1]
                turns[-1] = SpeakerTurn(
                    index=previous.index, start_ms=previous.start_ms, end_ms=max(previous.end_ms, end_ms)
                )
                continue
            turns.append(SpeakerTurn(index=index_of[speaker], start_ms=start_ms, end_ms=end_ms))
        progress(1.0)
        return DiarizationResult(turns=turns, provider=self.id, model=self.model, is_mock=False)
