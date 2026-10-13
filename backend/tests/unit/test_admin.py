from __future__ import annotations

from mvb.admin import main


def test_create_user_via_cli(capsys, tmp_path, monkeypatch):
    from tests.conftest import prepare_schema

    prepare_schema()
    # conftest's _clean_env gives every test a fresh sqlite db + media root already.
    code = main(["create-user", "--email", "Owner@Example.com", "--password", "long-enough-1"])
    assert code == 0
    from sqlalchemy import select

    from mvb.db.base import get_session_factory
    from mvb.db.models import User
    from mvb.security import verify_password

    with get_session_factory()() as session:
        user = session.scalar(select(User).where(User.email == "owner@example.com"))
    assert user is not None
    assert verify_password("long-enough-1", user.password_hash)


def test_create_user_duplicate_fails() -> None:
    from tests.conftest import prepare_schema

    prepare_schema()
    assert main(["create-user", "--email", "a@example.com", "--password", "long-enough-1"]) == 0
    assert main(["create-user", "--email", "A@example.com", "--password", "long-enough-2"]) == 1


def test_short_password_rejected() -> None:
    assert main(["create-user", "--email", "a@example.com", "--password", "short"]) == 1


def test_adopt_meetings_assigns_null_owner() -> None:
    from mvb.db.base import get_session_factory
    from mvb.db.models import Meeting
    from tests.conftest import prepare_schema

    prepare_schema()
    with get_session_factory()() as session:
        session.add(Meeting(title="legacy", status="uploaded"))
        session.commit()
    assert main(["create-user", "--email", "own@example.com", "--password", "long-enough-1"]) == 0
    assert main(["adopt-meetings", "--email", "own@example.com"]) == 0
    with get_session_factory()() as session:
        meeting = session.query(Meeting).one()
    assert meeting.user_id is not None
