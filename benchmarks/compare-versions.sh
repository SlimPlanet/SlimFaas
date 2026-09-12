#!/usr/bin/env bash
# Objective before/after benchmark of two SlimFaas commits, measured in the same session
# on the same machine with the same benchmark code and the same load driver.
#
#   benchmarks/compare-versions.sh --baseline <git-ref> [--candidate <git-ref>|HEAD]
#       [--profile quick|standard|async-queue] [--skip-micro] [--skip-e2e]
#       [--micro-baseline-api auto|true|false] [--output <dir>]
#
# Two tiers, both documented in docs/performance-benchmarks-cross-version.md:
#
#   micro  BenchmarkDotNet suite of THIS checkout (benchmarks/SlimFaas.Benchmarks), compiled
#          twice: once against the baseline worktree, once against the candidate. The very
#          same benchmark code measures both commits; results are merged by
#          benchmarks/compare-microbenchmarks.py.
#   e2e    The native-local three-node cluster of each commit is started with THIS
#          checkout's manifest (benchmarks/slimfaas.local.benchmark.yaml), benchmark target
#          and load driver (src/SlimFaasBenchmark); only the SlimFaas binary changes. The
#          two results.json are compared by `SlimFaasBenchmark compare`.
#
# The candidate defaults to the working tree of this checkout (so an uncommitted change can
# be measured); any other ref is checked out in a git worktree under <output>/worktrees.
# Baseline worktrees older than PR #313 get an InternalsVisibleTo entry for the benchmark
# assembly (no behavior change) and are compiled with -p:BaselineApi=true.
#
# Requirements: the repository .NET SDK, bash, curl, python3, git worktree support, and the
# ports of the native-local benchmark (31020-31023, 3162-3164, 31080, 32000-32015) free.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
baseline_ref=""
candidate_ref="HEAD"
profile="standard"
run_micro=true
run_e2e=true
micro_baseline_api="auto"
output=""
micro_args="${MICRO_ARGS:---iterationCount 10 --warmupCount 3}"

usage() { sed -n '2,25p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --baseline) baseline_ref="$2"; shift 2 ;;
    --candidate) candidate_ref="$2"; shift 2 ;;
    --profile) profile="$2"; shift 2 ;;
    --skip-micro) run_micro=false; shift ;;
    --skip-e2e) run_e2e=false; shift ;;
    --micro-baseline-api) micro_baseline_api="$2"; shift 2 ;;
    --output) output="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done
[[ -n "$baseline_ref" ]] || { echo "--baseline <git-ref> is required" >&2; exit 2; }

timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
output="${output:-$repo_root/artifacts/perf-compare/$timestamp}"
mkdir -p "$output"
log() { printf '[%s] %s\n' "$(date -u +%H:%M:%S)" "$*"; }

case "$profile" in
  quick) duration=3; warmup=1; repetitions=1; run_profile=standard ;;
  standard) duration=10; warmup=2; repetitions=3; run_profile=standard ;;
  async-queue) duration=10; warmup=2; repetitions=3; run_profile=async-queue ;;
  *) echo "--profile must be quick, standard or async-queue" >&2; exit 2 ;;
esac
duration="${BENCHMARK_DURATION_SECONDS:-$duration}"
warmup="${BENCHMARK_WARMUP_SECONDS:-$warmup}"
repetitions="${BENCHMARK_REPETITIONS:-$repetitions}"
payload_bytes="${BENCHMARK_PAYLOAD_BYTES:-64,4096,262144,2097152}"
concurrency="${BENCHMARK_CONCURRENCY:-1,16}"
scale_messages="${BENCHMARK_SCALE_MESSAGES:-200}"
scale_concurrency="${BENCHMARK_SCALE_CONCURRENCY:-32}"
scale_timeout="${BENCHMARK_SCALE_TIMEOUT_SECONDS:-120}"
async_drain_timeout="${BENCHMARK_ASYNC_DRAIN_TIMEOUT_SECONDS:-180}"
async_paced_messages="${BENCHMARK_ASYNC_PACED_MESSAGES:-100}"
async_paced_interval_ms="${BENCHMARK_ASYNC_PACED_INTERVAL_MS:-100}"
async_burst_messages="${BENCHMARK_ASYNC_BURST_MESSAGES:-1000}"
async_burst_concurrency="${BENCHMARK_ASYNC_BURST_CONCURRENCY:-64}"

# ---------------------------------------------------------------- checkouts
# prepare_tree <label> <ref> -> prints the directory holding the sources of <ref>
prepare_tree() {
  local label="$1" ref="$2" dir
  if [[ "$ref" == "HEAD" || "$ref" == "worktree" ]]; then
    echo "$repo_root"
    return
  fi
  dir="$output/worktrees/$label"
  if [[ ! -d "$dir" ]]; then
    git -C "$repo_root" worktree add --detach "$dir" "$(git -C "$repo_root" rev-parse --verify "$ref^{commit}")" >/dev/null
  fi
  echo "$dir"
}

