#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
duration_seconds="${DURATION_SECONDS:-120}"
warmup_seconds="${WARMUP_SECONDS:-30}"
repetitions="${REPETITIONS:-3}"
cooldown_seconds="${COOLDOWN_SECONDS:-5}"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
run_root="${BENCHMARK_RUN_ROOT:-$repo_root/artifacts/slimdata-benchmark/ab-$timestamp}"
before_publish="${BEFORE_PUBLISH_DIR:-$repo_root/artifacts/slimdata-benchmark/before/publish}"
after_publish="${AFTER_PUBLISH_DIR:-$repo_root/artifacts/slimdata-benchmark/after/publish}"
lab_project="$repo_root/tools/SlimFaas.MemoryLab/SlimFaas.MemoryLab.csproj"
lab_dll="$repo_root/tools/SlimFaas.MemoryLab/bin/Release/net10.0/SlimFaas.MemoryLab.dll"
status_csv="$run_root/run-status.csv"

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

if [[ ! -x "$before_publish/SlimFaas" ]]; then
  echo "Reference Native AOT binary is missing: $before_publish/SlimFaas" >&2
  echo "Publish commit 4d836f83c4e3c7df865cb2869c10a85108a6ed43 before running the matrix." >&2
  exit 1
fi

mkdir -p "$run_root/runs" "$after_publish"
dotnet build "$lab_project" -c Release --nologo

if [[ "${BENCHMARK_SKIP_AFTER_PUBLISH:-0}" == "1" ]]; then
  if [[ ! -x "$after_publish/SlimFaas" ]]; then
    echo "After Native AOT binary is missing: $after_publish/SlimFaas" >&2
    exit 1
  fi
  echo "Reusing after Native AOT binary"
else
  echo "Publishing after Native AOT binary with the reference options"
  dotnet publish "$repo_root/src/SlimFaas/SlimFaas.csproj" \
    -c Release \
    -r "$runtime_id" \
    -o "$after_publish" \
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
  echo "reference_commit=4d836f83c4e3c7df865cb2869c10a85108a6ed43"
  echo "after_commit=$(git -C "$repo_root" rev-parse HEAD)"
  echo "before_binary_sha256=$(shasum -a 256 "$before_publish/SlimFaas" | awk '{print $1}')"
  echo "after_binary_sha256=$(shasum -a 256 "$after_publish/SlimFaas" | awk '{print $1}')"
  echo "after_worktree_dirty=$(git -C "$repo_root" status --porcelain | grep -q . && echo true || echo false)"
  echo "runtime_id=$runtime_id"
  echo "dotnet_sdk=$(dotnet --version)"
  echo "duration_seconds=$duration_seconds"
  echo "warmup_seconds=$warmup_seconds"
  echo "repetitions=$repetitions"
  echo "started_at=$timestamp"
} >"$run_root/benchmark-manifest.txt"
echo "variant,scenario,concurrency,repetition,status" >"$status_csv"

run_one() {
  local variant="$1"
  local scenario="$2"
  local concurrency="$3"
  local repetition="$4"
  local publish_dir="$before_publish"
  if [[ "$variant" == "after" ]]; then
    publish_dir="$after_publish"
  fi

  echo "Running $variant scenario=$scenario concurrency=$concurrency repetition=$repetition"
  set +e
  WARMUP_SECONDS="$warmup_seconds" \
  COOLDOWN_SECONDS="$cooldown_seconds" \
  MEMORY_LAB_SKIP_PUBLISH=1 \
  MEMORY_LAB_SKIP_LAB_BUILD=1 \
  MEMORY_LAB_PUBLISH_DIR="$publish_dir" \
  MEMORY_LAB_RUN_ROOT="$run_root/runs" \
  MEMORY_LAB_RUN_LABEL="$variant-r$repetition" \
    "$repo_root/.bin/memory-lab.sh" aot "$scenario" "$duration_seconds" "$concurrency"
  local status="$?"
  set -e
  echo "$variant,$scenario,$concurrency,$repetition,$status" >>"$status_csv"
}

for scenario in slimdata-set slimdata-mixed; do
  for concurrency in 12 48; do
    for repetition in $(seq 1 "$repetitions"); do
      if (( repetition % 2 == 1 )); then
        run_one before "$scenario" "$concurrency" "$repetition"
        run_one after "$scenario" "$concurrency" "$repetition"
      else
        run_one after "$scenario" "$concurrency" "$repetition"
        run_one before "$scenario" "$concurrency" "$repetition"
      fi
    done
  done
done

set +e
dotnet "$lab_dll" compare \
  --root "$run_root/runs" \
  --output "$run_root/comparison.md"
comparison_status="$?"
set -e

echo "SlimData benchmark artifacts: $run_root"
exit "$comparison_status"
