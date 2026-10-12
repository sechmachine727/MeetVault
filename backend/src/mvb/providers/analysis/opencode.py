from __future__ import annotations

import json
import logging

import httpx

from mvb.providers.base import LLMResult

log = logging.getLogger(__name__)

# Route contract, pinned in docs/PROVIDERS.md. Change this AND the respx route in the
# test together. Verified against a live server only if the probe recorded one.
_CHAT_PATH = "/api/chat"
_MODELS_PATH = "/api/models"


class OpenCodeAnalyzer:
    """OpenCode HTTP server analyzer. When the server is unreachable the registry
    substitutes the mock (no startup failure)."""

    def __init__(self, *, base_url: str, api_key: str = "", model: str = "", timeout_s: float = 120.0) -> None:
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key
        self.model = model
        self.timeout_s = timeout_s
        self.id = "opencode"
        self.is_mock = False

    def _headers(self) -> dict[str, str]:
        headers = {"Content-Type": "application/json"}
        if self.api_key:
            headers["Authorization"] = f"Bearer {self.api_key}"
        return headers

    def ping(self) -> bool:
        try:
            response = httpx.get(f"{self.base_url}{_MODELS_PATH}", headers=self._headers(), timeout=5.0)
        except httpx.HTTPError as exc:
            log.warning("opencode_ping_failed: %s", exc)
            return False
        return response.status_code == 200

    def complete_json(self, *, system: str, user: str, schema: dict, timeout_s: float) -> LLMResult:
        body = {
            "model": self.model or None,
            "messages": [
                {"role": "system", "content": system + "\n\nJSON SCHEMA:\n" + json.dumps(schema, sort_keys=True)},
                {"role": "user", "content": user},
            ],
            "format": "json",
        }
        with httpx.Client(timeout=timeout_s or self.timeout_s) as client:
            response = client.post(f"{self.base_url}{_CHAT_PATH}", headers=self._headers(), json=body)
        response.raise_for_status()
        payload = response.json()
        content = payload.get("message", {}).get("content") or payload.get("content") or "{}"
        parsed = content if isinstance(content, dict) else json.loads(content)
        return LLMResult(payload=parsed, provider=self.id, model=self.model, is_mock=False)
