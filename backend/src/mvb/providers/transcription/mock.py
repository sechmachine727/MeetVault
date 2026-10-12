from __future__ import annotations

import hashlib
from collections.abc import Callable
from pathlib import Path

from mvb.providers.base import SegmentDraft, TranscriptResult

SENTENCES = [
    "Welcome everyone to the meeting.",
    "Today we are reviewing the project status and the open risks.",
    "The first topic is the release schedule for next month.",
    "We agreed to move the deadline to the twentieth.",
    "Action item: prepare the migration plan before Friday.",
    "The second topic is the customer feedback from last week.",
    "Summarizing: the deadline moves and we need the migration plan.",
]


class MockTranscriber:
    """Deterministic, offline, no AI. Labelled as mock everywhere.

    Rule: mock timings are derived from the REAL media duration, never invented, so a mock
    result can never contradict the audio it claims to describe.
    """

    id = "mock-transcriber"
    is_mock = True

    def __init__(self, segment_seconds: int = 8) -> None:
        self.model = "mock-asr-v1"
        self.segment_seconds = segment_seconds

    def transcribe(
        self,
        wav_path: Path,
        *,
        language: str | None,
        duration_ms: int,
        progress: Callable[[float], None] = lambda _p: None,
    ) -> TranscriptResult:
        lang = language or "auto"
        window = max(1, self.segment_seconds * 1000)
        total = max(duration_ms, window)
        segments: list[SegmentDraft] = []
        start = 0
        index = 0
        while start < total and index < 500:
            end = min(start + window, total)
            text = f"[mock:{lang}] {SENTENCES[index % len(SENTENCES)]}"
            segments.append(SegmentDraft(start_ms=start, end_ms=end, text=text, confidence=1.0))
            start = end
            index += 1
            progress(min(0.99, index * window / total))
        progress(1.0)
        detected = language or "en"
        return TranscriptResult(segments=segments, language=detected, provider=self.id, model=self.model, is_mock=True)

    @staticmethod
    def digest(segments: list[SegmentDraft]) -> str:
        joined = "|".join(f"{s.start_ms}-{s.end_ms}:{s.text}" for s in segments)
        return hashlib.sha256(joined.encode("utf-8")).hexdigest()[:16]
