#!/usr/bin/env bash
set -euo pipefail
root=/root/.cache/slimdata-lts/traffic-comparison-7e1
cp /mnt/c/github/SlimFaas/artifacts/slimdata-lts/replay-original-file-image.py "$root/replay-original-file-image.py"
test ! -e "$root/ci-7920-linux.zip"
cp /mnt/c/github/SlimFaas/artifacts/slimdata-lts/ci-7920-linux.zip "$root/ci-7920-linux.zip"
exec unshare --mount --net --propagation private -- python3 "$root/replay-original-file-image.py"
