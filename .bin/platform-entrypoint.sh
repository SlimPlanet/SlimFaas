#!/bin/sh
set -eu
if [ "${SLIMFAAS_VALIDATION_JOB:-0}" = "1" ]; then
    exec python3 /src/tests/SlimFaas.Tests/Local/chaos_function.py \
        --journal-directory /tmp/slimfaas-job-receipts --job "$@"
fi
exec /app/SlimFaas "$@"
