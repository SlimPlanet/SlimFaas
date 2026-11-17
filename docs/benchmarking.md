# Application, Resource and Autoscaling Benchmarks

SlimFaas includes a reproducible native-local benchmark for the warm synchronous
proxy path, asynchronous enqueue and delivery, and scale-to-zero followed by
PromQL scale-out.

## CPU, RAM and throughput

The application benchmarks already export these measurements. Unit tests check
the collectors and report calculations with fixtures; their execution times are
not performance results. Use the following fields for the website results table:

| Workload | Throughput | CPU | RAM | Evidence |
|---|---|---|---|---|
| HTTP sync | Successful requests/s, excluding warmup | Aggregate runtime CPU ms/successful request | Sampled peak aggregate working set, MiB | `results.json`: `LatencyRuns[].RatePerSecond` and `.Resources` |
| HTTP async, paced and burst | Accepted messages/s (HTTP 202), excluding delivery drain | Aggregate runtime CPU ms/accepted message, including delivery drain | Sampled peak aggregate working set, MiB, including drain | `LatencyRuns[].AsyncResources`; `AsyncWorkloads[].Resources` and `.RatePerSecond` |
| MemoryLab Set/Get, HashSet, counters, Files and mixed | Successful operations/s; Files also export logical payload MiB/s | Aggregate runtime CPU ms/successful operation during load | Peak of the sum of runtime nodes in each 500 ms round | `operations.json`: `report`, `resources`, `resourceWindow` |
| Streams, Jobs, official .NET/Python WebSocket clients | Acceptance/request rate and independently observed delivery/completion rates | Runtime CPU during load and a separate drain resource window | Peak cluster working set in each window | `feature-results.json`: `Rows`, `Resources`, `DrainResources`, `Receipts`, `Samples` |
| Microbenchmarks | BenchmarkDotNet operation timings | Elapsed operation time, not process CPU consumption | Managed allocated bytes/op, not process RAM | BenchmarkDotNet reports |

The HTTP `summary.md` contains a combined throughput/CPU/RAM table with
repetition counts, request failures, missing deliveries and duplicates. CPU is
CPU time per accepted message or successful request, **not CPU utilization %**.
RAM is measured in MiB (bytes / 1,048,576). The runtime totals exclude the load
generator, function processes, local supervisor and container/VM overhead;
background consensus and diagnostics work remain included. A 500 ms HTTP or
approximately 1 s platform sampling interval can miss shorter RAM spikes.
Acceptance throughput must not be relabelled as delivery throughput. Keep
delivery counts, latencies and drain results beside it.

To regenerate the HTTP table from retained results **without starting a runtime,
target or benchmark**, use the already built driver:

```bash
dotnet benchmarks/SlimFaas.Benchmarks/bin/Release/net11.0/SlimFaas.Benchmarks.dll report \
  --input artifacts/slimfaas-local-benchmark/overnight/results.json \
  --output artifacts/slimfaas-local-benchmark/overnight/publication-summary.md
```

`report --input` only reads JSON and writes Markdown. It does not reconstruct missing
metrics or certify acceptance. It reports unavailable resource values when any
repetition lacks them. The table uses mean per-repetition throughput and median
per-repetition CPU/peak RAM; raw repetitions remain in `results.json`.
Before publishing measured rows here, attach the runtime and driver source/hash,
OS/architecture, compilation mode, node count, payload, concurrency, durations,
repetitions and links to the raw reports and both enclosing-reference verdicts.
The JSON's framework/OS describe the driver host; obtain the measured product's
compilation mode and source from its manifest. Keep macOS Native AOT and Linux
managed/trimmed results separate. No final-source overnight results have been
added by the 17 November reporting review.

For MemoryLab platform comparisons, `rss_mib` now means **peak cluster sum**, not
largest individual node. Missing, duplicate, invalid or failed node samples make
resource evidence inconclusive. The sampler reads the live
`process_working_set_bytes` collector instead of the five-second diagnostics
gauge. Historical per-node-maximum verdicts must not be presented as cluster RAM
acceptance; retain their originals and qualify any offline recalculation. Use
the platform comparison for this cluster measure; MemoryLab's separate `report`
command remains a per-node slope/cooldown diagnostic. Legacy CPU totals for MemoryLab
can be calculated from the live `process_cpu_seconds_total` boundary deltas only
when every node has both boundaries, unchanged `process_start_time_seconds` and
nondecreasing counters. Missing evidence is unavailable, not zero CPU.

## Complete feature campaign and offline export

The default `.bin/run-platform-matrix.py` blocks now include `data` and
`features`, in addition to the existing HTTP and MemoryLab set/mixed controls.
The same frozen C# driver generates work in every ABBA role. Python controls
platforms and receiver processes; it does not generate the timed feature load.
Build the drivers and both reference/candidate products before measurements.
Container images must contain the same updated benchmark tools, official SDKs
and fixture sources; an old image with a new launcher is not a compatible driver.

| Block | Cases | Integrity and timing |
|---|---|---|
| `http` | Direct/sync/async; steady, paced, burst; existing payload/concurrency matrix | IDs, delivery, duplicates, queue drain; acceptance and completion windows are separate |
| `data` | `slimdata-set`, `get`, `set`, `delete`, `hashset`, `hashget`, `hashdelete`, `increment`, `decrement` | Isolated operations; reads/deletes preseed outside load; final values checked on every node. Set reads use the consistent API; hashset reads retain their documented visibility semantics |
| `data` | `files-upload`, `files-download`, `files`, `files-unique` | Upload only, preseeded download only, cross-node upload/read cycle, bounded unique uploads. Changing overwrite contents, length and SHA-256 verification, all-node final validation |
| `features` | Streams with one/two functions | 200 accepted messages, independent per-function receipts, missing/duplicate/corrupt checks and committed ACK/lag drain |
| `features` | Jobs | 200 real jobs, independent started/completed receipts and `Succeeded` state on every node; request latency and submission-to-start samples |
| `features` | .NET and Python WebSocket sync, async, events | Official SDK receivers; full response SHA-256, first-byte timing, callback receipts and two-function event fan-out |

