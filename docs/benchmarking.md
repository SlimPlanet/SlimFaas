# Latency and Autoscaling Benchmark

SlimFaas includes a reproducible native-local benchmark for the warm synchronous
proxy path, asynchronous enqueue and delivery, and scale-to-zero followed by
PromQL scale-out.

## Run the benchmark

The quick profile is intended for a smoke test and takes a few minutes:

```bash
BENCHMARK_PROFILE=quick .bin/slimfaas-local-benchmark.sh
```

On Windows, run the script in Git Bash with Python 3.10+. The launcher places the
runtime and its descendants in an owned Windows Job Object before execution.
Normal completion and an abrupt launcher exit both close the entire process
tree. Occupied benchmark ports are rejected before starting or cleaning state.
Run `python .bin/test-local-benchmark-host.py` to verify those lifecycle checks.

## Run the three-node recovery scenario

The restart-under-load scenario starts the same three-node SlimFaas Local
cluster, writes continuously to SlimData, generates application HTTP traffic,
kills a non-leader node, and fails unless the leader remains stable while the
restarted node reports both `/health` and `/ready` successfully and committed
Raft entries continue to advance:

```bash
RECOVERY_TIMEOUT_SECONDS=180 .bin/slimfaas-local-recovery-benchmark.sh
```

Use `RECOVERY_SKIP_BUILD=true` for repeated runs with existing Release binaries
and `RECOVERY_RUN_ROOT` to select the artifact directory. The scenario requires
`lsof` and writes `recovery-summary.txt`, traffic logs, and the local
supervisor log below the selected artifact directory.

The standard profile runs every case three times with longer measurement and
warm-up windows:

```bash
.bin/slimfaas-local-benchmark.sh
```

The `async-queue` profile uses the same payload/concurrency matrix and adds a
low-rate paced scenario plus a burst of 1,000 messages at concurrency 64:

```bash
BENCHMARK_PROFILE=async-queue \
BENCHMARK_RUN_ROOT=artifacts/slimfaas-local-benchmark/async-baseline \
.bin/slimfaas-local-benchmark.sh
```
The script builds SlimFaas without the dashboard, builds the benchmark driver,
validates `benchmarks/slimfaas.local.benchmark.yaml`, starts a clean three-node
cluster with `slimfaas local`, runs the matrix, and shuts down every managed
process. Docker and Kubernetes are not required.

It requires the repository's .NET 10 SDK plus Bash and `curl`. Ports 31020-31023,
3162-3164, 31080, and the 32000-32015 process pool must be free.

Results are written below
`artifacts/slimfaas-local-benchmark/<profile>-<UTC timestamp>/`:

- `summary.md`: tables for sync overhead, async latency, and scaling milestones;
- `results.json`: structured settings and results;
- `latency-samples.csv`: one row per measured HTTP request;
- `scaling-timeline.csv`: observed replicas and queue depths over time;
- `benchmark-manifest.txt`: commit, dirty-worktree flag, SDK, host, and matrix;
- `slimfaas-local.log`: control-plane and managed-process output.

## Compare an optimization with its baseline

For release safety checks, add `--criteria non-regression` to the comparison
command below. The default remains `optimization`. Non-regression evaluates the
complete sync, async, paced/burst and scaling evidence without requiring the
20–50% optimization targets. It retains the existing 10% throughput/tail guardrails,
20% CPU/message, 10% RSS and 2x Raft-entries/message limits. A missing measurement
or failed reference produces **INCONCLUSIVE** (exit 2), never a passing result.

### macOS, Linux ARM64 and Kubernetes campaigns

Use `.bin/run-platform-workload.py` for one attributable run and
`.bin/run-platform-matrix.py` for serial reference–candidate–candidate–reference
blocks. Publish both revisions before starting measurements; use the same
prebuilt benchmark and MemoryLab drivers for both. The full HTTP defaults are
`async-queue`, 64/4096/65536/262144/1048576/2000000/2097152/4194304 bytes,
concurrency 1/4/16, 2-second warmup, 10-second measurements and three repetitions.
MemoryLab defaults are Set and mixed at concurrency 12/48, 30-second warmup and
120-second load, with all-node reads and independent async SHA-256 receipts.
Its safety limits remain 90% throughput, 120% p99 and 115% RSS, with zero errors.

For example, save an environment configuration outside the source tree:

```json
{
  "environment": "macos",
  "before": "/absolute/path/baseline/SlimFaas",
  "after": "/absolute/path/candidate/SlimFaas",
  "benchmark": "/absolute/path/SlimFaasBenchmark.dll",
  "memorylab": "/absolute/path/SlimFaas.MemoryLab.dll",
  "compare_command": ["dotnet", "/absolute/path/SlimFaasBenchmark.dll"]
}
```

