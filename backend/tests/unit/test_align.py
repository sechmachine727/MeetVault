from __future__ import annotations

import pytest

from mvb.pipeline.align import align_segments
from mvb.pipeline.parse_transcript import ParsedSegment


def _check_invariants(segments: list, duration: int) -> None:  # type: ignore[type-arg]
    previous_end = -1
    for segment in segments:
        assert 0 <= segment.start_ms < segment.end_ms <= duration
        assert segment.start_ms >= previous_end - 1, "segments must not overlap"
        previous_end = segment.end_ms


def test_proportional_covers_the_whole_duration() -> None:
    rows = [ParsedSegment(text="a" * 10), ParsedSegment(text="b" * 30), ParsedSegment(text="c" * 10)]
    result = align_segments(rows, 100_000, mode="proportional")
    assert result[0].start_ms == 0
    assert result[-1].end_ms == 100_000
    assert result[1].end_ms - result[1].start_ms > result[0].end_ms - result[0].start_ms
    _check_invariants(result, 100_000)


def test_scale_rescales_existing_times() -> None:
    rows = [
        ParsedSegment(text="a", start_ms=0, end_ms=5_000),
        ParsedSegment(text="b", start_ms=5_000, end_ms=10_000),
    ]
    result = align_segments(rows, 60_000, mode="scale")
    assert result[-1].end_ms == 60_000
    assert result[0].end_ms == 30_000
    _check_invariants(result, 60_000)


def test_auto_picks_proportional_for_untimed() -> None:
    result = align_segments([ParsedSegment(text="one"), ParsedSegment(text="two")], 10_000)
    assert result[0].start_ms == 0
    assert result[-1].end_ms == 10_000


def test_auto_picks_scale_for_fully_timed() -> None:
    result = align_segments([ParsedSegment(text="one", start_ms=0, end_ms=1_000)], 2_000)
    assert result[0].end_ms == 2_000


def test_mixed_keeps_timed_and_fills_gaps() -> None:
    rows = [
        ParsedSegment(text="a", start_ms=0, end_ms=1_000),
        ParsedSegment(text="b"),
        ParsedSegment(text="c"),
        ParsedSegment(text="d", start_ms=9_000, end_ms=10_000),
    ]
    result = align_segments(rows, 10_000, mode="auto")
    _check_invariants(result, 10_000)
    assert (result[0].start_ms, result[0].end_ms) == (0, 1_000)
    assert (result[3].start_ms, result[3].end_ms) == (9_000, 10_000)
    assert result[1].start_ms == 1_000
    assert result[2].end_ms == 9_000


def test_mixed_with_leading_untimed_run() -> None:
    rows = [
        ParsedSegment(text="a"),
        ParsedSegment(text="b"),
        ParsedSegment(text="c", start_ms=8_000, end_ms=9_000),
    ]
    result = align_segments(rows, 10_000, mode="auto")
    _check_invariants(result, 10_000)
    assert result[-1].end_ms == 9_000
    assert result[0].start_ms == 0


@pytest.mark.parametrize("duration", [0, 1, 999])
def test_degenerate_durations_do_not_crash(duration: int) -> None:
    """A media shorter than its segment count cannot give every segment positive length:
    require bounded, monotonic times and no crash."""
    result = align_segments([ParsedSegment(text="x"), ParsedSegment(text="y")], duration)
    assert len(result) == 2
    total = max(1, duration)
    previous_end = 0
    for segment in result:
        assert 0 <= segment.start_ms <= segment.end_ms <= max(total, 2)
        assert segment.start_ms >= previous_end - 1
        previous_end = segment.end_ms


@pytest.mark.parametrize("duration", [2, 3, 10_000])
def test_non_degenerate_durations_keep_strict_invariants(duration: int) -> None:
    result = align_segments([ParsedSegment(text="x"), ParsedSegment(text="y")], duration)
    assert all(segment.start_ms < segment.end_ms <= duration for segment in result)


def test_empty_input() -> None:
    assert align_segments([], 1000) == []


def test_unknown_mode_raises() -> None:
    with pytest.raises(ValueError):
        align_segments([ParsedSegment(text="x")], 1000, mode="teleport")


def test_single_segment_spans_whole_duration() -> None:
    result = align_segments([ParsedSegment(text="only")], 30_000)
    assert (result[0].start_ms, result[0].end_ms) == (0, 30_000)
