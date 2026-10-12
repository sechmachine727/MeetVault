from __future__ import annotations

import json
import logging

import httpx

from mvb.providers.base import LLMResult

log = logging.getLogger(__name__)


class LmStudioAnalyzer:
    """LM Studio speaks the OpenAI wire format. `model` must be an id the server serves
    (`GET /v1/models`); an empty model lets LM Studio route to whatever is loaded."""

    def __init__(self, *, base_url: str, api_key: str = "", model: str = "", timeout_s: float = 120.0) -> None:
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key
        self.model = model
        self.timeout_s = timeout_s
        self.id = "lmstudio"
        self.is_mock = False

    def _headers(self) -> dict[str, str]:
        headers = {"Content-Type": "application/json"}
        if self.api_key:
            headers["Authorization"] = f"Bearer {self.api_key}"
        return headers

    def ping(self) -> bool:
        try:
            response = httpx.get(f"{self.base_url}/models", headers=self._headers(), timeout=5.0)
        except httpx.HTTPError as exc:
            log.warning("lmstudio_ping_failed: %s", exc)
            return False
        if response.status_code != 200:
            return False
        try:
            models = response.json().get("data", [])
        except (json.JSONDecodeError, AttributeError):
            return False
        if self.model and not any(item.get("id") == self.model for item in models):
            log.warning("lmstudio_model_not_served model=%s", self.model)
            return False
        return bool(models) or bool(self.model)

    def complete_json(self, *, system: str, user: str, schema: dict, timeout_s: float) -> LLMResult:
        body = {
            "model": self.model or "local-model",
            "messages": [
                {"role": "system", "content": system + "\n\nJSON SCHEMA:\n" + json.dumps(schema, sort_keys=True)},
                {"role": "user", "content": user},
            ],
            "temperature": 0.0,
            "stream": False,
            "response_format": {"type": "json_object"},
        }
        with httpx.Client(timeout=timeout_s or self.timeout_s) as client:
            response = client.post(f"{self.base_url}/chat/completions", headers=self._headers(), json=body)
        response.raise_for_status()
        payload = response.json()
        content = payload["choices"][0]["message"]["content"]
        return LLMResult(
            payload=json.loads(content),
            provider=self.id,
            model=self.model or payload.get("model", ""),
            is_mock=False,
        )