```bash
python3 .bin/run-platform-matrix.py --config artifacts/platform-macos.json \
  --output artifacts/platform-macos
```

The matrix writes every run and all four cross-comparisons. Contradictory
guardrails against the two surrounding references trigger one complete ABBA
repeat for that block; both attempts remain available. `--resume` continues an
interrupted matrix without overwriting completed or failed evidence. No builds,
other measurements or fault campaigns may run concurrently with measurements.
Low disk space stops a new phase; preserve state, WALs, snapshots and journals
when reclaiming verified inactive build caches or images.

The test-only `benchmarks/platform/Dockerfile` inherits the exact production
runtime image and adds a common load driver, SDK fixtures and Linux fault tools.
Build with `--build-arg RUNTIME_IMAGE=<source-built-runtime>`. Use `podman` in the
matrix configuration, image names for `before`/`after`, and a unique `prefix`.
State is stored on the VM's Linux filesystem, not a macOS bind mount. Podman
measures a managed, trimmed ARM64 runtime with the current production Dockerfile;
macOS Native AOT results are a separate comparison.

For Kubernetes, use a dedicated four-node kind cluster (one control plane and
three workers), an explicit kubeconfig and context `kind-slimfaas-pr506`. Verify
[kind's rootless prerequisites](https://kind.sigs.k8s.io/docs/user/rootless/),
including cgroup v2 delegation. The kind nodes need a VM-native `/evidence` mount
from `/var/home/core/slimfaas-pr506`. Preload the runtime and common controller
images. Set `environment` to `kubernetes`, `prefix` to `slimfaas-pr506-<campaign>`,
and provide `kubeconfig`, `controller_image`, `before_commit` and `after_commit`.
The launcher creates dedicated labelled namespaces, retained persistent volumes,
three anti-affined StatefulSet nodes, actual function Deployments and Jobs.
Generators and functions run inside the cluster through Services and pod DNS.
Port-forwarding is unnecessary for measurements.

Linux fault controls also inspect cgroup socket memory and OOM status. RSS
guardrails retain their existing definition, while an unplanned runtime/control
OOM fails the controlled recovery campaign even if the process restarts. See the
[Linux socket-memory controls](slimdata-reliability.md#linux-socket-memory-controls).

Runtime comparisons include the durable Raft append and local read-application
barriers described in [the architecture](how-it-works.md). Keep them enabled:
an acknowledgment from a volatile replica cannot be counted as equivalent to
a persisted quorum copy when comparing performance.

The platform runner preserves every latency sample in `latency-samples.csv.gz`.
The common driver compresses completed case samples between timed cases, using
`--samples-format csv.gz`; ordinary manual runs still default to CSV. Comparison
requires matching sample formats. Compression reduces evidence storage without
sampling or dropping request observations.
VM exports retain complete WALs and samples in `raw.tar.gz`, with its SHA-256 in
`raw-archive.json`; comparers extract only the smaller reports and logs. The
original VM state remains in place. Extract the archive when inspecting the
large raw members rather than keeping an additional uncompressed copy.

Long rootless campaigns also consume the Linux user's session-key quota. Inspect
`/proc/key-users` and `sysctl kernel.keys.maxkeys kernel.keys.maxbytes` in the
dedicated VM. If OCI startup reports `unable to create session key: disk quota
exceeded`, raise that VM's quota (for example, `sudo sysctl -w
kernel.keys.maxkeys=20000 kernel.keys.maxbytes=2000000`) and retain the original
failed attempt. This is unrelated to filesystem capacity.

The `normal` probe profile runs readiness and liveness checks with a 300-second
startup allowance for retained-state recovery. The `endurance`
profile disables liveness replacement while explicitly suspending only the
campaign runtime process; pod UID, PID and process start time fence every signal.
Per-pod/per-restart receipts and logs are retained before scaling down. See the
[reliability guide](slimdata-reliability.md) for fault and ledger invariants.
The protocol-probe regression reports crash-to-listener time separately from
majority recovery after the listener opens; the latter keeps its 15-second
deadline. The existing membership announcement can consume 200 seconds before
the restarted listener opens when its first peer is silent. Retain that startup
time in the report, even when the subsequent majority check succeeds.

Benchmark targets accept `--host 0.0.0.0` for container access (default remains
loopback). MemoryLab accepts `--node-urls http://node-0:5000,http://node-1:5000,http://node-2:5000`;
the existing local ports remain the default. URLs must be HTTP(S) origins without
credentials, paths, queries or fragments.

The real .NET SDK payload check uses the existing FibonacciBatch WebSocket job
from the native demo. Start the demo with its supervisor output redirected to
`artifacts/native-demo.log`, then create a unique worker:

```bash
curl --fail-with-body -H 'Content-Type: application/json' \
  --data '{"Args":["--ws-mode","--function-name","dotnet-payload-check","--ws-url","ws://127.0.0.1:30022/ws"],"DependsOn":[],"BackoffLimit":0}' \
  http://127.0.0.1:30020/job/fibonacci-ws
python3 .bin/test-dotnet-sdk-payloads.py --function dotnet-payload-check \
  --worker-log artifacts/native-demo.log --output artifacts/dotnet-payload-check
```

The probe waits for readiness and registration through every node and its base
URL. On Kubernetes, also wait for the application Service from the worker pod's
network namespace (for example, an init container polling `/ready`): direct pod
readiness can precede that node's Service routing update. Processing-proof GETs
may retry transport failures within their existing 180-second deadline; each
failure is recorded as `proof-read-unavailable`. Function and job mutations are
never resent by the probe.

Use a fresh function name and output directory for another run. The 72 cases
cover all six historical sizes through every node, Content-Length and chunked,
sync response streaming and actual async callbacks. Sync checks hash the returned
canonical JSON request; async checks hash the worker's logged decoded body and
require its persisted processing proof. Partial stdout lines are never counted.
The client journal is fsynced before each unretried request. Keep the returned
Job ID and delete only that test Job after collecting evidence.

To reproduce the in-cluster probe, build and preload the existing
`samples/FibonacciBatch/Dockerfile` worker image alongside the source-built
runtime/tools image. Use a fresh owned namespace for each probe:

```bash
campaign=slimfaas-pr506-sdk-check-01
campaign_kubeconfig=artifacts/pr506/kubernetes/kubeconfig
runtime_image=localhost/slimfaas-pr506:after-tools-websocket
worker_image=localhost/slimfaas-pr506:dotnet-worker
python3 .bin/run-kubernetes-campaign.py launch \
  --kubeconfig "$campaign_kubeconfig" --namespace "$campaign" \
  --output "artifacts/$campaign" --image "$runtime_image" \
  --controller-image "$runtime_image" --source-commit HEAD \
  --probes normal --scenarios api
python3 .bin/run-kubernetes-campaign.py wait \
  --kubeconfig "$campaign_kubeconfig" --namespace "$campaign" \
  --output "artifacts/$campaign" --timeout 900
python3 .bin/test-dotnet-sdk-kubernetes.py \
  --kubeconfig "$campaign_kubeconfig" --namespace "$campaign" \
  --image "$runtime_image" --worker-image "$worker_image" \
  --output "artifacts/$campaign/sdk"
```

The SDK command requires a successful initial campaign, checks namespace ownership
and the exact runtime image, and refuses existing SDK resources. It creates
actual worker/probe Jobs, gates the worker on Service readiness, and reads the
worker's logs from inside the cluster. It collects evidence before deleting only
its worker Job and stopping the runtime StatefulSet. Original state and raw
`/evidence/runs/<namespace>/dotnet-sdk/probe-output` remain on the VM.

Run the standard profile before changing the runtime, then run the exact same
matrix after the change. Compare the two structured reports with:

```bash
dotnet run --project benchmarks/SlimFaasBenchmark -- compare \
  --baseline artifacts/slimfaas-local-benchmark/baseline/results.json \
  --candidate artifacts/slimfaas-local-benchmark/candidate/results.json \
  --output artifacts/slimfaas-local-benchmark/comparison
```

For an async queue optimization, capture the candidate on the same machine and
compare with the async acceptance rules:

```bash
BENCHMARK_PROFILE=async-queue \
BENCHMARK_RUN_ROOT=artifacts/slimfaas-local-benchmark/async-candidate \
.bin/slimfaas-local-benchmark.sh

dotnet run --project benchmarks/SlimFaasBenchmark -- compare \
  --profile async \
  --baseline artifacts/slimfaas-local-benchmark/async-baseline/results.json \
  --candidate artifacts/slimfaas-local-benchmark/async-candidate/results.json \
  --output artifacts/slimfaas-local-benchmark/async-comparison
```
The command rejects reports whose duration, warm-up, repetitions, payloads,
concurrency, or scaling settings differ. It writes:

- `comparison.md`: human-readable before/after latency, overhead, throughput,
  async, scaling, and acceptance verdicts;
- `comparison.json`: the same comparison as structured data.

The command exits with a non-zero status when the candidate records errors or
timeouts, when the median added p50 reduction is below 20% for 64 B–4 KiB or
40% for 256 KiB–2 MiB, or when added p95/p99 or the throughput ratio regresses
by more than 10%. Async latency and scaling timings are reported for context;
only their errors and timeouts affect the verdict.

With `--profile async`, the verdict additionally requires no missing,
duplicated, failed, or expired message; median reductions of at least 50% for
the HTTP-202 p95 and 40% for the function-arrival p95; and at least 40% p50
improvement for 2 MiB. Each case is also guarded against a p99 or throughput
regression above 10%. CPU per message may grow by at most 20%, peak memory by
10%, and Raft entries per message may not exceed 2x the baseline. Missing
resource fields in an older `results.json` remain readable and are displayed
as unavailable.

The synchronous and scaling sections are still generated by an async
comparison to expose collateral changes. Sync microbenchmark guardrails are
informational for `--profile async`; the async p99/throughput/resource rules and
all correctness failures determine that profile's verdict. Use the default
`--profile sync` comparison when sync guardrails must be gating.
## What is measured

### Warm synchronous overhead

The benchmark target is one minimal HTTP/1.1 server. The driver calls that exact
process in two ways:

1. directly at `http://127.0.0.1:31080/echo`;
2. through `/function/benchmark-latency/echo`.

For each payload size and concurrency, the report includes throughput and HTTP
p50, p95, p99, and maximum latency. It then subtracts the direct percentile
from the SlimFaas percentile in milliseconds and percent. Calling the same
process on both paths isolates the warm routing/proxy overhead from application
work.

The standard matrix uses:

- payloads: 64 B, 4 KiB, 256 KiB, and 2 MiB;
- closed-loop concurrency: 1 and 16;
- warm-up: 2 seconds;
- measurement: 10 seconds;
- repetitions: 3.

The report uses the median percentile across repetitions and the mean throughput.
The driver alternates direct/proxy order between cases to reduce systematic CPU
frequency and temperature bias.

### Asynchronous latency

Each async case reports two different clocks:

- **acceptance latency**: client send until SlimFaas returns HTTP 202 after the
  request has been durably enqueued;
- **arrival latency**: client send until the target handler starts, including
  enqueue, queue wait, dispatch, and request-body transfer.

For both clocks, the report gives the mean (the simple average) and p50/p95/p99.
For example, p95 means that 95% of messages were no slower than that value. The
mean is the easiest headline number, while p95 and p99 reveal occasional slow
messages that an average can hide.
The target also records completion latency in `results.json`. The target and
driver are local processes on the same clock, so UTC ticks can be compared
without a network clock-synchronization error. The benchmark waits for every
accepted message to be observed by the target and for queue metrics to remain at
zero before starting another case.

The 2 MiB case is intentionally above SlimFaas' 1 MiB async body-offload
threshold. This makes the matrix cover both inline queue payloads and the
cluster-file path.

The async profile also records throughput, missing and duplicate IDs, errors,
expirations, Raft entries per accepted message, process CPU per message, and
peak aggregate SlimFaas working set. The paced scenario measures how quickly a
nearly idle queue wakes without waiting for the polling fallback. The burst
scenario measures coalescing and backpressure under sudden load.
### Scaling speed

`benchmark-scale` starts at zero replicas. The driver sends a burst of 200 async
messages at concurrency 32; each target request takes 250 ms and each replica is
limited to one concurrent request. The following durations are measured from
the first client send:

- first and last HTTP 202;
- desired replicas changing from 0 to at least 1;
- first ready replica;
- desired replicas reaching 4;
- all 4 replicas becoming ready;
- the ready, in-flight, and retry queues returning to zero.

The first delivered `/work` request makes the benchmark target expose
`slimfaas_benchmark_scale_pressure 4` for 30 seconds. SlimFaas scrapes it every
second and evaluates
`max_over_time(slimfaas_benchmark_scale_pressure[30s])`. This deterministic
demand signal exercises metric discovery, scraping, the internal metric store,
PromQL evaluation, scale policy application, local process launch, and health
readiness. Queue metrics are observed independently to measure backlog and drain
time.

## Configure the matrix

The wrapper accepts environment variables without changing tracked files:

```bash
BENCHMARK_PROFILE=quick \
BENCHMARK_DURATION_SECONDS=5 \
BENCHMARK_WARMUP_SECONDS=1 \
BENCHMARK_REPETITIONS=2 \
BENCHMARK_PAYLOAD_BYTES=64,4096,1048576,2097152 \
BENCHMARK_CONCURRENCY=1,8,32 \
BENCHMARK_SCALE_MESSAGES=500 \
BENCHMARK_SCALE_CONCURRENCY=64 \
.bin/slimfaas-local-benchmark.sh
```

Available variables are:

| Variable | Standard default | Purpose |
|---|---:|---|
| `BENCHMARK_PROFILE` | `standard` | `quick`, `standard`, or `async-queue` defaults |
| `BENCHMARK_DURATION_SECONDS` | `10` | measured seconds per route and case |
| `BENCHMARK_WARMUP_SECONDS` | `2` | unrecorded warm-up seconds |
| `BENCHMARK_REPETITIONS` | `3` | repetitions per payload/concurrency case |
| `BENCHMARK_PAYLOAD_BYTES` | `64,4096,262144,2097152` | comma-separated body sizes |
| `BENCHMARK_CONCURRENCY` | `1,16` | comma-separated closed-loop concurrency |
| `BENCHMARK_SCALE_MESSAGES` | `200` | messages in the scaling burst |
| `BENCHMARK_SCALE_CONCURRENCY` | `32` | burst enqueue concurrency |
| `BENCHMARK_SCALE_TIMEOUT_SECONDS` | `120` | maximum scaling observation window |
| `BENCHMARK_ASYNC_DRAIN_TIMEOUT_SECONDS` | `180` | maximum async delivery/drain wait |
| `BENCHMARK_ASYNC_PACED_MESSAGES` | `100` | messages in the low-rate wake-up scenario |
| `BENCHMARK_ASYNC_PACED_INTERVAL_MS` | `100` | interval between paced messages |
| `BENCHMARK_ASYNC_BURST_MESSAGES` | `1000` | messages in the burst scenario |
| `BENCHMARK_ASYNC_BURST_CONCURRENCY` | `64` | burst enqueue concurrency |
| `BENCHMARK_RUN_ROOT` | timestamped artifacts path | exact output directory |
| `BENCHMARK_SKIP_BUILD` | `false` | reuse existing Release binaries for repeated runs on the same checkout |
| `BENCHMARK_RUNTIME` | built Release SlimFaas DLL | preserved managed DLL or Native executable to compare |

The standard Local manifest's path and SHA-256 are also recorded. Its ephemeral
mode assigns a new state directory to each independent benchmark invocation.
The Windows host launcher requires a 512 MiB free-space reserve on the working
and temporary volumes before startup and while running. If exhausted, it stops
only its owned process tree and leaves state for investigation. Such an interrupted
run is an environment failure and cannot support a performance verdict.

## Interpret the result correctly

- Subtract percentiles in milliseconds first. A sub-millisecond direct baseline
  can make a small absolute delta look large as a percentage.
- Async HTTP 202 is enqueue latency, not execution latency. Use target delivery
  percentiles for end-to-end dispatch behavior.
- Native local scaling measures SlimFaas plus operating-system process startup.
  It deliberately excludes Kubernetes scheduling, admission, image pulls, CNI,
  and container runtime time. Run the same driver against a Kubernetes
  deployment when those costs are part of the question.
- Function status endpoints have short caches. Scaling times are externally
  observed milestones with that polling resolution, not internal timestamps.
- Avoid unrelated workloads, power-saving mode, thermal throttling, and debug
  builds. Keep the same host, SDK, commit, and matrix for before/after comparisons.
- A single run is exploratory. Use the standard repetitions, compare medians,
  and retain the raw manifest and samples for a publishable result.

See [Native Local Development Mode](native-local-mode.md) for the process
orchestrator and [Autoscaling](autoscaling.md) for the production scaling model.

## Dashboard inventory overhead

The memory lab can hold a metadata stream open while measuring the existing SlimData workloads. This also enables front tracking, and records both settings in the run manifest:

```bash
MEMORY_LAB_DATA_STREAM=1 WARMUP_SECONDS=2 COOLDOWN_SECONDS=1 \
  .bin/memory-lab.sh aot slimdata-set 5 12
```

For a baseline with front tracking enabled and no inventory viewer, use `MEMORY_LAB_ENABLE_FRONT=true` without `MEMORY_LAB_DATA_STREAM`. Set `MEMORY_LAB_PUBLISH_DIR` and `MEMORY_LAB_SKIP_PUBLISH=1` to compare prebuilt binaries. The stream must connect and stay alive throughout measurement; its frames are saved in the run artifacts.

For browser load, build the dashboard Storybook and open **Dashboard / Live / 10,000 jobs + 10,000 replicas**. It emits 1,000 events per second and refreshes the workload snapshot every second. Pan and zoom for five minutes; search for `fibonacci1-09999` and `daily-report-slimfaas-job-09999` to verify the final instances remain reachable. Record actual canvas draws, heap after collection, viewport and browser version.
