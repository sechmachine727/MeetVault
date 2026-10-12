from __future__ import annotations

import hmac
import logging
from collections.abc import Iterator
from typing import Annotated

from fastapi import Depends, Header, Query, Request
from sqlalchemy.orm import Session

from mvb.api.errors import ApiError
from mvb.config import Settings, get_settings
from mvb.db.base import get_session_factory

log = logging.getLogger(__name__)


def settings_dep() -> Settings:
    return get_settings()


def db_dep() -> Iterator[Session]:
    session = get_session_factory()()
    try:
        yield session
    finally:
        session.close()


def require_api_key(
    request: Request,
    settings: Annotated[Settings, Depends(settings_dep)],
    authorization: Annotated[str | None, Header()] = None,
) -> str:
    """Static bearer auth. Returns a non-secret key id (first 8 chars) for logging."""
    if not authorization or not authorization.lower().startswith("bearer "):
        raise ApiError(401, "unauthorized", "Missing bearer token")
    presented = authorization.split(" ", 1)[1].strip()
    for key in settings.api_keys:
        if hmac.compare_digest(presented, key):
            request.state.key_id = key[:8]
            return key[:8]
    raise ApiError(401, "unauthorized", "Invalid API key")


def pagination(
    limit: Annotated[int, Query(ge=1, le=200)] = 50,
    offset: Annotated[int, Query(ge=0)] = 0,
) -> tuple[int, int]:
    return limit, offset


DbSession = Annotated[Session, Depends(db_dep)]
SettingsDep = Annotated[Settings, Depends(settings_dep)]
ApiKeyId = Annotated[str, Depends(require_api_key)]
Pagination = Annotated[tuple[int, int], Depends(pagination)]
