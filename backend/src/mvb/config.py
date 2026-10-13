from __future__ import annotations

from functools import lru_cache
from pathlib import Path
from typing import Literal

from pydantic_settings import BaseSettings, SettingsConfigDict

TranscriberName = Literal["mock", "faster_whisper"]
DiarizerName = Literal["mock", "pyannote"]
AnalyzerName = Literal["mock", "lmstudio", "opencode-go"]


class Settings(BaseSettings):
    """All runtime configuration. Env-only (MVB_*); see .env.example."""

    model_config = SettingsConfigDict(env_prefix="MVB_", extra="ignore", env_file=".env", env_file_encoding="utf-8")

    env: Literal["dev", "prod", "test"] = "dev"
    host: str = "0.0.0.0"
    port: int = 8080
    database_url: str = "sqlite+pysqlite:///./_local.db"
    media_root: Path = Path("./data/media")
    max_upload_mb: int = 4096
    job_stale_seconds: int = 900
    worker_poll_seconds: float = 2.0
    worker_concurrency: int = 1
    session_ttl_days: int = 30

    transcriber: TranscriberName = "mock"
    transcriber_model: str = "small"
    diarizer: DiarizerName = "mock"
    diarizer_model: str = "pyannote/speaker-diarization-3.1"
    analyzer: AnalyzerName = "mock"
    analyzer_model: str = ""
    analyzer_timeout_s: float = 120.0

    lmstudio_base_url: str = "http://127.0.0.1:1234/v1"
    lmstudio_api_key: str = ""
    opencode_go_base_url: str = "https://opencode.ai/zen/go/v1"
    opencode_go_api_key: str = ""

    ffmpeg: str = "ffmpeg"
    ffprobe: str = "ffprobe"
    log_level: str = "INFO"

    @property
    def max_upload_bytes(self) -> int:
        return self.max_upload_mb * 1024 * 1024

    @property
    def is_sqlite(self) -> bool:
        return self.database_url.startswith("sqlite")


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    return Settings()
