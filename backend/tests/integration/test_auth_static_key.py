from __future__ import annotations

import logging

from fastapi import Depends, FastAPI
from fastapi.testclient import TestClient

from mvb.api.deps import require_api_key
from mvb.api.errors import install_error_handlers


def _client() -> TestClient:
    app = FastAPI()
    install_error_handlers(app)

    @app.get("/v1/ping", dependencies=[Depends(require_api_key)])
    def ping() -> dict[str, str]:
        return {"pong": "ok"}

    return TestClient(app)


def test_missing_key_is_401() -> None:
    response = _client().get("/v1/ping")
    assert response.status_code == 401
    assert response.json()["error"]["code"] == "unauthorized"


def test_wrong_key_is_401() -> None:
    assert _client().get("/v1/ping", headers={"Authorization": "Bearer nope"}).status_code == 401


def test_non_bearer_scheme_is_401() -> None:
    assert _client().get("/v1/ping", headers={"Authorization": "Basic test-key"}).status_code == 401


def test_correct_key_is_200() -> None:
    response = _client().get("/v1/ping", headers={"Authorization": "Bearer test-key"})
    assert response.status_code == 200
    assert response.json() == {"pong": "ok"}


def test_key_is_not_logged(caplog) -> None:  # type: ignore[no-untyped-def]
    with caplog.at_level(logging.DEBUG):
        _client().get("/v1/ping", headers={"Authorization": "Bearer test-key"})
    assert "test-key" not in caplog.text
