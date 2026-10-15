from __future__ import annotations

import pytest

from mvb.accounts import EmailTaken, InvalidRegistration, create_user, normalize_email
from mvb.security import verify_password


def test_normalize_email_strips_and_lowers() -> None:
    assert normalize_email("  Owner@Example.COM ") == "owner@example.com"


def test_create_user_stores_a_verifiable_hash(db_session) -> None:
    user = create_user(db_session, email="Owner@Example.com", password="long-enough-1", display_name="Owner")
    assert user.email == "owner@example.com"
    assert user.display_name == "Owner"
    assert user.is_active is True
    assert verify_password("long-enough-1", user.password_hash)


def test_display_name_defaults_to_the_local_part(db_session) -> None:
    user = create_user(db_session, email="demo@example.com", password="long-enough-1")
    assert user.display_name == "demo"


def test_duplicate_email_is_refused_case_insensitively(db_session) -> None:
    create_user(db_session, email="a@example.com", password="long-enough-1")
    with pytest.raises(EmailTaken):
        create_user(db_session, email="A@Example.com", password="long-enough-2")


@pytest.mark.parametrize(
    "email", ["", "nope", "no@domain", "a b@example.com", "@example.com", "a@.com", "a@example.", "a@b@c.com"]
)
def test_bad_emails_are_refused(db_session, email: str) -> None:
    with pytest.raises(InvalidRegistration) as exc:
        create_user(db_session, email=email, password="long-enough-1")
    assert exc.value.code == "invalid_email"


def test_short_password_is_refused(db_session) -> None:
    with pytest.raises(InvalidRegistration) as exc:
        create_user(db_session, email="a@example.com", password="short")
    assert exc.value.code == "weak_password"
    assert "8" in exc.value.message


def test_the_unique_constraint_race_also_raises_email_taken(db_session, monkeypatch) -> None:
    """Two concurrent registers: the pre-check passes for both, the INSERT loses the race."""
    import mvb.accounts as accounts

    monkeypatch.setattr(accounts, "find_user", lambda *_: None)  # pretend both checked first
    accounts.create_user(db_session, email="race@example.com", password="long-enough-1")
    with pytest.raises(accounts.EmailTaken):
        accounts.create_user(db_session, email="race@example.com", password="long-enough-1")
