from __future__ import annotations

import logging
from datetime import UTC, datetime, timedelta

from sqlalchemy import select

from mvb.db.base import get_session_factory
from mvb.db.models import AuthToken
from mvb.security import hash_password
from tests.helpers import create_user_with_token


def test_login_success(client) -> None:  # noqa: ANN001
    with get_session_factory()() as session:
        create_user_with_token(session, email="a@example.com", password="pw-123456")
    response = client.post("/v1/auth/login", json={"email": "a@example.com", "password": "pw-123456"})
    assert response.status_code == 200
    body = response.json()
    assert body["user"]["email"] == "a@example.com"
    assert len(body["token"]) > 20
    assert body["expires_at"]


def test_login_wrong_password_401(client) -> None:
    with get_session_factory()() as session:
        create_user_with_token(session, email="a@example.com", password="pw-123456")
    response = client.post("/v1/auth/login", json={"email": "a@example.com", "password": "nope"})
    assert response.status_code == 401
    assert response.json()["error"]["code"] == "invalid_credentials"


def test_login_unknown_email_same_code(client) -> None:
    response = client.post("/v1/auth/login", json={"email": "ghost@example.com", "password": "whatever"})
    assert response.status_code == 401
    assert response.json()["error"]["code"] == "invalid_credentials"


def test_me_requires_token(client) -> None:
    assert client.get("/v1/auth/me").status_code == 401
    assert client.get("/v1/auth/me", headers={"Authorization": "Bearer nope"}).status_code == 401


def test_me_ok_with_token(client, auth_user) -> None:  # noqa: ANN001
    response = client.get("/v1/auth/me", headers=auth_user.headers)
    assert response.status_code == 200
    assert response.json()["user"]["email"] == auth_user.email


def test_expired_token_is_session_expired(client, auth_user) -> None:  # noqa: ANN001
    with get_session_factory()() as session:
        token = session.scalar(select(AuthToken).where(AuthToken.user_id == auth_user.id))
        token.expires_at = datetime.now(UTC) - timedelta(minutes=1)
        session.commit()
    response = client.get("/v1/auth/me", headers=auth_user.headers)
    assert response.status_code == 401
    assert response.json()["error"]["code"] == "session_expired"


def test_logout_revokes_token(client, auth_user) -> None:  # noqa: ANN001
    assert client.post("/v1/auth/logout", headers=auth_user.headers).status_code == 200
    assert client.get("/v1/auth/me", headers=auth_user.headers).status_code == 401
    assert client.get("/v1/auth/me", headers=auth_user.headers).json()["error"]["code"] == "session_expired"


def test_token_is_not_logged(client, auth_user, caplog) -> None:  # noqa: ANN001
    with caplog.at_level(logging.DEBUG):
        client.get("/v1/auth/me", headers=auth_user.headers)
    assert auth_user.token not in caplog.text
