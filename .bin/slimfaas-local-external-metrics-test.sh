#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
run_root="${EXTERNAL_METRICS_TEST_RUN_ROOT:-$repo_root/artifacts/slimfaas-local-external-metrics/$timestamp}"
manifest="$repo_root/benchmarks/slimfaas.local.external-metrics.yaml"
slimfaas_project="$repo_root/src/SlimFaas/SlimFaas.csproj"
benchmark_project="$repo_root/src/SlimFaasBenchmark/SlimFaasBenchmark.csproj"
slimfaas_dll="$repo_root/src/SlimFaas/bin/Release/net10.0/SlimFaas.dll"
local_log="$run_root/slimfaas-local.log"
entrypoint="http://127.0.0.1:31120"
exporter="http://127.0.0.1:31180"
function_name="benchmark-external-scale"
local_pid=""

mkdir -p "$run_root"

cleanup() {
  local exit_code=$?
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
  if [[ "$exit_code" -ne 0 ]]; then
    echo "External metrics integration test failed. Logs are preserved in $run_root" >&2
    tail -n 100 "$local_log" >&2 2>/dev/null || true
  fi
  return "$exit_code"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

wait_for_http() {
  local url="$1"
  local description="$2"
  for attempt in $(seq 1 120); do
    if [[ -n "$local_pid" ]] && ! kill -0 "$local_pid" 2>/dev/null; then
      echo "slimfaas local exited while waiting for $description" >&2
      return 1
    fi
    if curl --silent --fail "$url" >/dev/null; then
      return 0
    fi
    sleep 1
  done
  echo "Timed out waiting for $description" >&2
  return 1
}

wait_for_scale() {
  local expected="$1"
  local status=""
  for _ in $(seq 1 120); do
    status="$(curl --silent --fail "$entrypoint/status-function/$function_name" 2>/dev/null || true)"
    if [[ "$status" == *"\"NumberRequested\":$expected"* &&
          "$status" == *"\"NumberReady\":$expected"* ]]; then
      echo "Validated scale target $expected: $status"
      return 0
    fi
    sleep 1
  done
  echo "Timed out waiting for requested/ready replicas=$expected. Last status: $status" >&2
  return 1
}

if [[ "${EXTERNAL_METRICS_TEST_SKIP_BUILD:-false}" != "true" ]]; then
  echo "Building SlimFaas and the controllable OpenMetrics exporter"
  dotnet build "$slimfaas_project" -c Release -p:SkipClientAppBuild=true --nologo
  dotnet build "$benchmark_project" -c Release --nologo
fi

dotnet "$slimfaas_dll" local validate -f "$manifest"

echo "Starting slimfaas local external metrics scenario"
(
  cd "$repo_root"
  exec dotnet "$slimfaas_dll" local up -f "$manifest" --clean
) >"$local_log" 2>&1 &
local_pid="$!"

wait_for_http "$entrypoint/ready" "SlimFaas readiness"
wait_for_http "$exporter/health" "external exporter readiness"
wait_for_scale 0

for expected in 2 4 0; do
  curl --silent --fail --request PUT "$exporter/benchmark/external-pressure/$expected" >/dev/null
  wait_for_scale "$expected"
done

echo "External OpenMetrics autoscaling integration test passed (0 -> 2 -> 4 -> 0)."
echo "Logs: $run_root"
