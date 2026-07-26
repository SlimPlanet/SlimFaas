#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
mode="${1:-trimmed}"
scenario="${2:-mixed}"
duration_seconds="${3:-120}"
concurrency="${4:-12}"
warmup_seconds="${WARMUP_SECONDS:-20}"
cooldown_seconds="${COOLDOWN_SECONDS:-20}"
target_requests_per_second="${TARGET_REQUESTS_PER_SECOND:-0}"
target_requests_per_replica="${TARGET_REQUESTS_PER_REPLICA:-}"
slimdata_batch_mode="${SLIMDATA_BATCH_MODE:-Global}"
slimdata_batch_partitions="${SLIMDATA_BATCH_PARTITIONS:-8}"
slimdata_low_load_fast_path="${SLIMDATA_LOW_LOAD_FAST_PATH:-false}"
slimdata_low_load_rps="${SLIMDATA_LOW_LOAD_RPS:-10}"

if [[ "$mode" != "trimmed" && "$mode" != "aot" ]]; then
  echo "Usage: .bin/memory-lab.sh [trimmed|aot] [mixed|sync|async|set|files|slimdata-set|slimdata-mixed] [duration_seconds] [concurrency]" >&2
  exit 2
fi

case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) runtime_id="osx-arm64" ;;
  Darwin-x86_64) runtime_id="osx-x64" ;;
  Linux-aarch64|Linux-arm64) runtime_id="linux-arm64" ;;
  Linux-x86_64) runtime_id="linux-x64" ;;
  *)
    echo "Unsupported host: $(uname -s)-$(uname -m)" >&2
    exit 2
    ;;
esac

timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
run_label="${MEMORY_LAB_RUN_LABEL:-$mode}"
run_root="${MEMORY_LAB_RUN_ROOT:-$repo_root/artifacts/memory-lab}"
run_dir="$run_root/${run_label}-${scenario}-c${concurrency}-${timestamp}"
publish_dir="${MEMORY_LAB_PUBLISH_DIR:-$repo_root/artifacts/memory-lab/publish-${mode}-${runtime_id}}"
lab_project="$repo_root/tools/SlimFaas.MemoryLab/SlimFaas.MemoryLab.csproj"
lab_dll="$repo_root/tools/SlimFaas.MemoryLab/bin/Release/net10.0/SlimFaas.MemoryLab.dll"
memory_csv="$run_dir/memory.csv"
diagnostics_csv="$run_dir/diagnostics.csv"
phase_file="$run_dir/phase"
target_replica_args=()
if [[ -n "$target_requests_per_replica" ]]; then
  target_replica_args=(--target-rps-per-replica "$target_requests_per_replica")
fi
mkdir -p "$run_dir" "$publish_dir"

{
  echo "timestamp=$timestamp"
  echo "mode=$mode"
  echo "scenario=$scenario"
  echo "duration_seconds=$duration_seconds"
  echo "warmup_seconds=$warmup_seconds"
  echo "concurrency=$concurrency"
  echo "target_requests_per_second=$target_requests_per_second"
  echo "target_requests_per_replica=$target_requests_per_replica"
  echo "slimdata_batch_mode=$slimdata_batch_mode"
  echo "slimdata_batch_partitions=$slimdata_batch_partitions"
  echo "slimdata_low_load_fast_path=$slimdata_low_load_fast_path"
  echo "slimdata_low_load_rps=$slimdata_low_load_rps"
  echo "runtime_id=$runtime_id"
  echo "publish_dir=$publish_dir"
  echo "run_label=$run_label"
} >"$run_dir/manifest.txt"

