from __future__ import annotations

import logging

from mvb.config import Settings
from mvb.providers.analysis.mock import MockAnalyzer
from mvb.providers.diarization.mock import MockDiarizer
from mvb.providers.transcription.mock import MockTranscriber

log = logging.getLogger(__name__)


def _fallback(capability: str, requested: str, reason: str) -> None:
    log.warning("provider_fallback capability=%s requested=%s reason=%s", capability, requested, reason)


def build_transcriber(settings: Settings):  # type: ignore[no-untyped-def]
    requested = settings.transcriber
    if requested == "faster_whisper":
        try:
            from mvb.providers.transcription import faster_whisper as module

            if module.is_available():
                return module.FasterWhisperTranscriber(model_size=settings.transcriber_model)
            _fallback("transcriber", requested, "package not installed")
        except Exception as exc:  # provider failure must never break startup
            _fallback("transcriber", requested, f"{type(exc).__name__}: {exc}")
    return MockTranscriber()


def build_diarizer(settings: Settings):  # type: ignore[no-untyped-def]
    requested = settings.diarizer
    if requested == "pyannote":
        try:
            from mvb.providers.diarization import pyannote as module

            if module.is_available():
                return module.PyannoteDiarizer(model_id=settings.diarizer_model)
            _fallback("diarizer", requested, "package or HF token missing")
        except Exception as exc:
            _fallback("diarizer", requested, f"{type(exc).__name__}: {exc}")
    return MockDiarizer()


def build_analyzer(settings: Settings):  # type: ignore[no-untyped-def]
    requested = settings.analyzer
    if requested == "lmstudio":
        try:
            from mvb.providers.analysis.lmstudio import LmStudioAnalyzer

            analyzer = LmStudioAnalyzer(
                base_url=settings.lmstudio_base_url, api_key=settings.lmstudio_api_key, model=settings.analyzer_model
            )
            if analyzer.ping():
                return analyzer
            _fallback("analyzer", requested, "lm studio unreachable or no model served")
        except Exception as exc:
            _fallback("analyzer", requested, f"{type(exc).__name__}: {exc}")
    if requested == "opencode-go":
        try:
            from mvb.providers.analysis.opencode_go import OpenCodeGoAnalyzer

            analyzer = OpenCodeGoAnalyzer(
                base_url=settings.opencode_go_base_url,
                api_key=settings.opencode_go_api_key,
                model=settings.analyzer_model,
            )
            if analyzer.ping():
                return analyzer
            _fallback("analyzer", requested, "opencode-go unreachable or model not served")
        except Exception as exc:
            _fallback("analyzer", requested, f"{type(exc).__name__}: {exc}")
    return MockAnalyzer()


def _describe(requested: str, provider: object, reason: str) -> dict[str, object]:
    return {
        "requested": requested,
        "effective": getattr(provider, "id", "mock"),
        "is_mock": getattr(provider, "is_mock", True),
        "model": getattr(provider, "model", ""),
        "reason": reason,
    }


def describe_providers(settings: Settings) -> dict[str, dict[str, object]]:
    """The truth about what will actually run. Never raises."""
    out: dict[str, dict[str, object]] = {}
    for capability, builder, requested in (
        ("transcriber", build_transcriber, settings.transcriber),
        ("diarizer", build_diarizer, settings.diarizer),
        ("analyzer", build_analyzer, settings.analyzer),
    ):
        try:
            provider = builder(settings)
        except Exception as exc:  # defence in depth; builders already never raise
            out[capability] = {
                "requested": requested,
                "effective": "mock",
                "is_mock": True,
                "model": "",
                "reason": f"builder crashed: {type(exc).__name__}: {exc}",
            }
            continue
        effective = str(getattr(provider, "id", "mock"))
        if effective.startswith("mock"):
            reason = "requested provider unavailable" if requested != "mock" else "mock selected by configuration"
        else:
            reason = "ok"
        out[capability] = _describe(requested, provider, reason)
    return out
