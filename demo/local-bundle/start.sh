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
"$runtime" local validate -f slimfaas.local.yaml -f slimfaas.local.prebuilt.yaml "$@"
printf '%s\n' 'Dashboard: http://127.0.0.1:30020/ (wait for readiness)' 'Press Ctrl+C to stop. State is kept in .slimfaas/slimfaas-demo.'
exec "$runtime" local up -f slimfaas.local.yaml -f slimfaas.local.prebuilt.yaml "$@"
