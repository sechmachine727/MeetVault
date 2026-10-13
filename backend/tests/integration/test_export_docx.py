from __future__ import annotations

from io import BytesIO

from docx import Document


def test_export_requires_analysis(client, auth_headers) -> None:
    created = client.post("/v1/meetings", json={"title": "No docs"}, headers=auth_headers).json()
    response = client.get(f"/v1/meetings/{created['id']}/export.docx", headers=auth_headers)
    assert response.status_code == 409
    assert response.json()["error"]["code"] == "no_analysis"


def test_export_returns_structured_docx(client, auth_headers, db_session) -> None:
    # Seed a summary document directly (the full mock pipeline path is covered by test_full_flow).
    import uuid as _uuid

    from mvb.db.models import AnalysisDocument, AnalysisRun, Meeting, Transcript

    created = client.post("/v1/meetings", json={"title": "Export Demo"}, headers=auth_headers).json()
    meeting = db_session.get(Meeting, _uuid.UUID(created["id"]))
    transcript = Transcript(id=_uuid.uuid4(), meeting_id=meeting.id, version=1, source="live_export", segment_count=1)
    run = AnalysisRun(
        id=_uuid.uuid4(), meeting_id=meeting.id, transcript_id=transcript.id, provider="mock", is_mock=True
    )
    doc = AnalysisDocument(
        id=_uuid.uuid4(),
        meeting_id=meeting.id,
        analysis_run_id=run.id,
        kind="summary",
        transcript_id=transcript.id,
        payload_json={"text": "Overview text", "bullets": ["Point one"]},
        is_mock=True,
        stale=False,
    )
    # Flush in dependency order: the three mappers have no relationship() between them, so
    # SQLAlchemy's unit of work would otherwise insert them alphabetically and trip the FK.
    db_session.add(transcript)
    db_session.flush()
    db_session.add(run)
    db_session.flush()
    db_session.add(doc)
    db_session.commit()
    response = client.get(f"/v1/meetings/{created['id']}/export.docx", headers=auth_headers)
    assert response.status_code == 200
    assert response.headers["content-type"].startswith("application/vnd.openxmlformats")
    assert "Export-Demo.docx" in response.headers["content-disposition"]
    parsed = Document(BytesIO(response.content))
    texts = [p.text for p in parsed.paragraphs]
    assert "Export Demo" in texts
    assert "Overview text" in texts and "Point one" in texts