describe_tree() {
  local dir="$1"
  printf 'commit=%s date=%s dirty=%s' \
    "$(git -C "$dir" rev-parse HEAD)" \
    "$(git -C "$dir" show -s --format=%cs HEAD)" \
    "$(git -C "$dir" status --porcelain -- src 2>/dev/null | grep -q . && echo true || echo false)"
}

log "Preparing checkouts"
baseline_dir="$(prepare_tree baseline "$baseline_ref")"
candidate_dir="$(prepare_tree candidate "$candidate_ref")"
baseline_desc="$(describe_tree "$baseline_dir")"
candidate_desc="$(describe_tree "$candidate_dir")"
log "baseline  $baseline_ref  ($baseline_desc)"
log "candidate $candidate_ref  ($candidate_desc)"

# ------------------------------------------------------------------- builds
build_slimfaas() {
  local dir="$1"
  log "Building SlimFaas (Release, no dashboard) in $dir"
  dotnet build "$dir/src/SlimFaas/SlimFaas.csproj" -c Release -p:SkipClientAppBuild=true --nologo -v q >"$output/build-$(basename "$dir").log" 2>&1 ||
    { cat "$output/build-$(basename "$dir").log" >&2; exit 1; }
}

if [[ "$run_e2e" == true ]]; then
  build_slimfaas "$baseline_dir"
  [[ "$candidate_dir" == "$baseline_dir" ]] || build_slimfaas "$candidate_dir"
  log "Building the benchmark target/driver of this checkout"
  dotnet build "$repo_root/src/SlimFaasBenchmark/SlimFaasBenchmark.csproj" -c Release --nologo -v q >"$output/build-driver.log" 2>&1 ||
    { cat "$output/build-driver.log" >&2; exit 1; }
fi

# -------------------------------------------------------------------- micro
needs_baseline_api() {
  local dir="$1"
  case "$micro_baseline_api" in
    true) return 0 ;;
    false) return 1 ;;
    *) ! grep -rqs "class SlimDataStateSnapshot" "$dir/src/SlimData" ;;
  esac
}

ensure_internals_visible() {
  local dir="$1" csproj
  for csproj in "$dir/src/SlimFaas/SlimFaas.csproj" "$dir/src/SlimData/SlimData.csproj"; do
    grep -q 'InternalsVisibleTo Include="SlimFaas.Benchmarks"' "$csproj" && continue
    [[ "$dir" != "$repo_root" ]] || { echo "this checkout lacks InternalsVisibleTo SlimFaas.Benchmarks" >&2; exit 1; }
    sed -i 's#</Project>#  <ItemGroup>\n    <InternalsVisibleTo Include="SlimFaas.Benchmarks" />\n  </ItemGroup>\n</Project>#' "$csproj"
    log "Added InternalsVisibleTo(SlimFaas.Benchmarks) to $csproj (baseline worktree only)"
  done
}

run_micro() {
  local label="$1" dir="$2" api=false
  local project="$output/micro/$label/project"
  ensure_internals_visible "$dir"
  if needs_baseline_api "$dir"; then api=true; fi
  rm -rf "$project"; mkdir -p "$project"
  cp -r "$repo_root/benchmarks/SlimFaas.Benchmarks/"*.cs "$repo_root/benchmarks/SlimFaas.Benchmarks/"*.csproj "$repo_root/benchmarks/SlimFaas.Benchmarks/Support" "$project/"
  log "Building the micro-benchmarks against $label (BaselineApi=$api)"
  dotnet build "$project/SlimFaas.Benchmarks.csproj" -c Release --nologo -v q \
    -p:SlimFaasSourceRoot="$dir/" -p:BaselineApi=$api >"$output/micro/$label/build.log" 2>&1 ||
    { cat "$output/micro/$label/build.log" >&2; exit 1; }
  log "Running the micro-benchmarks against $label ($micro_args)"
  # Tiered compilation off: every benchmark then measures fully optimized code whatever
  # its duration (see the theme 6 note in docs/performance-benchmarks.md).
  # shellcheck disable=SC2086
  DOTNET_TieredCompilation=0 dotnet "$project/bin/Release/net10.0/SlimFaas.Benchmarks.dll" \
    --filter '*' $micro_args --artifacts "$output/micro/$label" >"$output/micro/$label/run.log" 2>&1 ||
    { tail -50 "$output/micro/$label/run.log" >&2; exit 1; }
  grep -h '^// KubernetesJobsSyncBenchmarks' "$output/micro/$label/run.log" | sort -u >"$output/micro/$label/api-requests.txt" || true
}

