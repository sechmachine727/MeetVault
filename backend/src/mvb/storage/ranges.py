from __future__ import annotations

from collections.abc import Iterator
from dataclasses import dataclass


@dataclass(slots=True, frozen=True)
class RangeSpec:
    start: int
    end: int  # inclusive

    @property
    def length(self) -> int:
        return self.end - self.start + 1


def parse_range_header(value: str | None, size: int) -> RangeSpec | None:
    """Parse a single-range `bytes=…` header. Returns None when absent/unsupported (serves 200)."""
    if not value or not value.strip().lower().startswith("bytes="):
        return None
    spec = value.strip()[len("bytes=") :].strip()
    if "," in spec:  # multi-range: not supported, serve the whole file
        return None
    if "-" not in spec:
        return None
    raw_start, _, raw_end = spec.partition("-")
    try:
        if raw_start == "":  # suffix range: last N bytes
            suffix = int(raw_end)
            if suffix <= 0:
                return None
            start = max(0, size - suffix)
            end = size - 1
        else:
            start = int(raw_start)
            end = int(raw_end) if raw_end else size - 1
    except ValueError:
        return None
    if start >= size or start < 0:
        return RangeSpec(0, -1)  # unsatisfiable -> 416
    end = min(end, size - 1)
    if end < start:
        return RangeSpec(0, -1)
    return RangeSpec(start, end)


def iter_file_range(handle, start: int, end: int, *, chunk: int = 1024 * 256) -> Iterator[bytes]:  # type: ignore[no-untyped-def]
    handle.seek(start)
    remaining = end - start + 1
    while remaining > 0:
        data = handle.read(min(chunk, remaining))
        if not data:
            break
        remaining -= len(data)
        yield data
