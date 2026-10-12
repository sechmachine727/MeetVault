from __future__ import annotations

import hashlib
import json

from mvb.providers.base import LLMResult


class MockAnalyzer:
    """Deterministic, schema-valid, offline. Every text field starts with `[mock]` and carries
    a short digest of the input, so mock output is obvious and reproducible."""

    id = "mock-analyzer"
    is_mock = True

    def __init__(self) -> None:
        self.model = "mock-llm-v1"

    def complete_json(self, *, system: str, user: str, schema: dict, timeout_s: float) -> LLMResult:
        digest = hashlib.sha256(f"{system}\n{user}\n{json.dumps(schema, sort_keys=True)}".encode()).hexdigest()[:12]
        kind = schema.get("x-kind", "summary")
        payload = _build(kind, digest)
        return LLMResult(payload=payload, provider=self.id, model=self.model, is_mock=True)


def _build(kind: str, digest: str) -> dict:
    if kind == "agenda":
        return {
            "items": [
                {"title": f"[mock] Opening #{digest}", "topic": "intro", "start_ms": 0, "end_ms": 300_000},
                {"title": f"[mock] Main topic #{digest}", "topic": "main", "start_ms": 300_000, "end_ms": 900_000},
                {"title": f"[mock] Wrap-up #{digest}", "topic": "closing", "start_ms": 900_000, "end_ms": 1_200_000},
            ]
        }
    if kind == "summary":
        return {
            "text": f"[mock] Summary {digest}",
            "bullets": [f"[mock] point 1 {digest}", f"[mock] point 2 {digest}"],
            "language": "en",
        }
    if kind == "section_summary":
        return {
            "summary": f"[mock] Section summary {digest}",
            "bullets": [f"[mock] section point {digest}"],
            "decisions": [f"[mock] decision {digest}"],
            "action_items": [f"[mock] action {digest}"],
        }
    if kind == "decisions":
        # EXTRACT_SCHEMA shares this x-kind: return both halves.
        return {
            "items": [
                {"text": f"[mock] decision {digest}", "decided_by": ["Speaker 1"], "at_ms": 120_000, "segment_ids": []}
            ],
            "decisions": [
                {"text": f"[mock] decision {digest}", "decided_by": ["Speaker 1"], "at_ms": 120_000, "segment_ids": []}
            ],
            "action_items": [{"text": f"[mock] action {digest}", "owner": "Speaker 2", "due": None, "segment_ids": []}],
        }
    if kind == "action_items":
        return {"items": [{"text": f"[mock] action {digest}", "owner": "Speaker 2", "due": None, "segment_ids": []}]}
    if kind == "qa":
        return {"answer": f"[mock] answer {digest}", "citation_segment_ids": []}
    raise ValueError(f"mock analyzer has no builder for kind '{kind}'")
