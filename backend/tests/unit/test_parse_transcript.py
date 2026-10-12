from __future__ import annotations

import pytest

from mvb.api.errors import ApiError
from mvb.pipeline.parse_transcript import parse_transcript_payload


def test_format_a_with_timings() -> None:
    payload = {
        "source": "live_export",
        "language": "en",
        "segments": [{"start_ms": 0, "end_ms": 1000, "text": "hi", "speaker_label": "Speaker 1"}],
    }
    parsed = parse_transcript_payload(payload)
    assert parsed.has_timings is True
    assert parsed.segments[0].start_ms == 0
    assert parsed.segments[0].speaker_label == "Speaker 1"
    assert parsed.language == "en"
    assert parsed.source == "live_export"


def test_format_b_meetvault_payload_has_no_timings() -> None:
    payload = [
        {"Speaker": "Speaker 1", "Text": "Welcome everyone."},
        {"Speaker": "Speaker 1", "Text": "Let's start."},
    ]
    parsed = parse_transcript_payload(payload)
    assert parsed.has_timings is False
    assert parsed.partial_timings is False
    assert [s.text for s in parsed.segments] == ["Welcome everyone.", "Let's start."]
    assert parsed.segments[0].speaker_label == "Speaker 1"


def test_bare_list_of_segment_rows_is_accepted() -> None:
    payload = [{"id": "m-1-s1-0", "speaker_id": "spk_1", "start_ms": 10, "end_ms": 20, "text": "x", "is_final": 1}]
    parsed = parse_transcript_payload(payload)
    assert parsed.has_timings is True
    assert parsed.segments[0].speaker_label == "spk_1"


def test_lowercase_keys_also_work() -> None:
    parsed = parse_transcript_payload([{"text": "hello", "start_ms": 5, "end_ms": 15}])
    assert parsed.segments[0].text == "hello"
    assert parsed.segments[0].end_ms == 15


def test_mixed_timings_are_reported_as_partial() -> None:
    parsed = parse_transcript_payload([{"text": "a", "start_ms": 0, "end_ms": 10}, {"text": "b"}])
    assert parsed.has_timings is False
    assert parsed.partial_timings is True


@pytest.mark.parametrize("payload", ["just a string", {"nope": 1}, [], [{"unrelated": True}], 42])
def test_unsupported_payloads_raise_400(payload: object) -> None:
    with pytest.raises(ApiError) as excinfo:
        parse_transcript_payload(payload)
    assert excinfo.value.status_code == 400
    assert excinfo.value.code == "unsupported_transcript_format"


def test_empty_text_rows_are_dropped() -> None:
    parsed = parse_transcript_payload([{"Speaker": "S", "Text": "  "}, {"Speaker": "S", "Text": "real"}])
    assert [s.text for s in parsed.segments] == ["real"]


def test_confidence_is_parsed_when_present() -> None:
    parsed = parse_transcript_payload([{"text": "x", "confidence": "0.5"}])
    assert parsed.segments[0].confidence == 0.5
