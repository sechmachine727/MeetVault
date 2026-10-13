from __future__ import annotations


def test_system_info_requires_auth(client) -> None:
    assert client.get("/v1/system/info").status_code == 401


def test_system_info_reports_version_and_migration_head(client, auth_headers) -> None:
    response = client.get("/v1/system/info", headers=auth_headers)
    assert response.status_code == 200
    body = response.json()
    assert body["version"] == "0.1.0"
    assert body["migration_head"] == "20261014_0002"


def test_providers_endpoint_distinguishes_requested_and_effective(client, auth_headers) -> None:
    response = client.get("/v1/system/providers", headers=auth_headers)
    assert response.status_code == 200
    body = response.json()
    assert set(body) == {"transcriber", "diarizer", "analyzer"}
    for capability in body.values():
        assert "requested" in capability
        assert "effective" in capability
        assert isinstance(capability["is_mock"], bool)
        assert capability["reason"]