New cases use concurrency 1/4/16 and three repetitions by default. Files and
WebSocket use the HTTP payload list. Isolated data cases have an unmeasured
warmup. Reads are preseeded and verified before collection. Delete cases stop
when each seeded key has been deleted once; unique file uploads are bounded to
200 operations. After successful all-node validation, the `data` block deletes
only its own file IDs through the public API, outside measurement. Failed data,
WALs, journals and raw results remain available. Direct MemoryLab invocations
retain files unless `--cleanup-owned-files true` is supplied. Files report **logical payload MiB/s**, not the sum of upload and
download network traffic. Runtime RSS excludes the generator's payload buffers. In the direct HTTP control,
runtime CPU/RAM represent idle cluster background work, not the target function.
Mixed workload CPU/RAM describe the whole mix, never each constituent operation.
Jobs keep a configured dispatch limit of 16 in this feature profile; request
concurrency and job dispatch parallelism are different settings.

Receiver receipt polling and queue/job state confirmation are included in the
observed completion window, so completion rates are conservative and distinct
from acceptance rates. `DrainSeconds` records the additional observation time.
The Jobs profile launches real processes/pods; it measures their lifecycle,
including startup. It does not claim isolated scheduler capacity. Keep cold
start, timeout, cancellation, retries, reconnect and recovery tests in the
existing reliability campaigns rather than mixing faults into healthy capacity.

The shared `benchmarks/Common/ClusterResourceMonitor.cs` collector takes explicit
initial/final samples and periodic 500 ms samples. Every node must have live CPU,
RSS and an unchanged process start identity. Counter resets, missing nodes and
invalid samples cannot become zero usage. Async Raft diagnostics additionally
wait for a refresh on every node; the final refresh wait is outside the live CPU
window. MemoryLab's older `memory.csv` remains available for historical adapters.

Launch a prepared configuration manually when the machine is idle:

```bash
python3 .bin/run-platform-matrix.py \
  --config artifacts/pr506/your-frozen-platform-config.json \
  --output artifacts/pr506/your-new-feature-campaign

# Resume completed phases with exactly the same sources, binaries and settings.
python3 .bin/run-platform-matrix.py \
  --config artifacts/pr506/your-frozen-platform-config.json \
  --output artifacts/pr506/your-new-feature-campaign --resume
```

There is no overall campaign duration limit. The full feature matrix can take
more than one night; use `--blocks data` or `--blocks features` for separate,
fresh campaigns. Individual requests/drains remain bounded. Kubernetes phases
have a configurable `phase_timeout_seconds` (default seven days). Disk guards
stop new cases below 20 GiB free. Completed attempts and failures are retained.
A partially executed phase without `execution.json` is deliberately not restarted
in place: inspect and stop its owned processes, retain its directory, then use a
fresh campaign. Never attach to an unverified saved PID or change a WAL marker.

`campaign-identity.json` freezes configuration, selected blocks, launcher hashes
and local executable/managed dependency hashes. Resume refuses changes or a
legacy campaign without this identity. Immutable image digests are required for container
configurations. Comparisons also verify matching driver assemblies and fixture
hashes before accepting evidence. The same output directory cannot be used for a new candidate.
A known candidate integrity failure remains **FAIL** even if its reference has
missing measurements or a later repeat passes. Incomplete measurements remain
**INCONCLUSIVE**. New feature performance is **OBSERVED**, not a non-regression
PASS: retain the established HTTP/MemoryLab budgets and calibrate new budgets
before asserting optimization gains.

Generate publication artifacts without launching any workload:

```bash
dotnet benchmarks/SlimFaas.Benchmarks/bin/Release/net11.0/SlimFaas.Benchmarks.dll report \
  --campaign artifacts/pr506/your-new-feature-campaign \
  --output artifacts/pr506/your-new-feature-campaign/publication.md
```

This writes versioned `publication.json`, `publication.csv` and `publication.md`.
It adapts HTTP, MemoryLab, feature and supported historical standalone reports;
raw files are never rewritten. Every repetition keeps units, payload,
concurrency, latency, CPU, peak RAM, integrity, window and raw source links.
JSON summaries retain minimum/median/maximum per experiment, with unavailable
values when any repetition lacks evidence. Source SHA-256 and nearby
`parameters.json` preserve product/driver/environment attribution. Different
experiments, SDKs, platforms and measurement windows are not pooled. Missing
repetitions, unsupported legacy shapes and failed executions remain visible.
Review these artifacts before explicitly adding measured rows to this page.

## Offload, WAL and queue optimization candidates

No performance default is changed by the tooling work. The current async body
offload threshold remains **strictly greater than 1 MiB**. Existing WAL geometry
remains immutable; new Windows WALs use 64 KiB, while unmarked Linux/macOS WALs
use the system page size. A 512 KiB offload threshold and 64 KiB WAL are candidates
to measure, not proven best defaults. Keep persisted-copy requirements unchanged.

Prepare independent tuning configurations without running them:

```bash
python3 .bin/prepare-platform-tuning.py \
  --config artifacts/pr506/your-frozen-platform-config.json \
  --output artifacts/pr506/tuning-profiles
```

