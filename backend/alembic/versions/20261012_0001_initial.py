"""initial schema for every table

Revision ID: 20261012_0001
Revises:
Create Date: 2026-10-12

Hand-written on purpose: autogenerate emits PG-only types on a postgres URL and
BigInteger identity quirks on SQLite. Every column here mirrors src/mvb/db/models.py.
"""

from __future__ import annotations

import sqlalchemy as sa
from alembic import op

revision = "20261012_0001"
down_revision = None
branch_labels = None
depends_on = None

TS = sa.DateTime(timezone=True)
UUID = sa.Uuid(as_uuid=True)


def upgrade() -> None:
    op.create_table(
        "meetings",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("title", sa.String(500), nullable=False),
        sa.Column("started_at", TS),
        sa.Column("ended_at", TS),
        sa.Column("duration_ms", sa.BigInteger, nullable=False, server_default="0"),
        sa.Column("status", sa.String(32), nullable=False, server_default="uploaded"),
        sa.Column("language", sa.String(8), nullable=False, server_default=""),
        sa.Column("source", sa.String(32), nullable=False, server_default="meetvault"),
        sa.Column("external_id", sa.String(200)),
        sa.Column("metadata_json", sa.JSON, nullable=False),
        sa.Column("deleted_at", TS),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", TS, nullable=False, server_default=sa.func.now()),
        sa.UniqueConstraint("source", "external_id", name="uq_meetings_source_external"),
    )

    op.create_table(
        "media_assets",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("meeting_id", UUID, sa.ForeignKey("meetings.id", ondelete="CASCADE"), nullable=False),
        sa.Column("kind", sa.String(16), nullable=False),
        sa.Column("role", sa.String(32), nullable=False, server_default="primary"),
        sa.Column("original_name", sa.String(500), nullable=False),
        sa.Column("content_type", sa.String(128), nullable=False),
        sa.Column("size_bytes", sa.BigInteger, nullable=False, server_default="0"),
        sa.Column("sha256", sa.String(64), nullable=False, server_default=""),
        sa.Column("storage_key", sa.String(500), nullable=False),
        sa.Column("duration_ms", sa.BigInteger, nullable=False, server_default="0"),
        sa.Column("start_offset_ms", sa.BigInteger, nullable=False, server_default="0"),
        sa.Column("sample_rate", sa.Integer),
        sa.Column("channels", sa.Integer),
        sa.Column("probe_json", sa.JSON, nullable=False),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", TS, nullable=False, server_default=sa.func.now()),
    )
    op.create_index("ix_media_assets_meeting", "media_assets", ["meeting_id", "kind", "start_offset_ms"])

    op.create_table(
        "transcripts",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("meeting_id", UUID, sa.ForeignKey("meetings.id", ondelete="CASCADE"), nullable=False),
        sa.Column("version", sa.Integer, nullable=False),
        sa.Column("source", sa.String(32), nullable=False),
        sa.Column("provider", sa.String(64), nullable=False, server_default="client"),
        sa.Column("model", sa.String(200), nullable=False, server_default=""),
        sa.Column("language", sa.String(8), nullable=False, server_default=""),
        sa.Column("is_mock", sa.Boolean, nullable=False, server_default=sa.false()),
        sa.Column("is_active", sa.Boolean, nullable=False, server_default=sa.true()),
        sa.Column("segment_count", sa.Integer, nullable=False, server_default="0"),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", TS, nullable=False, server_default=sa.func.now()),
        sa.UniqueConstraint("meeting_id", "version", name="uq_transcripts_meeting_version"),
    )
    op.create_index("ix_transcripts_meeting_active", "transcripts", ["meeting_id", "is_active"])

    op.create_table(
        "speakers",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("meeting_id", UUID, sa.ForeignKey("meetings.id", ondelete="CASCADE"), nullable=False),
        sa.Column("label", sa.String(200), nullable=False),
        sa.Column("kind", sa.String(16), nullable=False, server_default="diarized"),
        sa.Column("color", sa.String(16), nullable=False, server_default=""),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", TS, nullable=False, server_default=sa.func.now()),
        sa.UniqueConstraint("meeting_id", "label", name="uq_speakers_meeting_label"),
    )

    op.create_table(
        "transcript_segments",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("transcript_id", UUID, sa.ForeignKey("transcripts.id", ondelete="CASCADE"), nullable=False),
        sa.Column("meeting_id", UUID, sa.ForeignKey("meetings.id", ondelete="CASCADE"), nullable=False),
        sa.Column("speaker_id", UUID, sa.ForeignKey("speakers.id", ondelete="SET NULL")),
        sa.Column("speaker_label", sa.String(200), nullable=False, server_default=""),
        sa.Column("start_ms", sa.BigInteger, nullable=False, server_default="0"),
        sa.Column("end_ms", sa.BigInteger, nullable=False, server_default="0"),
        sa.Column("text", sa.Text, nullable=False, server_default=""),
        sa.Column("confidence", sa.Float),
        sa.Column("is_final", sa.Boolean, nullable=False, server_default=sa.true()),
        sa.Column("source", sa.String(16), nullable=False, server_default="asr"),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", TS, nullable=False, server_default=sa.func.now()),
    )
    op.create_index("ix_segments_transcript_start", "transcript_segments", ["transcript_id", "start_ms"])
    op.create_index("ix_segments_meeting_start", "transcript_segments", ["meeting_id", "start_ms"])

    op.create_table(
        "jobs",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("meeting_id", UUID, sa.ForeignKey("meetings.id", ondelete="CASCADE")),
        sa.Column("type", sa.String(32), nullable=False),
        sa.Column("state", sa.String(16), nullable=False, server_default="queued"),
        sa.Column("progress", sa.Float, nullable=False, server_default="0"),
        sa.Column("attempts", sa.Integer, nullable=False, server_default="0"),
        sa.Column("max_attempts", sa.Integer, nullable=False, server_default="3"),
        sa.Column("priority", sa.Integer, nullable=False, server_default="100"),
        sa.Column("idempotency_key", sa.String(200)),
        sa.Column("params_json", sa.JSON, nullable=False),
        sa.Column("result_json", sa.JSON, nullable=False),
        sa.Column("error", sa.Text),
        sa.Column("is_mock", sa.Boolean, nullable=False, server_default=sa.false()),
        sa.Column("run_after", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("started_at", TS),
        sa.Column("finished_at", TS),
        sa.Column("heartbeat_at", TS),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", TS, nullable=False, server_default=sa.func.now()),
        sa.UniqueConstraint("idempotency_key", name="uq_jobs_idempotency_key"),
    )
    op.create_index("ix_jobs_claim", "jobs", ["state", "run_after", "priority"])

    op.create_table(
        "job_events",
        sa.Column("id", sa.BigInteger().with_variant(sa.Integer, "sqlite"), primary_key=True, autoincrement=True),
        sa.Column("job_id", UUID, sa.ForeignKey("jobs.id", ondelete="CASCADE"), nullable=False),
        sa.Column("meeting_id", UUID),
        sa.Column("at", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("level", sa.String(16), nullable=False, server_default="info"),
        sa.Column("message", sa.String(1000), nullable=False),
        sa.Column("data_json", sa.JSON, nullable=False),
    )
    op.create_index("ix_job_events_job", "job_events", ["job_id", "id"])

    op.create_table(
        "analysis_runs",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("meeting_id", UUID, sa.ForeignKey("meetings.id", ondelete="CASCADE"), nullable=False),
        sa.Column("transcript_id", UUID, sa.ForeignKey("transcripts.id", ondelete="CASCADE"), nullable=False),
        sa.Column("provider", sa.String(64), nullable=False),
        sa.Column("model", sa.String(200), nullable=False, server_default=""),
        sa.Column("prompt_version", sa.String(32), nullable=False, server_default="v1"),
        sa.Column("is_mock", sa.Boolean, nullable=False, server_default=sa.false()),
        sa.Column("status", sa.String(16), nullable=False, server_default="succeeded"),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
    )

    op.create_table(
        "sections",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("meeting_id", UUID, sa.ForeignKey("meetings.id", ondelete="CASCADE"), nullable=False),
        sa.Column("analysis_run_id", UUID, sa.ForeignKey("analysis_runs.id", ondelete="CASCADE"), nullable=False),
        sa.Column("ordinal", sa.Integer, nullable=False),
        sa.Column("title", sa.String(300), nullable=False),
        sa.Column("topic", sa.String(200), nullable=False, server_default=""),
        sa.Column("start_ms", sa.BigInteger, nullable=False, server_default="0"),
        sa.Column("end_ms", sa.BigInteger, nullable=False, server_default="0"),
        sa.Column("summary", sa.Text, nullable=False, server_default=""),
        sa.Column("is_mock", sa.Boolean, nullable=False, server_default=sa.false()),
    )
    op.create_index("ix_sections_meeting_ordinal", "sections", ["meeting_id", "ordinal"])

    op.create_table(
        "analysis_documents",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("meeting_id", UUID, sa.ForeignKey("meetings.id", ondelete="CASCADE"), nullable=False),
        sa.Column("analysis_run_id", UUID, sa.ForeignKey("analysis_runs.id", ondelete="CASCADE"), nullable=False),
        sa.Column("section_id", UUID, sa.ForeignKey("sections.id", ondelete="CASCADE")),
        sa.Column("kind", sa.String(32), nullable=False),
        sa.Column("payload_json", sa.JSON, nullable=False),
        sa.Column("transcript_id", UUID, sa.ForeignKey("transcripts.id", ondelete="CASCADE"), nullable=False),
        sa.Column("stale", sa.Boolean, nullable=False, server_default=sa.false()),
        sa.Column("is_mock", sa.Boolean, nullable=False, server_default=sa.false()),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", TS, nullable=False, server_default=sa.func.now()),
    )
    op.create_index("ix_docs_meeting_kind", "analysis_documents", ["meeting_id", "kind", "section_id"])
    op.create_index("ix_docs_transcript", "analysis_documents", ["transcript_id"])


def downgrade() -> None:
    for table in (
        "analysis_documents",
        "sections",
        "analysis_runs",
        "job_events",
        "jobs",
        "speakers",
        "transcript_segments",
        "transcripts",
        "media_assets",
        "meetings",
    ):
        op.drop_table(table)
