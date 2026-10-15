"""Account rules shared by the admin CLI and the HTTP API.

One module, so `python -m mvb.admin create-user` and `POST /v1/auth/register` can never disagree
about what an acceptable account is.
"""

from __future__ import annotations

from sqlalchemy import select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from mvb.db.models import User
from mvb.security import hash_password

MIN_PASSWORD_LENGTH = 8
MAX_EMAIL_LENGTH = 320


class EmailTaken(Exception):
    """That email already has an account (also raised when the unique constraint wins a race)."""


class InvalidRegistration(ValueError):
    """A rule the caller must surface to the user; `code` is API-facing."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message


def normalize_email(value: str) -> str:
    return (value or "").strip().lower()


def looks_like_email(value: str) -> bool:
    """Deliberately dependency-free (this image has no email-validator): one @, a dotted domain,
    no whitespace. Strong enough to catch typos; not a deliverability check."""
    if not value or len(value) > MAX_EMAIL_LENGTH or any(ch.isspace() for ch in value):
        return False
    local, at, domain = value.partition("@")
    if not at or not local or "@" in domain:
        return False
    return "." in domain and not domain.startswith(".") and not domain.endswith(".")


def validate_registration(email: str, password: str) -> None:
    """Raise InvalidRegistration for a bad email or a weak password. `email` must be normalized."""
    if not looks_like_email(email):
        raise InvalidRegistration("invalid_email", "That does not look like an email address.")
    if len(password) < MIN_PASSWORD_LENGTH:
        raise InvalidRegistration("weak_password", f"Password must be at least {MIN_PASSWORD_LENGTH} characters.")


def find_user(db: Session, email: str) -> User | None:
    return db.scalar(select(User).where(User.email == normalize_email(email)))


def create_user(db: Session, *, email: str, password: str, display_name: str = "") -> User:
    """Validate, insert, commit. Raises InvalidRegistration or EmailTaken."""
    email = normalize_email(email)
    validate_registration(email, password)
    if find_user(db, email) is not None:
        raise EmailTaken(email)
    user = User(
        email=email,
        display_name=(display_name or "").strip() or email.split("@")[0],
        password_hash=hash_password(password),
    )
    db.add(user)
    try:
        db.commit()
    except IntegrityError as exc:  # two registers for one email at the same moment
        db.rollback()
        raise EmailTaken(email) from exc
    return user
