from __future__ import annotations

from fastapi import FastAPI
from fastapi.testclient import TestClient

from mvb.api.errors import ApiError, install_error_handlers


def _app() -> FastAPI:
    app = FastAPI()
    install_error_handlers(app)

    @app.get("/boom")
    def boom() -> None:
        raise ApiError(409, "conflict", "Already exists", {"id": "1"})

    @app.get("/validate")
    def validate(limit: int) -> dict:  # type: ignore[type-arg]
        return {"limit": limit}

    return app


def test_api_error_uses_envelope() -> None:
    client = TestClient(_app())
    response = client.get("/boom")
    assert response.status_code == 409
    body = response.json()
    assert body["error"]["code"] == "conflict"
    assert body["error"]["message"] == "Already exists"
    assert body["error"]["detail"] == {"id": "1"}
    assert body["request_id"]


def test_validation_error_is_remapped() -> None:
    client = TestClient(_app())
    response = client.get("/validate", params={"limit": "abc"})
    assert response.status_code == 422
    body = response.json()
    assert body["error"]["code"] == "validation_error"
    assert isinstance(body["error"]["detail"]["errors"], list)


def test_unhandled_exception_is_500_envelope() -> None:
    app = FastAPI()
    install_error_handlers(app)

    @app.get("/kaboom")
    def kaboom() -> None:
        raise RuntimeError("nope")

    client = TestClient(app, raise_server_exceptions=False)
    response = client.get("/kaboom")
    assert response.status_code == 500
    assert response.json()["error"]["code"] == "internal_error"


def test_unknown_route_is_404_envelope() -> None:
    client = TestClient(_app())
    response = client.get("/nope")
    assert response.status_code == 404
    assert response.json()["error"]["code"] == "not_found"
