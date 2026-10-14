#!/bin/sh
# MeetVault container entrypoint: apply migrations, then exec the container
# command (the api's uvicorn CMD or the worker's python -m mvb.jobs.worker).
# `alembic upgrade head` is idempotent — an up-to-date schema is a no-op, so
# the worker re-running it after the api is harmless.
# Scale-out note: several api replicas starting at the exact same moment could
# race on the migration, exactly as the previous compose command could; the
# default single-instance deployment is unaffected and the worker waits for
# the api healthcheck in compose.
set -e
alembic upgrade head
exec "$@"
