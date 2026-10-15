"""users + auth_tokens + meetings.user_id

Revision ID: 20261014_0002
Revises: 20261012_0001
Create Date: 2026-10-14

Hand-written: mirrors src/mvb/db/models.py (see 20261012_0001 for why).
"""

from __future__ import annotations

import sqlalchemy as sa
from alembic import op

revision = "20261014_0002"
down_revision = "20261012_0001"
branch_labels = None
depends_on = None

TS = sa.DateTime(timezone=True)
UUID = sa.Uuid(as_uuid=True)


def upgrade() -> None:
    op.create_table(
        "users",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("email", sa.String(320), nullable=False),
        sa.Column("display_name", sa.String(200), nullable=False, server_default=""),
        sa.Column("password_hash", sa.String(500), nullable=False),
        sa.Column("is_active", sa.Boolean, nullable=False, server_default=sa.true()),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", TS, nullable=False, server_default=sa.func.now()),
        sa.UniqueConstraint("email", name="uq_users_email"),
    )
    op.create_table(
        "auth_tokens",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("user_id", UUID, sa.ForeignKey("users.id", ondelete="CASCADE"), nullable=False),
        sa.Column("token_hash", sa.String(64), nullable=False),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("expires_at", TS, nullable=False),
        sa.Column("last_used_at", TS),
        sa.Column("revoked_at", TS),
        sa.UniqueConstraint("token_hash", name="uq_auth_tokens_hash"),
    )
    with op.batch_alter_table("meetings") as batch:  # batch = SQLite table-recreate, plain ALTER on PG
        batch.add_column(sa.Column("user_id", UUID, nullable=True))
        batch.create_foreign_key("fk_meetings_user", "users", ["user_id"], ["id"], ondelete="CASCADE")
        batch.create_index("ix_meetings_user", ["user_id"])


def downgrade() -> None:
    with op.batch_alter_table("meetings") as batch:
        batch.drop_index("ix_meetings_user")
        batch.drop_constraint("fk_meetings_user", type_="foreignkey")
        batch.drop_column("user_id")
    op.drop_table("auth_tokens")
    op.drop_table("users")
