from __future__ import annotations

import logging
import uuid
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, Request
from fastapi.responses import FileResponse
from fastapi.staticfiles import StaticFiles

from mvb.api.errors import install_error_handlers
from mvb.api.routers import analysis, auth, health, jobs, media, meetings, speakers, system, transcripts
from mvb.config import Settings, get_settings
from mvb.logging_setup import configure_logging, request_id_var

log = logging.getLogger(__name__)

# The console page is dynamic-ish (it boots against the live API): never let a proxy or the
# browser cache it. The CSP keeps the page to its own origin — the session token lives in
# localStorage, so a third-party script or an injected inline script must not be able to run.
_WEB_INDEX_HEADERS = {
    "Cache-Control": "no-cache",
    "Content-Security-Policy": (
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; "
        "media-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'"
    ),
}


def mount_web_console(app: FastAPI, settings: Settings) -> None:
    """Serve the management console from MVB_WEB_ROOT (default ./web).

    A missing directory is NOT an error: the promise is that the service boots anywhere, so an
    API-only machine (no web assets on disk) logs and carries on.
    """
    root: Path = settings.web_root
    index = root / "index.html"
    if not index.is_file():
        log.warning("web_ui_disabled web_root=%s reason=missing_index", root)
        return
    app.mount("/assets", StaticFiles(directory=root), name="web-assets")

    @app.get("/", include_in_schema=False)
    def web_index() -> FileResponse:
        return FileResponse(index, media_type="text/html", headers=_WEB_INDEX_HEADERS)

    log.info("web_ui_enabled web_root=%s", root)


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

    # The management web console (static assets + the index route). API-only when absent.
    # A FRESH Settings (not get_settings()): this function also runs at import time via the
    # module-level `app = create_app()`, and populating the cached settings there would pin the
    # ambient database for a whole test session (the cache is cleared per test, so only the
    # first test would inherit it — which silently wrote rows into the dev ./_local.db).
    mount_web_console(app, Settings())
    return app


app = create_app()
