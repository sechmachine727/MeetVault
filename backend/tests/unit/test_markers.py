from __future__ import annotations

import os

import pytest


def test_live_and_docker_markers_are_off_by_default() -> None:
    """The default suite must be hermetic: no real providers, no Docker, no live server."""
    assert os.environ.get("MVB_RUN_LIVE") != "1"
    assert os.environ.get("MVB_RUN_DOCKER") != "1"


@pytest.mark.skipif(True, reason="placeholder proving the marker wiring")
def test_marked_skip_is_reported_as_skipped() -> None:  # pragma: no cover
    raise AssertionError("must never run")
