from __future__ import annotations

import uuid
from datetime import datetime
from typing import Any

import sqlalchemy as sa
from sqlalchemy.orm import Mapped, mapped_column, relationship

from mvb.db.base import Base

TS = sa.DateTime(timezone=True)


class User(Base):
    __tablename__ = "users"

    id: Mapped[uuid.UUID] = mapped_column(sa.Uuid(as_uuid=True), primary_key=True, default=uuid.uuid4)
    email: Mapped[str] = mapped_column(sa.String(320), nullable=False)
    display_name: Mapped[str] = mapped_column(sa.String(200), nullable=False, default="")
    password_hash: Mapped[str] = mapped_column(sa.String(500), nullable=False)
    is_active: Mapped[bool] = mapped_column(sa.Boolean, nullable=False, default=True)
    created_at: Mapped[datetime] = mapped_column(TS, nullable=False, server_default=sa.func.now())
    updated_at: Mapped[datetime] = mapped_column(
        TS, nullable=False, server_default=sa.func.now(), onupdate=sa.func.now()
    )

    __table_args__ = (sa.UniqueConstraint("email", name="uq_users_email"),)


class AuthToken(Base):
    __tablename__ = "auth_tokens"

    id: Mapped[uuid.UUID] = mapped_column(sa.Uuid(as_uuid=True), primary_key=True, default=uuid.uuid4)
    user_id: Mapped[uuid.UUID] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("users.id", ondelete="CASCADE"), nullable=False
    )
    token_hash: Mapped[str] = mapped_column(sa.String(64), nullable=False)
    created_at: Mapped[datetime] = mapped_column(TS, nullable=False, server_default=sa.func.now())
    expires_at: Mapped[datetime] = mapped_column(TS, nullable=False)
    last_used_at: Mapped[datetime | None] = mapped_column(TS)
    revoked_at: Mapped[datetime | None] = mapped_column(TS)

    __table_args__ = (sa.UniqueConstraint("token_hash", name="uq_auth_tokens_hash"),)


class Meeting(Base):
    __tablename__ = "meetings"

    id: Mapped[uuid.UUID] = mapped_column(sa.Uuid(as_uuid=True), primary_key=True, default=uuid.uuid4)
    title: Mapped[str] = mapped_column(sa.String(500), nullable=False)
    started_at: Mapped[datetime | None] = mapped_column(TS)
    ended_at: Mapped[datetime | None] = mapped_column(TS)
    duration_ms: Mapped[int] = mapped_column(sa.BigInteger, nullable=False, default=0)
    status: Mapped[str] = mapped_column(sa.String(32), nullable=False, default="uploaded")
    language: Mapped[str] = mapped_column(sa.String(8), nullable=False, default="")
    source: Mapped[str] = mapped_column(sa.String(32), nullable=False, default="meetvault")
    external_id: Mapped[str | None] = mapped_column(sa.String(200))
    user_id: Mapped[uuid.UUID | None] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("users.id", ondelete="CASCADE")
    )
    metadata_json: Mapped[dict[str, Any]] = mapped_column(sa.JSON, nullable=False, default=dict)
    deleted_at: Mapped[datetime | None] = mapped_column(TS)
    created_at: Mapped[datetime] = mapped_column(TS, nullable=False, server_default=sa.func.now())
    updated_at: Mapped[datetime] = mapped_column(
        TS, nullable=False, server_default=sa.func.now(), onupdate=sa.func.now()
    )

    media_assets: Mapped[list[MediaAsset]] = relationship(back_populates="meeting", cascade="all, delete-orphan")
    transcripts: Mapped[list[Transcript]] = relationship(back_populates="meeting", cascade="all, delete-orphan")
    speakers: Mapped[list[Speaker]] = relationship(back_populates="meeting", cascade="all, delete-orphan")

    __table_args__ = (
        sa.UniqueConstraint("source", "external_id", name="uq_meetings_source_external"),
        sa.Index("ix_meetings_user", "user_id"),
    )


