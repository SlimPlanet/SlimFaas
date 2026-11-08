#!/usr/bin/env bash
# Exhaust a new 128 MiB ext4 image in a private mount/network namespace. The
# image is retained; runtime state is never removed to obtain recovery.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "${1:-}" != "--inside" ]]; then
  if [[ "$EUID" != 0 || "$#" -lt 2 ]]; then
    echo "Usage (as root): run-local-disk-chaos.sh NEW_RESOURCE_DIRECTORY <campaign arguments>" >&2
    exit 2
  fi
  resources="$1"
  shift
  mkdir "$resources"
  resources="$(cd "$resources" && pwd)"
  truncate -s 128M "$resources/disk.img"
  mkfs.ext4 -q -F "$resources/disk.img"
  mkdir "$resources/mount"
  exec unshare --mount --net --propagation private -- bash "$0" --inside "$resources" "$@"
fi
shift
resources="$1"
shift
mount -o loop "$resources/disk.img" "$resources/mount"
trap 'umount "$resources/mount"' EXIT
printf '%s\n' 'slimdata-local-chaos-v1' > "$resources/mount/.slimdata-chaos-volume"
ip link set lo up
cd "$repo_root"
python3 .bin/test-slimdata-local-chaos.py --state-directory "$resources/mount/state" "$@"
