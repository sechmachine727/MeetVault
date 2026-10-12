from __future__ import annotations

import logging

from fastapi import APIRouter
from sqlalchemy import text

log = logging.getLogger(__name__)
router = APIRouter(tags=["health"])


@router.get("/healthz")
def healthz() -> dict[str, str]:
    """Liveness: the process answers. No dependencies touched."""
    return {"status": "ok"}


@router.get("/readyz")
def readyz() -> dict[str, object]:
    """Readiness: can we serve? Providers may be mock — that is still ready."""
    from mvb.config import get_settings
    from mvb.db.base import get_engine
    from mvb.providers.registry import describe_providers
    from mvb.storage.local import LocalStorage

    settings = get_settings()
    db_ok = True
    db_error: str | None = None
    try:
        with get_engine().connect() as connection:
            connection.execute(text("SELECT 1"))
    except Exception as exc:
        log.warning("readyz_db_failed")
        db_ok = False
        db_error = f"{type(exc).__name__}: {exc}"

    storage_ok = LocalStorage(settings.media_root).healthy()
    providers = describe_providers(settings)
    ready = db_ok and storage_ok
    body: dict[str, object] = {
        "status": "ready" if ready else "not_ready",
        "checks": {"database": db_ok, "storage": storage_ok},
        "providers": providers,
    }
    if db_error:
        body["db_error"] = db_error
    return body
