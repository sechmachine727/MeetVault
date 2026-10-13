from __future__ import annotations

import logging
import uuid
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request

from mvb.api.errors import install_error_handlers
from mvb.api.routers import analysis, auth, health, jobs, media, meetings, speakers, system, transcripts
from mvb.config import get_settings
from mvb.logging_setup import configure_logging, request_id_var

log = logging.getLogger(__name__)


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    settings = get_settings()
    configure_logging(settings.log_level)
    settings.media_root.mkdir(parents=True, exist_ok=True)
    log.info(
        "app_start env=%s media_root=%s transcriber=%s diarizer=%s analyzer=%s",
        settings.env,
        settings.media_root,
        settings.transcriber,
        settings.diarizer,
        settings.analyzer,
    )
    yield
    log.info("app_stop")


def create_app() -> FastAPI:
    app = FastAPI(
        title="Meetvault_Backend",
        version="0.1.0",
        description="Post-meeting transcription, diarization and LLM analysis for MeetVault recordings.",
        lifespan=lifespan,
    )
    install_error_handlers(app)

    @app.middleware("http")
    async def request_id_middleware(request: Request, call_next):  # type: ignore[no-untyped-def]
        request_id = request.headers.get("X-Request-Id") or str(uuid.uuid4())
        request.state.request_id = request_id
        token = request_id_var.set(request_id)
        try:
            response = await call_next(request)
        finally:
            request_id_var.reset(token)
        response.headers["X-Request-Id"] = request_id
        return response

    app.include_router(health.router)
    app.include_router(system.router, prefix="/v1")
    app.include_router(auth.router, prefix="/v1")
    app.include_router(meetings.router, prefix="/v1")
    app.include_router(media.router, prefix="/v1")
    app.include_router(transcripts.router, prefix="/v1")
    app.include_router(speakers.router, prefix="/v1")
    app.include_router(jobs.router, prefix="/v1")
    app.include_router(analysis.router, prefix="/v1")
    return app


app = create_app()
