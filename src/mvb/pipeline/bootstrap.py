from __future__ import annotations

# Importing this module registers every pipeline handler with mvb.jobs.handlers.REGISTRY.
# The worker imports it once at startup; tests import it to exercise the real pipeline.
from mvb.pipeline import analyze as _analyze  # noqa: F401
from mvb.pipeline import diarize as _diarize  # noqa: F401
from mvb.pipeline import media as _media  # noqa: F401
from mvb.pipeline import qa as _qa  # noqa: F401
from mvb.pipeline import transcribe as _transcribe  # noqa: F401

__all__ = ["registered_pipeline_types"]


def registered_pipeline_types() -> list[str]:
    from mvb.jobs.handlers import registered_types

    return registered_types()