if [[ "$run_micro" == true ]]; then
  run_micro baseline "$baseline_dir"
  run_micro candidate "$candidate_dir"
  python3 "$repo_root/benchmarks/compare-microbenchmarks.py" \
    --baseline "$output/micro/baseline" --candidate "$output/micro/candidate" \
    --baseline-label "$baseline_ref" --candidate-label "$candidate_ref" \
    --output "$output/micro/comparison.md" >/dev/null
  log "Micro-benchmark comparison: $output/micro/comparison.md"
fi

# ---------------------------------------------------------------------- e2e
manifest="$repo_root/benchmarks/slimfaas.local.benchmark.yaml"
driver_dll="$repo_root/src/SlimFaasBenchmark/bin/Release/net10.0/SlimFaasBenchmark.dll"
local_pid=""
sampler_pid=""

# Every 5 s, record the open file descriptors and resident memory of every SlimFaas
# process of the cluster (nodes, local supervisor, benchmark target) into
# <run_root>/resources.csv. Linux /proc only; a no-op elsewhere. The v0.79.2 baseline
# exhausted the 20 000 descriptors of the measurement container under the async matrix
# (see docs/performance-benchmarks-cross-version.md), so resource growth is part of
# the comparison.
sample_resources() {
  local csv="$1/resources.csv" pid kind fds rss cmd
  [[ -d /proc/self/fd ]] || return 0
  echo "epoch_seconds,pid,kind,open_fds,rss_kb" >"$csv"
  while :; do
    for pid in $(pgrep -f 'SlimFaas\.dll|SlimFaasBenchmark\.dll target' 2>/dev/null); do
      cmd="$(tr '\0' ' ' <"/proc/$pid/cmdline" 2>/dev/null || true)"
      case "$cmd" in
        *"local up"*) kind=supervisor ;;
        *"SlimFaasBenchmark.dll target"*) kind=target ;;
        *SlimFaas.dll*) kind=node ;;
        *) continue ;;
      esac
      fds="$(ls "/proc/$pid/fd" 2>/dev/null | wc -l)"
      rss="$(awk '/^VmRSS:/ {print $2}' "/proc/$pid/status" 2>/dev/null || echo 0)"
      echo "$(date +%s),$pid,$kind,$fds,${rss:-0}" >>"$csv"
    done
    sleep 5
  done
}

summarize_resources() {
  local run_root="$1"
  [[ -s "$run_root/resources.csv" ]] || return 0
  python3 - "$run_root/resources.csv" >"$run_root/resources-summary.md" <<'PY' || true
import csv, sys
from collections import defaultdict
rows = list(csv.DictReader(open(sys.argv[1])))
if not rows:
    sys.exit(0)
first, last = defaultdict(dict), defaultdict(dict)
peak_fds, peak_rss = defaultdict(int), defaultdict(int)
for r in rows:
    key = (r["kind"], r["pid"])
    first.setdefault(key, r)
    last[key] = r
    peak_fds[key] = max(peak_fds[key], int(r["open_fds"]))
    peak_rss[key] = max(peak_rss[key], int(r["rss_kb"]))
print("| process | pid | open fds first / peak / last | RSS peak (MB) |")
print("|---|---:|---:|---:|")
for key in sorted(peak_fds, key=lambda k: (k[0], int(k[1]))):
    f, l = first[key], last[key]
    print(f"| {key[0]} | {key[1]} | {f['open_fds']} / {peak_fds[key]} / {l['open_fds']} | {peak_rss[key] / 1024:,.0f} |")
nodes = [k for k in peak_fds if k[0] == "node"]
if nodes:
    print()
    print(f"Nodes: peak open fds = {max(peak_fds[k] for k in nodes)}, peak RSS = {max(peak_rss[k] for k in nodes) / 1024:,.0f} MB, samples every 5 s over {int(rows[-1]['epoch_seconds']) - int(rows[0]['epoch_seconds'])} s.")
PY
}

stop_sampler() {
  if [[ -n "$sampler_pid" ]] && kill -0 "$sampler_pid" 2>/dev/null; then
    kill "$sampler_pid" 2>/dev/null || true
    wait "$sampler_pid" 2>/dev/null || true
  fi
  sampler_pid=""
}

stop_cluster() {
  stop_sampler
  if [[ -n "$local_pid" ]] && kill -0 "$local_pid" 2>/dev/null; then
    kill -TERM "$local_pid" 2>/dev/null || true
    for _ in $(seq 1 100); do kill -0 "$local_pid" 2>/dev/null || break; sleep 0.1; done
    kill -KILL "$local_pid" 2>/dev/null || true
    wait "$local_pid" 2>/dev/null || true
  fi
  local_pid=""
}
trap stop_cluster EXIT
trap 'exit 130' INT TERM

