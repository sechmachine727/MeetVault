from __future__ import annotations

import subprocess
import sys
import textwrap
from pathlib import Path

SCRIPT = textwrap.dedent(
    """
    import os, sys
    os.environ["MVB_ENV"] = "dev"
    os.environ["MVB_API_KEYS"] = "k"
    os.environ["MVB_DATABASE_URL"] = "sqlite+pysqlite:///" + sys.argv[1]
    os.environ["MVB_MEDIA_ROOT"] = sys.argv[2]
    os.environ["MVB_LOG_LEVEL"] = "WARNING"

    import mvb.db.models  # noqa: F401  (registers the tables on Base.metadata)
    from mvb.db.base import Base, get_engine

    Base.metadata.create_all(get_engine())

    from mvb.jobs.handlers import registered_types

    assert "transcribe" not in registered_types(), "precondition: nothing imported the pipeline yet"
    assert registered_types() == [], registered_types()

    from mvb.jobs.worker import tick

    assert tick() is False, "an empty queue runs nothing"

    pipeline = {"media_probe", "transcribe", "diarize", "align", "analyze", "qa"}
    missing = pipeline - set(registered_types())
    assert not missing, f"tick() must register the pipeline handlers, missing: {sorted(missing)}"
    print("OK")
    """
)


def test_tick_registers_the_pipeline_handlers_by_itself(tmp_path: Path) -> None:
    """Regression: `python -m mvb.jobs.worker` worked because main() imported the pipeline, but
    any other caller of tick() (an embedded worker, an ops smoke test) claimed jobs it could not
    run and failed every one with "no handler registered for job type 'transcribe'".

    Runs in a fresh interpreter so no other test's imports can mask the bug.
    """
    result = subprocess.run(
        [sys.executable, "-c", SCRIPT, str(tmp_path / "worker.db"), str(tmp_path / "media")],
        capture_output=True,
        text=True,
        timeout=120,
    )
    assert result.returncode == 0, f"stdout={result.stdout!r} stderr={result.stderr!r}"
    assert result.stdout.strip().endswith("OK")


def test_ensure_handlers_is_idempotent() -> None:
    from mvb.jobs.handlers import registered_types
    from mvb.jobs.worker import _ensure_handlers

    _ensure_handlers()
    first = set(registered_types())
    _ensure_handlers()
    assert set(registered_types()) == first
    assert {"media_probe", "transcribe", "diarize", "align", "analyze", "qa"} <= first
