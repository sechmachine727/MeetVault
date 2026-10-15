from __future__ import annotations

import pytest
from sqlalchemy import text


def test_sqlite_engine_enables_wal_and_busy_timeout(db_session) -> None:
    from mvb.config import get_settings

    if not get_settings().is_sqlite:
        pytest.skip("sqlite-only pragma check")
    assert db_session.execute(text("PRAGMA journal_mode")).scalar() == "wal"
    assert int(db_session.execute(text("PRAGMA busy_timeout")).scalar()) >= 5000
