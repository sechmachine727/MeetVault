from __future__ import annotations

from pathlib import Path

from mvb.config import Settings


def test_web_root_defaults_to_the_repo_relative_web_dir() -> None:
    assert Settings().web_root == Path("./web")


def test_web_root_can_be_overridden_by_env(monkeypatch) -> None:
    monkeypatch.setenv("MVB_WEB_ROOT", "/srv/console")
    assert Settings().web_root == Path("/srv/console")
