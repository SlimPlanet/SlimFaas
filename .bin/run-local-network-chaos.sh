#!/usr/bin/env bash
# Keep packet-drop rules inside a disposable network namespace. No host rules
# are changed, even if the campaign fails or is interrupted.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "${1:-}" != "--inside" ]]; then
  if [[ "$EUID" != 0 ]]; then
    echo "Run as root with unshare, iproute2 and iptables installed." >&2
    exit 2
  fi
  # Keep PID 1 in the original namespace when this wrapper is a container's
  # entrypoint. The harness independently checks that its namespace differs.
  unshare --fork --kill-child=TERM --net -- bash "$0" --inside "$@" &
  child=$!
  trap 'kill -TERM "$child" 2>/dev/null || true' TERM INT
  wait "$child"
  exit $?
fi
shift
ip link set lo up
cd "$repo_root"
exec python3 .bin/test-slimdata-local-chaos.py "$@"
