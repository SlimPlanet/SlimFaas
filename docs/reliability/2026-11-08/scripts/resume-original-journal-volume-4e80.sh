#!/usr/bin/env bash
set -euo pipefail
root=/root/.cache/slimdata-lts/traffic-comparison-7e1
if [[ "${1:-}" != --inside ]]; then
  cp "$0" "$root/resume-original-journal-volume-4e80.sh"
  exec unshare --mount --net --propagation private -- bash "$root/resume-original-journal-volume-4e80.sh" --inside
fi
cd "$root"
sha256sum --check artifacts/journal-before-recovery-archive.sha256
mount -o loop disk-journal-before-19b-resources/disk.img disk-journal-before-19b-resources/mount
trap 'umount "$root/disk-journal-before-19b-resources/mount"' EXIT
test "$(cat disk-journal-before-19b-resources/mount/.slimdata-chaos-volume)" = slimdata-local-chaos-v1
ip link set lo up
export DOTNET_ROOT=/root/.cache/slimdata-lts/dotnet
export PATH="$DOTNET_ROOT:$PATH"
python3 .bin/test-slimdata-local-chaos.py --resume \
  --state-directory "$root/disk-journal-before-19b-resources/mount/state" \
  --runtime "$root/runtime-leader-event-4e80/SlimFaas.dll" --dotnet "$DOTNET_ROOT/dotnet" \
  --source-commit 4e80c9a19f3f5e2a633f23ad00b510e8b25676dc \
  --output "$root/disk-journal-before-19b" --scenarios api,restarts \
  --recovery-timeout 300
