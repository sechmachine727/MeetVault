from __future__ import annotations

import json
import logging

from mvb.logging_setup import JsonFormatter, configure_logging, job_id_var, request_id_var


def test_json_formatter_includes_context() -> None:
    configure_logging("INFO")
    token = request_id_var.set("req-1")
    job_token = job_id_var.set("job-1")
    record = logging.LogRecord("mvb.test", logging.INFO, __file__, 1, "hello %s", ("world",), None)
    try:
        payload = json.loads(JsonFormatter().format(record))
    finally:
        request_id_var.reset(token)
        job_id_var.reset(job_token)
    assert payload["msg"] == "hello world"
    assert payload["level"] == "INFO"
    assert payload["request_id"] == "req-1"
    assert payload["job_id"] == "job-1"
    assert payload["logger"] == "mvb.test"
    assert "ts" in payload


def test_extra_fields_are_merged() -> None:
    record = logging.LogRecord("mvb.jobs", logging.WARNING, __file__, 1, "job_failed", (), None)
    record.provider = "lmstudio"
    record.duration_ms = 42
    record.unrelated = "secret-ish"
    payload = json.loads(JsonFormatter().format(record))
    assert payload["provider"] == "lmstudio"
    assert payload["duration_ms"] == 42
    assert "unrelated" not in payload
