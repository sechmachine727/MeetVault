from __future__ import annotations

import pytest
from pydantic import ValidationError

from mvb.config import Settings, get_settings


def test_defaults_are_dev_safe(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("MVB_ENV", "dev")
    s = Settings()
    assert s.env == "dev"
    assert s.port == 8080
    assert s.transcriber == "mock"
    assert s.diarizer == "mock"
    assert s.analyzer == "mock"
    assert s.max_upload_mb == 4096


def test_api_keys_are_split_and_trimmed(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("MVB_API_KEYS", " alpha , beta,, ")
    s = Settings()
    assert s.api_keys == ["alpha", "beta"]


def test_prod_requires_keys(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("MVB_ENV", "prod")
    monkeypatch.setenv("MVB_API_KEYS", "")
    with pytest.raises(ValidationError):
        Settings()


def test_provider_choices_are_validated(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("MVB_ANALYZER", "not-a-provider")
    with pytest.raises(ValidationError):
        Settings()


def test_get_settings_is_cached(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("MVB_LOG_LEVEL", "DEBUG")
    get_settings.cache_clear()
    assert get_settings().log_level == "DEBUG"
    monkeypatch.setenv("MVB_LOG_LEVEL", "INFO")
    assert get_settings().log_level == "DEBUG"  # cached
    get_settings.cache_clear()
    assert get_settings().log_level == "INFO"


def test_max_upload_bytes_and_sqlite_flag() -> None:
    s = Settings()
    s.max_upload_mb = 2
    assert s.max_upload_bytes == 2 * 1024 * 1024
    assert s.is_sqlite is True