node_pids=()
function_pid=""
sampler_pid=""
cleanup() {
  if [[ -n "$sampler_pid" ]] && kill -0 "$sampler_pid" 2>/dev/null; then
    kill "$sampler_pid" 2>/dev/null || true
  fi
  for pid in ${node_pids[@]+"${node_pids[@]}"}; do
    if kill -0 "$pid" 2>/dev/null; then
      kill "$pid" 2>/dev/null || true
    fi
  done
  if [[ -n "$function_pid" ]] && kill -0 "$function_pid" 2>/dev/null; then
    kill "$function_pid" 2>/dev/null || true
  fi
  for _ in $(seq 1 50); do
    running=0
    for pid in ${node_pids[@]+"${node_pids[@]}"} "$function_pid" "$sampler_pid"; do
      if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
        running=1
      fi
    done
    if [[ "$running" == "0" ]]; then
      break
    fi
    sleep 0.1
  done
  for pid in ${node_pids[@]+"${node_pids[@]}"} "$function_pid" "$sampler_pid"; do
    if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
      kill -KILL "$pid" 2>/dev/null || true
    fi
  done
  for pid in ${node_pids[@]+"${node_pids[@]}"} "$function_pid" "$sampler_pid"; do
    if [[ -n "$pid" ]]; then
      wait "$pid" 2>/dev/null || true
    fi
  done
}
trap cleanup EXIT
trap 'exit 130' INT TERM

echo "Building memory lab"
if [[ "${MEMORY_LAB_SKIP_LAB_BUILD:-0}" == "1" ]]; then
  if [[ ! -f "$lab_dll" ]]; then
    echo "Cannot reuse memory lab because $lab_dll does not exist" >&2
    exit 1
  fi
else
  dotnet build "$lab_project" -c Release --nologo
fi

publish_aot=false
if [[ "$mode" == "aot" ]]; then
  publish_aot=true
fi

if [[ "${MEMORY_LAB_SKIP_PUBLISH:-0}" == "1" ]]; then
  if [[ ! -x "$publish_dir/SlimFaas" ]]; then
    echo "Cannot reuse publication because $publish_dir/SlimFaas does not exist" >&2
    exit 1
  fi
  echo "Reusing SlimFaas publication mode=$mode rid=$runtime_id"
else
  echo "Publishing SlimFaas mode=$mode rid=$runtime_id"
  dotnet publish "$repo_root/src/SlimFaas/SlimFaas.csproj" \
    -c Release \
    -r "$runtime_id" \
    -o "$publish_dir" \
    -p:PublishAot="$publish_aot" \
    -p:PublishTrimmed=true \
    -p:TrimMode=full \
    -p:SelfContained=true \
    -p:SkipClientAppBuild=true \
    -p:DebugType=embedded \
    -p:StripSymbols=false \
    --nologo
fi

echo "Starting local function"
dotnet "$lab_dll" function --port 5050 >"$run_dir/function.log" 2>&1 &
function_pid="$!"

for attempt in $(seq 1 60); do
  if curl --silent --fail "http://127.0.0.1:5050/health" >/dev/null; then
    break
  fi
  if [[ "$attempt" == "60" ]]; then
    echo "Local function did not become ready" >&2
    exit 1
  fi
  sleep 1
done

for index in 0 1 2; do
  echo "Starting slimfaas-$index"
  (
    cd "$publish_dir"
    if [[ "${MEMORY_LAB_MALLOC_STACKS:-0}" == "1" ]]; then
      export MallocStackLogging=1
    fi
    exec env \
      HOSTNAME="slimfaas-$index" \
      SlimFaas__Orchestrator="Local" \
      SlimFaas__Namespace="memory-lab" \
      SlimFaas__BaseSlimDataUrl='http://{pod_ip}:{pod_port_0}' \
      SlimFaas__BaseFunctionUrl='http://{pod_ip}:{pod_port}' \
      SlimFaas__BaseFunctionPodUrl='http://{pod_ip}:{pod_port}' \
      SlimFaas__EnableFront="false" \
      SlimFaas__WebSocketPort="0" \
      SlimFaas__Local__NodeCount="3" \
      SlimFaas__Local__NodeNamePrefix="slimfaas-" \
      SlimFaas__Local__SlimDataPortBase="3262" \
      SlimFaas__Local__HttpPortBase="30021" \
      SlimFaas__Local__FunctionName="memory-function" \
      SlimFaas__Local__FunctionHost="127.0.0.1" \
      SlimFaas__Local__FunctionPort="5050" \
      SlimFaas__Local__NumberParallelRequest="64" \
      SlimData__Directory="$run_dir/state" \
      SlimData__AllowColdStart="true" \
      SlimData__WarmupRounds="10000" \
      SlimData__BatchMode="$slimdata_batch_mode" \
      SlimData__BatchPartitionCount="$slimdata_batch_partitions" \
      SlimData__LowLoadFastPath="$slimdata_low_load_fast_path" \
      SlimData__LowLoadRequestsPerSecond="$slimdata_low_load_rps" \
      Data__DefaultVisibility="Public" \
      Logging__LogLevel__Default="Warning" \
      Logging__LogLevel__Microsoft.AspNetCore="Error" \
      Logging__LogLevel__SlimData="Warning" \
      Logging__LogLevel__SlimFaas="Warning" \
      "$publish_dir/SlimFaas"
  ) >"$run_dir/slimfaas-$index.log" 2>&1 &
  node_pids+=("$!")
