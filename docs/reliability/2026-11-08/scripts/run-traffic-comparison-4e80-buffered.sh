#!/usr/bin/env bash
set -euo pipefail
root=/root/.cache/slimdata-lts/traffic-comparison-7e1
if [[ "${1:-}" != --inside ]]; then
  test -f "$root/runtime-baseline-published-496/SlimFaas.dll"
  cp "$0" "$root/run-traffic-comparison-4e80-buffered.sh"
  exec unshare --net -- bash "$root/run-traffic-comparison-4e80-buffered.sh" --inside
fi
cd "$root"
ip link set lo up
export DOTNET_ROOT=/root/.cache/slimdata-lts/dotnet
export PATH="$DOTNET_ROOT:$PATH"
output="$root/artifacts/traffic-4e80-buffered"
test ! -e "$output"
mkdir "$output"
sha256sum runtime-baseline-published-496/SlimFaas.dll runtime-leader-event-4e80/SlimFaas.dll \
  .bin/test-slimdata-local-chaos.py .bin/raft_traffic_probe.py > "$output/source-files.sha256"
ordinal=0
failed=0
for variant in baseline candidate candidate baseline; do
  ordinal=$((ordinal+1))
  if [[ "$variant" == baseline ]]; then
    runtime="$root/runtime-baseline-published-496/SlimFaas.dll"
    revision=496e8739e5f7b4326f64520fe4caa4e0e23f8d06
  else
    runtime="$root/runtime-leader-event-4e80/SlimFaas.dll"
    revision=4e80c9a19f3f5e2a633f23ad00b510e8b25676dc
  fi
  label="$ordinal-$variant"
  if python3 .bin/raft_traffic_probe.py --ports 35130,35131,35132 \
    --output "$output/$label-tcp.json" -- python3 .bin/test-slimdata-local-chaos.py \
    --runtime "$runtime" --dotnet "$DOTNET_ROOT/dotnet" --source-commit "$revision" \
    --output "$output/$label" --scenarios payloads --concurrency 4 \
    --payload-sizes 65536,262144,1048576,2000000,2097152,4194304,65536,262144,1048576,2000000,2097152,4194304 \
    --request-timeout 120 --recovery-timeout 300 > "$output/$label.log" 2>&1; then
    code=0
  else
    code=$?
    failed=1
  fi
  printf '{"ordinal":%d,"variant":"%s","exit_code":%d}\n' "$ordinal" "$variant" "$code" >> "$output/status.jsonl"
done
exit "$failed"