class MediaAsset(Base):
    __tablename__ = "media_assets"

    id: Mapped[uuid.UUID] = mapped_column(sa.Uuid(as_uuid=True), primary_key=True, default=uuid.uuid4)
    meeting_id: Mapped[uuid.UUID] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("meetings.id", ondelete="CASCADE"), nullable=False
    )
    kind: Mapped[str] = mapped_column(sa.String(16), nullable=False)
    role: Mapped[str] = mapped_column(sa.String(32), nullable=False, default="primary")
    original_name: Mapped[str] = mapped_column(sa.String(500), nullable=False)
    content_type: Mapped[str] = mapped_column(sa.String(128), nullable=False)
    size_bytes: Mapped[int] = mapped_column(sa.BigInteger, nullable=False, default=0)
    sha256: Mapped[str] = mapped_column(sa.String(64), nullable=False, default="")
    storage_key: Mapped[str] = mapped_column(sa.String(500), nullable=False)
    duration_ms: Mapped[int] = mapped_column(sa.BigInteger, nullable=False, default=0)
    start_offset_ms: Mapped[int] = mapped_column(sa.BigInteger, nullable=False, default=0)
    sample_rate: Mapped[int | None] = mapped_column(sa.Integer)
    channels: Mapped[int | None] = mapped_column(sa.Integer)
    probe_json: Mapped[dict[str, Any]] = mapped_column(sa.JSON, nullable=False, default=dict)
    created_at: Mapped[datetime] = mapped_column(TS, nullable=False, server_default=sa.func.now())
    updated_at: Mapped[datetime] = mapped_column(
        TS, nullable=False, server_default=sa.func.now(), onupdate=sa.func.now()
    )

    meeting: Mapped[Meeting] = relationship(back_populates="media_assets")

    __table_args__ = (sa.Index("ix_media_assets_meeting", "meeting_id", "kind", "start_offset_ms"),)


class Transcript(Base):
    __tablename__ = "transcripts"

    id: Mapped[uuid.UUID] = mapped_column(sa.Uuid(as_uuid=True), primary_key=True, default=uuid.uuid4)
    meeting_id: Mapped[uuid.UUID] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("meetings.id", ondelete="CASCADE"), nullable=False
    )
    version: Mapped[int] = mapped_column(sa.Integer, nullable=False)
    source: Mapped[str] = mapped_column(sa.String(32), nullable=False)
    provider: Mapped[str] = mapped_column(sa.String(64), nullable=False, default="client")
    model: Mapped[str] = mapped_column(sa.String(200), nullable=False, default="")
    language: Mapped[str] = mapped_column(sa.String(8), nullable=False, default="")
    is_mock: Mapped[bool] = mapped_column(sa.Boolean, nullable=False, default=False)
    is_active: Mapped[bool] = mapped_column(sa.Boolean, nullable=False, default=True)
    segment_count: Mapped[int] = mapped_column(sa.Integer, nullable=False, default=0)
    created_at: Mapped[datetime] = mapped_column(TS, nullable=False, server_default=sa.func.now())
    updated_at: Mapped[datetime] = mapped_column(
        TS, nullable=False, server_default=sa.func.now(), onupdate=sa.func.now()
    )

    meeting: Mapped[Meeting] = relationship(back_populates="transcripts")
    segments: Mapped[list[TranscriptSegment]] = relationship(back_populates="transcript", cascade="all, delete-orphan")

    __table_args__ = (
        sa.UniqueConstraint("meeting_id", "version", name="uq_transcripts_meeting_version"),
        sa.Index("ix_transcripts_meeting_active", "meeting_id", "is_active"),
    )


class TranscriptSegment(Base):
    __tablename__ = "transcript_segments"

    id: Mapped[uuid.UUID] = mapped_column(sa.Uuid(as_uuid=True), primary_key=True, default=uuid.uuid4)
    transcript_id: Mapped[uuid.UUID] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("transcripts.id", ondelete="CASCADE"), nullable=False
    )
    meeting_id: Mapped[uuid.UUID] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("meetings.id", ondelete="CASCADE"), nullable=False
    )
    speaker_id: Mapped[uuid.UUID | None] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("speakers.id", ondelete="SET NULL")
    )
    speaker_label: Mapped[str] = mapped_column(sa.String(200), nullable=False, default="")
    start_ms: Mapped[int] = mapped_column(sa.BigInteger, nullable=False, default=0)
    end_ms: Mapped[int] = mapped_column(sa.BigInteger, nullable=False, default=0)
    text: Mapped[str] = mapped_column(sa.Text, nullable=False, default="")
    confidence: Mapped[float | None] = mapped_column(sa.Float)
    is_final: Mapped[bool] = mapped_column(sa.Boolean, nullable=False, default=True)
    source: Mapped[str] = mapped_column(sa.String(16), nullable=False, default="asr")
    created_at: Mapped[datetime] = mapped_column(TS, nullable=False, server_default=sa.func.now())
    updated_at: Mapped[datetime] = mapped_column(
        TS, nullable=False, server_default=sa.func.now(), onupdate=sa.func.now()
    )

    transcript: Mapped[Transcript] = relationship(back_populates="segments")

    __table_args__ = (
        sa.Index("ix_segments_transcript_start", "transcript_id", "start_ms"),
        sa.Index("ix_segments_meeting_start", "meeting_id", "start_ms"),
    )


