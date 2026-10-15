"""The deployable image and stack stay in step with the tree they are built from.

deploy/ is a self-contained copy of backend/Dockerfile and backend/compose.yaml with the paths
re-rooted, so two files describe the same image and the same stack. Nothing else tests a
Dockerfile, and the failure mode is silent: the deploy folder keeps building yesterday's engine
set while the backend folder moves on. These checks are text-level on purpose — they need no
Docker daemon and run everywhere the rest of the unit suite runs.
"""

from __future__ import annotations

import re
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]  # tests/unit/<file> -> tests -> backend -> repo root
BACKEND_DOCKERFILE = REPO / "backend" / "Dockerfile"
DEPLOY_DOCKERFILE = REPO / "deploy" / "Dockerfile"
BACKEND_COMPOSE = REPO / "backend" / "compose.yaml"
DEPLOY_COMPOSE = REPO / "deploy" / "docker-compose.yml"
DEPLOY_ENV_EXAMPLE = REPO / "deploy" / ".env.example"
BACKEND_ENV_EXAMPLE = REPO / "backend" / "docker.env.example"


def _arg_names(text: str) -> set[str]:
    return set(re.findall(r"^ARG\s+([A-Z_][A-Z0-9_]*)", text, re.MULTILINE))


def _arg_default(text: str, name: str) -> str | None:
    match = re.search(rf"^ARG\s+{name}=(\S+)", text, re.MULTILINE)
    return match.group(1) if match else None


def _env_anchor_keys(compose: str) -> set[str]:
    """The keys of the `x-app-env: &app-env` block — what reaches the containers."""
    block = re.search(r"x-app-env:\s*&app-env\n(.*?)\n\n", compose, re.DOTALL)
    assert block, "x-app-env anchor not found"
    return set(re.findall(r"^\s{2}([A-Z_][A-Z0-9_]*):", block.group(1), re.MULTILINE))


def test_deploy_folder_assets_exist() -> None:
    for path in (DEPLOY_DOCKERFILE, DEPLOY_COMPOSE, DEPLOY_ENV_EXAMPLE, REPO / "deploy" / "docs" / "DEPLOY.md"):
        assert path.exists(), f"missing deployment asset: {path}"


def test_dockerfiles_agree_on_build_args() -> None:
    backend = BACKEND_DOCKERFILE.read_text(encoding="utf-8")
    deploy = DEPLOY_DOCKERFILE.read_text(encoding="utf-8")
    assert _arg_names(backend) == _arg_names(deploy), "build args drifted between the two Dockerfiles"
    for name in ("APP_EXTRAS", "WITH_TRANSCRIBE", "WITH_DIARIZE"):
        assert _arg_default(backend, name) == _arg_default(deploy, name), f"default for {name} differs"


def test_dockerfiles_agree_on_runtime_contract() -> None:
    backend = BACKEND_DOCKERFILE.read_text(encoding="utf-8")
    deploy = DEPLOY_DOCKERFILE.read_text(encoding="utf-8")
    for pattern, label in (
        (r'ENTRYPOINT \["([^"]+)"\]', "entrypoint"),
        (r'CMD \["uvicorn", "mvb\.main:app", "--host", "([^"]+)", "--port", "(\d+)"\]', "CMD"),
        (r"^EXPOSE\s+(\d+)", "exposed port"),
        (r"HF_HOME=(\S+)", "model cache location"),
        (r"^USER\s+(\S+)", "runtime user"),
    ):
        assert re.findall(pattern, backend, re.MULTILINE) == re.findall(pattern, deploy, re.MULTILINE), (
            f"{label} drifted between the two Dockerfiles"
        )


def test_deploy_compose_builds_the_deploy_dockerfile_from_the_repo_root() -> None:
    compose = DEPLOY_COMPOSE.read_text(encoding="utf-8")
    assert re.search(r"^  context: \.\.$", compose, re.MULTILINE), "build context must be the repository root"
    assert re.search(r"^  dockerfile: deploy/Dockerfile$", compose, re.MULTILINE), (
        "the deploy stack must build deploy/Dockerfile"
    )


