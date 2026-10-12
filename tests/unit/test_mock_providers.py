from __future__ import annotations

from pathlib import Path

import pytest

from mvb.providers.analysis.mock import MockAnalyzer
from mvb.providers.analysis.prompts import KIND_TO_SCHEMA, kind_schema
from mvb.providers.diarization.mock import MockDiarizer
from mvb.providers.transcription.mock import SENTENCES, MockTranscriber

# ---- transcriber -------------------------------------------------------------------


def test_mock_transcriber_is_deterministic_and_bounded() -> None:
    transcriber = MockTranscriber(segment_seconds=10)
    first = transcriber.transcribe(Path("x.wav"), language="en", duration_ms=25_000)
    second = transcriber.transcribe(Path("x.wav"), language="en", duration_ms=25_000)
    assert first == second
    assert first.is_mock is True
    assert first.provider == "mock-transcriber"
    assert [s.start_ms for s in first.segments] == [0, 10_000, 20_000]
    assert first.segments[-1].end_ms == 25_000
    for draft in first.segments:
        assert draft.start_ms < draft.end_ms
        assert draft.text.startswith("[mock:en]")
        assert draft.text.removeprefix("[mock:en] ").strip() in SENTENCES


def test_mock_respects_language_argument() -> None:
    result = MockTranscriber().transcribe(Path("x.wav"), language="ja", duration_ms=5_000)
    assert result.language == "ja"
    assert result.segments[0].text.startswith("[mock:ja]")


def test_mock_covers_short_audio() -> None:
    result = MockTranscriber(segment_seconds=8).transcribe(Path("x.wav"), language=None, duration_ms=1_200)
    assert len(result.segments) == 1
    assert result.segments[0].end_ms == 8_000  # min window: never zero-length


def test_progress_reaches_one_and_is_monotonic() -> None:
    seen: list[float] = []
    MockTranscriber().transcribe(Path("x.wav"), language="en", duration_ms=30_000, progress=seen.append)
    assert seen[-1] == 1.0
    assert seen == sorted(seen)


# ---- diarizer ----------------------------------------------------------------------


def test_diarizer_alternates_and_covers_duration() -> None:
    result = MockDiarizer(turn_seconds=10).diarize(Path("x.wav"), duration_ms=25_000)
    assert result.is_mock is True
    assert [t.index for t in result.turns] == [0, 1, 0]
    assert result.turns[0].start_ms == 0
    assert result.turns[-1].end_ms == 25_000
    assert all(turn.start_ms < turn.end_ms for turn in result.turns)


def test_diarizer_single_speaker_request() -> None:
    result = MockDiarizer().diarize(Path("x.wav"), duration_ms=30_000, num_speakers=1)
    assert {t.index for t in result.turns} == {0}


def test_diarizer_is_deterministic() -> None:
    a = MockDiarizer().diarize(Path("x.wav"), duration_ms=40_000)
    b = MockDiarizer().diarize(Path("x.wav"), duration_ms=40_000)
    assert a == b


# ---- analyzer ----------------------------------------------------------------------


@pytest.mark.parametrize("kind", sorted(KIND_TO_SCHEMA))
def test_mock_analyzer_returns_schema_shaped_payload(kind: str) -> None:
    result = MockAnalyzer().complete_json(system="s", user="u", schema=kind_schema(kind), timeout_s=5)
    assert result.is_mock is True
    assert result.provider == "mock-analyzer"
    payload = result.payload
    if kind == "summary":
        assert payload["text"].startswith("[mock]")
        assert all(bullet.startswith("[mock]") for bullet in payload["bullets"])
    elif kind == "agenda":
        assert payload["items"][0]["start_ms"] < payload["items"][0]["end_ms"]
    elif kind == "qa":
        assert "answer" in payload
        assert payload["answer"].startswith("[mock]")
    else:
        assert payload


def test_extract_schema_returns_both_halves() -> None:
    from mvb.providers.analysis.prompts import EXTRACT_SCHEMA

    payload = MockAnalyzer().complete_json(system="s", user="u", schema=EXTRACT_SCHEMA, timeout_s=5).payload
    assert payload["decisions"] and payload["action_items"]


def test_mock_analyzer_is_deterministic() -> None:
    analyzer = MockAnalyzer()
    schema = kind_schema("summary")
    assert analyzer.complete_json(system="s", user="u", schema=schema, timeout_s=5) == analyzer.complete_json(
        system="s", user="u", schema=schema, timeout_s=5
    )


def test_mock_analyzer_distinct_inputs_differ() -> None:
    analyzer = MockAnalyzer()
    a = analyzer.complete_json(system="s", user="one", schema=kind_schema("summary"), timeout_s=5).payload["text"]
    b = analyzer.complete_json(system="s", user="two", schema=kind_schema("summary"), timeout_s=5).payload["text"]
    assert a != b


def test_mock_analyzer_rejects_unknown_kind() -> None:
    with pytest.raises(ValueError, match="no builder"):
        MockAnalyzer().complete_json(system="s", user="u", schema={"x-kind": "teleport"}, timeout_s=5)
