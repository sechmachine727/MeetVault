from __future__ import annotations

import shutil
import subprocess
import uuid
from pathlib import Path
from typing import Any

import mvb.pipeline.bootstrap  # noqa: F401  (registers every pipeline handler)


def ffmpeg_available() -> bool:
    return shutil.which("ffmpeg") is not None


def make_wav(path: Path, seconds: float = 6.0, *, freq: int = 440, channels: int = 1, rate: int = 48000) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        [
            "ffmpeg",
            "-y",
            "-f",
            "lavfi",
            "-i",
            f"sine=frequency={freq}:duration={seconds}",
            "-ac",
            str(channels),
            "-ar",
            str(rate),
            str(path),
        ],
        check=True,
        capture_output=True,
    )
    return path


def seed_meeting_with_audio(
    session: Any,
    media_root: Path,
    *,
    seconds: float = 6.0,
    title: str = "Test meeting",
    external_id: str | None = None,
    language: str = "en",
) -> tuple[Any, Any]:
    """Insert a meeting plus one probed audio asset whose file exists under media_root."""
    from mvb.db.models import MediaAsset, Meeting
    from mvb.media.probe import probe_media

    meeting_id = uuid.uuid4()
    wav = make_wav(Path(media_root) / str(meeting_id) / "audio.wav", seconds)
    info = probe_media(wav)
    meeting = Meeting(
        id=meeting_id,
        title=title,
        status="uploaded",
        language=language,
        external_id=external_id,
        metadata_json={},
    )
    session.add(meeting)
    asset = MediaAsset(
        id=uuid.uuid4(),
        meeting_id=meeting_id,
        kind="audio",
        role="primary",
        original_name="audio.wav",
        content_type="audio/wav",
        size_bytes=wav.stat().st_size,
        sha256="0" * 64,
        storage_key=f"{meeting_id}/audio.wav",
        duration_ms=info.duration_ms,
        sample_rate=info.sample_rate,
        channels=info.channels,
        probe_json={},
    )
    session.add(asset)
    session.commit()
    return meeting, asset


def create_user_with_token(
    session: Any,
    *,
    email: str = "test@example.com",
    password: str = "test-password-1",
    display_name: str = "Test User",
    ttl_days: int = 30,
) -> tuple[Any, str]:
    """Create a user + session token row; return (User, raw_token)."""
    import uuid as _uuid
    from datetime import UTC, datetime, timedelta

    from mvb.db.models import AuthToken, User
    from mvb.security import hash_password, new_session_token

    user = User(
        id=_uuid.uuid4(),
        email=email.lower(),
        display_name=display_name,
        password_hash=hash_password(password),
    )
    session.add(user)
    raw, digest = new_session_token()
    session.add(
        AuthToken(
            id=_uuid.uuid4(),
            user_id=user.id,
            token_hash=digest,
            expires_at=datetime.now(UTC) + timedelta(days=ttl_days),
        )
    )
    session.commit()
    return user, raw
