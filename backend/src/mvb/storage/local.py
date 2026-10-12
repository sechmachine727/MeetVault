from __future__ import annotations

import hashlib
import logging
import shutil
from pathlib import Path
from typing import BinaryIO

from mvb.api.errors import ApiError

log = logging.getLogger(__name__)
CHUNK = 1024 * 1024


class LocalStorage:
    """Disk-backed storage under a single root. Keys cannot escape the root."""

    def __init__(self, root: Path) -> None:
        self.root = Path(root).resolve()
        self.root.mkdir(parents=True, exist_ok=True)

    def _resolve(self, key: str) -> Path:
        candidate = (self.root / key).resolve()
        if candidate != self.root and self.root not in candidate.parents:
            raise ApiError(400, "invalid_storage_key", "Storage key escapes the media root", {"key": key})
        return candidate

    def put_stream(self, key: str, source: BinaryIO, *, max_bytes: int) -> tuple[int, str]:
        destination = self._resolve(key)
        destination.parent.mkdir(parents=True, exist_ok=True)
        digest = hashlib.sha256()
        written = 0
        tmp = destination.with_suffix(destination.suffix + ".part")
        try:
            with tmp.open("wb") as handle:
                while True:
                    chunk = source.read(CHUNK)
                    if not chunk:
                        break
                    written += len(chunk)
                    if written > max_bytes:
                        raise ApiError(
                            413,
                            "payload_too_large",
                            "Upload exceeds the configured limit",
                            {"max_bytes": max_bytes},
                        )
                    digest.update(chunk)
                    handle.write(chunk)
            tmp.replace(destination)
        except Exception:
            tmp.unlink(missing_ok=True)
            raise
        return written, digest.hexdigest()

    def open(self, key: str) -> BinaryIO:
        path = self._resolve(key)
        if not path.exists():
            raise ApiError(404, "media_not_found", "Media file is missing on disk", {"key": key})
        return path.open("rb")

    def exists(self, key: str) -> bool:
        return self._resolve(key).exists()

    def size(self, key: str) -> int:
        return self._resolve(key).stat().st_size

    def delete(self, key: str) -> None:
        self._resolve(key).unlink(missing_ok=True)

    def path_for(self, key: str) -> Path:
        return self._resolve(key)

    def healthy(self) -> bool:
        try:
            probe = self.root / ".health"
            probe.write_text("ok", encoding="utf-8")
            probe.unlink(missing_ok=True)
            return True
        except OSError:
            return False

    def delete_meeting_dir(self, meeting_id: str) -> None:
        shutil.rmtree(self.root / str(meeting_id), ignore_errors=True)
