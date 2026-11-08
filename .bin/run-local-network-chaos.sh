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
  exec unshare --net -- bash "$0" --inside "$@"
fi
shift
ip link set lo up
cd "$repo_root"
exec python3 .bin/test-slimdata-local-chaos.py "$@"
