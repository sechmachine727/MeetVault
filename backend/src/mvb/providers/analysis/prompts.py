from __future__ import annotations

PROMPT_VERSION = "v1"

TRANSCRIPT_BLOCK_HEADER = "TRANSCRIPT (id | start_ms-end_ms | speaker): text"
QA_MAX_SEGMENTS = 40

KIND_TO_SCHEMA: dict[str, dict] = {
    "summary": {
        "type": "object",
        "required": ["text", "bullets"],
        "properties": {"text": {"type": "string"}, "bullets": {"type": "array", "items": {"type": "string"}}},
    },
    "agenda": {
        "type": "object",
        "required": ["items"],
        "properties": {
            "items": {
                "type": "array",
                "items": {
                    "type": "object",
                    "required": ["title", "start_ms", "end_ms"],
                    "properties": {
                        "title": {"type": "string"},
                        "topic": {"type": "string"},
                        "start_ms": {"type": "integer", "minimum": 0},
                        "end_ms": {"type": "integer", "minimum": 0},
                    },
                },
            }
        },
    },
    "section_summary": {
        "type": "object",
        "required": ["summary"],
        "properties": {
            "summary": {"type": "string"},
            "bullets": {"type": "array", "items": {"type": "string"}},
            "decisions": {"type": "array", "items": {"type": "string"}},
            "action_items": {"type": "array", "items": {"type": "string"}},
        },
    },
    "decisions": {
        "type": "object",
        "required": ["items"],
        "properties": {
            "items": {
                "type": "array",
                "items": {
                    "type": "object",
                    "required": ["text"],
                    "properties": {
                        "text": {"type": "string"},
                        "decided_by": {"type": "array", "items": {"type": "string"}},
                        "at_ms": {"type": "integer"},
                        "segment_ids": {"type": "array", "items": {"type": "string"}},
                    },
                },
            }
        },
    },
    "action_items": {
        "type": "object",
        "required": ["items"],
        "properties": {
            "items": {
                "type": "array",
                "items": {
                    "type": "object",
                    "required": ["text"],
                    "properties": {
                        "text": {"type": "string"},
                        "owner": {"type": "string"},
                        "due": {"type": "string"},
                        "segment_ids": {"type": "array", "items": {"type": "string"}},
                    },
                },
            }
        },
    },
    "qa": {
        "type": "object",
        "required": ["answer", "citation_segment_ids"],
        "properties": {
            "answer": {"type": "string"},
            "citation_segment_ids": {"type": "array", "items": {"type": "string"}},
        },
    },
}

SYSTEM_ANALYST = (
    "You are a meticulous meeting analyst. Answer ONLY with a single JSON object that matches the "
    "provided JSON schema. Never add prose outside the JSON. Cite segment ids exactly as they appear "
    "in the transcript. Do not invent segment ids, times or speakers."
)

AGENDA_PROMPT = (
    "Segment this meeting transcript into an agenda of contiguous sections.\n"
    "Rules: sections must be contiguous and non-overlapping; start_ms of the first section is 0; "
    "end_ms of the last section is the meeting duration; each section gets a short title (<= 60 chars) "
    "and a one-word topic.\n\n"
)

SUMMARY_PROMPT = (
    "Write a meeting summary: 2-4 sentences in `text` and 3-7 short `bullets`. "
    "Stay strictly grounded in the transcript; do not speculate.\n\n"
)

SECTION_PROMPT = (
    "Summarize ONE section of a meeting transcript: `summary` (2-3 sentences), `bullets` (2-5), "
    "`decisions` (verbatim-ish, may be empty) and `action_items` (may be empty).\n\n"
)

EXTRACT_PROMPT = (
    "Extract every explicit decision and every action item from the transcript. "
    "An action item must name a task; include its owner when stated and a due date when stated.\n\n"
)

QA_PROMPT = (
    "Answer the user's question using ONLY the provided transcript excerpt. "
    "If the excerpt does not answer it, say so. Always list the segment ids you used in "
    "`citation_segment_ids` (they must be real ids from the excerpt).\n\n"
)


EXTRACT_SCHEMA: dict = {
    "type": "object",
    "required": ["decisions", "action_items"],
    "properties": {
        "decisions": KIND_TO_SCHEMA["decisions"]["properties"]["items"],
        "action_items": KIND_TO_SCHEMA["action_items"]["properties"]["items"],
    },
    "x-kind": "decisions",
}


def meeting_qa_schema() -> dict:
    return dict(KIND_TO_SCHEMA["qa"], **{"x-kind": "qa"})


def kind_schema(kind: str) -> dict:
    return dict(KIND_TO_SCHEMA[kind], **{"x-kind": kind})


def transcript_block(rows: list[tuple[str, int, int, str, str]]) -> str:
    """rows: (segment_id, start_ms, end_ms, speaker_label, text) -> a compact, citable block."""
    lines = [TRANSCRIPT_BLOCK_HEADER]
    lines += [f"{sid} | {start}-{end} | {speaker}: {text}" for sid, start, end, speaker, text in rows]
    return "\n".join(lines)


def duration_note(duration_ms: int) -> str:
    return f"MEETING DURATION: {duration_ms} ms\n\n"
