from __future__ import annotations

from pathlib import Path

from mvb.config import Settings


def test_web_root_defaults_to_the_repo_relative_web_dir() -> None:
    assert Settings().web_root == Path("./web")


def test_web_root_can_be_overridden_by_env(monkeypatch) -> None:
    monkeypatch.setenv("MVB_WEB_ROOT", "/srv/console")
    assert Settings().web_root == Path("/srv/console")


def test_app_module_import_does_not_populate_the_settings_cache() -> None:
    """`app = create_app()` runs at import time (collection included). If create_app reads settings
    through the lru_cached get_settings(), the ambient environment is pinned for the whole session
    and the first test inherits the dev database instead of its throwaway one.

    Proven to bite: with `mount_web_console(app, get_settings())` this asserts currsize == 1."""
    import subprocess
    import sys

    script = (
        "import mvb.main;"
        "from mvb.config import get_settings;"
        "info = get_settings.cache_info();"
        "print('currsize', info.currsize);"
        "raise SystemExit(0 if info.currsize == 0 else 1)"
    )
    result = subprocess.run([sys.executable, "-c", script], capture_output=True, text=True)
    assert result.returncode == 0, f"create_app() populated the settings cache at import time: {result.stdout} {result.stderr}"
