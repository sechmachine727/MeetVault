from __future__ import annotations

from pathlib import Path

import pytest

# The console's real source directory (backend/web), resolved from this file, never from cwd.
WEB_ROOT = Path(__file__).resolve().parents[2] / "web"


@pytest.fixture(autouse=True)
def _web_root_env(monkeypatch: pytest.MonkeyPatch) -> None:
    """Point the app at the real web dir. autouse => this runs after conftest's own autouse
    `_clean_env` (which deletes every MVB_* var), so the value is not wiped."""
    monkeypatch.setenv("MVB_WEB_ROOT", str(WEB_ROOT))


def test_root_serves_the_app_shell(client) -> None:
    res = client.get("/")
    assert res.status_code == 200
    assert res.headers["content-type"].startswith("text/html")
    assert "MeetVault" in res.text
    assert res.headers["cache-control"] == "no-cache"
    assert "default-src 'self'" in res.headers["content-security-policy"]


def test_assets_are_served_with_the_right_mime_types(client) -> None:
    css = client.get("/assets/app.css")
    assert css.status_code == 200
    assert css.headers["content-type"].startswith("text/css")
    js = client.get("/assets/app.js")
    assert js.status_code == 200
    assert "javascript" in js.headers["content-type"]


def test_unknown_asset_is_a_404(client) -> None:
    assert client.get("/assets/does-not-exist.js").status_code == 404


def test_api_docs_and_openapi_are_still_served(client) -> None:
    assert client.get("/openapi.json").status_code == 200
    assert client.get("/docs").status_code == 200


def test_api_routes_are_unaffected(client) -> None:
    assert client.get("/healthz").status_code == 200
    assert client.get("/v1/meetings").status_code == 401


def test_missing_web_root_degrades_to_api_only(client, monkeypatch) -> None:
    """A machine with only the Python package must still boot and serve the API."""
    from fastapi.testclient import TestClient

    from mvb.config import get_settings
    from mvb.main import create_app

    monkeypatch.setenv("MVB_WEB_ROOT", "/definitely/not/here")
    get_settings.cache_clear()
    with TestClient(create_app()) as solo:
        assert solo.get("/healthz").status_code == 200
        assert solo.get("/").status_code == 404


ASSETS_EVERY_PAGE_NEEDS = [
    "index.html",
    "app.css",
    "app.js",
    "lib/api.js",
    "lib/format.js",
    "lib/router.js",
    "lib/state.js",
    "lib/dom.js",
    "views/signin.js",
    "views/meetings.js",
    "views/meeting.js",
    "views/system.js",
]


@pytest.mark.parametrize("relative", ASSETS_EVERY_PAGE_NEEDS)
def test_every_console_asset_is_served(client, relative: str) -> None:
    res = client.get(f"/assets/{relative}")
    assert res.status_code == 200, relative
    assert res.content, relative


def test_app_js_imports_only_local_modules(client) -> None:
    """No CDN, no third-party module: the CSP allows 'self' only."""
    body = client.get("/assets/app.js").text
    assert "http://" not in body and "https://" not in body
