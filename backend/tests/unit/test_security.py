from __future__ import annotations

from mvb.security import hash_password, new_session_token, token_hash, verify_password


def test_hash_roundtrip() -> None:
    stored = hash_password("correct horse battery")
    assert stored.startswith("scrypt$")
    assert verify_password("correct horse battery", stored) is True


def test_wrong_password_is_false() -> None:
    stored = hash_password("right-password")
    assert verify_password("wrong-password", stored) is False


def test_malformed_stored_hash_is_false() -> None:
    assert verify_password("x", "not-a-hash") is False
    assert verify_password("x", "scrypt$bad") is False


def test_session_token_is_random_and_hash_is_stable() -> None:
    raw_a, digest_a = new_session_token()
    raw_b, _ = new_session_token()
    assert raw_a != raw_b
    assert len(digest_a) == 64
    assert token_hash(raw_a) == digest_a
