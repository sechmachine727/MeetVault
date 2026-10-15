from __future__ import annotations

import uuid
from datetime import UTC, datetime, timedelta

from fastapi import APIRouter
from sqlalchemy import select
from sqlalchemy.orm import Session

from mvb.accounts import EmailTaken, InvalidRegistration, create_user
from mvb.api.deps import DbSession, PrincipalDep, SettingsDep
from mvb.api.errors import ApiError
from mvb.config import Settings
from mvb.db.models import AuthToken, User
from mvb.schemas.auth import LoginIn, LoginOut, MeOut, RegisterIn, UserOut
from mvb.schemas.common import OkResponse
from mvb.security import DUMMY_HASH, new_session_token, verify_password

router = APIRouter(tags=["auth"])


def issue_session(db: Session, user: User, settings: Settings) -> tuple[str, datetime]:
    """Mint a session token for `user` and persist its hash. Returns (raw_token, expires_at)."""
    raw, digest = new_session_token()
    expires_at = datetime.now(UTC) + timedelta(days=settings.session_ttl_days)
    db.add(AuthToken(id=uuid.uuid4(), user_id=user.id, token_hash=digest, expires_at=expires_at))
    db.commit()
    return raw, expires_at


@router.post("/auth/login", response_model=LoginOut)
def login(payload: LoginIn, db: DbSession, settings: SettingsDep) -> LoginOut:
    email = payload.email.strip().lower()
    user = db.scalar(select(User).where(User.email == email))
    stored = user.password_hash if user is not None else DUMMY_HASH
    ok = verify_password(payload.password, stored)
    if user is None or not ok or not user.is_active:
        raise ApiError(401, "invalid_credentials", "Email or password is incorrect.")
    raw, expires_at = issue_session(db, user, settings)
    return LoginOut(token=raw, expires_at=expires_at, user=UserOut.model_validate(user))


@router.post("/auth/register", response_model=LoginOut, status_code=201)
def register(payload: RegisterIn, db: DbSession, settings: SettingsDep) -> LoginOut:
    """Self-service account creation from an email + password, followed by an immediate session.

    403 `signup_disabled` when MVB_ALLOW_SIGNUP=false; 400 `invalid_email` / `weak_password`;
    409 `email_taken`.
    """
    if not settings.allow_signup:
        raise ApiError(403, "signup_disabled", "Sign-up is disabled on this server. Ask the owner for an account.")
    try:
        user = create_user(db, email=payload.email, password=payload.password, display_name=payload.display_name)
    except InvalidRegistration as exc:
        raise ApiError(400, exc.code, exc.message) from exc
    except EmailTaken as exc:
        raise ApiError(409, "email_taken", "That email already has an account. Sign in instead.") from exc
    raw, expires_at = issue_session(db, user, settings)
    return LoginOut(token=raw, expires_at=expires_at, user=UserOut.model_validate(user))


@router.post("/auth/logout", response_model=OkResponse)
def logout(principal: PrincipalDep, db: DbSession) -> OkResponse:
    """Revoke the presented session token; after this call the token answers
    `session_expired`."""
    if principal.token.revoked_at is None:
        principal.token.revoked_at = datetime.now(UTC)
        db.commit()
    return OkResponse()


@router.get("/auth/me", response_model=MeOut)
def me(principal: PrincipalDep) -> MeOut:
    return MeOut(
        user=UserOut.model_validate(principal.user),
        expires_at=principal.token.expires_at,
        issued_at=principal.token.created_at,
    )