class Speaker(Base):
    __tablename__ = "speakers"

    id: Mapped[uuid.UUID] = mapped_column(sa.Uuid(as_uuid=True), primary_key=True, default=uuid.uuid4)
    meeting_id: Mapped[uuid.UUID] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("meetings.id", ondelete="CASCADE"), nullable=False
    )
    label: Mapped[str] = mapped_column(sa.String(200), nullable=False)
    kind: Mapped[str] = mapped_column(sa.String(16), nullable=False, default="diarized")
    color: Mapped[str] = mapped_column(sa.String(16), nullable=False, default="")
    created_at: Mapped[datetime] = mapped_column(TS, nullable=False, server_default=sa.func.now())
    updated_at: Mapped[datetime] = mapped_column(
        TS, nullable=False, server_default=sa.func.now(), onupdate=sa.func.now()
    )

    meeting: Mapped[Meeting] = relationship(back_populates="speakers")

    __table_args__ = (sa.UniqueConstraint("meeting_id", "label", name="uq_speakers_meeting_label"),)


class Job(Base):
    __tablename__ = "jobs"

    id: Mapped[uuid.UUID] = mapped_column(sa.Uuid(as_uuid=True), primary_key=True, default=uuid.uuid4)
    meeting_id: Mapped[uuid.UUID | None] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("meetings.id", ondelete="CASCADE")
    )
    type: Mapped[str] = mapped_column(sa.String(32), nullable=False)
    state: Mapped[str] = mapped_column(sa.String(16), nullable=False, default="queued")
    progress: Mapped[float] = mapped_column(sa.Float, nullable=False, default=0.0)
    attempts: Mapped[int] = mapped_column(sa.Integer, nullable=False, default=0)
    max_attempts: Mapped[int] = mapped_column(sa.Integer, nullable=False, default=3)
    priority: Mapped[int] = mapped_column(sa.Integer, nullable=False, default=100)
    idempotency_key: Mapped[str | None] = mapped_column(sa.String(200))
    params_json: Mapped[dict[str, Any]] = mapped_column(sa.JSON, nullable=False, default=dict)
    result_json: Mapped[dict[str, Any]] = mapped_column(sa.JSON, nullable=False, default=dict)
    error: Mapped[str | None] = mapped_column(sa.Text)
    is_mock: Mapped[bool] = mapped_column(sa.Boolean, nullable=False, default=False)
    run_after: Mapped[datetime] = mapped_column(TS, nullable=False, server_default=sa.func.now())
    started_at: Mapped[datetime | None] = mapped_column(TS)
    finished_at: Mapped[datetime | None] = mapped_column(TS)
    heartbeat_at: Mapped[datetime | None] = mapped_column(TS)
    created_at: Mapped[datetime] = mapped_column(TS, nullable=False, server_default=sa.func.now())
    updated_at: Mapped[datetime] = mapped_column(
        TS, nullable=False, server_default=sa.func.now(), onupdate=sa.func.now()
    )

    __table_args__ = (
        sa.UniqueConstraint("idempotency_key", name="uq_jobs_idempotency_key"),
        sa.Index("ix_jobs_claim", "state", "run_after", "priority"),
    )


class JobEvent(Base):
    __tablename__ = "job_events"

    id: Mapped[int] = mapped_column(
        sa.BigInteger().with_variant(sa.Integer, "sqlite"), primary_key=True, autoincrement=True
    )
    job_id: Mapped[uuid.UUID] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("jobs.id", ondelete="CASCADE"), nullable=False
    )
    meeting_id: Mapped[uuid.UUID | None] = mapped_column(sa.Uuid(as_uuid=True))
    at: Mapped[datetime] = mapped_column(TS, nullable=False, server_default=sa.func.now())
    level: Mapped[str] = mapped_column(sa.String(16), nullable=False, default="info")
    message: Mapped[str] = mapped_column(sa.String(1000), nullable=False)
    data_json: Mapped[dict[str, Any]] = mapped_column(sa.JSON, nullable=False, default=dict)

    __table_args__ = (sa.Index("ix_job_events_job", "job_id", "id"),)


