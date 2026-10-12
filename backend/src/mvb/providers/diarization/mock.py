from __future__ import annotations

from collections.abc import Callable
from pathlib import Path

from mvb.providers.base import DiarizationResult, SpeakerTurn


class MockDiarizer:
    """Deterministic two-speaker alternation every `turn_seconds`, bounded by the real duration."""

    id = "mock-diarizer"
    is_mock = True

    def __init__(self, turn_seconds: int = 20) -> None:
        self.model = "mock-diar-v1"
        self.turn_seconds = turn_seconds

    def diarize(
        self,
        wav_path: Path,
        *,
        duration_ms: int,
        num_speakers: int | None = None,
        progress: Callable[[float], None] = lambda _p: None,
    ) -> DiarizationResult:
        window = max(1000, self.turn_seconds * 1000)
        total = max(duration_ms, window)
        speakers = max(1, num_speakers or 2)
        turns: list[SpeakerTurn] = []
        start = 0
        index = 0
        while start < total:
            end = min(start + window, total)
            turns.append(SpeakerTurn(index=index % speakers, start_ms=start, end_ms=end))
            start = end
            index += 1
            progress(min(0.99, start / total))
        progress(1.0)
        return DiarizationResult(turns=turns, provider=self.id, model=self.model, is_mock=True)
