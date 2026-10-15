from __future__ import annotations

from dataclasses import dataclass

from mvb.pipeline.parse_transcript import ParsedSegment


@dataclass(slots=True)
class AlignedSegment:
    start_ms: int
    end_ms: int
    text: str
    speaker_label: str = ""
    confidence: float | None = None


def align_segments(segments: list[ParsedSegment], duration_ms: int, *, mode: str = "auto") -> list[AlignedSegment]:
    """Fit segment times onto the real media duration.

    auto         : all timed -> scale; none timed -> proportional; mixed -> keep timed, fill gaps
    scale        : rescale the observed span onto [0, duration]
    proportional : distribute [0, duration] by character weight, contiguous
    """
    if not segments:
        return []
    total = max(1, int(duration_ms))
    if mode == "auto":
        timed = [s for s in segments if s.start_ms is not None and s.end_ms is not None]
        if len(timed) == len(segments):
            mode = "scale"
        elif not timed:
            mode = "proportional"
        else:
            mode = "mixed"
    if mode == "scale":
        return _scale(segments, total)
    if mode == "proportional":
        return _proportional(segments, total)
    if mode == "mixed":
        return _mixed(segments, total)
    raise ValueError(f"unknown alignment mode '{mode}'")


def _scale(segments: list[ParsedSegment], total: int) -> list[AlignedSegment]:
    observed = max((s.end_ms or 0) for s in segments)
    factor = total / observed if observed > 0 else 1.0
    out: list[AlignedSegment] = []
    for segment in segments:
        start = round((segment.start_ms or 0) * factor)
        end = round((segment.end_ms or 0) * factor)
        start = max(0, min(start, total - 1))
        end = max(start + 1, min(end, total))
        out.append(AlignedSegment(start, end, segment.text, segment.speaker_label, segment.confidence))
    return out


def _weights(segments: list[ParsedSegment]) -> list[int]:
    return [max(1, len(s.text.strip())) for s in segments]


def _proportional(segments: list[ParsedSegment], total: int) -> list[AlignedSegment]:
    weights = _weights(segments)
    weight_sum = sum(weights)
    out: list[AlignedSegment] = []
    cursor = 0
    for index, (segment, weight) in enumerate(zip(segments, weights, strict=True)):
        if index == len(segments) - 1:
            end = total
        else:
            end = cursor + max(1, round(total * weight / weight_sum))
            end = min(end, total - (len(segments) - index - 1))
        end = max(cursor + 1, end)
        end = min(end, total)
        out.append(AlignedSegment(cursor, end, segment.text, segment.speaker_label, segment.confidence))
        cursor = end
    return out


def _mixed(segments: list[ParsedSegment], total: int) -> list[AlignedSegment]:
    """Keep the real times; spread each untimed run proportionally inside its surrounding gap."""
    out: list[AlignedSegment | None] = [None] * len(segments)
    known = [i for i, s in enumerate(segments) if s.start_ms is not None and s.end_ms is not None]
    for i in known:
        segment = segments[i]
        start = max(0, min(int(segment.start_ms or 0), total - 1))
        end = max(start + 1, min(int(segment.end_ms or 0), total))
        out[i] = AlignedSegment(start, end, segment.text, segment.speaker_label, segment.confidence)

    for position, index in enumerate(known):
        previous = out[known[position - 1]] if position > 0 else None
        gap_start = previous.end_ms if previous is not None else 0
        # The untimed run BEFORE this known segment ends where the known segment starts.
        current = out[index]
        gap_end = current.start_ms if current is not None else gap_start
        first_unset = known[position - 1] + 1 if position > 0 else 0
        unknown = [i for i in range(first_unset, index) if out[i] is None]
        if not unknown:
            continue
        _fill_run(out, segments, unknown, gap_start, max(gap_end, gap_start + len(unknown)))

    tail_unknown = list(range(known[-1] + 1, len(segments))) if known else list(range(len(segments)))
    if tail_unknown:
        last = out[known[-1]] if known else None
        cursor = last.end_ms if last is not None else 0
        _fill_run(out, segments, tail_unknown, cursor, total)
    return [segment for segment in out if segment is not None]


def _fill_run(
    out: list[AlignedSegment | None],
    segments: list[ParsedSegment],
    indexes: list[int],
    start: int,
    end: int,
) -> None:
    """Distribute [start, end) across the given slots by character weight, contiguous."""
    end = max(end, start + len(indexes))
    weights = _weights([segments[i] for i in indexes])
    weight_sum = sum(weights)
    cursor = start
    span = end - start
    for offset, (slot, weight) in enumerate(zip(indexes, weights, strict=True)):
        stop = end if offset == len(indexes) - 1 else cursor + max(1, round(span * weight / weight_sum))
        stop = min(stop, end)
        stop = max(cursor + 1, stop)
        out[slot] = AlignedSegment(
            cursor,
            stop,
            segments[slot].text,
            segments[slot].speaker_label,
            segments[slot].confidence,
        )
        cursor = stop
