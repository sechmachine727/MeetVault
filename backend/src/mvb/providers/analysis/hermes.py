from __future__ import annotations

from mvb.providers.analysis.lmstudio import LmStudioAnalyzer


class HermesAnalyzer(LmStudioAnalyzer):
    """Hermes proxy analyzer: the proxy speaks OpenAI (`/models`, `/chat/completions`),
    so the LM Studio wire format is reused verbatim; only the identity differs."""

    def __init__(self, *, base_url: str, api_key: str = "", model: str = "", timeout_s: float = 120.0) -> None:
        super().__init__(base_url=base_url, api_key=api_key, model=model, timeout_s=timeout_s)
        self.id = "hermes"
