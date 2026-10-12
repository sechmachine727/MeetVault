from __future__ import annotations

import uuid

from sqlalchemy import update
from sqlalchemy.orm import Session

from mvb.db.models import AnalysisDocument


def mark_documents_stale(session: Session, meeting_id: uuid.UUID, *, active_transcript_id: uuid.UUID | None) -> int:
    """Docs whose transcript is no longer the active one become stale.

    Returns how many rows changed. Docs that already analyse the active transcript are
    left fresh — only a re-analysis supersedes them.
    """
    statement = update(AnalysisDocument).where(AnalysisDocument.meeting_id == meeting_id)
    if active_transcript_id is not None:
        statement = statement.where(AnalysisDocument.transcript_id != active_transcript_id)
    statement = statement.where(AnalysisDocument.stale.is_(False)).values(stale=True)
    result = session.execute(statement)
    return int(result.rowcount or 0)


def mark_all_stale(session: Session, meeting_id: uuid.UUID) -> int:
    """The media changed under the analysis: every document is stale."""
    result = session.execute(
        update(AnalysisDocument)
        .where(AnalysisDocument.meeting_id == meeting_id, AnalysisDocument.stale.is_(False))
        .values(stale=True)
    )
    return int(result.rowcount or 0)
