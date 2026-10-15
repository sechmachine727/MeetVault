from __future__ import annotations

from fastapi import APIRouter

from mvb.api.deps import CurrentUser
from mvb.config import get_settings
from mvb.providers.registry import describe_providers

router = APIRouter(tags=["system"])

MIGRATION_HEAD = "20261014_0002"


@router.get("/system/info")
def system_info(_user: CurrentUser) -> dict[str, object]:
    settings = get_settings()
    return {
        "version": "0.1.0",
        "env": settings.env,
        "migration_head": MIGRATION_HEAD,
        "media_root": str(settings.media_root),
        "max_upload_mb": settings.max_upload_mb,
    }


@router.get("/system/providers")
def system_providers(_user: CurrentUser) -> dict[str, object]:
    return describe_providers(get_settings())
