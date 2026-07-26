#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
benchmark_phase="${BENCHMARK_PHASE:-all}"
official_duration="${DURATION_SECONDS:-120}"
official_warmup="${WARMUP_SECONDS:-30}"
official_repetitions="${REPETITIONS:-3}"
official_scope="${OFFICIAL_SCOPE:-all}"
official_low_targets="${OFFICIAL_LOW_TARGETS:-1 3 10}"
screening_duration="${SCREENING_DURATION_SECONDS:-60}"
screening_warmup="${SCREENING_WARMUP_SECONDS:-15}"
screening_repetitions="${SCREENING_REPETITIONS:-1}"
cooldown_seconds="${COOLDOWN_SECONDS:-5}"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
run_root="${BENCHMARK_RUN_ROOT:-$repo_root/artifacts/slimdata-benchmark/batch-modes-$timestamp}"
publish_dir="${BENCHMARK_PUBLISH_DIR:-$run_root/publish}"
lab_project="$repo_root/tools/SlimFaas.MemoryLab/SlimFaas.MemoryLab.csproj"
lab_dll="$repo_root/tools/SlimFaas.MemoryLab/bin/Release/net10.0/SlimFaas.MemoryLab.dll"
status_csv="$run_root/run-status.csv"
selection_file="$run_root/partition-selection.env"

case "$benchmark_phase" in
  screening|official|all) ;;
  *)
    echo "BENCHMARK_PHASE must be screening, official, or all" >&2
    exit 2
    ;;
esac
case "$official_scope" in
  low|high|all) ;;
  *)
    echo "OFFICIAL_SCOPE must be low, high, or all" >&2
    exit 2
    ;;
esac

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

mkdir -p "$run_root" "$publish_dir"
dotnet build "$lab_project" -c Release --nologo

if [[ "${BENCHMARK_SKIP_PUBLISH:-0}" == "1" ]]; then
  if [[ ! -x "$publish_dir/SlimFaas" ]]; then
    echo "Native AOT binary is missing: $publish_dir/SlimFaas" >&2
    exit 1
  fi
  echo "Reusing Native AOT binary: $publish_dir/SlimFaas"
else
  echo "Publishing the shared Native AOT binary"
  dotnet publish "$repo_root/src/SlimFaas/SlimFaas.csproj" \
    -c Release \
    -r "$runtime_id" \
    -o "$publish_dir" \
    -p:PublishAot=true \
    -p:PublishTrimmed=true \
    -p:TrimMode=full \
    -p:SelfContained=true \
    -p:SkipClientAppBuild=true \
    -p:DebugType=embedded \
    -p:StripSymbols=false \
    --nologo
fi

{
  echo "commit=$(git -C "$repo_root" rev-parse HEAD)"
  echo "worktree_dirty=$(git -C "$repo_root" status --porcelain | grep -q . && echo true || echo false)"
  echo "binary_sha256=$(shasum -a 256 "$publish_dir/SlimFaas" | awk '{print $1}')"
  echo "runtime_id=$runtime_id"
  echo "dotnet_sdk=$(dotnet --version)"
  echo "phase=$benchmark_phase"
  echo "official_duration_seconds=$official_duration"
  echo "official_warmup_seconds=$official_warmup"
  echo "official_repetitions=$official_repetitions"
  echo "screening_duration_seconds=$screening_duration"
  echo "screening_warmup_seconds=$screening_warmup"
  echo "screening_repetitions=$screening_repetitions"
  echo "started_at=$timestamp"
} >"$run_root/benchmark-manifest-$benchmark_phase.txt"
if [[ ! -f "$status_csv" ]]; then
  echo "phase,variant,scenario,target_rps,concurrency,repetition,status" >"$status_csv"
fi

