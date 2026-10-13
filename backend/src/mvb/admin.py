from __future__ import annotations

import argparse
import getpass
import sys

from sqlalchemy import select

from mvb.db.base import session_scope
from mvb.db.models import Meeting, User
from mvb.security import hash_password

MIN_PASSWORD_LENGTH = 8


def _create_user(email: str, password: str, display_name: str) -> int:
    email = email.strip().lower()
    if "@" not in email:
        print(f"invalid email: {email}", file=sys.stderr)
        return 1
    if len(password) < MIN_PASSWORD_LENGTH:
        print(f"password must be at least {MIN_PASSWORD_LENGTH} characters", file=sys.stderr)
        return 1
    with session_scope() as session:
        if session.scalar(select(User).where(User.email == email)) is not None:
            print(f"user already exists: {email}", file=sys.stderr)
            return 1
        session.add(User(email=email, display_name=display_name, password_hash=hash_password(password)))
    print(f"created user: {email}")
    return 0


def _list_users() -> int:
    with session_scope() as session:
        for user in session.scalars(select(User).order_by(User.created_at, User.id)).all():
            print(f"{user.email}\t{user.display_name}\tactive={user.is_active}\t{user.id}")
    return 0


def _adopt_meetings(email: str) -> int:
    email = email.strip().lower()
    with session_scope() as session:
        user = session.scalar(select(User).where(User.email == email))
        if user is None:
            print(f"no such user: {email}", file=sys.stderr)
            return 1
        rows = session.scalars(select(Meeting).where(Meeting.user_id.is_(None))).all()
        for meeting in rows:
            meeting.user_id = user.id
        print(f"adopted {len(rows)} meeting(s) for {email}")
    return 0


def _prompt_password() -> str:
    first = getpass.getpass("Password: ")
    second = getpass.getpass("Repeat password: ")
    if first != second:
        print("passwords do not match", file=sys.stderr)
        raise SystemExit(1)
    return first


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="mvb.admin", description="MeetVault backend administration")
    sub = parser.add_subparsers(dest="command", required=True)

    create = sub.add_parser("create-user", help="create a user account")
    create.add_argument("--email", required=True)
    create.add_argument("--name", default="")
    create.add_argument("--password", default=None, help="omit to get a secure prompt")

    sub.add_parser("list-users", help="list user accounts")

    adopt = sub.add_parser("adopt-meetings", help="assign meetings without an owner to a user")
    adopt.add_argument("--email", required=True)

    args = parser.parse_args(argv)
    if args.command == "create-user":
        password = args.password or _prompt_password()
        return _create_user(args.email, password, args.name)
    if args.command == "list-users":
        return _list_users()
    if args.command == "adopt-meetings":
        return _adopt_meetings(args.email)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