class AnalysisRun(Base):
    __tablename__ = "analysis_runs"

    id: Mapped[uuid.UUID] = mapped_column(sa.Uuid(as_uuid=True), primary_key=True, default=uuid.uuid4)
    meeting_id: Mapped[uuid.UUID] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("meetings.id", ondelete="CASCADE"), nullable=False
    )
    transcript_id: Mapped[uuid.UUID] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("transcripts.id", ondelete="CASCADE"), nullable=False
    )
    provider: Mapped[str] = mapped_column(sa.String(64), nullable=False)
    model: Mapped[str] = mapped_column(sa.String(200), nullable=False, default="")
    prompt_version: Mapped[str] = mapped_column(sa.String(32), nullable=False, default="v1")
    is_mock: Mapped[bool] = mapped_column(sa.Boolean, nullable=False, default=False)
    status: Mapped[str] = mapped_column(sa.String(16), nullable=False, default="succeeded")
    created_at: Mapped[datetime] = mapped_column(TS, nullable=False, server_default=sa.func.now())


class Section(Base):
    __tablename__ = "sections"

    id: Mapped[uuid.UUID] = mapped_column(sa.Uuid(as_uuid=True), primary_key=True, default=uuid.uuid4)
    meeting_id: Mapped[uuid.UUID] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("meetings.id", ondelete="CASCADE"), nullable=False
    )
    analysis_run_id: Mapped[uuid.UUID] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("analysis_runs.id", ondelete="CASCADE"), nullable=False
    )
    ordinal: Mapped[int] = mapped_column(sa.Integer, nullable=False)
    title: Mapped[str] = mapped_column(sa.String(300), nullable=False)
    topic: Mapped[str] = mapped_column(sa.String(200), nullable=False, default="")
    start_ms: Mapped[int] = mapped_column(sa.BigInteger, nullable=False, default=0)
    end_ms: Mapped[int] = mapped_column(sa.BigInteger, nullable=False, default=0)
    summary: Mapped[str] = mapped_column(sa.Text, nullable=False, default="")
    is_mock: Mapped[bool] = mapped_column(sa.Boolean, nullable=False, default=False)

    __table_args__ = (sa.Index("ix_sections_meeting_ordinal", "meeting_id", "ordinal"),)


class AnalysisDocument(Base):
    __tablename__ = "analysis_documents"

    id: Mapped[uuid.UUID] = mapped_column(sa.Uuid(as_uuid=True), primary_key=True, default=uuid.uuid4)
    meeting_id: Mapped[uuid.UUID] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("meetings.id", ondelete="CASCADE"), nullable=False
    )
    analysis_run_id: Mapped[uuid.UUID] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("analysis_runs.id", ondelete="CASCADE"), nullable=False
    )
    section_id: Mapped[uuid.UUID | None] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("sections.id", ondelete="CASCADE")
    )
    kind: Mapped[str] = mapped_column(sa.String(32), nullable=False)
    payload_json: Mapped[dict[str, Any]] = mapped_column(sa.JSON, nullable=False, default=dict)
    transcript_id: Mapped[uuid.UUID] = mapped_column(
        sa.Uuid(as_uuid=True), sa.ForeignKey("transcripts.id", ondelete="CASCADE"), nullable=False
    )
    stale: Mapped[bool] = mapped_column(sa.Boolean, nullable=False, default=False)
    is_mock: Mapped[bool] = mapped_column(sa.Boolean, nullable=False, default=False)
    created_at: Mapped[datetime] = mapped_column(TS, nullable=False, server_default=sa.func.now())
    updated_at: Mapped[datetime] = mapped_column(
        TS, nullable=False, server_default=sa.func.now(), onupdate=sa.func.now()
    )

    __table_args__ = (
        sa.Index("ix_docs_meeting_kind", "meeting_id", "kind", "section_id"),
        sa.Index("ix_docs_transcript", "transcript_id"),
    )
