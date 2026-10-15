from __future__ import annotations

import logging
from datetime import UTC, datetime, timedelta

from sqlalchemy import select

from mvb.db.base import get_session_factory
from mvb.db.models import AuthToken
from tests.helpers import create_user_with_token


def test_login_success(client) -> None:
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


def test_me_ok_with_token(client, auth_user) -> None:
    response = client.get("/v1/auth/me", headers=auth_user.headers)
    assert response.status_code == 200
    assert response.json()["user"]["email"] == auth_user.email


def test_expired_token_is_session_expired(client, auth_user) -> None:
    with get_session_factory()() as session:
        token = session.scalar(select(AuthToken).where(AuthToken.user_id == auth_user.id))
        token.expires_at = datetime.now(UTC) - timedelta(minutes=1)
        session.commit()
    response = client.get("/v1/auth/me", headers=auth_user.headers)
    assert response.status_code == 401
    assert response.json()["error"]["code"] == "session_expired"


def test_logout_revokes_token(client, auth_user) -> None:
    assert client.post("/v1/auth/logout", headers=auth_user.headers).status_code == 200
    assert client.get("/v1/auth/me", headers=auth_user.headers).status_code == 401
    assert client.get("/v1/auth/me", headers=auth_user.headers).json()["error"]["code"] == "session_expired"


def test_token_is_not_logged(client, auth_user, caplog) -> None:
    with caplog.at_level(logging.DEBUG):
        client.get("/v1/auth/me", headers=auth_user.headers)
    assert auth_user.token not in caplog.text


def test_register_creates_an_account_and_signs_it_in(client) -> None:
    response = client.post(
        "/v1/auth/register",
        json={"email": "New@Example.com", "password": "brand-new-1", "display_name": "New Person"},
    )
    assert response.status_code == 201
    body = response.json()
    assert body["user"]["email"] == "new@example.com"  # normalized
    assert body["user"]["display_name"] == "New Person"
    assert len(body["token"]) > 20
    assert body["expires_at"]
    # the returned token is a live session, not a decoration
    me = client.get("/v1/auth/me", headers={"Authorization": f"Bearer {body['token']}"})
    assert me.status_code == 200
    assert me.json()["user"]["email"] == "new@example.com"


def test_register_then_login_with_the_same_password(client) -> None:
    created = client.post("/v1/auth/register", json={"email": "two@example.com", "password": "brand-new-1"})
    assert created.status_code == 201
    login = client.post("/v1/auth/login", json={"email": "TWO@example.com", "password": "brand-new-1"})
    assert login.status_code == 200
    assert login.json()["user"]["email"] == "two@example.com"


def test_a_new_account_starts_with_an_empty_cloud(client) -> None:
    body = client.post("/v1/auth/register", json={"email": "fresh@example.com", "password": "brand-new-1"}).json()
    page = client.get("/v1/meetings", headers={"Authorization": f"Bearer {body['token']}"}).json()
    assert page["total"] == 0


def test_register_display_name_defaults_to_the_local_part(client) -> None:
    body = client.post("/v1/auth/register", json={"email": "noname@example.com", "password": "brand-new-1"}).json()
    assert body["user"]["display_name"] == "noname"


def test_register_duplicate_email_is_409(client) -> None:
    payload = {"email": "dupe@example.com", "password": "brand-new-1"}
    assert client.post("/v1/auth/register", json=payload).status_code == 201
    again = client.post("/v1/auth/register", json=payload)
    assert again.status_code == 409
    assert again.json()["error"]["code"] == "email_taken"
    assert "already has an account" in again.json()["error"]["message"]


def test_register_duplicate_email_is_case_insensitive(client) -> None:
    assert client.post("/v1/auth/register", json={"email": "case@example.com", "password": "brand-new-1"}).status_code == 201
    assert client.post("/v1/auth/register", json={"email": "CASE@Example.COM", "password": "brand-new-1"}).status_code == 409


def test_register_short_password_is_400(client) -> None:
    response = client.post("/v1/auth/register", json={"email": "weak@example.com", "password": "short"})
    assert response.status_code == 400
    assert response.json()["error"]["code"] == "weak_password"
    assert "8" in response.json()["error"]["message"]


def test_register_bad_email_is_400(client) -> None:
    response = client.post("/v1/auth/register", json={"email": "not-an-email", "password": "brand-new-1"})
    assert response.status_code == 400
    assert response.json()["error"]["code"] == "invalid_email"


def test_register_is_refused_when_signup_is_disabled(client, monkeypatch) -> None:
    from mvb.config import get_settings

    monkeypatch.setenv("MVB_ALLOW_SIGNUP", "false")
    get_settings.cache_clear()  # SettingsDep resolves through the lru_cache
    response = client.post("/v1/auth/register", json={"email": "closed@example.com", "password": "brand-new-1"})
    assert response.status_code == 403
    assert response.json()["error"]["code"] == "signup_disabled"


def test_register_does_not_create_a_row_when_it_refuses(client) -> None:
    client.post("/v1/auth/register", json={"email": "ghost2@example.com", "password": "short"})
    from mvb.accounts import find_user
    from mvb.db.base import get_session_factory

    with get_session_factory()() as session:
        assert find_user(session, "ghost2@example.com") is None
