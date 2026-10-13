from __future__ import annotations

from functools import lru_cache
from pathlib import Path
from typing import Annotated, Literal

from pydantic import Field, field_validator, model_validator
from pydantic_settings import BaseSettings, NoDecode, SettingsConfigDict

TranscriberName = Literal["mock", "faster_whisper"]
DiarizerName = Literal["mock", "pyannote"]
AnalyzerName = Literal["mock", "lmstudio", "opencode", "opencode-go"]


class Settings(BaseSettings):
    """All runtime configuration. Env-only (MVB_*); see .env.example."""

    model_config = SettingsConfigDict(env_prefix="MVB_", extra="ignore", env_file=".env", env_file_encoding="utf-8")

    env: Literal["dev", "prod", "test"] = "dev"
    host: str = "0.0.0.0"
    port: int = 8080
    # NoDecode: the env value is a comma-separated string, not JSON, so the raw value
    # reaches the validator below (pydantic-settings otherwise tries json.loads on it).
    api_keys: Annotated[list[str], NoDecode] = Field(default_factory=list)

    database_url: str = "sqlite+pysqlite:///./_local.db"
    media_root: Path = Path("./data/media")
    max_upload_mb: int = 4096
    job_stale_seconds: int = 900
    worker_poll_seconds: float = 2.0
    worker_concurrency: int = 1

    transcriber: TranscriberName = "mock"
    transcriber_model: str = "small"
    diarizer: DiarizerName = "mock"
    diarizer_model: str = "pyannote/speaker-diarization-3.1"
    analyzer: AnalyzerName = "mock"
    analyzer_model: str = ""
    analyzer_timeout_s: float = 120.0

    lmstudio_base_url: str = "http://127.0.0.1:1234/v1"
    lmstudio_api_key: str = ""
    opencode_base_url: str = "http://127.0.0.1:4096"
    opencode_api_key: str = ""
    opencode_go_base_url: str = "https://opencode.ai/zen/go/v1"
    opencode_go_api_key: str = ""

    ffmpeg: str = "ffmpeg"
    ffprobe: str = "ffprobe"
    log_level: str = "INFO"

    @field_validator("api_keys", mode="before")
    @classmethod
    def _split_keys(cls, value: object) -> object:
        if isinstance(value, str):
            return [part.strip() for part in value.split(",") if part.strip()]
        return value

    @model_validator(mode="after")
    def _prod_requires_keys(self) -> Settings:
        if self.env == "prod" and not self.api_keys:
            raise ValueError("MVB_API_KEYS must be set when MVB_ENV=prod")
        return self

    @property
    def max_upload_bytes(self) -> int:
        return self.max_upload_mb * 1024 * 1024

    @property
    def is_sqlite(self) -> bool:
        return self.database_url.startswith("sqlite")


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    return Settings()