def test_both_compose_files_require_the_database_password() -> None:
    for path in (BACKEND_COMPOSE, DEPLOY_COMPOSE):
        assert "POSTGRES_PASSWORD:?" in path.read_text(encoding="utf-8"), (
            f"{path.name} does not require POSTGRES_PASSWORD"
        )


def test_neither_compose_file_pins_a_shared_project_name() -> None:
    """A hardcoded `name:` made both stacks one project, so `down -v` in one folder deleted the
    other's database and media (observed while building this deployment). The project name comes
    from COMPOSE_PROJECT_NAME in each .env instead."""
    for path in (BACKEND_COMPOSE, DEPLOY_COMPOSE):
        text = path.read_text(encoding="utf-8")
        assert not re.search(r"^name:\s*\S+$", text, re.MULTILINE), f"{path.name} pins a project name"
        env_example = BACKEND_ENV_EXAMPLE if path is BACKEND_COMPOSE else DEPLOY_ENV_EXAMPLE
        assert re.search(r"^COMPOSE_PROJECT_NAME=\S+$", env_example.read_text(encoding="utf-8"), re.MULTILINE), (
            f"{env_example.name} must set COMPOSE_PROJECT_NAME"
        )


def test_the_two_env_examples_use_different_project_names() -> None:
    backend = re.search(r"^COMPOSE_PROJECT_NAME=(\S+)$", BACKEND_ENV_EXAMPLE.read_text(encoding="utf-8"), re.MULTILINE)
    deploy = re.search(r"^COMPOSE_PROJECT_NAME=(\S+)$", DEPLOY_ENV_EXAMPLE.read_text(encoding="utf-8"), re.MULTILINE)
    assert backend and deploy and backend.group(1) != deploy.group(1), (
        "the dev stack and the deployment stack must not share a project name"
    )


def test_worker_disables_the_api_healthcheck() -> None:
    """The image HEALTHCHECK curls the API port; the worker serves no port, so inheriting it
    reports the worker unhealthy forever."""
    for path in (BACKEND_COMPOSE, DEPLOY_COMPOSE):
        compose = path.read_text(encoding="utf-8")
        worker = compose[compose.index("  worker:") :]
        assert re.search(r"healthcheck:\n\s+disable: true", worker), f"{path.name}: worker must disable the healthcheck"


# Passed to the containers but deliberately NOT a .env setting: container-internal paths, and the
# database URL, which is derived from POSTGRES_PASSWORD + the compose service name.
INTERNAL_ONLY = {"DATABASE_URL", "HF_HOME", "MEDIA_ROOT", "WEB_ROOT"}


def test_env_examples_cover_every_setting_the_stack_passes_through() -> None:
    for compose_path, env_path in ((BACKEND_COMPOSE, BACKEND_ENV_EXAMPLE), (DEPLOY_COMPOSE, DEPLOY_ENV_EXAMPLE)):
        keys = _env_anchor_keys(compose_path.read_text(encoding="utf-8"))
        documented = set(re.findall(r"^([A-Z_][A-Z0-9_]*)=", env_path.read_text(encoding="utf-8"), re.MULTILINE))
        # MVB_LMSTUDIO_* etc. are documented without the prefix in some blocks; normalise by
        # stripping a leading MVB_ from both sides before comparing.
        bare_keys = {k[4:] if k.startswith("MVB_") else k for k in keys}
        bare_documented = {k[4:] if k.startswith("MVB_") else k for k in documented}
        missing = sorted(bare_keys - bare_documented - INTERNAL_ONLY)
        assert not missing, f"{env_path.name} does not document: {missing}"


def test_the_host_port_is_configurable_and_the_container_port_is_fixed() -> None:
    for path in (BACKEND_COMPOSE, DEPLOY_COMPOSE):
        assert re.search(r'"\$\{MVB_PORT:-\d+\}:8080"', path.read_text(encoding="utf-8")), (
            f"{path.name}: publish MVB_PORT on the host against the fixed container port 8080"
        )
