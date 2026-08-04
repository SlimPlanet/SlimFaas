#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
profile="${BENCHMARK_PROFILE:-standard}"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
run_root="${BENCHMARK_RUN_ROOT:-$repo_root/artifacts/slimfaas-local-benchmark/$profile-$timestamp}"
manifest="$repo_root/benchmarks/slimfaas.local.benchmark.yaml"
slimfaas_project="$repo_root/src/SlimFaas/SlimFaas.csproj"
benchmark_project="$repo_root/src/SlimFaasBenchmark/SlimFaasBenchmark.csproj"
slimfaas_dll="$repo_root/src/SlimFaas/bin/Release/net10.0/SlimFaas.dll"
benchmark_dll="$repo_root/src/SlimFaasBenchmark/bin/Release/net10.0/SlimFaasBenchmark.dll"
local_log="$run_root/slimfaas-local.log"

case "$profile" in
  quick)
    duration_seconds="${BENCHMARK_DURATION_SECONDS:-3}"
    warmup_seconds="${BENCHMARK_WARMUP_SECONDS:-1}"
    repetitions="${BENCHMARK_REPETITIONS:-1}"
    ;;
  standard)
    duration_seconds="${BENCHMARK_DURATION_SECONDS:-10}"
    warmup_seconds="${BENCHMARK_WARMUP_SECONDS:-2}"
    repetitions="${BENCHMARK_REPETITIONS:-3}"
    ;;
  *)
    echo "BENCHMARK_PROFILE must be quick or standard" >&2
    exit 2
    ;;
esac

payload_bytes="${BENCHMARK_PAYLOAD_BYTES:-64,4096,262144,2097152}"
concurrency="${BENCHMARK_CONCURRENCY:-1,16}"
scale_messages="${BENCHMARK_SCALE_MESSAGES:-200}"
scale_concurrency="${BENCHMARK_SCALE_CONCURRENCY:-32}"
scale_timeout="${BENCHMARK_SCALE_TIMEOUT_SECONDS:-120}"
async_drain_timeout="${BENCHMARK_ASYNC_DRAIN_TIMEOUT_SECONDS:-180}"

mkdir -p "$run_root"

if [[ "${BENCHMARK_SKIP_BUILD:-false}" == "true" ]]; then
  echo "Skipping build; using existing Release binaries"
else
  echo "Building SlimFaas and the benchmark target/driver"
  dotnet build "$slimfaas_project" -c Release -p:SkipClientAppBuild=true --nologo
  dotnet build "$benchmark_project" -c Release --nologo
fi

dotnet "$slimfaas_dll" local validate -f "$manifest"

{
  echo "started_at=$timestamp"
  echo "profile=$profile"
  echo "commit=$(git -C "$repo_root" rev-parse HEAD)"
  echo "worktree_dirty=$(git -C "$repo_root" status --porcelain | grep -q . && echo true || echo false)"
  echo "dotnet_sdk=$(dotnet --version)"
  echo "host=$(uname -s)-$(uname -m)"
  echo "duration_seconds=$duration_seconds"
  echo "warmup_seconds=$warmup_seconds"
  echo "repetitions=$repetitions"
  echo "payload_bytes=$payload_bytes"
  echo "concurrency=$concurrency"
  echo "scale_messages=$scale_messages"
  echo "scale_concurrency=$scale_concurrency"
} >"$run_root/benchmark-manifest.txt"

local_pid=""
cleanup() {
  if [[ -n "$local_pid" ]] && kill -0 "$local_pid" 2>/dev/null; then
    kill -TERM "$local_pid" 2>/dev/null || true
    for _ in $(seq 1 100); do
      if ! kill -0 "$local_pid" 2>/dev/null; then
        break
      fi
      sleep 0.1
    done
    if kill -0 "$local_pid" 2>/dev/null; then
      kill -KILL "$local_pid" 2>/dev/null || true
    fi
    wait "$local_pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT
trap 'exit 130' INT TERM

echo "Starting the three-node native local cluster"
(
  cd "$repo_root"
  exec dotnet "$slimfaas_dll" local up -f "$manifest" --clean
) >"$local_log" 2>&1 &
local_pid="$!"

ready=0
for attempt in $(seq 1 180); do
  if ! kill -0 "$local_pid" 2>/dev/null; then
    echo "slimfaas local exited before readiness; see $local_log" >&2
    exit 1
  fi
  if curl --silent --fail "http://127.0.0.1:31020/ready" >/dev/null &&
     curl --silent --fail "http://127.0.0.1:31080/health" >/dev/null; then
    ready=1
    break
  fi
  if (( attempt % 10 == 0 )); then
    echo "Waiting for cluster and direct target readiness (${attempt}s)"
  fi
  sleep 1
done
if [[ "$ready" != "1" ]]; then
  echo "Benchmark environment did not become ready; see $local_log" >&2
  exit 1
fi

dotnet "$benchmark_dll" run \
  --slimfaas-url http://127.0.0.1:31020 \
  --direct-url http://127.0.0.1:31080 \
  --node-urls http://127.0.0.1:31021,http://127.0.0.1:31022,http://127.0.0.1:31023 \
  --duration "$duration_seconds" \
  --warmup "$warmup_seconds" \
  --repetitions "$repetitions" \
  --payload-bytes "$payload_bytes" \
  --concurrency "$concurrency" \
  --async-drain-timeout "$async_drain_timeout" \
  --scale-messages "$scale_messages" \
  --scale-concurrency "$scale_concurrency" \
  --scale-timeout "$scale_timeout" \
  --output "$run_root"

echo "SlimFaas local benchmark artifacts: $run_root"
