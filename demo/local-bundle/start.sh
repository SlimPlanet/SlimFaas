#!/bin/sh
# Run from any directory; paths in the overlay are expanded by SlimFaas itself.
set -eu
demo_root=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
cd "$demo_root"
SLIMFAAS_DEMO_ROOT=$demo_root
SLIMFAAS_DEMO_EXE_SUFFIX=
case $(uname -s) in
    MINGW*|MSYS*|CYGWIN*)
        SLIMFAAS_DEMO_ROOT=$(cygpath -am "$demo_root")
        SLIMFAAS_DEMO_EXE_SUFFIX=.exe
        ;;
esac
export SLIMFAAS_DEMO_ROOT SLIMFAAS_DEMO_EXE_SUFFIX
runtime="$demo_root/runtime/SlimFaas$SLIMFAAS_DEMO_EXE_SUFFIX"
if [ "${1:-}" = --validate ]; then
    shift
    exec "$runtime" local validate -f slimfaas.local.yaml -f slimfaas.local.prebuilt.yaml "$@"
fi
# --clean belongs to local up only. Preserve all other arguments, including
# overlay paths containing spaces, when validating before startup.
clean=false
remaining=$#
while [ "$remaining" -gt 0 ]; do
    argument=$1
    shift
    case "$argument" in
        --clean) clean=true ;;
        *) set -- "$@" "$argument" ;;
    esac
    remaining=$((remaining - 1))
done
"$runtime" local validate -f slimfaas.local.yaml -f slimfaas.local.prebuilt.yaml "$@"
if [ "$clean" = true ]; then set -- "$@" --clean; fi
printf '%s\n' 'Dashboard: http://127.0.0.1:30020/ (wait for readiness)' 'Press Ctrl+C to stop. State is kept in .slimfaas/slimfaas-demo.'
exec "$runtime" local up -f slimfaas.local.yaml -f slimfaas.local.prebuilt.yaml "$@"
