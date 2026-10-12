from __future__ import annotations

import importlib.util
from collections.abc import Callable
from pathlib import Path

from mvb.providers.base import SegmentDraft, TranscriptResult


def is_available() -> bool:
    """True only when the heavy package is importable. Must not import it."""
    return importlib.util.find_spec("faster_whisper") is not None


class FasterWhisperTranscriber:
    """Real offline ASR. Imported and constructed lazily; the registry falls back to the
    mock when the package is absent or the model cannot be loaded."""

    id = "faster_whisper"
    is_mock = False

    def __init__(self, model_size: str = "small", device: str = "cpu", compute_type: str = "int8") -> None:
        self.model = model_size
        self.device = device
        self.compute_type = compute_type
        self._model = None

    def _ensure_model(self):  # type: ignore[no-untyped-def]
        if self._model is None:
            from faster_whisper import WhisperModel

            self._model = WhisperModel(self.model, device=self.device, compute_type=self.compute_type)
        return self._model

    def transcribe(
        self,
        wav_path: Path,
        *,
        language: str | None,
        duration_ms: int,
        progress: Callable[[float], None] = lambda _p: None,
    ) -> TranscriptResult:
        model = self._ensure_model()
        segments, info = model.transcribe(str(wav_path), language=language, vad_filter=True)
        drafts: list[SegmentDraft] = []
        for segment in segments:
            text = (segment.text or "").strip()
            if not text:
                continue
            start_ms = int(segment.start * 1000)
            end_ms = max(start_ms + 1, int(segment.end * 1000))
            drafts.append(SegmentDraft(start_ms=start_ms, end_ms=end_ms, text=text))
            if duration_ms > 0:
                progress(min(0.99, end_ms / duration_ms))
        progress(1.0)
        return TranscriptResult(
            segments=drafts,
            language=getattr(info, "language", language or ""),
            provider=self.id,
            model=self.model,
            is_mock=False,
        )