done

echo "Waiting for the three-node Raft cluster"
for attempt in $(seq 1 180); do
  ready=0
  for port in 30021 30022 30023; do
    if curl --silent --fail "http://127.0.0.1:$port/ready" >/dev/null; then
      ready=$((ready + 1))
    fi
  done
  if [[ "$ready" == "3" ]]; then
    break
  fi
  if (( attempt % 10 == 0 )); then
    echo "Cluster readiness: $ready/3 after ${attempt}s"
  fi
  if [[ "$attempt" == "180" ]]; then
    echo "Cluster did not become ready; logs are in $run_dir" >&2
    exit 1
  fi
  sleep 1
done

collect_cluster_state() {
  local phase="$1"
  for index in 0 1 2; do
    public_port=$((30021 + index))
    raft_port=$((3262 + index))
    curl --silent "http://127.0.0.1:$public_port/metrics" >"$run_dir/metrics-${phase}-slimfaas-${index}.prom" || true
    curl --silent "http://127.0.0.1:$raft_port/SlimData/leader" >"$run_dir/leader-${phase}-slimfaas-${index}.txt" || true
    state_dir="$run_dir/state/slimfaas-$index"
    if [[ -d "$state_dir" ]]; then
      du -sk "$state_dir" >"$run_dir/state-bytes-${phase}-slimfaas-${index}.txt" || true
      if [[ -d "$state_dir/wal" ]]; then
        if stat -f '%z %N' "$state_dir/wal" >/dev/null 2>&1; then
          find "$state_dir/wal" -type f -print0 | xargs -0 stat -f '%z %N' >"$run_dir/wal-files-${phase}-slimfaas-${index}.txt" 2>/dev/null || true
        else
          find "$state_dir/wal" -type f -print0 | xargs -0 stat -c '%s %n' >"$run_dir/wal-files-${phase}-slimfaas-${index}.txt" 2>/dev/null || true
        fi
      fi
    fi
  done
}

echo "Warm-up: ${warmup_seconds}s"
if ! dotnet "$lab_dll" load \
  --scenario "$scenario" \
  --duration "$warmup_seconds" \
  --concurrency "$concurrency" \
  --target-rps "$target_requests_per_second" \
  "${target_replica_args[@]}" \
  --first-port 30021 \
  --nodes 3 \
  --key-prefix "warmup-${timestamp}" \
  >"$run_dir/warmup.log" 2>&1; then
  echo "Warm-up reported errors; continuing so the measured run captures them" >&2
fi

collect_cluster_state "before"

