from __future__ import annotations

import os
from collections.abc import Iterator
from pathlib import Path

import pytest

# Set MVB_TEST_DATABASE_URL (e.g. a Postgres URL) to run the whole suite against another
# engine — CI's postgres job does exactly that. Unset, every test gets a throwaway SQLite file.
POSTGRES_TEST_URL = os.environ.get("MVB_TEST_DATABASE_URL")


def prepare_schema() -> None:
    """Create the schema for the DATABASE_URL in use. On a shared server (Postgres in CI) the
    tables are dropped first so tests cannot see each other's rows."""
    from mvb.config import get_settings
    from mvb.db.base import Base, get_engine

    engine = get_engine()
    if not get_settings().is_sqlite:
        Base.metadata.drop_all(engine)
    Base.metadata.create_all(engine)


@pytest.fixture
def media_root(tmp_path: Path) -> Path:
    root = tmp_path / "media"
    root.mkdir(parents=True, exist_ok=True)
    return root


@pytest.fixture(autouse=True)
def _clean_env(monkeypatch: pytest.MonkeyPatch, media_root: Path, tmp_path: Path) -> Iterator[None]:
    """Every test starts from a known env and a throwaway database/media root."""
    for key in list(os.environ):
        if key.startswith("MVB_"):
            monkeypatch.delenv(key, raising=False)
    monkeypatch.setenv("MVB_ENV", "test")
    monkeypatch.setenv("MVB_API_KEYS", "test-key")
    monkeypatch.setenv("MVB_DATABASE_URL", POSTGRES_TEST_URL or f"sqlite+pysqlite:///{tmp_path / 'test.db'}")
    monkeypatch.setenv("MVB_MEDIA_ROOT", str(media_root))
    monkeypatch.setenv("MVB_LOG_LEVEL", "WARNING")
    yield
    from mvb.config import get_settings
    from mvb.db.base import reset_engine_for_tests

    reset_engine_for_tests()
    get_settings.cache_clear()


@pytest.fixture
def db_session():  # type: ignore[no-untyped-def]
    from mvb.db.base import get_session_factory

    prepare_schema()
    session = get_session_factory()()
    try:
        yield session
    finally:
        session.close()


@pytest.fixture
def client():  # type: ignore[no-untyped-def]
    """In-process API client with a fresh schema per test."""
    from fastapi.testclient import TestClient

    from mvb.main import create_app

    prepare_schema()
    with TestClient(create_app()) as test_client:
        yield test_client


@pytest.fixture
def auth_headers() -> dict[str, str]:
    return {"Authorization": "Bearer test-key"}


@pytest.fixture
def auth_user(db_session):  # type: ignore[no-untyped-def]
    from types import SimpleNamespace

    from tests.helpers import create_user_with_token

    user, raw = create_user_with_token(db_session)
    return SimpleNamespace(id=user.id, email=user.email, token=raw, headers={"Authorization": f"Bearer {raw}"})
