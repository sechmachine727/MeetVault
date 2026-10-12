from __future__ import annotations

import uuid
from collections.abc import Iterator
from typing import Annotated, BinaryIO

from fastapi import APIRouter, Query, Request
from fastapi.responses import StreamingResponse
from sqlalchemy.orm import Session

from mvb.api.deps import ApiKeyId, DbSession, SettingsDep
from mvb.api.errors import ApiError
from mvb.db.models import MediaAsset
from mvb.schemas.common import OkResponse
from mvb.schemas.media import MediaAssetOut
from mvb.storage.local import LocalStorage
from mvb.storage.ranges import iter_file_range, parse_range_header

router = APIRouter(tags=["media"])

CHUNK = 1024 * 256


def get_asset(db: Session, asset_id: uuid.UUID) -> MediaAsset:
    asset = db.get(MediaAsset, asset_id)
    if asset is None:
        raise ApiError(404, "not_found", "Media asset not found", {"asset_id": str(asset_id)})
    return asset


def _stream(handle: BinaryIO, start: int, end: int) -> Iterator[bytes]:
    try:
        yield from iter_file_range(handle, start, end, chunk=CHUNK)
    finally:
        handle.close()


@router.get("/media/{asset_id}/info", response_model=MediaAssetOut)
def media_info(asset_id: uuid.UUID, _: ApiKeyId, db: DbSession) -> MediaAssetOut:
    """Metadata for one stored recording (no bytes)."""
    return MediaAssetOut.model_validate(get_asset(db, asset_id))


@router.get("/media/{asset_id}")
def download_media(
    asset_id: uuid.UUID,
    request: Request,
    _: ApiKeyId,
    db: DbSession,
    settings: SettingsDep,
    disposition: Annotated[str, Query(pattern="^(inline|attachment)$")] = "inline",
) -> StreamingResponse:
    """Stream the stored file, honouring a single `Range` header (206 Partial Content) so
    clients can seek inside recordings."""
    asset = get_asset(db, asset_id)
    storage = LocalStorage(settings.media_root)
    path = storage.path_for(asset.storage_key)
    if not path.exists():
        raise ApiError(404, "media_not_found", "Media file is missing on disk", {"key": asset.storage_key})
    size = path.stat().st_size
    headers = {
        "Accept-Ranges": "bytes",
        "Content-Disposition": f'{disposition}; filename="{asset.original_name}"',
        "Content-Type": asset.content_type or "application/octet-stream",
    }
    spec = parse_range_header(request.headers.get("range"), size)
    if spec is None:
        headers["Content-Length"] = str(size)
        return StreamingResponse(_stream(path.open("rb"), 0, max(0, size - 1)), headers=headers)
    if spec.end < spec.start:  # unsatisfiable range
        raise ApiError(416, "range_not_satisfiable", "Requested range is not satisfiable", {"size": size})
    headers["Content-Range"] = f"bytes {spec.start}-{spec.end}/{size}"
    headers["Content-Length"] = str(spec.length)
    return StreamingResponse(_stream(path.open("rb"), spec.start, spec.end), status_code=206, headers=headers)


@router.delete("/media/{asset_id}", response_model=OkResponse)
def delete_media(asset_id: uuid.UUID, _: ApiKeyId, db: DbSession, settings: SettingsDep) -> OkResponse:
    """Remove one stored recording (file and row)."""
    asset = get_asset(db, asset_id)
    LocalStorage(settings.media_root).delete(asset.storage_key)
    db.delete(asset)
    db.commit()
    return OkResponse(detail={"asset_id": str(asset_id)})
