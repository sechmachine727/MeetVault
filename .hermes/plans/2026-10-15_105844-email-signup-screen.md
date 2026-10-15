# Email sign-up screen for the MeetVault cloud console

Baseline: branch `caption-bar-2026-10-10` @ `26e6255` (working tree clean except the untracked
`.hermes/`, `EVALUATION.md`, `eval-evidence/`). Written 2026-10-15 10:58 +07.

---

## Goal

Let a visitor create an account on the MeetVault web console with an email address and a password,
be signed in immediately, and land on their own (empty) cloud — instead of needing an admin to run
`python -m mvb.admin create-user` on the server.

---

## Current context / assumptions

### The repo (facts, verified by reading it — do not re-guess)

`D:/workspace/MeetVault` is a monorepo: `client/` (WinUI 3 + .NET 8, **not touched by this plan**)
and `backend/` (FastAPI + SQLAlchemy + a static ES-module web console).

**Auth today** — `backend/src/mvb/api/routers/auth.py` has exactly three routes:
`POST /v1/auth/login`, `POST /v1/auth/logout`, `GET /v1/auth/me`. There is **no** register endpoint
and no `register`/`signup`/`invite` code anywhere under `backend/src/mvb/`.

**Accounts today** are provisioned by `backend/src/mvb/admin.py::_create_user`, a CLI:
`python -m mvb.admin create-user --email x --name "X" --password ...` (omit `--password` for a
`getpass` prompt). It enforces: email must contain `@`, password length >= `MIN_PASSWORD_LENGTH = 8`,
email lower-cased, duplicate refused with exit code 1.

**The data model** (`backend/src/mvb/db/models.py`):

```python
class User(Base):
    __tablename__ = "users"
    id: uuid.UUID            # pk, default uuid4
    email: str               # String(320), UNIQUE (uq_users_email)
    display_name: str        # String(200), default ""
    password_hash: str       # String(500) — scrypt string from mvb.security.hash_password
    is_active: bool          # default True
    created_at / updated_at
```

**Password + token primitives** (`backend/src/mvb/security.py`): `hash_password(password)`,
`verify_password(password, stored)`, `new_session_token() -> (raw, sha256_hex)`,
`token_hash(raw)`, `DUMMY_HASH` (timing-equaliser for unknown emails).

**Config** (`backend/src/mvb/config.py`): one pydantic-settings `Settings` class, env prefix `MVB_`,
`extra="ignore"`, `env_file=".env"`, plus `@lru_cache(maxsize=1) get_settings()`. Adding a field is
one line. **`SettingsDep` resolves through `get_settings()` — the cached one** (see
`backend/src/mvb/api/deps.py::settings_dep`), which matters for testing a new flag (Task B5).

**Errors** (`backend/src/mvb/api/errors.py`): raise `ApiError(status_code, code, message, detail)` →
envelope `{"error": {"code", "message", "detail"}, "request_id": ...}`.

**The console** (`backend/web/`, served by the same FastAPI image from `MVB_WEB_ROOT`, no bundler):
- `app.js` — shell + `renderRoute()` + `ctx` (has `signIn`, `signOut`, `navigate`, `toast`).
  The route gate is
  `if (route.name === "login" || (!ctx.session && route.name !== "system")) main.append(renderSignIn(ctx));`
- `lib/router.js` — `TAB_ROUTES = new Set(["meetings", "login", "system"])`, `parseHash`, `toHash`.
- `lib/api.js` — the only place that knows the HTTP contract. Its auth guard is
  `if (res.status === 401 && !path.startsWith("/v1/auth/login")) fail();`
- `lib/format.js` — `friendlyError(body)` maps `error.code` → a sentence via `ERROR_TEXT`.
- `views/signin.js` — the sign-in form ("Sign in to the cloud"), 42 lines.
- `lib/dom.js` — `el(tag, props, ...children)` (skips null children), `clear`, `appendAll`.

**Tests and their exact commands** (run all four before and after; these are the regression gates):

```bash
cd backend && ./.venv/Scripts/python.exe -m pytest -q                 # 236 passed, 2 skipped
cd backend && ./.venv/Scripts/python.exe -m ruff check src tests      # All checks passed!
cd backend/web && node --test "tests/*.test.mjs"                      # tests 31, pass 31, fail 0
cd backend/web && MVB_DEMO_URL=http://127.0.0.1:8099 npx playwright test --reporter=line   # 60 passed
```

The Playwright suite needs the demo stack running (see "Running the demo stack" below) and runs two
projects: `narrow` (420px, only `*responsive.spec.js`) first, then `desktop` (everything else).

### Assumptions (chosen, not asked)

1. **Open self-service sign-up**, not invite-gated: the request was "user can sign up using email".
   Guarded by a new `MVB_ALLOW_SIGNUP` flag that **defaults to `true`** so the feature works out of
   the box; setting it to `false` makes the endpoint answer `403 signup_disabled` and the console
   hides the link. (If the owner would rather ship it closed, Task A3 + C5 are the two lines to flip.)
2. **No email verification** — this deployment has no mail server configured, so there is nothing to
   send a link with. Stated as a limitation, not faked.
3. **Sign-up signs you in**: the endpoint returns the same `LoginOut` payload as login (token +
   expiry + user), so the console reuses one session-establishment path.
4. **Display name is optional**; empty means "use the local part of the email" (`demo@x.com` →
   `demo`), so the console's account line is never blank.
