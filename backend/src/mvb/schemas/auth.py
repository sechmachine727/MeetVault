from __future__ import annotations

import uuid
from datetime import datetime

from pydantic import Field

from mvb.schemas.common import ORMModel


class UserOut(ORMModel):
    id: uuid.UUID
    email: str
    display_name: str


class LoginIn(ORMModel):
    email: str = Field(min_length=3, max_length=320)
    password: str = Field(min_length=1, max_length=256)


class RegisterIn(ORMModel):
    """Sign-up payload.

    Password strength is deliberately NOT enforced here: a pydantic failure answers the opaque 422
    `validation_error`, while mvb.accounts returns a 400 with a sentence the user can act on.
    """

    email: str = Field(min_length=3, max_length=320)
    password: str = Field(min_length=1, max_length=256)
    display_name: str = Field(default="", max_length=200)


class LoginOut(ORMModel):
    token: str
    expires_at: datetime
    user: UserOut


class MeOut(ORMModel):
    user: UserOut
    expires_at: datetime
    issued_at: datetime
