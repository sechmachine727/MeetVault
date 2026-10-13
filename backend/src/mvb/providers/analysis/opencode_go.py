from __future__ import annotations

import uuid

from mvb.providers.analysis.lmstudio import LmStudioAnalyzer

_USER_AGENT = "meetvault-backend/1.0"
_SESSION_HEADER = "x-opencode-session"


class OpenCodeGoAnalyzer(LmStudioAnalyzer):
    """OpenCode Go (managed gateway) analyzer.

    Same OpenAI wire shape as LM Studio, plus Go's client contract: an identifying
    User-Agent (generic SDK names risk blocks) and a stable session id per analyzer
    instance — one instance serves one analysis run, which aids routing and caching.
    """

    def __init__(
        self,
        *,
        base_url: str,
        api_key: str = "",
        model: str = "",
        timeout_s: float = 120.0,
        session_id: str = "",
    ) -> None:
        super().__init__(base_url=base_url, api_key=api_key, model=model, timeout_s=timeout_s)
        self.id = "opencode-go"
        self.session_id = session_id or uuid.uuid4().hex

    def _headers(self) -> dict[str, str]:
        headers = super()._headers()
        headers["User-Agent"] = _USER_AGENT
        headers[_SESSION_HEADER] = self.session_id
        return headers
