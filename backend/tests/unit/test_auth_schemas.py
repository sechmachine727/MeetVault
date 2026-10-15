from __future__ import annotations

import pytest
from pydantic import ValidationError

from mvb.schemas.auth import RegisterIn


def test_register_in_defaults_the_display_name() -> None:
    payload = RegisterIn(email="a@example.com", password="long-enough-1")
    assert payload.display_name == ""


def test_register_in_rejects_an_overlong_email() -> None:
    with pytest.raises(ValidationError):
        RegisterIn(email="a" * 400 + "@example.com", password="long-enough-1")


def test_register_in_rejects_an_overlong_display_name() -> None:
    with pytest.raises(ValidationError):
        RegisterIn(email="a@example.com", password="long-enough-1", display_name="x" * 201)


def test_register_in_accepts_a_short_password_on_purpose() -> None:
    """Strength is a 400 from mvb.accounts (a readable sentence), never a pydantic 422."""
    payload = RegisterIn(email="a@example.com", password="short")
    assert payload.password == "short"