The generator creates nine configurations, including reference controls:
WAL 4/8/16/32/64 KiB and thresholds 128/256/512 KiB/1 MiB. Every configuration
uses the same product for A and B, changes only one setting, and includes payloads
one byte below, exactly at, and one byte above each threshold. Fresh volumes are
mandatory. Local/Podman preparation refuses existing WAL directories; Kubernetes
initialization refuses incompatible or unmarked existing WALs. These settings
are benchmark fixtures, not a new production migration mechanism.

`QueuePipelineBenchmarks` prepares current ZLinq, .NET LINQ and direct-loop
variants for running/retry selection, with empty/200/2000-element queues and
semantic equivalence tests. Routing microbenchmarks remain the control. No
benchmark was run while implementing these variants. Adopt a runtime variant
only after repeated ABBA comparisons beat both enclosing references beyond
observed variation and preserve throughput, latency, CPU, RAM and integrity
budgets. Keep serialization, copy elimination and file I/O experiments separate.

The [implementation handoff](reliability/2026-11-17/benchmark-improvements-implementation.md)
records the isolated candidate branches, validation and remaining manual runs.

The [17 November PR #506 handoff](reliability/2026-11-17/pr506-handoff.md)
records its merge assessment, CPU/RAM evidence, six then-running sourcef2
long roles and the remaining scope for a follow-up validation PR. It also locates
the reusable `$slimfaas-benchmarks` Codex skill installed on the campaign Mac.
Always inspect live ownership/state before resuming; historical source results
do not validate a later runtime revision.

The [completed .NET 11 campaign report](reliability/2026-11-13/README.md)
retains macOS Native AOT and Podman/Kubernetes managed ARM64 comparisons,
failed guardrails, incomplete HTTP references and a Kubernetes duplicate-delivery
failure. Its [performance summary](reliability/2026-11-13/performance-summary-net11-03.json)
and [file-index metadata](reliability/2026-11-13/performance-raw-file-index-metadata-net11-03.json)
identify the frozen sources, outputs and separate maintenance/isolation segments.
The completed campaigns do not establish non-regression or LTS certification.

The [16 November final-source endurances](reliability/2026-11-16/README.md)
complete all six Native macOS/managed kind roles, with 92,332 confirmed
operations, 35,186 separate unknown outcomes and zero confirmed loss or receipt
corruption. One extra successful delivery on an unknown outcome during an
injected fault remains counted. Those recovery/integrity results do not replace
the still-required formal performance comparisons or establish LTS acceptance.
The [final-source managed microbenchmark ABBA](reliability/2026-11-16/micro-abba-189.md)
retains 296 valid case executions, including routing/allocation gains and seven
consistently slower cases above 110% of reference time. These descriptive
microbenchmarks remain separate from application non-regression acceptance.

The [15 November follow-up](reliability/2026-11-15/README.md) retains bounded
Kubernetes reproductions, managed-OOM audits, failed healthy-delivery controls and
separate allocation regressions. Its corrections preserve wire formats, queue
deadlines and WAL geometry defaults. These diagnostic passes do not replace the
formal matrices; each runtime revision requires its own final validation.

The opt-in Files acceptance policy uses `SlimData__Files__RequiredDurableCopies`:
`0` retains historical acceptance, `1` confirms local persistence, and `2`
requires a distinct confirmed persisted remote copy before atomic publication.
Compare these policies at fixed WAL geometry and offload threshold before
combining improvements. Record acceptance and first-delivery distributions,
confirmed/unknown IDs, duplicate receipts, process CPU/allocations/GC/RSS,
WAL/Files I/O and inter-node traffic with their exact sampling windows.
Periodic reference repair and directory synchronization are part of the runtime
cost; kernel I/O counters do not separately identify WAL and Files writes.
The underlying command envelope remains unchanged, but atomic admission adds
**SLDC/6**: use separate clusters for an SLDC/5 reference and SLDC/6 candidate.
Upgrade every member of a candidate cluster together. The dedicated live
admission-fault matrix and final-source performance verdict remain pending.

The [source511 application report](reliability/2026-11-15/persisted-copy-validation-511.md)
records passing targeted volume faults and refusal/cleanup without a remote copy.
Its bounded cost screen uses **0–1–2–2–1–0** with both surrounding references,
then one complete repeat after contradictory checks. It remains inconclusive;
the two-copy budget failures are retained. CPU to first confirmed drain and CPU
through the full idle/cleanup observation are separate scopes. A short batch
does not replace timed HTTP/MemoryLab comparisons. Subsequent
[pooled verification microprobes](reliability/2026-11-15/pooled-file-verification.md)
and component tests require their own application validation. The subsequent
[admission initialization investigation](reliability/2026-11-15/admission-root-registration.md)
retains an HTTP 500 in the first source511 reference. An incomplete or unhealthy
reference cannot establish non-regression; its original state and per-ID
accounting remain preserved while the reproduced serializer defect is corrected.
The [corrected-source follow-up](reliability/2026-11-15/persisted-copy-validation-189.md)
passes all four enclosing-reference comparisons for the bounded two-copy
200 × 1 MiB/C16 case. This diagnostic result does not replace the full timed
HTTP/MemoryLab matrices or earlier policy-cost failures.

## Run the benchmark

One managed driver, `benchmarks/SlimFaas.Benchmarks`, provides `micro`, `target`,
`run`, `compare` and the offline `report` command:

```bash
dotnet build benchmarks/SlimFaas.Benchmarks -c Release \
  -p:PublishAot=false -p:SelfContained=false -p:SkipClientAppBuild=true
dotnet benchmarks/SlimFaas.Benchmarks/bin/Release/net11.0/SlimFaas.Benchmarks.dll micro --list flat
dotnet benchmarks/SlimFaas.Benchmarks/bin/Release/net11.0/SlimFaas.Benchmarks.dll micro --extended --filter '*RoutingQuery*'
dotnet benchmarks/SlimFaas.Benchmarks/bin/Release/net11.0/SlimFaas.Benchmarks.dll target --port 31080
# With the runtime, target and scaling fixture already running:
dotnet benchmarks/SlimFaas.Benchmarks/bin/Release/net11.0/SlimFaas.Benchmarks.dll run --profile async-queue
dotnet benchmarks/SlimFaas.Benchmarks/bin/Release/net11.0/SlimFaas.Benchmarks.dll compare \
  --criteria non-regression --baseline before/results.json --candidate after/results.json
```

`micro` retains BenchmarkDotNet filters, exporters and `--extended`. Existing
option-only microbenchmark invocations remain accepted. Invoking without a command returns exit
code 2; help returns 0; malformed commands return 1. `compare` retains exit codes
0 (pass), 1 (failure) and 2 (inconclusive). The HTTP driver retains its exports,
load profiles and deadlines. The scripts build this driver before measurement.
Use the **same frozen driver and configuration** for both sides of each new
application comparison. Record the driver hash separately from each runtime hash.
Earlier source-pinned reports retain their original project names and binaries.

The driver runs managed code even when its targets are Native AOT executables
or containerized runtimes. Build and publish the measured products separately
with their own settings; the managed driver does not change their compilation
mode. For a framework-dependent driver publication, use the same global
`PublishAot=false`, `SelfContained=false` and `SkipClientAppBuild=true` properties.
The platform tools image publishes a self-contained, untrimmed managed driver.

### Analysis scope and ownership

The C# drivers own workload generation, application timing, exports and comparison
criteria. Python coordinates environments/faults and independently reconciles
receipts; shell scripts provide launch/environment adapters. Shared paths should
delegate to those owners instead of introducing a second load or statistics loop.

Benchmark C# projects are excluded from Sonar with `SonarQubeExclude`, while
[the targeted script inventory](../eng/sonar-benchmark-exclusions.txt) supplies
`sonar.exclusions`. Runtime, SDK, release and production tools stay eligible for
analysis. Benchmark builds/tests continue in CI. Coverage exclusions alone do
not exclude static analysis. The completed-scan check waits for processing,
checks submitted C# project classification, rejects indexed benchmark sources,
and verifies production coverage import. PR APIs can omit unchanged files, so
their absence is checked against submitted project metadata rather than treated
as an analysis exclusion. See the [scope investigation](reliability/2026-11-16/sonar-analysis-boundary.md).

The quick profile is intended for a smoke test and takes a few minutes:

```bash
BENCHMARK_PROFILE=quick .bin/slimfaas-local-benchmark.sh
```

On Windows, run the script in Git Bash with Python 3.10+. The launcher places the
runtime and its descendants in an owned Windows Job Object before execution.
Normal completion and an abrupt launcher exit both close the entire process
tree. Occupied benchmark ports are rejected before starting or cleaning state.
Run `python .bin/test-local-benchmark-host.py` to verify those lifecycle checks.

Fresh Windows WALs use recorded 64 KiB data chunks; existing unmarked volumes
keep their original geometry. Preserve `wal/slimdata-chunk-size` with the WAL.
The [Windows investigation](reliability/2026-11-13/windows-benchmark.md) retains
the failed controls, reproduction commands and validation of large messages,
Streams and recovery. This changes neither the load nor its request deadlines.

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

It requires the repository's .NET 11 SDK plus Bash and `curl`. Ports 31020-31023,
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

Async resource collection waits for a fresh diagnostics sample on every node
before sending measured requests. CPU and RSS come from the live process
collectors across load and delivery drain. After drain, the collector waits for
each node's five-second diagnostics exporter to refresh before reading the final
Raft counter. This wait is outside the timed load and the CPU/RSS window; the
Raft delta includes background entries within that exporter window. It does not
extend the 180-second delivery deadline. Initial, final process and final
diagnostics snapshots, including per-node timestamps and counters, are retained
in `AsyncResources` / focused-workload `Resources` for audit. A missing counter,
counter reset or refresh timeout cannot produce passing resource evidence.
Process start time and CPU/diagnostics/Raft counters are checked per node at
each sample and boundary: progress on another node cannot mask a reset. The
focused paced/burst comparisons apply the same +20% CPU/message, +10% RAM and
2x Raft/message limits to the median resources of each shape; their resource
deltas are retained in `comparison.json` and `comparison.md`.

Earlier subsecond bursts could finish between periodic samples and report zero
CPU or Raft work. Keep those observations as **INCONCLUSIVE** and rerun both
revisions with the same corrected driver; do not backfill their missing samples.

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

After integrating PR #504 and PR #514, build the candidate and comparison
reference with SDK 11.0.100 and the same compilation mode. The .NET 11 reference
is PR #514's `f36084643a75d803051a6918d2950d1a4f674ada`, before the collection
and reliability changes. Keep the original `496e8739` .NET 10 reference and its
observations as historical evidence; do not combine their performance samples
with the .NET 11 comparison. Record both source SHAs and the common driver's
hash in each new campaign configuration. Existing campaign directories and
original persisted volumes must remain available after this toolchain change.

For example, save an environment configuration outside the source tree:

```json
{
  "environment": "macos",
  "before": "/absolute/path/baseline/SlimFaas",
  "after": "/absolute/path/candidate/SlimFaas",
  "before_commit": "<reference-source-SHA>",
  "after_commit": "<candidate-source-SHA>",
  "benchmark": "/absolute/path/SlimFaas.Benchmarks.dll",
  "memorylab": "/absolute/path/SlimFaas.MemoryLab.dll",
  "compare_command": ["dotnet", "/absolute/path/SlimFaas.Benchmarks.dll"]
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
matrix configuration, immutable image digests for `before`/`after`, and a unique `prefix`.
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
Campaign namespaces must fit Kubernetes' 63-character limit, including the
matrix block, attempt and ordinal suffixes. PersistentVolume names append a
storage suffix and can exceed the label-value limit, so volumes carry the
campaign ownership label without an `app` label. Their unique names, claim
references and retained storage paths identify each node's data. A launch with
unbound claims has no valid workload measurement; preserve its API errors and
replay after correcting the deployment with fresh campaign identities.
Each matrix phase keeps launcher manifests, ownership records and Kubernetes
diagnostics under `controller/`, alongside its workload reports. The launcher
requires this controller directory to be new; existing phase metadata must not
be used as the controller output or overwritten during a resume.

Before exporting a Kubernetes phase, `stop` scales only Deployments and
StatefulSets carrying that campaign's label. It then waits up to 180 seconds
for their pods to disappear and for other labelled pod writers to finish;
completed Job pods remain available as evidence. A deletion timestamp alone
does not establish that a process has finished its final WAL flush.
`controller/shutdown.json` retains pod UIDs, pending writers and the final
quiescence verdict. A failed stop prevents the VM archive and the next phase
from starting. This collection deadline is separate from the unchanged
180-second async delivery drain and the leader-change deadline.
The reliability workflow also runs for changes to the platform/Kubernetes
harnesses and SDK probes, including pull requests without runtime changes.

`collect` requests timestamped current container logs and the previous instance
when a restart is reported. Originals under `controller/pod-logs/` retain pod UID,
container identity and restart index; the historical flat filenames remain
convenience copies. `pod-log-coverage.json` records retrieval outcomes, hashes and
missing generations. A missing prior log cannot establish absence of managed
OOM, even when the current process appears healthy. Long-running controllers
can call the same collector with `previous_only=True` after observed restarts
to retain older generations before Kubernetes discards them. Log collection
failure is recorded so campaign shutdown can still finish and preserve state.

HTTP and MemoryLab fixtures omit the fault-agent sidecar's `containerPort`
declaration for both revisions; its control socket still listens normally.
The historical runtime also bound declared sidecar ports, which conflicts with
that agent. The separate chaos fixture declares the sidecar port to exercise
the corrected listener ownership. Keep this common performance convention when
checking reference readiness; retain failed compatibility controls separately.

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

After the current guest workload and its writers have exited, filesystem discard
can return already unallocated Linux blocks to the Mac's sparse VM disk. Record
host/VM free space before and after, and complete this housekeeping before the
next timed load. It removes no file, image, WAL, snapshot or volume:

```bash
podman machine ssh sudo fstrim -v /
```

Retain the discard log and any interrupted observations. Filesystem discard is
separate from deleting a cache or pruning an unused image.

Long rootless campaigns also consume the Linux user's session-key quota. Inspect
`/proc/key-users` and `sysctl kernel.keys.maxkeys kernel.keys.maxbytes` in the
dedicated VM. If OCI startup reports `unable to create session key: disk quota
exceeded`, raise that VM's quota (for example, `sudo sysctl -w
kernel.keys.maxkeys=20000 kernel.keys.maxbytes=2000000`) and retain the original
failed attempt. This is unrelated to filesystem capacity.

`sysctl -w` settings are lost when the VM restarts. To preserve the campaign's
validated key quotas and inotify limits across restarts, install a dedicated
configuration inside that VM:

```bash
podman machine ssh sudo tee /etc/sysctl.d/90-slimfaas-pr506-rootless.conf <<'EOF'
kernel.keys.maxkeys = 20000
kernel.keys.maxbytes = 2000000
fs.inotify.max_user_watches = 524288
fs.inotify.max_user_instances = 524288
EOF
podman machine ssh sudo sysctl -p /etc/sysctl.d/90-slimfaas-pr506-rootless.conf
```

Recheck these values after a host/VM restart before resuming container campaigns.
On macOS, prevent idle sleep for the measurement command's lifetime, for example
with `caffeinate -ims python3 .bin/run-platform-matrix.py --config config.json
--output artifacts/matrix`. A host suspension interrupts measurement isolation;
retain the partial results and start a fresh complete ABBA block.

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

Keep these bounds separate in container endurance campaigns: pass
`--protocol-startup-timeout 300 --recovery-timeout 180` through the Kubernetes
launcher to the chaos driver. The startup option applies only to the process
restart and HTTP-listener waits in `protocol-timeout`; its default remains the
recovery timeout for compatibility with existing commands. The subsequent
ready/write guard stays fixed at 15 seconds, and recovery/confirmed-drain waits
remain at 180 seconds. The report records both the selected startup allowance
and actual crash-to-listener time. A startup failure remains a retained failed
observation and must not be counted as a completed long endurance.

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
dotnet run --project benchmarks/SlimFaas.Benchmarks -- compare \
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

dotnet run --project benchmarks/SlimFaas.Benchmarks -- compare \
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

The 2 MiB case is intentionally above SlimFaas' default 1 MiB async body-offload
threshold (strictly greater; exactly 1 MiB stays inline). This makes the matrix
cover both inline queue payloads and the cluster-file path. Record
`SlimFaas__AsyncBodyOffloadThresholdBytes` when overriding that default; vary it
separately from the fresh WAL geometry and keep it identical across comparison
brackets. See [async body configuration](functions.md#async-body-storage-threshold).

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
| `BENCHMARK_RUNTIME_PATH` | unset | launch a prebuilt self-contained JIT or Native AOT `SlimFaas` executable; use with `BENCHMARK_SKIP_BUILD=true` |
| `BENCHMARK_RUNTIME_MODE` | `jit` | record the runtime mode in the run manifest; it does not change the supplied executable |

## Compare JIT and Native AOT on a new machine

The [Apple M4 Pro ZLinq report](performance-benchmarks-m4.md) records the
before/after measurements, resource definitions and limitations of this protocol.

The four-variant campaign requires Python 3.11+ on macOS or Linux; its orchestration
uses POSIX process groups, `ps` and host load averages. Rebuild and measure **both**
revisions on the same host. Previous measurements
from a different CPU or OS are historical context, not a baseline. Publish four
isolated directories named `baseline-jit`, `candidate-jit`, `baseline-aot` and
`candidate-aot` below a common directory. Use the same SDK and Release settings:

```bash
# Run from each revision, changing the output directory accordingly.
dotnet publish src/SlimFaas/SlimFaas.csproj -c Release -r osx-arm64 \
  -p:SkipClientAppBuild=true -p:PublishAot=false -p:PublishTrimmed=false \
  --self-contained true -o /absolute/path/publish/baseline-jit

dotnet publish src/SlimFaas/SlimFaas.csproj -c Release -r osx-arm64 \
  -p:SkipClientAppBuild=true -o /absolute/path/publish/baseline-aot

dotnet build benchmarks/SlimFaas.Benchmarks -c Release \
  -p:PublishAot=false -p:SelfContained=false -p:SkipClientAppBuild=true
dotnet build tools/SlimFaas.MemoryLab -c Release

python3 .bin/slimfaas-zlinq-benchmark.py \
  --publish-root /absolute/path/publish \
  --output artifacts/zlinq-comparison \
  --rounds 3 --duration 20 --warmup 5 \
  --payloads 64,4096,2097152 --concurrency 1,16 \
  --data-duration 60 --stream-messages 200 --dispatch-parallelism 16

python3 .bin/summarize-zlinq-benchmark.py artifacts/zlinq-comparison
```

Use the RID matching the host. The JIT publication is self-contained and
untrimmed; the native publication uses the product's AOT settings. The campaign
requires identical dashboard assets in all publications. `SkipClientAppBuild`
skips compilation but can still copy an existing generated `wwwroot`; remove it
from the **benchmark publication copies** for a backend-only comparison.
The campaign
never compiles during measurement. It runs variants sequentially, reverses their
order on alternate rounds and rotates the third round. Every repetition starts
a clean three-node `slimfaas local` cluster. The unchanged benchmark driver and
target use the same JIT runtime for all four variants.

Each round covers direct/synchronous/asynchronous HTTP, paced and burst queue
traffic, scale-to-zero/scale-out, sustained sets, mixed set/delete/increment/
hashset/async operations, cross-node file uploads/reads, and a bounded durable
Stream burst. Set/hashset/counter state is checked on all three nodes; queue and
Stream delivery checks reject missing or duplicate IDs. The Stream burst must
reach zero lag and pending deliveries with committed ACKs. Microbenchmarks
exercise larger collection populations separately.
File payloads default to 256 KiB and can be changed with `--file-bytes`.
The file workload reuses one ID per worker, uploads to one node and drains a read
from the next. It measures warm repeated-file access and HTTP success, without
byte-by-byte comparison. Unique 2 MiB async bodies exercise offload separately.
`--dispatch-parallelism` defaults to 16 and sets both async dispatch limits on
the benchmark function through a generated local overlay. The original manifest
stays unchanged. Use 256 explicitly to retain the original high-parallelism
stress profile. Sustained 2 MiB traffic exposed quorum interruptions and delivery
deadlines at both capacities 256 and 16 on this three-node setup, including the
baseline without ZLinq. Retain unsuccessful attempts alongside completed runs;
performance medians conditional on successful completion cannot establish reliability.

CPU and sampled peak RSS sum the runtime nodes, excluding the supervisor,
functions and load generator. Async and Stream CPU windows include delivery and
drain. RSS reflects process working sets, including shared/native pages; it is
different from managed allocation per operation. Data resources include driver
startup before load, but end before report serialization and state validation.
Data operations may issue multiple HTTP requests. Stream throughput includes
delivery/drain; its p95 is append acceptance latency.

The summarizer rejects incomplete rounds and correctness failures. It retains
every repetition in `comparison.json`, reports medians plus rate/CPU/RSS/p95 ranges, and
includes the unchanged direct-target control. Compressed per-request HTTP samples,
per-case resources, validation logs, binary hashes and settings remain in the
campaign directory. A noisy direct control or overlapping repetition ranges
limits conclusions about small differences; longer runs alone do not remove
shared-host variance.

After an interrupted campaign, repeat its exact command with `--resume`. The
runner checks settings and executable/assembly hashes, skips completed variants,
and archives incomplete attempts before restarting them with clean state.
It waits for ports to become reusable and accepts TCP `TIME_WAIT` during restart.

If instability prevents a planned repetition from completing, retain its logs and
failure counts. An explicit `analysis.json` can limit repetitions symmetrically
before/after within each runtime mode; `--resume` honors those limits and the
summarizer displays the analyzed counts. Keep the original manifest unchanged:

```json
{
  "rounds_by_variant": {
    "baseline-jit": 3, "candidate-jit": 3,
    "baseline-aot": 2, "candidate-aot": 2
  },
  "reason": "Stop AOT retries after repeated heavy-body failures; retain both completed repetitions per revision."
}
```

These are conditional performance statistics, with fewer AOT repetitions. Report
unsuccessful attempts alongside them; do not describe the workload as reliable.

For more stable collection timing and allocation estimates, run the same
benchmark sources against each revision:

```bash
dotnet run -c Release --project benchmarks/SlimFaas.Benchmarks \
  -p:PublishAot=false -p:SelfContained=false -p:SkipClientAppBuild=true -- micro --extended \
  --filter '*DataFeatureBenchmarks*' '*CollectionPipelineBenchmarks*' \
           '*RoutingQueryBenchmarks*' '*SlimDataQueueBenchmarks*' \
           '*QueueSelectionBenchmarks*' \
  --exporters json csv --join
```

`--extended` uses six warmup and twelve measured iterations with a 500 ms target
iteration time. Queue mutation benchmarks retain their explicit invocation count
and fresh-state setup; their short iterations make mutation timings exploratory.
These microbenchmarks run in-process under JIT; the full
local campaign supplies the actual JIT/AOT comparison.

Build this reflection-based BenchmarkDotNet driver in managed, framework-dependent
mode for both revisions. Its referenced products normally publish AOT and can
otherwise be inferred as self-contained; publishing the driver with inconsistent
settings fails with `NETSDK1151` before measurement. Global command properties
apply to those references as well. Compiler/analyzer/audit policies remain on,
and referenced products retain their trim-compatibility checks during build.
The separate production AOT publications keep their normal settings.

For frozen drivers, build once, inspect the method catalog, and execute the DLL
without rebuilding during the comparisons:

```bash
dotnet build benchmarks/SlimFaas.Benchmarks/SlimFaas.Benchmarks.csproj \
  -c Release -p:PublishAot=false -p:SelfContained=false \
  -p:SkipClientAppBuild=true -nr:false -o artifacts/micro-driver
dotnet artifacts/micro-driver/SlimFaas.Benchmarks.dll micro --list flat
dotnet artifacts/micro-driver/SlimFaas.Benchmarks.dll micro --filter '*'
```

Record the driver/configuration hashes and verify that both runtime configuration
files use the same framework versions. The
[retained build-mode control](reliability/2026-11-16/managed-micro-driver-144.json)
preserves the failed publish and a corrected warning-free build, matching
framework-dependent configurations and 33 discovered methods before timed runs.

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

## Safe storage initialization for dedicated kind faults

The dedicated PV-content-loss fixture uses `.bin/slimdata_kubernetes_storage.py`
to keep its one-shot bootstrap consumption record outside every data PV. The
coordinator must first prove its owned namespace and VM run root absent before
preparing that control directory. Restarted pods verify the existing WAL chunk
marker; an unknown or conflicting geometry fails closed. An empty replacement
data volume receives its requested geometry without a new bootstrap permission.
These helpers are test infrastructure and do not change product WAL defaults.

```bash
python3 .bin/test-kubernetes-storage.py
python3 .bin/test-platform-validation.py
```

The platform suite includes the seven storage regressions on Unix. They cover
fresh/repeated initialization, complete data-volume loss, incompatible/unmarked
WALs, a missing WAL with retained state, missing control proof and lost bootstrap
control. Windows skips this Unix directory-fsync fixture. Record the entire
rendered Kubernetes manifest, controller source and control proof with each
fault campaign. Preserve original data before recovery or logical PV-content loss.


## Request journals and orchestration ownership

C# owns request generation, pacing and latency measurement. The unified driver
and MemoryLab compile the same percentile interpolation from `benchmarks/Common`.
Python controls process/pod faults and independently reconciles receipts. Shell
scripts prepare environments and invoke these tools; they must not introduce
another load generator or silently retry application requests.

Use `--journal-directory <new-directory>` with `run` or MemoryLab `load` when
individual outcomes matter. Each process creates a unique JSONL file containing
its pod UID, writer ID, UTC and monotonic clock, caller ID, target URI, length and
SHA-256 when available, response headers/status/version and transport exceptions.
Async response headers also retain `QueueElementId`, linking the caller ID to
the accepted queue element before execution and acknowledgment reconciliation.
Intents are flushed before entering the HTTP transport. An exception after that
point is recorded as unknown; it is not proof of rejection or non-execution.
MemoryLab sync/async calls share the caller ID and SHA-256 with the independent
function fixture, and record operation completion only after response validation
and draining. Sync still sends one application-level request; no retry is added.

JSONL lines are flushed to the OS, without a per-line disk fsync. They survive a
process crash when the OS retains the file; they do not prove host power-loss
durability. The independent Python function receipts retain their existing fsync.
The HTTP pilot journal observes response headers, not successful full-body
consumption. It does not observe transparent transport retries or redirects as
separate wire exchanges. Reconcile those with server receipts and runtime logs.
Journaling is opt-in and adds driver hashing/I/O cost; use the same setting and
binary for both sides of a comparison. Original reports are never retrofilled.

The Kubernetes pod-replacement adapter reuses the three-node campaign manifests
and executes the C# generator as a Job. It keeps the deleted function process
alive during preStop to distinguish stale connection reuse from pod selection.
Function headless DNS excludes unready addresses; Raft's separate headless service
retains its own membership policy. API observations include pod UIDs/IPs,
termination/readiness, EndpointSlices and events. Runtime debug logs retain the
actual sent URLs; receipt headers identify the function that served each request.

The default `--replacement-mode terminating` retains those established connections
until the observation window ends. Use `--replacement-mode exit` for a separate
case where deletion also ends the old fixture process. Its identity is checked
before SIGINT, and a replacement UID is never signaled by that action. Keep these
two failure modes distinct in the report. `--function-delay-seconds 0.5` optionally
holds function requests in flight; combine it with C16 and a suitable target rate
to exercise active connections rather than merely configuring 16 idle slots.
The delay applies to POST workload calls, leaving readiness GETs unaffected.
Use `--function-controller statefulset --replacement-mode exit` to exercise a
StatefulSet function with a stable ordinal and a replaced UID/IP. The old process
must exit before that controller can recreate its ordinal; the adapter rejects
the long preStop mode for this case.

For a separate forensic run, `--delivery-trace` enables Trace logging at admission,
dequeue, HTTP transport entry/result, callback command and committed Raft apply
boundaries. Join caller journals' `QueueElementId` to queue element IDs, worker
generation/try number, producer generation/sequence, operation request ID and Raft
index/term. A transport entry does not prove bytes reached the function. A network
exception may become the existing synthetic HTTP 500; the transport trace keeps
that outcome distinct from actual response headers. A confirmed callback command
is distinct from proof that it matched a still-running attempt. Missing trace
lines never prove non-application; preserve WAL/snapshots for replay, especially
on followers and restarts that have no local apply context. Trace logging adds
I/O cost: keep this diagnostic separate from ordinary performance acceptance.

Routing cases also use the existing resource sampler to read the owned runtime's
CPU ticks, RSS, open FDs and socket FDs every five seconds. Live TCP states are
matched to that process's socket inodes; TIME_WAIT and interface byte counters
belong to the entire pod network namespace, including probes and diagnostics.
Keep this scope and sampler overhead identical across comparisons. Each sample
includes pod UID and process start identity; a recycled PID is rejected. Sampling
never signals a process and does not rehash the executables on every poll.

```bash
python3 .bin/run-kubernetes-routing.py \
  --kubeconfig artifacts/pr506/kubernetes/kubeconfig \
  --namespace slimfaas-pr506-routing-service-example \
  --image localhost/slimfaas-pr506:function-pool-467-03 \
  --controller-image localhost/slimfaas-pr506:routing-tools \
  --source-commit 29daa097fd4562c39e4b362646c16ddd5f1cf7c0 \
  --route service --concurrency 1 --before-seconds 60 --after-seconds 420 \
  --output artifacts/pr506/routing/service-example
```

Build/import the selected images first. The adapter refuses namespace reuse and
clusters outside the dedicated four-node `kind-slimfaas-pr506` environment. It
retains volumes and evidence after stopping its owned workloads. Execution
completion is not a convergence/non-regression verdict: reconcile receipts,
unknowns, actual five-minute connection age and the separate 15-second routing
budget before issuing one. Use `--dry-run` to inspect the resource manifest.


During routing teardown, the adapter first scales its owned workloads to zero.
It then ends only its Python function fixture, checking namespace/campaign labels,
image, command, downward-API pod UID and the container PID1 command before SIGINT.
This happens after measurement, keeping the long preStop from adding ten minutes
to every case. Other processes are never signaled, and the retained-volume/log
quiescence check still runs before export.

For async or mixed routing, the C# generator stops admitting work before a separate
**180-second** read-only receipt drain. The sampler marks that interval as `drain`.
There is no resubmission. Failed HTTP execution receipts remain separate from body
corruption and successful duplicates; a later 2xx execution can reconcile an
accepted retry. Controlled-fault duplicate counts are retained rather than erased.
Normal benchmark receipt checks still reject duplicates by default. The fault
comparison must assess attempts and successful executions before issuing a verdict.

The [isolated Service connection-lifetime ABBA report](reliability/2026-11-16/kubernetes-service-pool-lifetime.md)
records 38,400 reconciled calls and the actual five-minute pool rotation. It
distinguishes a still-alive terminating pod from a stopped destination, and
keeps this diagnostic separate from capacity and final-source acceptance.

The [headless DNS comparison](reliability/2026-11-16/kubernetes-headless-pool-lifetime.md)
retains its original pre-traffic health failure and the measurements from all
four runs. The [async ownership report](reliability/2026-11-16/async-reservation-coalescing.md)
separates the deterministic worker regression from historical duplicate causes
that still require real per-ID traces and affected application replays.

The [source f2f80124 publication ledger](reliability/2026-11-16/ownership-publications-f2.md)
identifies the new Native AOT executables and shared Linux drivers/images.
Publication and CI success remain separate from application and endurance
acceptance; follow its recorded preflights before using those images.

Detailed Trace can rotate a Kubernetes log within one pod/container generation.
A successful `kubectl logs` call and unchanged restart count therefore do not
establish complete history. The collector now rejects a canonical snapshot that
omits a previously captured prefix. It keeps that earlier snapshot and reports
the gap; a passing receipt reconciliation does not remove it. The in-cluster
chaos backend uses the same UID/restart collector as the host, captures current
and previous generations, and saves output before successive intentional kills.

For new dedicated kind clusters, merge this patch into the first control-plane
node's configuration, retaining the existing evidence mounts and worker layout:

```yaml
kubeadmConfigPatches:
  - |
    kind: KubeletConfiguration
    containerLogMaxSize: 128Mi
```

[kind applies the first node's KubeletConfiguration to all nodes](https://kind.sigs.k8s.io/docs/user/configuration/#kubeadm-config-patches).
Verify the effective configuration before each comparison series and keep it
identical for both variants. This run's four-node dedicated cluster was updated
while campaigns were stopped, with original configurations retained and the
effective limit verified through each node's `configz` endpoint. Larger retention
does not prove absence of other gaps. The original source-f2 async routing case
has complete receipts but incomplete detailed leader history; its per-ID trace
verdict remains inconclusive and requires a fresh run.