5. Password rule stays **min 8 characters** (the CLI's existing rule) — shared, not re-invented.
6. The desktop client is **out of scope**; it has no sign-up and the change is purely additive to
   `/v1`, so it cannot break.

### Running the demo stack (needed for the Playwright tasks)

```bash
# terminals 1 and 2 (from backend/, with the repo venv)
MVB_ENV=dev MVB_DATABASE_URL="sqlite+pysqlite:///C:/Users/supersanta/AppData/Local/hermes/cache/scratch/mvweb/demo.db" \
MVB_MEDIA_ROOT="C:/Users/supersanta/AppData/Local/hermes/cache/scratch/mvweb/media" \
MVB_ANALYZER=mock MVB_TRANSCRIBER=mock MVB_DIARIZER=mock MVB_WEB_ROOT="D:/workspace/MeetVault/backend/web" \
./.venv/Scripts/python.exe -m uvicorn mvb.main:app --host 127.0.0.1 --port 8099

MVB_ENV=dev MVB_DATABASE_URL="sqlite+pysqlite:///C:/Users/supersanta/AppData/Local/hermes/cache/scratch/mvweb/demo.db" \
MVB_MEDIA_ROOT="C:/Users/supersanta/AppData/Local/hermes/cache/scratch/mvweb/media" \
MVB_ANALYZER=mock MVB_TRANSCRIBER=mock MVB_DIARIZER=mock \
./.venv/Scripts/python.exe -m mvb.jobs.worker

# then seed (refuses a non-fresh DB — delete demo.db + media/ first if re-seeding)
MVB_DEMO_URL=http://127.0.0.1:8099 ./.venv/Scripts/python.exe web/e2e/seed_demo.py
```

Demo account: `demo@meetvault.local` / `demo-password-123`. The seeded DB has 3 meetings, one of
which is pinned `processing`.

---

## Architecture / proposed approach

Put the account rules in **one** new module, `backend/src/mvb/accounts.py`, and have both the admin
CLI and the new endpoint call it, so validation cannot drift between the two ways of creating a user.
Add `POST /v1/auth/register` to the existing auth router behind a new `MVB_ALLOW_SIGNUP` setting; it
returns the same `LoginOut` as login (extracting the token-issuing code into `issue_session()` that
both routes share). On the console side, add a `#/signup` hash route, a `views/signup.js` screen
built from the same `el()`/`friendlyError()` primitives as `views/signin.js`, an `api.register()`,
and a `ctx.signUp()` that reuses the session-establishment path — with links both ways between the
two screens.

---

## Step-by-step tasks

Every task is RED → GREEN → commit. Do not batch the commits; the point is that each step is
independently revertable.

### Phase A — the shared account rules (backend, no HTTP yet)

#### A1. `accounts.py` + its unit tests (TDD)

**1. Write the failing test** — create `backend/tests/unit/test_accounts.py`:

```python
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


@pytest.mark.parametrize("email", ["", "nope", "no@domain", "a b@example.com", "@example.com", "a@.com", "a@example."])
def test_bad_emails_are_refused(db_session, email: str) -> None:
    with pytest.raises(InvalidRegistration) as exc:
        create_user(db_session, email=email, password="long-enough-1")
    assert exc.value.code == "invalid_email"


def test_short_password_is_refused(db_session) -> None:
    with pytest.raises(InvalidRegistration) as exc:
        create_user(db_session, email="a@example.com", password="short")
    assert exc.value.code == "weak_password"
    assert "8" in exc.value.message
```

Run it and watch it fail (there is no `mvb.accounts`):

```bash
cd backend && ./.venv/Scripts/python.exe -m pytest tests/unit/test_accounts.py -q
# Expected: ModuleNotFoundError: No module named 'mvb.accounts'  (collection error, exit code 2)
```

**2. Implement** — create `backend/src/mvb/accounts.py`:

```python
"""Account rules shared by the admin CLI and the HTTP API.

One module, so `python -m mvb.admin create-user` and `POST /v1/auth/register` can never disagree
about what an acceptable account is.
"""

from __future__ import annotations

from sqlalchemy import select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from mvb.db.models import User
from mvb.security import hash_password

MIN_PASSWORD_LENGTH = 8
MAX_EMAIL_LENGTH = 320


class EmailTaken(Exception):
    """That email already has an account (also raised when the unique constraint wins a race)."""


class InvalidRegistration(ValueError):
    """A rule the caller must surface to the user; `code` is API-facing."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message


def normalize_email(value: str) -> str:
    return (value or "").strip().lower()


def looks_like_email(value: str) -> bool:
    """Deliberately dependency-free (no email-validator in this image): one @, a dotted domain,
    no whitespace. Strong enough to catch typos; not a deliverability check."""
    if not value or len(value) > MAX_EMAIL_LENGTH or any(ch.isspace() for ch in value):
        return False
    local, at, domain = value.partition("@")
    if not at or not local or "@" in domain:
        return False
    return "." in domain and not domain.startswith(".") and not domain.endswith(".")


def validate_registration(email: str, password: str) -> None:
    """Raise InvalidRegistration for a bad email or a weak password. `email` must be normalized."""
    if not looks_like_email(email):
        raise InvalidRegistration("invalid_email", "That does not look like an email address.")
    if len(password) < MIN_PASSWORD_LENGTH:
        raise InvalidRegistration(
            "weak_password", f"Password must be at least {MIN_PASSWORD_LENGTH} characters."
        )


def find_user(db: Session, email: str) -> User | None:
    return db.scalar(select(User).where(User.email == normalize_email(email)))


def create_user(db: Session, *, email: str, password: str, display_name: str = "") -> User:
    """Validate, insert, commit. Raises InvalidRegistration or EmailTaken."""
    email = normalize_email(email)
    validate_registration(email, password)
    if find_user(db, email) is not None:
        raise EmailTaken(email)
    user = User(
        email=email,
        display_name=(display_name or "").strip() or email.split("@")[0],
        password_hash=hash_password(password),
    )
    db.add(user)
    try:
        db.commit()
    except IntegrityError as exc:  # two registers for one email at the same moment
        db.rollback()
        raise EmailTaken(email) from exc
    return user
```

**3. Verify GREEN**:

```bash
cd backend && ./.venv/Scripts/python.exe -m pytest tests/unit/test_accounts.py -q
# Expected: 12 passed
cd backend && ./.venv/Scripts/python.exe -m ruff check src tests && ./.venv/Scripts/python.exe -m ruff format --check src tests
# Expected: All checks passed!  /  ... already formatted
```

**4. Commit**: `feat(accounts): one shared module for the account rules (CLI + API)`

#### A2. Make the admin CLI use it (no behaviour change)

**1. Run the existing CLI tests first** — they are the contract you must not break:

```bash
cd backend && ./.venv/Scripts/python.exe -m pytest tests/unit/test_admin.py -q
# Expected: 4 passed
```

**2. Replace** the body of `_create_user` in `backend/src/mvb/admin.py` (keep the CLI's printed
messages and exit codes identical) and drop the now-duplicated constant:

```python
from mvb.accounts import EmailTaken, InvalidRegistration, create_user, normalize_email

# delete: MIN_PASSWORD_LENGTH = 8   (it lives in mvb.accounts now)


def _create_user(email: str, password: str, display_name: str) -> int:
    try:
        with session_scope() as session:
            user = create_user(session, email=email, password=password, display_name=display_name)
    except InvalidRegistration as exc:
        print(exc.message, file=sys.stderr)
        return 1
    except EmailTaken:
        print(f"user already exists: {normalize_email(email)}", file=sys.stderr)
        return 1
    print(f"created user: {user.email}")
    return 0
```

Also replace the `_adopt_meetings` email lookup with `find_user(session, email)` if you like — optional,
not required.

**3. Verify**:

```bash
cd backend && ./.venv/Scripts/python.exe -m pytest tests/unit/test_admin.py tests/unit/test_accounts.py -q
# Expected: 16 passed
```

**4. Commit**: `refactor(admin): create-user delegates to mvb.accounts (same messages, same codes)`

#### A3. The `MVB_ALLOW_SIGNUP` setting

**1. Failing test** — append to `backend/tests/unit/test_config.py`:

```python
def test_signup_defaults_on_and_can_be_closed(monkeypatch):
    monkeypatch.delenv("MVB_ALLOW_SIGNUP", raising=False)
    assert Settings().allow_signup is True
    monkeypatch.setenv("MVB_ALLOW_SIGNUP", "false")
    assert Settings().allow_signup is False
```

```bash
cd backend && ./.venv/Scripts/python.exe -m pytest tests/unit/test_config.py -q
# Expected: 1 failed (AttributeError: 'Settings' object has no attribute 'allow_signup')
```

**2. Implement** — in `backend/src/mvb/config.py`, add the field next to `session_ttl_days`:

```python
    session_ttl_days: int = 30
    # Self-service account creation on POST /v1/auth/register. Set false to close a public server
    # and keep provisioning accounts with `python -m mvb.admin create-user`.
    allow_signup: bool = True
```

**3. Verify**: `cd backend && ./.venv/Scripts/python.exe -m pytest tests/unit/test_config.py -q` → `7 passed`

**4. Commit**: `feat(config): MVB_ALLOW_SIGNUP (default on)`

### Phase B — the register endpoint

#### B1. `RegisterIn` schema

**1. Failing test** — append to `backend/tests/unit/test_schema_parity.py`? **No** — that file is about
DB/schema parity. Create `backend/tests/unit/test_auth_schemas.py` instead:

```python
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
```

```bash
cd backend && ./.venv/Scripts/python.exe -m pytest tests/unit/test_auth_schemas.py -q
# Expected: ImportError / collection error (RegisterIn does not exist)
```

**2. Implement** — in `backend/src/mvb/schemas/auth.py`, after `LoginIn`:

```python
class RegisterIn(ORMModel):
    """Password strength is NOT enforced here on purpose: a pydantic failure answers the opaque
    422 `validation_error`, while mvb.accounts returns a 400 with a sentence the user can act on."""

    email: str = Field(min_length=3, max_length=320)
    password: str = Field(min_length=1, max_length=256)
    display_name: str = Field(default="", max_length=200)
```

**3. Verify**: `cd backend && ./.venv/Scripts/python.exe -m pytest tests/unit/test_auth_schemas.py -q` → `3 passed`

**4. Commit**: `feat(api): RegisterIn schema (length limits only; strength is a 400, not a 422)`

#### B2. Extract `issue_session()` from `login` (pure refactor)

**1. Failing test**: none — this is a refactor; the guard is the existing suite.

```bash
cd backend && ./.venv/Scripts/python.exe -m pytest tests/integration/test_auth.py -q
# Expected: 8 passed   (note this number; it must stay 8 after the refactor)
```

**2. Implement** — in `backend/src/mvb/api/routers/auth.py`, replace the token block in `login` and
add the helper above it:

```python
def issue_session(db: Session, user: User, settings: Settings) -> tuple[str, datetime]:
    """Mint a session token for `user` and persist its hash. Returns (raw_token, expires_at)."""
    raw, digest = new_session_token()
    expires_at = datetime.now(UTC) + timedelta(days=settings.session_ttl_days)
    db.add(AuthToken(id=uuid.uuid4(), user_id=user.id, token_hash=digest, expires_at=expires_at))
    db.commit()
    return raw, expires_at
```

`login` becomes:

```python
@router.post("/auth/login", response_model=LoginOut)
def login(payload: LoginIn, db: DbSession, settings: SettingsDep) -> LoginOut:
    email = payload.email.strip().lower()
    user = db.scalar(select(User).where(User.email == email))
    stored = user.password_hash if user is not None else DUMMY_HASH
    ok = verify_password(payload.password, stored)
    if user is None or not ok or not user.is_active:
        raise ApiError(401, "invalid_credentials", "Email or password is incorrect.")
    raw, expires_at = issue_session(db, user, settings)
    return LoginOut(token=raw, expires_at=expires_at, user=UserOut.model_validate(user))
```

Add `from sqlalchemy.orm import Session` and `from mvb.config import Settings` to the imports.

**3. Verify**: `cd backend && ./.venv/Scripts/python.exe -m pytest tests/integration/test_auth.py -q` → `8 passed`

**4. Commit**: `refactor(api): one issue_session() for login (register will reuse it)`

#### B3. `POST /v1/auth/register` — the happy path

**1. Failing test** — append to `backend/tests/integration/test_auth.py`:

```python
def test_register_creates_an_account_and_signs_it_in(client) -> None:
    response = client.post(
        "/v1/auth/register",
        json={"email": "New@Example.com", "password": "brand-new-1", "display_name": "New Person"},
    )
    assert response.status_code == 201
    body = response.json()
    assert body["user"]["email"] == "new@example.com"       # normalized
    assert body["user"]["display_name"] == "New Person"
    assert len(body["token"]) > 20
    assert body["expires_at"]
    # the returned token is a live session, not a decoration
    me = client.get("/v1/auth/me", headers={"Authorization": f"Bearer {body['token']}"})
    assert me.status_code == 200
    assert me.json()["user"]["email"] == "new@example.com"


def test_register_then_login_with_the_same_password(client) -> None:
    assert client.post("/v1/auth/register", json={"email": "two@example.com", "password": "brand-new-1"}).status_code == 201
    login = client.post("/v1/auth/login", json={"email": "TWO@example.com", "password": "brand-new-1"})
    assert login.status_code == 200
    assert login.json()["user"]["email"] == "two@example.com"


def test_a_new_account_starts_with_an_empty_cloud(client) -> None:
    body = client.post("/v1/auth/register", json={"email": "fresh@example.com", "password": "brand-new-1"}).json()
    page = client.get("/v1/meetings", headers={"Authorization": f"Bearer {body['token']}"}).json()
    assert page["total"] == 0
```

```bash
cd backend && ./.venv/Scripts/python.exe -m pytest tests/integration/test_auth.py -q
# Expected: 3 failed (405 Method Not Allowed on POST /v1/auth/register)
```

**2. Implement** — in `backend/src/mvb/api/routers/auth.py`, add the route after `login`:

```python
@router.post("/auth/register", response_model=LoginOut, status_code=201)
def register(payload: RegisterIn, db: DbSession, settings: SettingsDep) -> LoginOut:
    """Self-service account creation with an email + password, then an immediate session.

    403 `signup_disabled` when MVB_ALLOW_SIGNUP=false; 400 `invalid_email` / `weak_password`;
    409 `email_taken`.
    """
    if not settings.allow_signup:
        raise ApiError(403, "signup_disabled", "Sign-up is disabled on this server. Ask the owner for an account.")
    try:
        user = create_user(db, email=payload.email, password=payload.password, display_name=payload.display_name)
    except InvalidRegistration as exc:
        raise ApiError(400, exc.code, exc.message) from exc
    except EmailTaken as exc:
        raise ApiError(409, "email_taken", "That email already has an account. Sign in instead.") from exc
    raw, expires_at = issue_session(db, user, settings)
    return LoginOut(token=raw, expires_at=expires_at, user=UserOut.model_validate(user))
```

Imports to add: `from mvb.accounts import EmailTaken, InvalidRegistration, create_user` and
`RegisterIn` in the `mvb.schemas.auth` import list. `status_code=201` needs FastAPI's `status` — the
router module does not import it yet, so either add `from fastapi import status` and use
`status.HTTP_201_CREATED`, or write `status_code=201` (this file already avoids the import; use the
literal `201`).

**3. Verify**: `cd backend && ./.venv/Scripts/python.exe -m pytest tests/integration/test_auth.py -q` → `11 passed`

**4. Commit**: `feat(api): POST /v1/auth/register — email sign-up that signs you in`

#### B4. The refusal paths (409 / 400 / 403) and the race

**1. Failing tests** — append to `backend/tests/integration/test_auth.py`:

```python
def test_register_duplicate_email_is_409(client) -> None:
    payload = {"email": "dupe@example.com", "password": "brand-new-1"}
    assert client.post("/v1/auth/register", json=payload).status_code == 201
    again = client.post("/v1/auth/register", json=payload)
    assert again.status_code == 409
    assert again.json()["error"]["code"] == "email_taken"
    assert "already has an account" in again.json()["error"]["message"]


def test_register_duplicate_email_is_case_insensitive(client) -> None:
    assert client.post("/v1/auth/register", json={"email": "case@example.com", "password": "brand-new-1"}).status_code == 201
    assert client.post("/v1/auth/register", json={"email": "CASE@Example.COM", "password": "brand-new-1"}).status_code == 409


def test_register_short_password_is_400(client) -> None:
    response = client.post("/v1/auth/register", json={"email": "weak@example.com", "password": "short"})
    assert response.status_code == 400
    assert response.json()["error"]["code"] == "weak_password"
    assert "8" in response.json()["error"]["message"]


def test_register_bad_email_is_400(client) -> None:
    response = client.post("/v1/auth/register", json={"email": "not-an-email", "password": "brand-new-1"})
    assert response.status_code == 400
    assert response.json()["error"]["code"] == "invalid_email"


def test_register_is_refused_when_signup_is_disabled(client, monkeypatch) -> None:
    from mvb.config import get_settings

    monkeypatch.setenv("MVB_ALLOW_SIGNUP", "false")
    get_settings.cache_clear()          # SettingsDep resolves through the lru_cache
    response = client.post("/v1/auth/register", json={"email": "closed@example.com", "password": "brand-new-1"})
    assert response.status_code == 403
    assert response.json()["error"]["code"] == "signup_disabled"


def test_register_does_not_create_a_row_when_it_refuses(client) -> None:
    client.post("/v1/auth/register", json={"email": "ghost2@example.com", "password": "short"})
    from mvb.accounts import find_user
    from mvb.db.base import get_session_factory

    with get_session_factory()() as session:
        assert find_user(session, "ghost2@example.com") is None
```

```bash
cd backend && ./.venv/Scripts/python.exe -m pytest tests/integration/test_auth.py -q
# Expected: 5 failed / 11 passed
```

**2. Implement**: B3's code already covers these — the only fix if a test fails is in
`mvb.accounts` (e.g. a bad-email case slipping through `looks_like_email`). Do **not** add a second
validation path in the router.

For the unique-constraint race there is nothing to write: `create_user` already catches
`IntegrityError` → `EmailTaken` → 409. Prove it is not dead code with a direct unit test in
`backend/tests/unit/test_accounts.py`:

```python
def test_the_unique_constraint_race_also_raises_email_taken(db_session, monkeypatch) -> None:
    """Two concurrent registers: the pre-check passes for both, the INSERT loses the race."""
    import mvb.accounts as accounts

    monkeypatch.setattr(accounts, "find_user", lambda db, email: None)  # pretend both checked first
    accounts.create_user(db_session, email="race@example.com", password="long-enough-1")
    with pytest.raises(accounts.EmailTaken):
        accounts.create_user(db_session, email="race@example.com", password="long-enough-1")
```

**3. Verify**: `cd backend && ./.venv/Scripts/python.exe -m pytest -q` → `~250 passed, 2 skipped` (no failures)

**4. Commit**: `test(api): the sign-up refusals — duplicate, weak, malformed, closed server, race`

#### B5. Document the contract

**1.** In `backend/docs/API.md`, extend the Auth table (it currently lists login/logout/me) with:

```markdown
| POST | `/v1/auth/register` | create an account from email + password and return a session token (`201`; `403 signup_disabled` when `MVB_ALLOW_SIGNUP=false`; `400 invalid_email`/`weak_password`; `409 email_taken`) |
```

and one sentence under it:

```markdown
Registration is self-service when `MVB_ALLOW_SIGNUP=true` (the default). There is no email
verification — this deployment has no mail server, so anyone who can reach the server can create an
account; set `MVB_ALLOW_SIGNUP=false` on a public host and provision accounts with
`python -m mvb.admin create-user`. A `409 email_taken` also tells a caller that an address is
registered (standard for a self-hosted service, and the alternative — silently succeeding — would
break the sign-up screen).
```

**2.** Add to `backend/.env.example` (near `MVB_SESSION_TTL_DAYS`):

```bash
# Self-service sign-up on POST /v1/auth/register. Set false on a public host and use
# `python -m mvb.admin create-user` instead.
MVB_ALLOW_SIGNUP=true
```

**3. Verify**: `grep -n "auth/register" backend/docs/API.md backend/.env.example` prints both lines.

**4. Commit**: `docs(api): the register endpoint, the signup flag and the no-verification tradeoff`

### Phase C — the console screen

#### C1. `api.register()`

**1. Failing test** — append to `backend/web/tests/api.test.mjs` (it already has a `jsonResponse`
helper and uses `node:test` + `node:assert/strict`):

```js
test("register posts the account body to /v1/auth/register", async () => {
  const calls = [];
  const api = createApi({
    fetchImpl: async (url, opts) => {
      calls.push({ url, body: JSON.parse(opts.body) });
      return jsonResponse(201, { token: "t", expires_at: "2026-01-01T00:00:00Z", user: { email: "a@b.com" } });
    },
    onAuthFailure: () => {},
  });
  const out = await api.register("a@b.com", "long-enough-1", "A");
  assert.equal(calls[0].url, "/v1/auth/register");
  assert.deepEqual(calls[0].body, { email: "a@b.com", password: "long-enough-1", display_name: "A" });
  assert.equal(out.token, "t");
});

test("a 401 from REGISTER is not a dead session either", async () => {
  let fired = 0;
  const api = createApi({
    fetchImpl: async () => jsonResponse(401, { error: { code: "email_taken", message: "taken" }, request_id: "r9" }),
    onAuthFailure: () => { fired += 1; },
  });
  await assert.rejects(() => api.register("a@b.com", "long-enough-1", ""));
  assert.equal(fired, 0);
});
```

```bash
cd backend/web && node --test "tests/api.test.mjs"
# Expected: 2 failing (api.register is not a function)
```

**2. Implement** — in `backend/web/lib/api.js`:

(a) replace the auth-failure guard inside `request`:

```js
    // A 401 from an auth ATTEMPT is bad credentials, not a dead session — firing the hook there
    // wiped the form (and the wrong-password message) with "Your session expired".
    const isAuthAttempt = path === "/v1/auth/login" || path === "/v1/auth/register";
    if (res.status === 401 && !isAuthAttempt) fail();
```

(b) add to the returned object next to `login`:

```js
    register: (email, password, displayName) =>
      request("POST", "/v1/auth/register", {
        body: { email, password, display_name: displayName || "" },
      }),
```

**3. Verify**: `cd backend/web && node --test "tests/*.test.mjs"` → `tests 33, pass 33, fail 0`

**4. Commit**: `feat(web): api.register + the auth-attempt guard covers both auth POSTs`

#### C2. The `#/signup` route

**1. Failing test** — append to `backend/web/tests/router.test.mjs`:

```js
test("the signup route parses and round-trips", () => {
  assert.equal(parseHash("#/signup").name, "signup");
  assert.equal(toHash("signup"), "#/signup");
});
```

```bash
cd backend/web && node --test "tests/router.test.mjs"
# Expected: 1 failing (parseHash("#/signup").name === "meetings")
```

**2. Implement** — in `backend/web/lib/router.js`:

```js
const TAB_ROUTES = new Set(["meetings", "login", "signup", "system"]);
```

**3. Verify**: `cd backend/web && node --test "tests/*.test.mjs"` → all green

**4. Commit**: `feat(web): #/signup is a route`

#### C3. Friendly messages for the new error codes

**1. Failing test** — append to `backend/web/tests/format.test.mjs`:

```js
test("the sign-up error codes get human sentences", () => {
  assert.equal(friendlyError({ code: "email_taken" }), "That email already has an account. Sign in instead.");
  assert.equal(friendlyError({ code: "weak_password" }), "Password must be at least 8 characters.");
  assert.equal(friendlyError({ code: "invalid_email" }), "That does not look like an email address.");
  assert.equal(
    friendlyError({ code: "signup_disabled" }),
    "Sign-up is turned off on this server. Ask the owner for an account.",
  );
});
```

```bash
cd backend/web && node --test "tests/format.test.mjs"
# Expected: 1 failing (unmapped codes fall back to the server sentence)
```

**2. Implement** — add to `ERROR_TEXT` in `backend/web/lib/format.js`:

```js
  email_taken: "That email already has an account. Sign in instead.",
  weak_password: "Password must be at least 8 characters.",
  invalid_email: "That does not look like an email address.",
  signup_disabled: "Sign-up is turned off on this server. Ask the owner for an account.",
```

**3. Verify**: `cd backend/web && node --test "tests/*.test.mjs"` → all green

**4. Commit**: `feat(web): friendly text for the sign-up error codes`

#### C4. `views/signup.js` — the screen

**1. Create** `backend/web/views/signup.js`:

```js
import { el } from "../lib/dom.js";
import { friendlyError } from "../lib/format.js";

// Sign-up screen. Fields carry data-testids because getByLabel("Password") would also match
// "Confirm password" (Playwright's label matching is substring + case-insensitive).
export function renderSignUp(ctx) {
  const error = el("div", { class: "banner error", hidden: true, "data-testid": "signup-error" });
  const name = el("input", { type: "text", id: "display-name", autocomplete: "name", "data-testid": "signup-name" });
  const email = el("input", {
    type: "email", id: "email", autocomplete: "username", required: true, "data-testid": "signup-email",
  });
  const password = el("input", {
    type: "password", id: "password", autocomplete: "new-password", required: true, "data-testid": "signup-password",
  });
  const confirm = el("input", {
    type: "password", id: "confirm", autocomplete: "new-password", required: true, "data-testid": "signup-confirm",
  });
  const submit = el("button", { class: "primary", type: "submit", "data-testid": "signup-submit" }, "Create account");

  const showError = (text) => {
    error.textContent = text;
    error.hidden = false;
  };

  const form = el(
    "form",
    {
      onsubmit: async (event) => {
        event.preventDefault();
        error.hidden = true;
        // Mirror the server's rule so the user gets a sentence instead of "The server rejected
        // that request." (the 422 path). The server still enforces it.
        if (password.value.length < 8) {
          showError("Password must be at least 8 characters.");
          return;
        }
        if (password.value !== confirm.value) {
          showError("Those passwords do not match.");
          return;
        }
        submit.disabled = true;
        submit.setAttribute("aria-busy", "true");
        try {
          await ctx.signUp(email.value.trim(), password.value, name.value.trim());
          ctx.toast("Account created.");
          ctx.navigate("meetings");
        } catch (err) {
          showError(friendlyError({ code: err && err.code ? err.code : "error", message: err && err.message }));
        } finally {
          submit.disabled = false;
          submit.removeAttribute("aria-busy");
        }
      },
    },
    el("h2", {}, "Create your account"),
    el("p", { class: "sub" }, "Sign up with your email — the desktop app uses the same account."),
    error,
    el("label", { class: "field" }, "Name (optional)", name),
    el("label", { class: "field" }, "Email", email),
    el("label", { class: "field" }, "Password", password),
    el("label", { class: "field" }, "Confirm password", confirm),
    submit,
    el("p", { class: "sub" }, el("a", { href: "#/login" }, "Already have an account? Sign in")),
  );
  return el("div", { class: "signin" }, el("div", { class: "card" }, form));
}
```

**2. Add the reverse link** — in `backend/web/views/signin.js`, after the `submit` child of the form:

```js
    el("p", { class: "sub" }, el("a", { href: "#/signup" }, "Create an account")),
```

**3. Verify** (no unit test — this view is covered by the Playwright spec in C6; check the module
parses): `cd backend/web && node --input-type=module -e "import('./views/signup.js').then(() => console.log('ok'))"`
→ prints `ok`. (`./views/signup.js` imports `../lib/dom.js`, which touches `document` only inside
functions, so a bare import is safe.)

**4. Commit**: `feat(web): the sign-up screen (email + password, client-side mirror of the rule)`

#### C5. Wire it into the shell

**1. Implement** in `backend/web/app.js`:

(a) import at the top with the other views:

```js
import { renderSignUp } from "./views/signup.js";
```

(b) replace `signIn`/`ctx.signIn` with a shared session path (DRY — the only difference between the
two flows is which endpoint answers):

```js
function establishSession(result) {
  store.save(result.token, result.user);
  api.setToken(result.token);
  ctx.session = { user: result.user, expires_at: result.expires_at };
}

async function signIn(email, password) {
  const result = await api.login(email, password);
  establishSession(result);
  return result;
}

async function signUp(email, password, displayName) {
  const result = await api.register(email, password, displayName);
  establishSession(result);
  return result;
}
ctx.signIn = signIn;
ctx.signUp = signUp;
```

(c) in `renderRoute()`, handle `signup` **before** the signed-out gate (otherwise a visitor without a
session is bounced to the sign-in form):

```js
    if (route.name === "signup") {
      main.append(renderSignUp(ctx));
      return;
    }
    if (route.name === "login" || (!ctx.session && route.name !== "system")) {
      main.append(renderSignIn(ctx));
      return;
    }
```

(d) in `renderRail()`'s signed-out footer, next to the existing sign-in link:

```js
      : el(
          "div",
          { class: "account" },
          el("a", { href: "#/login" }, "Sign in"),
          el("a", { href: "#/signup", "data-testid": "signup-link" }, "Create account"),
        ),
```

**2. Verify by hand** (the demo stack must be running):

```bash
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:8099/          # 200
```
then open `http://127.0.0.1:8099/#/signup` in the browser tool and confirm the heading
"Create your account" is present (this is exactly what C6 asserts automatically).

**3. Commit**: `feat(web): sign-up route, ctx.signUp and the links between the two screens`

#### C6. The end-to-end spec

**1. Create** `backend/web/e2e/01b-signup.spec.js` (name it `01b` so it sorts next to `01-auth`; it
only ADDS a user, so it cannot disturb the seeded dataset the numbered specs mutate):

```js
import { test, expect, EMAIL, PASSWORD } from "./fixtures.js";

const newEmail = () => `signup-${Date.now()}-${Math.floor(Math.random() * 1e6)}@example.com`;

test("the sign-in screen links to sign-up and back", async ({ page }) => {
  await page.goto("/#/login");
  await page.getByRole("link", { name: "Create an account" }).click();
  await expect(page.getByRole("heading", { name: "Create your account" })).toBeVisible();
  await page.getByRole("link", { name: "Already have an account? Sign in" }).click();
  await expect(page.getByRole("heading", { name: "Sign in to the cloud" })).toBeVisible();
});

test("a short or mismatched password is refused before it reaches the server", async ({ page }) => {
  await page.goto("/#/signup");
  await page.getByTestId("signup-email").fill(newEmail());
  await page.getByTestId("signup-password").fill("short");
  await page.getByTestId("signup-confirm").fill("short");
  await page.getByTestId("signup-submit").click();
  await expect(page.getByTestId("signup-error")).toHaveText("Password must be at least 8 characters.");

  await page.getByTestId("signup-password").fill("long-enough-1");
  await page.getByTestId("signup-confirm").fill("long-enough-2");
  await page.getByTestId("signup-submit").click();
  await expect(page.getByTestId("signup-error")).toHaveText("Those passwords do not match.");
  // still on the form: nothing was created
  await expect(page.getByRole("heading", { name: "Create your account" })).toBeVisible();
});

test("signing up with an email creates the account and lands on an empty cloud", async ({ page, consoleErrors }) => {
  const email = newEmail();
  await page.goto("/#/signup");
  await page.getByTestId("signup-name").fill("Signup Tester");
  await page.getByTestId("signup-email").fill(email);
  await page.getByTestId("signup-password").fill("signup-password-1");
  await page.getByTestId("signup-confirm").fill("signup-password-1");
  await page.getByTestId("signup-submit").click();

  await expect(page.getByRole("heading", { name: "Meetings in the cloud" })).toBeVisible();
  await expect(page.locator(".account .mail")).toHaveText(email);
  await expect(page.locator(".account .who")).toHaveText("Signup Tester");
  // The new account must NOT see the demo account's three seeded meetings.
  await expect(page.getByTestId("empty")).toContainText("No meetings on the server yet.");
  await expect(page.locator(".row")).toHaveCount(0);
  expect(consoleErrors).toEqual([]);
});

test("the new credentials work after signing out", async ({ page }) => {
  const email = newEmail();
  await page.goto("/#/signup");
  await page.getByTestId("signup-email").fill(email);
  await page.getByTestId("signup-password").fill("signup-password-1");
  await page.getByTestId("signup-confirm").fill("signup-password-1");
  await page.getByTestId("signup-submit").click();
  await expect(page.getByRole("heading", { name: "Meetings in the cloud" })).toBeVisible();

  await page.getByRole("button", { name: "Sign out" }).click();
  await expect(page.getByRole("heading", { name: "Sign in to the cloud" })).toBeVisible();
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill("signup-password-1");
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("heading", { name: "Meetings in the cloud" })).toBeVisible();
});

test("an email that already has an account is refused with a friendly message", async ({ page }) => {
  await page.goto("/#/signup");
  await page.getByTestId("signup-email").fill(EMAIL); // the seeded demo account
  await page.getByTestId("signup-password").fill(PASSWORD);
  await page.getByTestId("signup-confirm").fill(PASSWORD);
  await page.getByTestId("signup-submit").click();
  await expect(page.getByTestId("signup-error")).toHaveText("That email already has an account. Sign in instead.");
  await expect(page.getByRole("heading", { name: "Create your account" })).toBeVisible();
});
```

**2. Run it** (demo stack up):

```bash
cd backend/web && MVB_DEMO_URL=http://127.0.0.1:8099 npx playwright test e2e/01b-signup.spec.js --reporter=line
# Expected: 5 passed
```

**3. Add the screenshot** — in `backend/web/e2e/10-screenshots.spec.js`, inside
`"capture the console flow in order"`, right after `await shot(page, "01-sign-in.png");`:

```js
  await page.goto("/#/signup");
  await page.getByRole("heading", { name: "Create your account" }).waitFor();
  await shot(page, "01b-sign-up.png");
  await page.goto("/#/login");
  await page.getByRole("heading", { name: "Sign in to the cloud" }).waitFor();
```

**4. Verify the whole suite** (fresh seed first — see "Running the demo stack"; the suite mutates the
DB, so delete `demo.db` + `media/` and re-seed before a full run):

```bash
cd backend/web && MVB_DEMO_URL=http://127.0.0.1:8099 \
  MVB_ARTIFACT_DIR="C:/Users/supersanta/AppData/Local/hermes/cache/scratch/mvweb/downloads" \
  MVB_REPORT_DIR="D:/workspace/MeetVault/backend/docs/web-report/screens" \
  npx playwright test --reporter=line
# Expected: 65 passed (60 existing + 5 new)
```

**5. Commit**: `test(web): the sign-up flow end to end, with the screen captured`

### Phase D — docs, report and the full gate

#### D1. Report + progress log

In `backend/docs/web-report/REPORT.md`: add a parity-matrix row
(`| 20 | Create an account (email sign-up) | ... | POST /v1/auth/register | 01b-signup |`), add
`| screens/01b-sign-up.png | The sign-up screen: name (optional), email, password, confirm |` to the
screens table, and update the Playwright count in §4 to the number you actually got. In
`backend/docs/progress/progress_supersanta/PROGRESS_2026-10-15.md`, add the same count and one line
about the signup flag.

**Commit**: `docs(web): the sign-up screen in the report and the progress log`

#### D2. Full gate (all four suites, fresh seed for the browser one)

```bash
cd backend && ./.venv/Scripts/python.exe -m pytest -q
cd backend && ./.venv/Scripts/python.exe -m ruff check src tests
cd backend && ./.venv/Scripts/python.exe -m ruff format --check src tests
cd backend/web && node --test "tests/*.test.mjs"
cd backend/web && npx playwright test --reporter=line     # after a fresh seed
```

Expected: `~250 passed, 2 skipped`; `All checks passed!`; `... already formatted`; `tests 38, pass 38,
fail 0`; `65 passed`. Any deviation is a blocker — fix it before committing, and never write a
number into the report that a command did not print.

#### D3. Record the lessons in the skill

Update the `meetvault-web-console` skill (`skill_manage`) with: the auth-attempt guard must list
**every** auth POST (login *and* register) or a 401 there wipes the form; `SettingsDep` resolves
through the lru-cached `get_settings()`, so a flag test must `monkeypatch.setenv` **and**
`get_settings.cache_clear()`; and `getByLabel("Password")` is substring + case-insensitive, so a
sign-up form needs `data-testid` handles.

**Commit**: `docs(skill): sign-up lessons (auth guard, cached settings, label ambiguity)` — the skill
lives outside the repo, so this commit is only for the repo-side files if any changed.

---

## Tests / validation

| Layer | File | What it proves |
| --- | --- | --- |
| unit (py) | `backend/tests/unit/test_accounts.py` | normalization, hashing, display-name default, duplicate (incl. the constraint race), bad emails, weak password |
| unit (py) | `backend/tests/unit/test_auth_schemas.py` | the schema's own limits, and that strength is *not* a 422 |
| unit (py) | `backend/tests/unit/test_config.py` | `allow_signup` default true + `false` override |
| unit (py) | `backend/tests/unit/test_admin.py` | the CLI still behaves exactly as before |
| integration | `backend/tests/integration/test_auth.py` | 201 + live token, login-after-register, empty cloud, 409/400/400/403, no row on refusal |
| unit (js) | `backend/web/tests/{api,router,format}.test.mjs` | request shape, the auth-attempt guard, the route, the sentences |
| e2e | `backend/web/e2e/01b-signup.spec.js` | the whole flow in a real browser, plus account isolation from the demo user |

Nothing is "done" until `pytest -q`, `ruff`, `node --test` and `playwright test` have all been run and
their real output pasted into the report.

---

## Risks, tradeoffs, and open questions

1. **Open registration on a public host is an abuse surface.** With `MVB_ALLOW_SIGNUP=true` anyone
   who can reach the server can create unlimited accounts, and each registration runs scrypt
   (~16 MiB, deliberately expensive), so it is also a cheap CPU/memory DoS vector. There is **no rate
   limiter anywhere in this backend today** (verified: no `429`, no limiter module). Mitigations, in
   order of effort: flip `MVB_ALLOW_SIGNUP=false` (one env var, the console then shows the disabled
   message); add a per-IP limiter on `/v1/auth/register`; add invite codes. **Open question for the
   owner: which of those do you want?** The plan ships option 1 as a flag and does not pretend the
   other two exist.
2. **Email enumeration**: `409 email_taken` tells a caller whether an address is registered. Chosen
   deliberately — the sign-up screen must say "sign in instead" — but it is a real (minor) leak.
3. **No verification, no password reset.** Anyone can register with an email they do not own, and a
   user who forgets their password has no self-service path (the owner would run the CLI). Both need
   a mail server; neither is in scope.
4. **`email_validator` is not installed**, so validation is a deliberate hand-rolled `@` + dotted
   domain check. It will accept a few technically-invalid addresses (e.g. `a@b.c.d`) and reject none
   that matter. If strict validation is wanted, add the dependency and switch `looks_like_email` to
   pydantic's `EmailStr` — a one-task change.
5. **Public deployment stays blocked**: `https://meetvault.supersanta.io.vn/healthz` answers `000`
   (the nginx vhost/cert installer has never run). The sign-up screen will work on
   `http://127.0.0.1:8099/` and is reachable publicly only after that is fixed — which also makes the
   abuse question in (1) urgent rather than theoretical.
6. **Two schemas, one rule**: `RegisterIn` deliberately does *not* enforce the 8-character minimum,
   so a weak password produces our 400 with a readable message rather than pydantic's opaque 422.
   If someone later "tidies" that by adding `min_length=8`, the console message regresses to "The
   server rejected that request." — hence the comment in the schema.
7. **Playwright ordering**: `01b-signup.spec.js` adds a user per run and never deletes it, so the demo
   DB accumulates accounts across runs. Harmless (each uses a unique timestamped email) but it means
   the demo DB is not perfectly reproducible — re-seed before a report-grade run.
