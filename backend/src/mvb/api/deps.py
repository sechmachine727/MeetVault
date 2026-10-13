from __future__ import annotations

import hmac
import logging
from collections.abc import Iterator
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from typing import Annotated

from fastapi import Depends, Header, Query, Request
from sqlalchemy import select
from sqlalchemy.orm import Session

from mvb.api.errors import ApiError
from mvb.config import Settings, get_settings
from mvb.db.base import get_session_factory
from mvb.db.models import AuthToken, User
from mvb.security import token_hash

log = logging.getLogger(__name__)


def _aware(dt: datetime) -> datetime:
    """SQLite returns naive datetimes for DateTime(timezone=True); treat them as UTC."""
    return dt if dt.tzinfo is not None else dt.replace(tzinfo=UTC)


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


@dataclass
class Principal:
    user: User
    token: AuthToken


def resolve_principal(
    request: Request,
    db: Annotated[Session, Depends(db_dep)],
    authorization: Annotated[str | None, Header()] = None,
) -> Principal:
    """Bearer session-token auth.

    401 `unauthorized`  — missing/malformed header or unknown token.
    401 `session_expired` — token exists but is expired or revoked.
    """
    if not authorization or not authorization.lower().startswith("bearer "):
        raise ApiError(401, "unauthorized", "Missing bearer token")
    presented = authorization.split(" ", 1)[1].strip()
    token = db.scalar(select(AuthToken).where(AuthToken.token_hash == token_hash(presented)))
    if token is None:
        raise ApiError(401, "unauthorized", "Invalid API token")
    now = datetime.now(UTC)
    if token.revoked_at is not None or _aware(token.expires_at) <= now:
        raise ApiError(401, "session_expired", "This session has expired. Sign in again.")
    user = db.get(User, token.user_id)
    if user is None or not user.is_active:
        raise ApiError(401, "unauthorized", "Invalid API token")
    # Throttled touch: at most one write per 5 minutes per token.
    if token.last_used_at is None or (now - _aware(token.last_used_at)) > timedelta(minutes=5):
        token.last_used_at = now
        db.commit()
    request.state.user_id = str(user.id)
    return Principal(user=user, token=token)


def require_user(principal: Annotated[Principal, Depends(resolve_principal)]) -> User:
    return principal.user


def pagination(
    limit: Annotated[int, Query(ge=1, le=200)] = 50,
    offset: Annotated[int, Query(ge=0)] = 0,
) -> tuple[int, int]:
    return limit, offset


DbSession = Annotated[Session, Depends(db_dep)]
SettingsDep = Annotated[Settings, Depends(settings_dep)]
ApiKeyId = Annotated[str, Depends(require_api_key)]
CurrentUser = Annotated[User, Depends(require_user)]
PrincipalDep = Annotated[Principal, Depends(resolve_principal)]
Pagination = Annotated[tuple[int, int], Depends(pagination)]
