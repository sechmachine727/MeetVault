from __future__ import annotations

from fastapi.testclient import TestClient

from mvb.main import create_app

client = TestClient(create_app())


def test_healthz_is_open_and_cheap() -> None:
    response = client.get("/healthz")
    assert response.status_code == 200
    assert response.json()["status"] == "ok"


def test_readyz_reports_db_and_providers() -> None:
    response = client.get("/readyz")
    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "ready"
    assert body["checks"]["database"] is True
    assert body["checks"]["storage"] is True
    assert body["providers"]["transcriber"]["effective"] == "mock-transcriber"
    assert body["providers"]["transcriber"]["is_mock"] is True


def test_openapi_is_served() -> None:
    response = client.get("/openapi.json")
    assert response.status_code == 200
    assert response.json()["info"]["title"] == "Meetvault_Backend"


def test_request_id_header_is_echoed() -> None:
    response = client.get("/healthz", headers={"X-Request-Id": "abc-123"})
    assert response.headers["X-Request-Id"] == "abc-123"


def test_generated_request_id_when_absent() -> None:
    response = client.get("/healthz")
    assert len(response.headers["X-Request-Id"]) >= 8
