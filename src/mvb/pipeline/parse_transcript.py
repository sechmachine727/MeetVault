from __future__ import annotations

from dataclasses import dataclass, field

from mvb.api.errors import ApiError


@dataclass(slots=True)
class ParsedSegment:
    text: str
    start_ms: int | None = None
    end_ms: int | None = None
    speaker_label: str = ""
    confidence: float | None = None


@dataclass(slots=True)
class ParsedTranscript:
    segments: list[ParsedSegment] = field(default_factory=list)
    language: str = ""
    source: str = "uploaded"
    has_timings: bool = False  # every segment carries times
    partial_timings: bool = False  # some do


def _as_int(value: object) -> int | None:
    if value is None or isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return int(value)
    if isinstance(value, str) and value.strip().lstrip("-").isdigit():
        return int(value.strip())
    return None


def _first(row: dict, *keys: str) -> object:
    """First key that is present and not None (0 and "" are valid values)."""
    for key in keys:
        if key in row and row[key] is not None:
            return row[key]
    return None


def _as_float(value: object) -> float | None:
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        return float(value)
    if isinstance(value, str):
        try:
            return float(value)
        except ValueError:
            return None
    return None


def _row_to_segment(row: dict) -> ParsedSegment | None:
    text = _first(row, "text", "Text")
    if text is None:
        return None
    text = str(text).strip()
    if not text:
        return None
    speaker = _first(row, "speaker_label", "Speaker", "speaker", "speaker_id") or ""
    return ParsedSegment(
        text=text,
        start_ms=_as_int(_first(row, "start_ms", "startMs")),
        end_ms=_as_int(_first(row, "end_ms", "endMs")),
        speaker_label=str(speaker),
        confidence=_as_float(_first(row, "confidence")),
    )


def parse_transcript_payload(payload: object) -> ParsedTranscript:
    """Accept Format A (segments, optionally with times) and Format B (MeetVault's
    list of Speaker/Text rows). Anything else raises ApiError(400)."""
    rows: list[dict] = []
    language = ""
    source = "uploaded"
    if isinstance(payload, dict):
        if not isinstance(payload.get("segments"), list):
            raise ApiError(400, "unsupported_transcript_format", "Object payload must contain a segments array")
        rows = [row for row in payload["segments"] if isinstance(row, dict)]
        language = str(payload.get("language") or "")
        source = str(payload.get("source") or source)
    elif isinstance(payload, list):
        rows = [row for row in payload if isinstance(row, dict)]
    else:
        raise ApiError(400, "unsupported_transcript_format", "Transcript must be an object or an array")

    segments = [segment for segment in (_row_to_segment(row) for row in rows) if segment is not None]
    if not segments:
        raise ApiError(400, "unsupported_transcript_format", "No usable text segments found in the transcript")

    timed = sum(1 for segment in segments if segment.start_ms is not None and segment.end_ms is not None)
    return ParsedTranscript(
        segments=segments,
        language=language,
        source=source,
        has_timings=timed == len(segments),
        partial_timings=0 < timed < len(segments),
    )
