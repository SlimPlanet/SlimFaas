#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
manifest="$repo_root/benchmarks/slimfaas.local.benchmark.yaml"
slimfaas_dll="$repo_root/src/SlimFaas/bin/Release/net10.0/SlimFaas.dll"
run_root="${RECOVERY_RUN_ROOT:-$repo_root/artifacts/slimfaas-local-recovery/$(date -u +%Y%m%dT%H%M%SZ)}"
timeout_seconds="${RECOVERY_TIMEOUT_SECONDS:-180}"
traffic_interval="${RECOVERY_TRAFFIC_INTERVAL_MS:-25}"
traffic_sleep="$(printf '%d.%03d' "$((traffic_interval / 1000))" "$((traffic_interval % 1000))")"
entrypoint_http_port=31020
node_http_ports=(31021 31022 31023)
raft_ports=(3162 3163 3164)
local_pid=""
traffic_pid=""
write_pid=""

mkdir -p "$run_root"

if [[ "${RECOVERY_SKIP_BUILD:-false}" == "true" ]]; then
  echo "Skipping build; using existing Release binaries"
else
  dotnet build "$repo_root/src/SlimFaas/SlimFaas.csproj" -c Release -p:SkipClientAppBuild=true --nologo
  dotnet build "$repo_root/src/SlimFaasBenchmark/SlimFaasBenchmark.csproj" -c Release --nologo
fi

dotnet "$slimfaas_dll" local validate -f "$manifest"

cleanup() {
  [[ -n "$traffic_pid" ]] && kill "$traffic_pid" 2>/dev/null || true
  [[ -n "$write_pid" ]] && kill "$write_pid" 2>/dev/null || true
  if [[ -n "$local_pid" ]] && kill -0 "$local_pid" 2>/dev/null; then
    kill -TERM "$local_pid" 2>/dev/null || true
    wait "$local_pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

(
  cd "$repo_root"
  exec dotnet "$slimfaas_dll" local up -f "$manifest" --clean
) >"$run_root/slimfaas-local.log" 2>&1 &
local_pid="$!"

wait_for() {
  local url="$1"
  local deadline=$((SECONDS + timeout_seconds))
  while (( SECONDS < deadline )); do
    curl --silent --fail --max-time 2 "$url" >/dev/null 2>&1 && return 0
    sleep 1
  done
  return 1
}

for port in "$entrypoint_http_port" "${node_http_ports[@]}"; do
  wait_for "http://127.0.0.1:$port/ready" || {
    echo "Timed out waiting for $port/ready; see $run_root/slimfaas-local.log" >&2
    exit 1
  }
done

leader_port() {
  for port in "${raft_ports[@]}"; do
    response="$(curl --silent --max-time 2 "http://127.0.0.1:$port/SlimData/leader" || true)"
    if [[ "$response" =~ Leader\ address\ is\ http://127\.0\.0\.1:([0-9]+) ]]; then
      echo "${BASH_REMATCH[1]}"
      return 0
    fi
  done
  return 1
}

metric() {
  curl --silent --max-time 2 "http://127.0.0.1:$1/metrics" |
    awk -v name="$2" '$1 == name { value=$2 } END { if (value == "") exit 1; print value }'
}

leader="$(leader_port)" || { echo "Unable to identify the SlimData leader" >&2; exit 1; }
leader_http_port=""
restart_raft_port=""
restart_http_port=""
for index in "${!raft_ports[@]}"; do
  if [[ "${raft_ports[$index]}" == "$leader" ]]; then
    leader_http_port="${node_http_ports[$index]}"
  elif [[ -z "$restart_raft_port" ]]; then
    restart_raft_port="${raft_ports[$index]}"
    restart_http_port="${node_http_ports[$index]}"
  fi
done
[[ -n "$leader_http_port" && -n "$restart_raft_port" ]] ||
  { echo "Unexpected leader port: $leader" >&2; exit 1; }
leader_before="$leader"
echo "Leader is $leader; restarting non-leader node $restart_http_port"

(
  while :; do
    curl --silent --show-error --max-time 2 \
      -X POST -H 'Content-Type: application/octet-stream' \
      --data-binary "recovery-$SECONDS-$RANDOM" \
      "http://127.0.0.1:$entrypoint_http_port/data/sets/recovery" >/dev/null || true
    sleep "$traffic_sleep"
  done
) >"$run_root/writes.log" 2>&1 &
write_pid="$!"

(
  while :; do
    curl --silent --show-error --max-time 2 \
      "http://127.0.0.1:$entrypoint_http_port/function/benchmark-latency/echo" >/dev/null || true
    sleep "$traffic_sleep"
  done
) >"$run_root/http-traffic.log" 2>&1 &
traffic_pid="$!"

sleep 3
node_pid="$(lsof -t -i "TCP:$restart_raft_port" -sTCP:LISTEN | head -n1 || true)"
[[ -n "$node_pid" ]] || { echo "Unable to find node process on $restart_raft_port" >&2; exit 1; }
kill -KILL "$node_pid"

start_index=""
metric_deadline=$((SECONDS + 10))
while (( SECONDS < metric_deadline )); do
  if start_index="$(metric "$leader_http_port" slimdata_raft_committed_log_index)"; then
    break
  fi
  sleep 1
done
[[ -n "$start_index" ]] || { echo "Unable to read the leader committed index" >&2; exit 1; }
deadline=$((SECONDS + timeout_seconds))
health_seen=0
ready_seen=0
last_index="$start_index"
leader_missing_since=0
while (( SECONDS < deadline )); do
  current_leader="$(leader_port || true)"
  if [[ -z "$current_leader" ]]; then
    if (( leader_missing_since == 0 )); then
      leader_missing_since="$SECONDS"
    fi
    if (( SECONDS - leader_missing_since > 10 )); then
      echo "Leader endpoint unavailable for more than 10s" >&2
      exit 1
    fi
  else
    leader_missing_since=0
    [[ "$current_leader" == "$leader_before" ]] || {
      echo "Leader changed from $leader_before to $current_leader" >&2
      exit 1
    }
  fi
  if curl --silent --fail --max-time 2 "http://127.0.0.1:$restart_http_port/health" >/dev/null; then
    health_seen=1
  fi
  if curl --silent --fail --max-time 2 "http://127.0.0.1:$restart_http_port/ready" >/dev/null; then
    ready_seen=1
  fi
  last_index="$(metric "$leader_http_port" slimdata_raft_committed_log_index || echo "$last_index")"
  if (( health_seen == 1 && ready_seen == 1 )) && [[ "$last_index" -gt "$start_index" ]]; then
    printf 'leader_port=%s\nrestarted_http_port=%s\nstart_committed_index=%s\nfinal_committed_index=%s\n' \
      "$leader_before" "$restart_http_port" "$start_index" "$last_index" >"$run_root/recovery-summary.txt"
    echo "Recovery completed: committed index $start_index -> $last_index"
    exit 0
  fi
  sleep 1
done

echo "Recovery did not complete within ${timeout_seconds}s (health=$health_seen ready=$ready_seen committed=$start_index->$last_index)" >&2
exit 1
