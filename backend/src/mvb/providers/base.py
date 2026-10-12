from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol


@dataclass(slots=True, frozen=True)
class SegmentDraft:
    start_ms: int
    end_ms: int
    text: str
    confidence: float | None = None


@dataclass(slots=True, frozen=True)
class TranscriptResult:
    segments: list[SegmentDraft]
    language: str
    provider: str
    model: str
    is_mock: bool


@dataclass(slots=True, frozen=True)
class SpeakerTurn:
    index: int
    start_ms: int
    end_ms: int


@dataclass(slots=True, frozen=True)
class DiarizationResult:
    turns: list[SpeakerTurn]
    provider: str
    model: str
    is_mock: bool


@dataclass(slots=True, frozen=True)
class LLMResult:
    payload: dict
    provider: str
    model: str
    is_mock: bool


class Transcriber(Protocol):
    id: str

    def transcribe(
        self,
        wav_path: Path,
        *,
        language: str | None,
        duration_ms: int,
        progress: Callable[[float], None] = lambda _p: None,
    ) -> TranscriptResult: ...


class Diarizer(Protocol):
    id: str

    def diarize(
        self,
        wav_path: Path,
        *,
        duration_ms: int,
        num_speakers: int | None = None,
        progress: Callable[[float], None] = lambda _p: None,
    ) -> DiarizationResult: ...


class Analyzer(Protocol):
    id: str

    def complete_json(self, *, system: str, user: str, schema: dict, timeout_s: float) -> LLMResult: ...
