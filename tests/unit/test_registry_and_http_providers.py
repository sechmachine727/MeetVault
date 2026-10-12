from __future__ import annotations

import httpx
import pytest
import respx

from mvb.config import Settings
from mvb.providers.analysis.lmstudio import LmStudioAnalyzer
from mvb.providers.analysis.opencode import OpenCodeAnalyzer
from mvb.providers.registry import build_analyzer, build_diarizer, build_transcriber, describe_providers


def test_defaults_are_mocks() -> None:
    settings = Settings()
    assert build_transcriber(settings).id == "mock-transcriber"
    assert build_diarizer(settings).id == "mock-diarizer"
    assert build_analyzer(settings).id == "mock-analyzer"


def test_missing_package_falls_back_to_mock(monkeypatch: pytest.MonkeyPatch) -> None:
    settings = Settings()
    settings.transcriber = "faster_whisper"  # type: ignore[assignment]
    monkeypatch.setattr("mvb.providers.transcription.faster_whisper.is_available", lambda: False)
    assert build_transcriber(settings).id == "mock-transcriber"


def test_unreachable_analyzer_falls_back_to_mock() -> None:
    settings = Settings()
    settings.analyzer = "lmstudio"  # type: ignore[assignment]
    settings.lmstudio_base_url = "http://127.0.0.1:1/v1"  # nothing listens here
    assert build_analyzer(settings).id == "mock-analyzer"


def test_describe_providers_reports_reasons() -> None:
    described = describe_providers(Settings())
    assert set(described) == {"transcriber", "diarizer", "analyzer"}
    for capability, info in described.items():
        assert info["effective"].startswith("mock"), capability
        assert info["is_mock"] is True
        assert info["reason"]


def test_describe_providers_explains_a_fallback(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("MVB_ANALYZER", "lmstudio")
    monkeypatch.setenv("MVB_LMSTUDIO_BASE_URL", "http://127.0.0.1:1/v1")
    described = describe_providers(Settings())
    assert described["analyzer"]["requested"] == "lmstudio"
    assert described["analyzer"]["effective"] == "mock-analyzer"
    assert described["analyzer"]["reason"] == "requested provider unavailable"


# ---- LM Studio client (respx, no network) ------------------------------------------

BASE = "http://127.0.0.1:1234/v1"


@respx.mock
def test_lmstudio_ping_true_when_model_served() -> None:
    respx.get(f"{BASE}/models").mock(return_value=httpx.Response(200, json={"data": [{"id": "unsloth/qwen3.8-27b"}]}))
    assert LmStudioAnalyzer(base_url=BASE, model="unsloth/qwen3.8-27b").ping() is True


@respx.mock
def test_lmstudio_ping_false_when_model_missing() -> None:
    respx.get(f"{BASE}/models").mock(return_value=httpx.Response(200, json={"data": [{"id": "other"}]}))
    assert LmStudioAnalyzer(base_url=BASE, model="unsloth/qwen3.8-27b").ping() is False


@respx.mock
def test_lmstudio_ping_false_when_unreachable() -> None:
    respx.get(f"{BASE}/models").mock(side_effect=httpx.ConnectError("refused"))
    assert LmStudioAnalyzer(base_url=BASE).ping() is False


@respx.mock
def test_lmstudio_complete_json_parses_content() -> None:
    respx.post(f"{BASE}/chat/completions").mock(
        return_value=httpx.Response(
            200,
            json={
                "model": "unsloth/qwen3.8-27b",
                "choices": [{"message": {"content": '{"text": "hi", "bullets": []}'}}],
            },
        )
    )
    result = LmStudioAnalyzer(base_url=BASE, model="unsloth/qwen3.8-27b").complete_json(
        system="s", user="u", schema={"type": "object"}, timeout_s=10
    )
    assert result.payload == {"text": "hi", "bullets": []}
    assert result.is_mock is False
    assert result.provider == "lmstudio"


@respx.mock
def test_lmstudio_complete_json_raises_on_http_error() -> None:
    respx.post(f"{BASE}/chat/completions").mock(return_value=httpx.Response(500, json={"error": "boom"}))
    with pytest.raises(httpx.HTTPStatusError):
        LmStudioAnalyzer(base_url=BASE, model="m").complete_json(system="s", user="u", schema={}, timeout_s=10)


# ---- OpenCode client (respx, no network) -------------------------------------------

OC = "http://127.0.0.1:4096"


@respx.mock
def test_opencode_ping_true_on_200() -> None:
    respx.get(f"{OC}/api/models").mock(return_value=httpx.Response(200, json={"models": []}))
    assert OpenCodeAnalyzer(base_url=OC).ping() is True


@respx.mock
def test_opencode_ping_false_on_unreachable() -> None:
    respx.get(f"{OC}/api/models").mock(side_effect=httpx.ConnectError("refused"))
    assert OpenCodeAnalyzer(base_url=OC).ping() is False


@respx.mock
def test_opencode_complete_json_reads_message_content() -> None:
    respx.post(f"{OC}/api/chat").mock(return_value=httpx.Response(200, json={"message": {"content": '{"items": []}'}}))
    result = OpenCodeAnalyzer(base_url=OC, model="m").complete_json(system="s", user="u", schema={}, timeout_s=5)
    assert result.payload == {"items": []}
    assert result.provider == "opencode"
    assert result.is_mock is False
