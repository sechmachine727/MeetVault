from __future__ import annotations

import uuid

from mvb.db.models import Meeting
from mvb.jobs.queue import complete, enqueue


def _meeting(db_session, title: str = "standup", user_id=None) -> Meeting:  # type: ignore[no-untyped-def]
    meeting = Meeting(id=uuid.uuid4(), title=title, user_id=user_id)
    db_session.add(meeting)
    db_session.commit()
    return meeting


def test_missing_bearer_key_is_401_on_every_route(client) -> None:  # type: ignore[no-untyped-def]
    job_id = uuid.uuid4()
    meeting_id = uuid.uuid4()
    assert client.get(f"/v1/jobs/{job_id}").status_code == 401
    assert client.get(f"/v1/jobs/{job_id}/events").status_code == 401
    assert client.post(f"/v1/jobs/{job_id}/cancel").status_code == 401
    assert client.get(f"/v1/meetings/{meeting_id}/jobs").status_code == 401


def test_unknown_job_is_404_with_envelope(client, auth_headers) -> None:  # type: ignore[no-untyped-def]
    job_id = uuid.uuid4()
    detail = client.get(f"/v1/jobs/{job_id}", headers=auth_headers)
    assert detail.status_code == 404
    assert detail.json()["error"]["code"] == "not_found"
    assert "request_id" in detail.json()
    assert client.post(f"/v1/jobs/{job_id}/cancel", headers=auth_headers).status_code == 404
    assert client.get(f"/v1/jobs/{job_id}/events", headers=auth_headers).status_code == 404


def test_job_detail_includes_events(client, auth_headers, auth_user, db_session) -> None:  # type: ignore[no-untyped-def]
    meeting = _meeting(db_session, user_id=auth_user.id)
    job = enqueue(db_session, job_type="media_probe", meeting_id=meeting.id, params={"a": 1})
    db_session.commit()
    response = client.get(f"/v1/jobs/{job.id}", headers=auth_headers)
    assert response.status_code == 200
    body = response.json()
    assert body["id"] == str(job.id)
    assert body["type"] == "media_probe"
    assert body["state"] == "queued"
    assert body["result"] == {}
    assert len(body["events"]) >= 1
    first = body["events"][0]
    assert set(first) >= {"id", "at", "level", "message", "data"}
    assert body["events"][-1]["message"] == "queued media_probe"


def test_cancel_queued_job(client, auth_headers, auth_user, db_session) -> None:  # type: ignore[no-untyped-def]
    meeting = _meeting(db_session, user_id=auth_user.id)
    job = enqueue(db_session, job_type="transcribe", meeting_id=meeting.id)
    db_session.commit()
    response = client.post(f"/v1/jobs/{job.id}/cancel", headers=auth_headers)
    assert response.status_code == 200
    body = response.json()
    assert body["state"] == "cancelled"
    assert body["finished_at"] is not None


def test_cancel_already_succeeded_job_is_unchanged(client, auth_headers, auth_user, db_session) -> None:  # type: ignore[no-untyped-def]
    meeting = _meeting(db_session, user_id=auth_user.id)
    job = enqueue(db_session, job_type="transcribe", meeting_id=meeting.id)
    db_session.commit()
    complete(db_session, job, result={"ok": True})
    db_session.commit()
    response = client.post(f"/v1/jobs/{job.id}/cancel", headers=auth_headers)
    assert response.status_code == 200
    assert response.json()["state"] == "succeeded"


def test_meeting_jobs_list_is_paginated(client, auth_headers, auth_user, db_session) -> None:  # type: ignore[no-untyped-def]
    meeting = _meeting(db_session, user_id=auth_user.id)
    other = _meeting(db_session, "other", user_id=auth_user.id)
    enqueue(db_session, job_type="transcribe", meeting_id=meeting.id)
    enqueue(db_session, job_type="analyze", meeting_id=meeting.id)
    enqueue(db_session, job_type="transcribe", meeting_id=other.id)
    db_session.commit()
    response = client.get(f"/v1/meetings/{meeting.id}/jobs", headers=auth_headers)
    assert response.status_code == 200
    body = response.json()
    assert set(body) == {"items", "total", "limit", "offset"}
    assert body["total"] == 2
    assert body["limit"] == 50
    assert body["offset"] == 0
    assert [item["meeting_id"] for item in body["items"]] == [str(meeting.id)] * 2
    assert {item["type"] for item in body["items"]} == {"transcribe", "analyze"}


def test_job_events_stream(client, auth_headers, auth_user, db_session) -> None:  # type: ignore[no-untyped-def]
    meeting = _meeting(db_session, user_id=auth_user.id)
    job = enqueue(db_session, job_type="media_probe", meeting_id=meeting.id)
    db_session.commit()
    complete(db_session, job, result={})
    db_session.commit()
    response = client.get(f"/v1/jobs/{job.id}/events", headers=auth_headers)
    assert response.status_code == 200
    assert response.headers["content-type"].startswith("text/event-stream")
    assert "data: " in response.text