echo "load" >"$phase_file"
echo "timestamp,node,pid,rss_kb,vsz_kb,phase" >"$memory_csv"
echo "timestamp,node,pid,phase,cpu_percent,managed_heap_bytes,process_cpu_seconds,raft_committed_index,raft_applied_index,wal_bytes_since_snapshot,state_queue_items,batch_queue_requests,batch_queue_bytes,batch_raft_total,batch_operations_total,snapshot_size_bytes,local_batch_rps,low_load_fast_path_active,local_batch_delay_ms,local_batch_coalesce_ms,batch_partition_count" >"$diagnostics_csv"
(
  metric_value() {
    local metrics="$1"
    local metric="$2"
    awk -v name="$metric" '$1 == name || index($1, name "{") == 1 { print $NF; exit }' <<<"$metrics"
  }
  while true; do
    sample_time="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    phase="$(<"$phase_file")"
    for index in 0 1 2; do
      pid="${node_pids[$index]}"
      memory="$(ps -o rss= -o vsz= -p "$pid" 2>/dev/null || true)"
      if [[ -n "$memory" ]]; then
        read -r rss_kb vsz_kb <<<"$memory"
        echo "$sample_time,slimfaas-$index,$pid,$rss_kb,$vsz_kb,$phase" >>"$memory_csv"
      fi
      cpu_percent="$(ps -o %cpu= -p "$pid" 2>/dev/null | xargs || true)"
      public_port=$((30021 + index))
      metrics="$(curl --silent --max-time 2 "http://127.0.0.1:$public_port/metrics" || true)"
      managed_heap="$(metric_value "$metrics" slimdata_process_managed_heap_bytes)"
      process_cpu="$(metric_value "$metrics" slimdata_process_cpu_seconds_total)"
      committed="$(metric_value "$metrics" slimdata_raft_committed_log_index)"
      applied="$(metric_value "$metrics" slimdata_raft_applied_log_index)"
      wal_bytes="$(metric_value "$metrics" slimdata_wal_bytes_since_snapshot)"
      queue_items="$(metric_value "$metrics" slimdata_state_queue_items)"
      batch_queue_requests="$(metric_value "$metrics" slimdata_command_batch_queue_requests)"
      batch_queue_bytes="$(metric_value "$metrics" slimdata_command_batch_queue_bytes)"
      batch_raft_total="$(metric_value "$metrics" slimdata_command_batch_raft_total)"
      batch_operations_total="$(metric_value "$metrics" slimdata_command_batch_operations_total)"
      snapshot_size="$(metric_value "$metrics" slimdata_snapshot_last_size_bytes)"
      local_batch_rps="$(metric_value "$metrics" slimdata_batch_local_requests_per_second)"
      fast_path_active="$(metric_value "$metrics" slimdata_batch_low_load_fast_path_active)"
      local_batch_delay="$(metric_value "$metrics" slimdata_batch_local_delay_milliseconds)"
      local_batch_coalesce="$(metric_value "$metrics" slimdata_batch_local_coalesce_milliseconds)"
      batch_partition_count="$(metric_value "$metrics" slimdata_batch_partition_count)"
      echo "$sample_time,slimfaas-$index,$pid,$phase,${cpu_percent:-0},${managed_heap:-0},${process_cpu:-0},${committed:-0},${applied:-0},${wal_bytes:-0},${queue_items:-0},${batch_queue_requests:-0},${batch_queue_bytes:-0},${batch_raft_total:-0},${batch_operations_total:-0},${snapshot_size:-0},${local_batch_rps:-0},${fast_path_active:-0},${local_batch_delay:-0},${local_batch_coalesce:-0},${batch_partition_count:-0}" >>"$diagnostics_csv"
    done
    sleep 2
  done
) &
sampler_pid="$!"

echo "Measured load: scenario=$scenario duration=${duration_seconds}s concurrency=$concurrency"
validate_mixed="false"
if [[ "$scenario" == "slimdata-mixed" ]]; then
  validate_mixed="true"
fi
set +e
dotnet "$lab_dll" load \
  --scenario "$scenario" \
  --duration "$duration_seconds" \
  --concurrency "$concurrency" \
  --target-rps "$target_requests_per_second" \
  "${target_replica_args[@]}" \
  --first-port 30021 \
  --nodes 3 \
  --payload-bytes 4096 \
  --keys-per-worker 16 \
  --key-prefix "measure-${timestamp}" \
  --validate "$validate_mixed" \
  --csv "$run_dir/operations.csv" \
  --json "$run_dir/operations.json" \
  | tee "$run_dir/load.log"
load_status="${PIPESTATUS[0]}"
set -e

collect_cluster_state "after"

echo "Cooldown: ${cooldown_seconds}s"
echo "cooldown" >"$phase_file"
sleep "$cooldown_seconds"
kill "$sampler_pid" 2>/dev/null || true
wait "$sampler_pid" 2>/dev/null || true
sampler_pid=""

dotnet "$lab_dll" report --csv "$memory_csv" | tee "$run_dir/report.csv"
echo "Memory lab artifacts: $run_dir"
exit "$load_status"
