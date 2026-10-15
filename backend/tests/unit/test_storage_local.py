from __future__ import annotations

import io
from pathlib import Path

import pytest

from mvb.api.errors import ApiError
from mvb.storage.local import LocalStorage


@pytest.fixture
def storage(tmp_path: Path) -> LocalStorage:
    return LocalStorage(tmp_path / "media")


def test_put_and_read_roundtrip(storage: LocalStorage) -> None:
    written, digest = storage.put_stream("m/1/a.wav", io.BytesIO(b"hello"), max_bytes=100)
    assert written == 5
    assert len(digest) == 64
    assert storage.size("m/1/a.wav") == 5
    with storage.open("m/1/a.wav") as handle:
        assert handle.read() == b"hello"


def test_oversize_is_rejected_and_leaves_no_partial(storage: LocalStorage) -> None:
    with pytest.raises(ApiError) as excinfo:
        storage.put_stream("m/1/big.bin", io.BytesIO(b"x" * 50), max_bytes=10)
    assert excinfo.value.status_code == 413
    assert not (storage.root / "m/1/big.bin").exists()
    assert not list(storage.root.rglob("*.part"))


@pytest.mark.parametrize("key", ["../escape.wav", "a/../../escape.wav", "/abs/escape.wav"])
def test_path_traversal_is_refused(storage: LocalStorage, key: str) -> None:
    with pytest.raises(ApiError):
        storage.put_stream(key, io.BytesIO(b"x"), max_bytes=10)


def test_open_missing_file_is_404(storage: LocalStorage) -> None:
    with pytest.raises(ApiError) as excinfo:
        storage.open("nope/missing.bin")
    assert excinfo.value.status_code == 404


def test_delete_is_idempotent(storage: LocalStorage) -> None:
    storage.delete("nope/missing.bin")
    storage.put_stream("m/x.bin", io.BytesIO(b"1"), max_bytes=10)
    storage.delete("m/x.bin")
    assert not storage.exists("m/x.bin")


def test_healthy_on_writable_root(storage: LocalStorage) -> None:
    assert storage.healthy() is True


def test_delete_meeting_dir(storage: LocalStorage) -> None:
    storage.put_stream("abc/one.wav", io.BytesIO(b"1"), max_bytes=10)
    storage.put_stream("abc/nested/two.wav", io.BytesIO(b"2"), max_bytes=10)
    storage.delete_meeting_dir("abc")
    assert not (storage.root / "abc").exists()
