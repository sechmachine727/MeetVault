from __future__ import annotations

import logging
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any

from sqlalchemy.orm import Session

from mvb.db.models import Job
from mvb.jobs.queue import add_event, cancel, complete, fail, set_progress

log = logging.getLogger(__name__)


@dataclass(slots=True)
class JobContext:
    """Everything a handler may touch. No globals, no hidden state."""

    session: Session
    job: Job
    settings: Any  # mvb.config.Settings
    storage: Any  # mvb.storage.local.LocalStorage
    cancellable: Callable[[], bool]  # True when this job has been cancelled out-of-band

    @property
    def params(self) -> dict[str, Any]:
        return dict(self.job.params_json or {})

    def progress(self, value: float) -> None:
        set_progress(self.session, self.job, value)
        self.session.commit()

    def event(self, message: str, **data: Any) -> None:
        add_event(self.session, self.job, message, data=data)
        self.session.commit()

    def check_cancelled(self) -> None:
        if self.cancellable():
            raise JobCancelled("cancelled by request")


class JobCancelled(Exception):
    pass


Handler = Callable[[JobContext], dict[str, Any]]
REGISTRY: dict[str, Handler] = {}


def handler(job_type: str) -> Callable[[Handler], Handler]:
    def decorator(function: Handler) -> Handler:
        REGISTRY[job_type] = function
        return function

    return decorator


def registered_types() -> list[str]:
    return sorted(REGISTRY)


def run_handler(context: JobContext) -> None:
    """Run the registered handler for this job; persist success/failure. Never raises."""
    function = REGISTRY.get(context.job.type)
    if function is None:
        fail(context.session, context.job, f"no handler registered for job type '{context.job.type}'")
        context.session.commit()
        return
    try:
        result = function(context)
    except JobCancelled:
        cancel(context.session, context.job)
        context.session.commit()
        return
    except Exception as exc:  # a job must never kill the worker
        log.exception("job_failed", extra={"job_type": context.job.type})
        fail(context.session, context.job, f"{type(exc).__name__}: {exc}")
        context.session.commit()
        return
    complete(
        context.session,
        context.job,
        result=result or {},
        is_mock=bool((result or {}).get("is_mock", False)),
    )
    context.session.commit()