run_e2e() {
  local label="$1" dir="$2"
  local run_root="$output/e2e/$label"
  local dll="$dir/src/SlimFaas/bin/Release/net10.0/SlimFaas.dll"
  mkdir -p "$run_root"
  if [[ -s "$run_root/results.json" ]]; then
    log "Reusing the existing $label results in $run_root (delete results.json to re-run)"
    return
  fi
  dotnet "$dll" local validate -f "$manifest" >"$run_root/validate.log" 2>&1 ||
    { cat "$run_root/validate.log" >&2; exit 1; }
  log "Starting the $label three-node cluster ($dll)"
  ( cd "$repo_root" && exec dotnet "$dll" local up -f "$manifest" --clean ) >"$run_root/slimfaas-local.log" 2>&1 &
  local_pid=$!
  sample_resources "$run_root" &
  sampler_pid=$!
  local ready=0 attempt
  for attempt in $(seq 1 180); do
    kill -0 "$local_pid" 2>/dev/null || { echo "slimfaas local ($label) exited before readiness; see $run_root/slimfaas-local.log" >&2; exit 1; }
    if curl --silent --fail http://127.0.0.1:31020/ready >/dev/null && curl --silent --fail http://127.0.0.1:31080/health >/dev/null; then ready=1; break; fi
    sleep 1
  done
  [[ "$ready" == 1 ]] || { echo "$label cluster did not become ready; see $run_root/slimfaas-local.log" >&2; exit 1; }
  log "Running the $run_profile matrix against $label"
  local driver_status=0
  dotnet "$driver_dll" run --profile "$run_profile" \
    --slimfaas-url http://127.0.0.1:31020 --direct-url http://127.0.0.1:31080 \
    --node-urls http://127.0.0.1:31021,http://127.0.0.1:31022,http://127.0.0.1:31023 \
    --duration "$duration" --warmup "$warmup" --repetitions "$repetitions" \
    --payload-bytes "$payload_bytes" --concurrency "$concurrency" \
    --async-drain-timeout "$async_drain_timeout" \
    --scale-messages "$scale_messages" --scale-concurrency "$scale_concurrency" --scale-timeout "$scale_timeout" \
    --async-paced-messages "$async_paced_messages" --async-paced-interval-ms "$async_paced_interval_ms" \
    --async-burst-messages "$async_burst_messages" --async-burst-concurrency "$async_burst_concurrency" \
    --output "$run_root" >"$run_root/driver.log" 2>&1 || driver_status=$?
  if [[ ! -s "$run_root/results.json" ]]; then
    tail -30 "$run_root/driver.log" >&2
    stop_cluster
    summarize_resources "$run_root"
    exit 1
  fi
  # The driver exits non-zero when a case recorded failed or timed-out requests; the
  # results are still complete and the comparison reports those errors, so the run
  # goes on (the verdict is in the comparison, not in this exit code).
  [[ "$driver_status" == 0 ]] || log "The $label driver reported errors (exit $driver_status): see $run_root/summary.md"
  stop_cluster
  summarize_resources "$run_root"
  sleep 2
}

if [[ "$run_e2e" == true ]]; then
  run_e2e baseline "$baseline_dir"
  run_e2e candidate "$candidate_dir"
  compare_profile=sync
  [[ "$run_profile" == "async-queue" ]] && compare_profile=async
  dotnet "$driver_dll" compare --profile "$compare_profile" \
    --baseline "$output/e2e/baseline/results.json" --candidate "$output/e2e/candidate/results.json" \
    --output "$output/e2e/comparison" >"$output/e2e/compare.log" 2>&1 || log "compare verdict: FAILED acceptance rules (see $output/e2e/compare.log)"
  log "End-to-end comparison: $output/e2e/comparison/comparison.md"
fi

# ----------------------------------------------------------------- manifest
{
  echo "started_at=$timestamp"
  echo "baseline_ref=$baseline_ref"
  echo "baseline=$baseline_desc"
  echo "candidate_ref=$candidate_ref"
  echo "candidate=$candidate_desc"
  echo "driver_checkout=$(git -C "$repo_root" rev-parse HEAD)"
  echo "profile=$profile"
  echo "micro=$run_micro micro_args=$micro_args"
  echo "e2e=$run_e2e duration=$duration warmup=$warmup repetitions=$repetitions payload_bytes=$payload_bytes concurrency=$concurrency"
  echo "dotnet_sdk=$(dotnet --version)"
  echo "host=$(uname -s)-$(uname -m) cpus=$(nproc 2>/dev/null || echo '?')"
  grep -m1 'model name' /proc/cpuinfo 2>/dev/null | sed 's/.*: /cpu=/' || true
} >"$output/manifest.txt"
log "Done: $output"
