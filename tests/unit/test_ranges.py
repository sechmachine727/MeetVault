from __future__ import annotations

from pathlib import Path

import pytest

from mvb.storage.ranges import RangeSpec, iter_file_range, parse_range_header

SIZE = 1000


@pytest.mark.parametrize(
    ("header", "expected"),
    [
        (None, None),
        ("", None),
        ("items=0-10", None),
        ("bytes=0-99", RangeSpec(0, 99)),
        ("bytes=0-", RangeSpec(0, 999)),
        ("bytes=-100", RangeSpec(900, 999)),
        ("bytes=500-2000", RangeSpec(500, 999)),
        ("bytes=0-0", RangeSpec(0, 0)),
        ("bytes=abc", None),
        ("bytes=0-10,20-30", None),
        ("bytes=1000-1010", RangeSpec(0, -1)),
        ("bytes=-0", None),
    ],
)
def test_parse_range_header(header: str | None, expected: RangeSpec | None) -> None:
    assert parse_range_header(header, SIZE) == expected


def test_range_length() -> None:
    assert RangeSpec(0, 9).length == 10
    assert RangeSpec(0, -1).length == 0


def test_iter_file_range_reads_exact_bytes(tmp_path: Path) -> None:
    path = tmp_path / "f.bin"
    path.write_bytes(bytes(range(256)) * 4)
    with path.open("rb") as handle:
        data = b"".join(iter_file_range(handle, 10, 19, chunk=3))
    assert data == bytes(range(256))[10:20]


def test_iter_file_range_stops_at_eof(tmp_path: Path) -> None:
    path = tmp_path / "f.bin"
    path.write_bytes(b"abc")
    with path.open("rb") as handle:
        assert b"".join(iter_file_range(handle, 1, 99)) == b"bc"
