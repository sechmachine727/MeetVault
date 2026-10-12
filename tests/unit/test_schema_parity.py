from __future__ import annotations

import os
from pathlib import Path

import pytest
import sqlalchemy as sa

import mvb.db.models  # noqa: F401
from mvb.db.base import Base

EXPECTED_TABLES = {
    "meetings",
    "media_assets",
    "transcripts",
    "transcript_segments",
    "speakers",
    "jobs",
    "job_events",
    "analysis_runs",
    "sections",
    "analysis_documents",
}


def test_models_declare_every_table() -> None:
    assert set(Base.metadata.tables) == EXPECTED_TABLES


def test_no_postgres_only_types() -> None:
    """Portability rule: PG-only types must not appear in model metadata."""
    banned = {"JSONB", "ARRAY", "INET", "TSVECTOR", "BYTEA"}
    for table in Base.metadata.tables.values():
        for column in table.columns:
            assert type(column.type).__name__ not in banned, f"{table.name}.{column.name}"


def test_create_all_matches_model_metadata(tmp_path: Path) -> None:
    engine = sa.create_engine(f"sqlite+pysqlite:///{tmp_path / 'parity.db'}")
    Base.metadata.create_all(engine)
    inspector = sa.inspect(engine)
    assert set(inspector.get_table_names()) == EXPECTED_TABLES


def test_every_foreign_key_cascades() -> None:
    """Hard delete relies on DB-level cascade (see api/routers/meetings.py)."""
    for table in Base.metadata.tables.values():
        for fk in table.foreign_keys:
            if table.name == "transcript_segments" and fk.parent.name == "speaker_id":
                assert fk.ondelete == "SET NULL"
                continue
            assert fk.ondelete == "CASCADE", f"{table.name}.{fk.parent.name}"


@pytest.mark.skipif(
    os.environ.get("MVB_RUN_POSTGRES") != "1",
    reason="needs a postgres server (CI job sets MVB_RUN_POSTGRES=1)",
)
def test_migrations_round_trip_on_postgres() -> None:  # pragma: no cover - CI only
    from alembic.config import Config
    from alembic.script import ScriptDirectory

    script = ScriptDirectory.from_config(Config("alembic.ini"))
    assert script.get_current_head() == "20261012_0001"
