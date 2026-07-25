#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
mode="${1:-trimmed}"
scenario="${2:-mixed}"
duration_seconds="${3:-120}"
concurrency="${4:-12}"
warmup_seconds="${WARMUP_SECONDS:-20}"
cooldown_seconds="${COOLDOWN_SECONDS:-20}"

if [[ "$mode" != "trimmed" && "$mode" != "aot" ]]; then
  echo "Usage: .bin/memory-lab.sh [trimmed|aot] [mixed|sync|async|set|files] [duration_seconds] [concurrency]" >&2
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
run_dir="$repo_root/artifacts/memory-lab/${mode}-${scenario}-${timestamp}"
publish_dir="$repo_root/artifacts/memory-lab/publish-${mode}-${runtime_id}"
lab_project="$repo_root/tools/SlimFaas.MemoryLab/SlimFaas.MemoryLab.csproj"
lab_dll="$repo_root/tools/SlimFaas.MemoryLab/bin/Release/net10.0/SlimFaas.MemoryLab.dll"
memory_csv="$run_dir/memory.csv"
phase_file="$run_dir/phase"
mkdir -p "$run_dir" "$publish_dir"

node_pids=()
function_pid=""
sampler_pid=""
cleanup() {
  if [[ -n "$sampler_pid" ]] && kill -0 "$sampler_pid" 2>/dev/null; then
    kill "$sampler_pid" 2>/dev/null || true
  fi
  for pid in "${node_pids[@]}"; do
    if kill -0 "$pid" 2>/dev/null; then
      kill "$pid" 2>/dev/null || true
    fi
  done
  if [[ -n "$function_pid" ]] && kill -0 "$function_pid" 2>/dev/null; then
    kill "$function_pid" 2>/dev/null || true
  fi
  for _ in $(seq 1 50); do
    running=0
    for pid in "${node_pids[@]}" "$function_pid" "$sampler_pid"; do
      if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
        running=1
      fi
    done
    if [[ "$running" == "0" ]]; then
      break
    fi
    sleep 0.1
  done
  for pid in "${node_pids[@]}" "$function_pid" "$sampler_pid"; do
    if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
      kill -KILL "$pid" 2>/dev/null || true
    fi
  done
  for pid in "${node_pids[@]}" "$function_pid" "$sampler_pid"; do
    if [[ -n "$pid" ]]; then
      wait "$pid" 2>/dev/null || true
    fi
  done
}
trap cleanup EXIT
trap 'exit 130' INT TERM

echo "Building memory lab"
dotnet build "$lab_project" -c Release --nologo

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

echo "Warm-up: ${warmup_seconds}s"
dotnet "$lab_dll" load \
  --scenario "$scenario" \
  --duration "$warmup_seconds" \
  --concurrency "$concurrency" \
  --first-port 30021 \
  --nodes 3 \
  >"$run_dir/warmup.log" 2>&1

echo "load" >"$phase_file"
echo "timestamp,node,pid,rss_kb,vsz_kb,phase" >"$memory_csv"
(
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
    done
    sleep 2
  done
) &
sampler_pid="$!"

echo "Measured load: scenario=$scenario duration=${duration_seconds}s concurrency=$concurrency"
dotnet "$lab_dll" load \
  --scenario "$scenario" \
  --duration "$duration_seconds" \
  --concurrency "$concurrency" \
  --first-port 30021 \
  --nodes 3 \
  | tee "$run_dir/load.log"

echo "Cooldown: ${cooldown_seconds}s"
echo "cooldown" >"$phase_file"
sleep "$cooldown_seconds"
kill "$sampler_pid" 2>/dev/null || true
wait "$sampler_pid" 2>/dev/null || true
sampler_pid=""

dotnet "$lab_dll" report --csv "$memory_csv" | tee "$run_dir/report.csv"
echo "Memory lab artifacts: $run_dir"