run_one() {
  local phase="$1"
  local variant="$2"
  local scenario="$3"
  local target_rps="$4"
  local concurrency="$5"
  local repetition="$6"
  local duration="$7"
  local warmup="$8"
  local batch_mode="Global"
  local partitions="8"
  local fast_path="false"
  local aggregate_target_rps="0"
  if [[ "$target_rps" != "0" ]]; then
    aggregate_target_rps=$((target_rps * 3))
  fi

  if [[ "${BENCHMARK_RESUME:-1}" == "1" ]]; then
    for existing_manifest in \
      "$run_root/$phase/runs"/"$variant-r$repetition-$scenario-c$concurrency-"*/manifest.txt; do
      if [[ -f "$existing_manifest" &&
            -f "$(dirname "$existing_manifest")/operations.json" ]] &&
         grep -Fqx "target_requests_per_replica=$target_rps" "$existing_manifest"; then
        echo "Skipping completed run $phase $variant scenario=$scenario target=$target_rps concurrency=$concurrency repetition=$repetition"
        return 0
      fi
    done
  fi

  case "$variant" in
    global-current)
      ;;
    global-low-latency)
      fast_path="true"
      ;;
    partitioned-low-latency-p*)
      batch_mode="PartitionedByKey"
      partitions="${variant##*-p}"
      fast_path="true"
      ;;
    *)
      echo "Unknown benchmark variant: $variant" >&2
      return 2
      ;;
  esac

  echo "Running $phase $variant scenario=$scenario target=$target_rps concurrency=$concurrency repetition=$repetition"
  set +e
  WARMUP_SECONDS="$warmup" \
  COOLDOWN_SECONDS="$cooldown_seconds" \
  TARGET_REQUESTS_PER_SECOND="$aggregate_target_rps" \
  TARGET_REQUESTS_PER_REPLICA="$target_rps" \
  SLIMDATA_BATCH_MODE="$batch_mode" \
  SLIMDATA_BATCH_PARTITIONS="$partitions" \
  SLIMDATA_LOW_LOAD_FAST_PATH="$fast_path" \
  SLIMDATA_LOW_LOAD_RPS="10" \
  MEMORY_LAB_SKIP_PUBLISH=1 \
  MEMORY_LAB_SKIP_LAB_BUILD=1 \
  MEMORY_LAB_PUBLISH_DIR="$publish_dir" \
  MEMORY_LAB_RUN_ROOT="$run_root/$phase/runs" \
  MEMORY_LAB_RUN_LABEL="$variant-r$repetition" \
    "$repo_root/.bin/memory-lab.sh" aot "$scenario" "$duration" "$concurrency"
  local status="$?"
  set -e
  echo "$phase,$variant,$scenario,$target_rps,$concurrency,$repetition,$status" >>"$status_csv"
}

compare_runs() {
  local phase="$1"
  local baseline="$2"
  set +e
  dotnet "$lab_dll" compare \
    --root "$run_root/$phase/runs" \
    --output "$run_root/$phase/comparison.md" \
    --baseline "$baseline"
  local status="$?"
  set -e
  return "$status"
}

if [[ "$benchmark_phase" == "screening" || "$benchmark_phase" == "all" ]]; then
  screening_variants=(
    global-low-latency
    partitioned-low-latency-p2
    partitioned-low-latency-p4
    partitioned-low-latency-p8
    partitioned-low-latency-p16
  )
  for scenario in slimdata-set slimdata-mixed; do
    for concurrency in 48 96 192; do
      for repetition in $(seq 1 "$screening_repetitions"); do
        for variant in "${screening_variants[@]}"; do
          run_one \
            screening \
            "$variant" \
            "$scenario" \
            0 \
            "$concurrency" \
            "$repetition" \
            "$screening_duration" \
            "$screening_warmup"
        done
      done
    done
  done

  compare_runs screening global-low-latency || true
  dotnet "$lab_dll" select-partition \
    --root "$run_root/screening/runs" \
    --output "$selection_file" \
    --baseline global-low-latency
fi

selected_partitions_override="${SELECTED_PARTITIONS:-}"
selected_partitions="$selected_partitions_override"
if [[ -f "$selection_file" ]]; then
  # The file is generated locally by SlimFaas.MemoryLab and contains only scalar values.
  source "$selection_file"
  if [[ -n "$selected_partitions_override" ]]; then
    selected_partitions="$selected_partitions_override"
  fi
fi
if [[ -z "$selected_partitions" ]]; then
  selected_partitions="8"
fi
if [[ "$selected_partitions" != "2" &&
      "$selected_partitions" != "4" &&
      "$selected_partitions" != "8" &&
      "$selected_partitions" != "16" ]]; then
  echo "SELECTED_PARTITIONS must be 2, 4, 8, or 16" >&2
  exit 2
fi

official_status=0
if [[ "$benchmark_phase" == "official" || "$benchmark_phase" == "all" ]]; then
  selected_partition_variant="partitioned-low-latency-p$selected_partitions"
  official_variants=(
    global-current
    global-low-latency
    "$selected_partition_variant"
  )

  if [[ "$official_scope" != "high" ]]; then
  for target_rps in $official_low_targets; do
    low_concurrency="1"
    if (( target_rps >= 10 )); then
      low_concurrency="12"
    fi
    for repetition in $(seq 1 "$official_repetitions"); do
      for offset in 0 1 2; do
        variant_index=$(((offset + repetition - 1) % 3))
        run_one \
          official \
          "${official_variants[$variant_index]}" \
          slimdata-set \
          "$target_rps" \
          "$low_concurrency" \
          "$repetition" \
          "$official_duration" \
          "$official_warmup"
      done
    done
  done
  fi

  if [[ "$official_scope" != "low" ]]; then
  for scenario in slimdata-set slimdata-mixed; do
    for concurrency in 12 48 192; do
      for repetition in $(seq 1 "$official_repetitions"); do
        for offset in 0 1 2; do
          variant_index=$(((offset + repetition - 1) % 3))
          run_one \
            official \
            "${official_variants[$variant_index]}" \
            "$scenario" \
            0 \
            "$concurrency" \
            "$repetition" \
            "$official_duration" \
            "$official_warmup"
        done
      done
    done
  done
  fi

  compare_runs official global-current || official_status="$?"
fi

echo "SlimData batch-mode benchmark artifacts: $run_root"
exit "$official_status"
