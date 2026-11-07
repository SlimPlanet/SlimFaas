# SlimFaas performance benchmarks

This document tracks the methodology and results of the micro-benchmarks used to
validate the performance commits of the `claude/performance-improvements-9h83ah` branch
(PR #313) and of its follow-ups (theme 6, branch `claude/slimdata-queue`).

## Infrastructure

- Project: `benchmarks/SlimFaas.Benchmarks` ([BenchmarkDotNet](https://benchmarkdotnet.org/)).
- Configuration: `Job.ShortRun` (3 warmups, 3 iterations) + in-process toolchain +
  `MemoryDiagnoser`. The goal is to compare a **before/after on the same machine in the
  same session**, not to produce publishable absolute numbers. Allocation gains
  (`Allocated` column) are exact; timings are indicative (a few % of variance).
- Running:

  ```bash
  dotnet run -c Release --project benchmarks/SlimFaas.Benchmarks -- --filter '*'
  dotnet run -c Release --project benchmarks/SlimFaas.Benchmarks -- --filter '*DeploymentsSnapshot*'
  ```

## Methodology

1. **Commit 0**: this infrastructure is introduced before any code change, and the
   full baseline is measured on the unmodified code.
2. For every performance commit:
   - non-regression tests are added whenever the existing suite does not cover the
     modified behavior (tests that document a behavior fix ship with the commit that
     fixes it);
   - the theme's benchmarks are run **before** (baseline) and **after** the change;
   - the commit is kept **only if a gain is measured**; the numbers are recorded here
     and in the commit message.

Two benchmark styles are used:

- **cross-commit before/after**: the benchmark calls the modified public API; the
  measurement at commit N-1 is compared with the one at commit N
  (e.g. `ReadDeployments`).
- **in-run A/B**: when the modified code cannot be reached in isolation (private path
  deep inside SlimDataService, dependent on a Raft cluster), the benchmark faithfully
  reproduces both strategies in the same run (e.g. `QueueCountBenchmarks`, which
  reproduces `DoListCountElementAsync` with and without materializing the message
  bodies).

## Coverage per theme

| Theme | Benchmark | Style |
|---|---|---|
| 1. Defensive snapshot copies (`ReplicasService.Deployments`, `JobService.Jobs`) | `DeploymentsSnapshotBenchmarks` | before/after |
| 2. Queue counting without body materialization | `QueueCountBenchmarks` | in-run A/B |
| 3. Per-HTTP-request allocations (port check, visibility, pod selection) | `HttpHotPathBenchmarks` | before/after (+ A/B for the port check) |
| 4. Recurring worker cost (NodaTime schedules, PromQL registry) | `SchedulingBenchmarks` | before/after |
| 5. Cost of one `PayloadBytes` evaluation (3× → 1× per snapshot) | `SlimDataStateBenchmarks` | unit cost × avoided calls |
| 6. SlimData queue commands applied by the Raft state machine (dequeue, HTTP callbacks, push) | `SlimDataQueueBenchmarks` | before/after |

## Results

### ZLinq routing queries (issue #121)

Measured on 2026-11-07, Windows 11 x64, AMD Ryzen 5 3500U (4 cores / 8 logical
processors), .NET SDK 10.0.403 / runtime 10.0.14, BenchmarkDotNet 0.15.8.
Baseline runtime: `b4f7caf0`; commit `430b49ab` contains only the benchmark
instrumentation and can be used to reproduce the baseline. The candidate adds
ZLinq 1.6.1 to explicitly selected queries, not as a global LINQ replacement.
No Docker was used.

#### Scan checklist and selection

Five routing/model/wake-up files were checked. Exact recipe counts:

| Recipe | Hits |
|---|---:|
| Culture-sensitive literal IndexOf / StartsWith / EndsWith / Contains | 0 / 0 / 0 / 0 |
| Substring / blocking Task.Result or Wait / stackalloc / new HttpClient | 0 / 0 / 0 / 0 |
| Static Dictionary / FrozenDictionary / CurrentCulture comparer | 0 / 0 / 0 |
| Explicit new List / Dictionary | 6 / 2 |
| Select / Where / Cast / Take / Aggregate chains | 12 |
| Sealed instance classes / total instance classes | 1 / 4 |

Existing workload/result containers and public class inheritance were left alone.
The history synchronization worker has no LINQ query to replace.

- **Moderate: function lookup** avoids the captured predicate and obtains a
  span through ZLinq when the `IList<T>` is an array or list. Other collection
  implementations retain value-enumerator traversal and disposal. Endpoint,
  proxy and wake-up lookups share the same implementation; comparison remains
  ordinal, case-sensitive and first-match, without caching an old snapshot.
- **Moderate: first-ready-pod ports** uses explicit value `Where` / `Select`
  enumeration, preserving a null port list on the first ready pod.
- **Info: predicated counts and readiness checks** were not migrated. .NET 10
  LINQ already has array/list fast paths. The attempted status-count substitution
  kept the same 96 B allocation and was discarded.

#### Routing micro-benchmarks

Same ShortRun in-process harness before and after, with three warmups and three
measurement iterations. Searches target the last function; each function has
five ready pods. These call the actual endpoint helper and proxy, not a copied
implementation. Allocation is managed bytes per operation, not process RAM.

| Operation | Functions | Before mean | After mean | Before allocated | After allocated |
|---|---:|---:|---:|---:|---:|
| SearchFunction | 2 | 18.48 ns | 12.72 ns | 24 B | 0 B |
| SearchFunction | 20 | 91.70 ns | 77.72 ns | 24 B | 0 B |
| SearchFunction | 200 | 743.88 ns | 720.66 ns | 24 B | 0 B |
| FirstReadyPodPorts | 2 | 96.88 ns | 23.72 ns | 176 B | 0 B |
| FirstReadyPodPorts | 20 | 206.54 ns | 88.84 ns | 176 B | 0 B |
| FirstReadyPodPorts | 200 | 1175.41 ns | 725.56 ns | 176 B | 0 B |
| StatusReadyCount (unchanged control) | 20 | 52.64 ns | 52.44 ns | 96 B | 96 B |

The first candidate used only value `foreach`, but its 200-function lookup took
1020.10 ns, versus 743.88 ns before. It was replaced with the span fast path.
ShortRun confidence intervals are wide; the final 200-function lookup timing is
not a statistically established speedup. The zero-allocation list/array lookup
is also enforced by regression tests.

#### Three-node local cluster: end-to-end RAM, CPU and latency

Used `benchmarks/slimfaas.local.benchmark.yaml` and `slimfaas local up`, with fresh
ephemeral state for each run. These measurements use Release **JIT** runtime
processes, not Native AOT binaries. Baseline ran 14:24:44-14:33:59 UTC; candidate
ran 14:52:25-15:01:18 UTC. Each case has three repetitions, 3 s warmup and 10 s
closed-loop load. Direct/sync order alternates, followed by async delivery/drain.

Rates are arithmetic means of repetitions. Latency, CPU/request and sampled peak
RAM are medians of per-repetition values. CPU is cumulative process CPU across
all three runtime nodes divided by successful requests. RAM sums their working
sets, sampled every 500 ms. Both exclude the supervisor, target and load generator;
async resource measurement includes delivery and queue drain. Working set is
neither managed allocation nor private committed memory.

Each cell below is **before -> after**:

| Mode | Bytes | Concurrency | Requests/s | p95 ms | p99 ms | CPU ms/request | Sampled peak RAM MiB |
|---|---:|---:|---:|---:|---:|---:|---:|
| Sync | 64 | 1 | 575.7 -> 791.2 | 2.375 -> 1.463 | 4.678 -> 1.815 | 2.682 -> 2.078 | 414.2 -> 410.5 |
| Sync | 64 | 16 | 3334.7 -> 3072.5 | 10.278 -> 14.358 | 14.367 -> 28.929 | 1.165 -> 1.275 | 471.1 -> 477.9 |
| Sync | 4096 | 1 | 572.9 -> 750.7 | 2.890 -> 1.820 | 4.606 -> 2.246 | 2.366 -> 2.072 | 486.4 -> 487.4 |
| Sync | 4096 | 16 | 2276.0 -> 2655.8 | 17.106 -> 10.868 | 33.155 -> 15.250 | 1.338 -> 1.260 | 503.3 -> 504.4 |
| Async | 64 | 1 | 30.7 -> 32.0 | 38.748 -> 34.462 | 58.014 -> 38.355 | 49.030 -> 38.162 | 420.0 -> 402.8 |
| Async | 64 | 16 | 250.9 -> 359.3 | 148.085 -> 97.355 | 488.607 -> 251.706 | 7.438 -> 7.057 | 484.1 -> 472.2 |
| Async | 4096 | 1 | 24.1 -> 35.2 | 81.360 -> 34.962 | 144.471 -> 72.861 | 45.803 -> 42.418 | 500.0 -> 488.9 |
| Async | 4096 | 16 | 106.3 -> 135.1 | 389.212 -> 387.145 | 842.409 -> 823.275 | 20.415 -> 18.371 | 493.4 -> 501.8 |

**Do not infer a general throughput, CPU or RAM improvement.** The 64 B / 16-client
sync case regressed: throughput -7.9%, p95 +39.7%, p99 +101.4%. Even the unchanged
direct target's 64 B / 16-client mean rate moved from 18289.3/s to 24389.2/s.
The shared laptop's run-to-run variation, runtime warmup and evolving queue/worker
activity confound the end-to-end comparison. The retained changes establish a
bounded query-allocation gain, not a causal explanation for the macro results.

All 36 cases in each run completed without HTTP errors, missing async deliveries
or duplicates: 1,338,995 successful requests before and 1,703,004 after.
The 200-message scale-from-zero burst accepted all messages and reached four
ready replicas in both runs: first ready / four ready / drained was
17.701 / 25.162 / 39.213 s before, and 7.278 / 13.695 / 28.664 s after.
These startup observations are not attributed to ZLinq.

#### Native AOT and license checks

Windows x64 Native AOT publish succeeded for both baseline and candidate.
Executable sizes were 72,951,808 and 72,966,144 bytes (+14,336 bytes, +0.020%).
Both produced the same pre-existing MemoryPack.Core IL2104/IL3053 and
System.Configuration.ConfigurationManager IL2104 summary warnings; ZLinq
produced no additional trim/AOT warning.

The candidate's native executable also ran the real three-node Fibonacci demo:
`/status-functions` and `/function/fibonacci1/hello/local` succeeded with
`Hello local!`. The smoke overlay used ephemeral state and already-built sample
DLLs and disabled scheduled demo jobs; the routing/replica annotations were kept.
The focused history test now waits for observed persistence under a bounded
deadline instead of assuming a 120 ms scheduler window, and always stops its worker.
The final full .NET suite passed: 1,936 tests (1,498 SlimFaas, 282 SlimData,
106 MCP, 39 .NET client and 11 Kafka).

ZLinq's NuGet distribution declares MIT and has no transitive dependency for
net10.0. Its distributed files were inspected (assemblies, XML documentation,
README and icon; no vendored source/test tree). The PR's FOSSA License Compliance
status must still pass before merging.

#### Reproducing

Use commit `430b49ab` for the baseline and this change for the candidate, with the
same host and SDK. Do not benchmark concurrently with builds or other load.

```bash
dotnet run -c Release --project benchmarks/SlimFaas.Benchmarks -p:SkipClientAppBuild=true -- --filter '*RoutingQuery*'
BENCHMARK_PROFILE=standard BENCHMARK_DURATION_SECONDS=10 BENCHMARK_WARMUP_SECONDS=3 \
  BENCHMARK_REPETITIONS=3 BENCHMARK_PAYLOAD_BYTES=64,4096 BENCHMARK_CONCURRENCY=1,16 \
  .bin/slimfaas-local-benchmark.sh
```

On Windows, use separate terminals: build first, keep `local up` running, then
run the same driver (Ctrl+C stops the local cluster):

```powershell
dotnet build src\SlimFaas\SlimFaas.csproj -c Release -p:SkipClientAppBuild=true
dotnet build benchmarks\SlimFaasBenchmark\SlimFaasBenchmark.csproj -c Release
dotnet src\SlimFaas\bin\Release\net10.0\SlimFaas.dll local up -f benchmarks\slimfaas.local.benchmark.yaml --clean
dotnet benchmarks\SlimFaasBenchmark\bin\Release\net10.0\SlimFaasBenchmark.dll run --duration 10 --warmup 3 --repetitions 3 --payload-bytes 64,4096 --concurrency 1,16 --output artifacts\zlinq\manual
```

The driver's `results.json` includes per-case `Resources` for direct, sync and
async runs, and `summary.md` includes their CPU/working-set table. Missing or
invalid required process metrics fail explicitly rather than reporting zero.

| Severity | Count | Top issue |
|---|---:|---|
| Critical | 0 | No critical API-pattern hit in the scoped files |
| Moderate | 2 | Repeated lookup and query iterator allocations |
| Info | 1 | Count substitutions have no measured allocation benefit |

> AI-assisted pattern analysis can miss issues or suggest unsuitable changes.
> Verify with benchmarks and human review; these short measurements are not
> production performance guarantees.

### DLQ metadata and retained body sizes

Windows x64, .NET SDK 10.0.401, ShortRun in-process. Each operation scans
1,000 retained entries; the payload dictionary shares an immutable body buffer.
This isolates metadata processing from body size, not end-to-end throughput.

| Method | Bytes per body | Mean | Allocated |
|---|---:|---:|---:|
| PayloadOwnerReferences | 0 | 295.4 us | 234.63 KB |
| PayloadOwnerReferences | 1,048,576 | 248.3 us | 234.63 KB |
| RetentionSweep | 0 | 536.2 us | 278.80 KB |
| RetentionSweep | 1,048,576 | 554.4 us | 278.81 KB |

Maintenance allocations do not scale with retained body bytes. These short
timings have wide confidence intervals and are not latency guarantees.
Run `--filter '*DeadLetterMetadata*'` to reproduce.

The queue benchmark uses the real `Queue:` namespace and compares opt-in
capture against an unmanaged queue, including timed-out/finished elements:

| Queue depth | Capture enabled | Dequeue mean | Allocated |
|---:|:---:|---:|---:|
| 50 | No | 21.57 us | 3.16 KB |
| 50 | Yes | 343.43 us | 40.65 KB |
| 500 | No | 169.27 us | 9.46 KB |
| 500 | Yes | 876.82 us | 315.13 KB |

Managed queues pay for staged immutable transitions and terminal capture.
The disabled default avoids that path. These figures are an in-run comparison,
not a cross-commit disabled-path regression claim.
Run `--filter '*SlimDataQueueBenchmarks.ListRightPop*'` to reproduce.

_Machine: Linux x64 container, .NET SDK 10.0.1xx. Tables are copied from the
BenchmarkDotNet output of each step._

### Baseline (commit 0 — unmodified code)

Measurement environment:

```
BenchmarkDotNet v0.14.0, Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.110 — Job=ShortRun, Toolchain=InProcess, IterationCount=3
```

#### DeploymentsSnapshotBenchmarks (theme 1)

| Method          | Mean     | Allocated |
|---------------- |---------:|----------:|
| ReadDeployments | 84.52 ns |     376 B |
| SearchFunction  | 88.48 ns |     376 B |
| ReadJobs        | 82.76 ns |     240 B |

#### QueueCountBenchmarks (theme 2, in-run A/B)

| Method               | QueueDepth | Mean         | Allocated | Alloc Ratio |
|--------------------- |----------- |-------------:|----------:|------------:|
| MaterializeThenCount | 50         |   9,517.2 ns |  108656 B |       1.000 |
| CountOnly            | 50         |     408.4 ns |     848 B |       0.008 |
| MaterializeThenCount | 500        | 109,454.3 ns | 1084257 B |       1.000 |
| CountOnly            | 500        |   3,888.6 ns |    8048 B |       0.007 |

The production path currently uses the `MaterializeThenCount` strategy (every message
body is copied only for the caller to read `.Count`).

#### HttpHotPathBenchmarks (theme 3)

| Method                      | Mean       | Allocated |
|---------------------------- |-----------:|----------:|
| PortCheck_WithArrays        |  34.265 ns |     152 B |
| PortCheck_AllocationFree    |   1.401 ns |         - |
| ResolveVisibility_PathRules | 301.730 ns |     528 B |
| ProxyAcquireReleaseSync     | 581.602 ns |     320 B |

The global middleware currently uses the `PortCheck_WithArrays` variant.

#### SchedulingBenchmarks (theme 4)

| Method                    | Mean      | Allocated |
|-------------------------- |----------:|----------:|
| ScheduleLastTicks         | 21.273 µs |  46.42 KB |
| RegisterAlreadyKnownQuery |  2.159 µs |   1.29 KB |

`ScheduleLastTicks` is dominated by the `TzdbDateTimeZoneSource.ForId` resolution
repeated on every evaluation; called for every scheduled function on every 1 s tick.

#### SlimDataStateBenchmarks (theme 5)

| Method       | Mean     | Allocated |
|------------- |---------:|----------:|
| PayloadBytes | 43.45 µs |   3.92 KB |

`PersistAsync` currently evaluates `PayloadBytes` 3 times per Raft snapshot
(≈ 87 µs of avoidable state walking per snapshot on this test state).

---

### Commit "perf: return atomic snapshots instead of deep-copying on every access" (theme 1)

Non-regression tests: `SnapshotAccessRegressionTests` (+ existing
ReplicasService/JobService/Proxy suites — 64 tests green).

| Method          | Before (baseline)  | After              | Gain |
|---------------- |-------------------:|-------------------:|------|
| ReadDeployments | 84.52 ns / 376 B   |  0.95 ns / 0 B     | ~89× faster, zero allocation |
| SearchFunction  | 88.48 ns / 376 B   | 12.23 ns / 0 B     | ~7× faster, zero allocation |
| ReadJobs        | 82.76 ns / 240 B   |  1.11 ns / 0 B     | ~74× faster, zero allocation |

These properties are read several times per proxied HTTP request and up to ~100×/s
by the queue workers: the allocation savings translate directly into GC pressure.
**Measured gain → commit kept.**

---

### Commit "perf: count queue elements without materializing message bodies" (theme 2)

Non-regression tests: `QueueCountRegressionTests` (+ MetricsWorker, SlimJobsWorker,
QueueDispatchState, WebSocket, StatusStream — 34 tests green).

The production path switches from the `MaterializeThenCount` strategy to
`CountOnly` (in-run A/B benchmark, 2 KB payloads):

| Strategy             | QueueDepth | Mean         | Allocated | Ratio |
|--------------------- |----------- |-------------:|----------:|------:|
| MaterializeThenCount | 50         |   9,646.9 ns |  108624 B | 1.000 |
| **CountOnly**        | 50         |     379.3 ns |     848 B | 0.008 |
| MaterializeThenCount | 500        |  96,237.3 ns | 1084224 B | 1.000 |
| **CountOnly**        | 500        |   3,756.3 ns |    8048 B | 0.007 |

That is ~25× faster and ~128× fewer allocations per count. Callers:
MetricsWorker (also went from 3 reads to 1 per function per second),
SlimJobsWorker (duplicated read removed), the SSE status cache, and
WebSocketQueuesWorker (no longer reads the store when no connection exists).
**Measured gain → commit kept.**

---

### Commit "perf: cut per-request allocations on the HTTP proxy hot path" (theme 3)

Tests: SendClient, SyncFunctionEndpoint, FunctionEndpointsHelpers, Proxy,
EventEndpoint + PerfRegression suites — 102 tests green. The uppercase visibility
rule test (behavior fix) ships with this commit.

| Method                      | Before (baseline)   | After               | Gain |
|---------------------------- |--------------------:|--------------------:|------|
| ProxyAcquireReleaseSync     | 581.60 ns / 320 B   | 531.57 ns / 280 B   | −9 % time, −40 B/request |
| ResolveVisibility_PathRules | 301.73 ns / 528 B   | 260.75 ns / 528 B   | −13 % time¹ |
| Port check (production)     | 35.42 ns / 152 B (WithArrays) | 1.32 ns / 0 B (AllocationFree) | ~27× faster, zero allocation |

¹ The test path is already lowercase, so `ToLowerInvariant` benefited from the
allocation-free .NET fast path; for mixed-case paths the allocation gain adds to
the time gain. The header copies (request/response) and the publish-event loop
(20 ms poll → `Task.WhenAll`) cannot be micro-benchmarked in isolation (private
paths tied to HttpContext); they are covered by the endpoint tests.
**Measured gain → commit kept.**

---

### Commit "perf: reduce recurring work in periodic background services" (theme 4)

Non-regression tests: `ScheduleEvaluationRegressionTests`,
`ScheduleJobBackupSkipRegressionTests` (+ AutoScaler, RequestedMetricsRegistry,
ScheduleJobBackup, ReplicasScale — 81 tests green).

| Method                    | Before (baseline)   | After             | Gain |
|-------------------------- |--------------------:|------------------:|------|
| ScheduleLastTicks         | 21.273 µs / 46.42 KB | 409.07 ns / 592 B | ~52× faster, ~80× fewer allocations |
| RegisterAlreadyKnownQuery |  2.159 µs / 1.29 KB  |  42.50 ns / 0 B   | ~51× faster, zero allocation |

`ScheduleLastTicks` is called for every scheduled function on every 1 s tick of
the ScaleReplicasWorker; `RegisterFromQuery` for every trigger every 3 s.
The schedule-jobs backup now only serializes (base64 + JSON + SHA-256) when the
incremental hash of the raw data actually changed, and the activity-sync HTTP
client is no longer created/disposed every 2 s (not micro-benchmarkable in
isolation; covered by the tests).
**Measured gain → commit kept.**

---

### Commit "perf: trim SlimDataService read-path and snapshot overheads" (theme 5)

Tests: SlimData.Tests (SlimPersistentState, CommandBatch, Snapshot — 35 green) +
SlimFaas.Tests (SlimDataBatchMode, Retry, PerfRegression — 23 green).

This theme reduces **operation counts** rather than an independently benchmarkable
path; the gain is quantified from the measured unit cost:

| Saved operation | Measured unit cost | Avoided frequency |
|---|---:|---|
| `PayloadBytes` evaluation (full state walk) | 41–43 µs (test state: 200 KV + 400 hashset fields + 500 queue elements) | 2 out of 3 evaluations removed **per Raft snapshot** (+ 2 `GC.GetTotalMemory` probes when Information logging is disabled) |
| `GetRequiredService<SlimPersistentState>()` DI resolution | — | 1 per store read → 1 per instance |
| `HttpClient` created/disposed | — | 1 per batch flush (up to 1 every ~3-5 ms under load) → 1 per instance |
| `[1,1,1]` array | 48 B | 1 per store read → 0 (shared static array) |

The unit cost of `PayloadBytes` is unchanged after the commit (41.35 µs vs the
43.45 µs baseline — within the variance): it is the **number of evaluations** that
drops from 3 to 1 per snapshot, i.e. ≈ 85 µs of state walking saved per snapshot on
the test state (more on larger real states).
**Measured gain (operation count reduction × unit cost) → commit kept.**

---

### Commit "perf: single-pass queue dequeue and O(N + M) callback application" (theme 6)

Follow-up of the "SlimData queue data structure" item listed as future work in PR #313
(branch `claude/slimdata-queue`). Benchmarks and non-regression tests were added in a
preceding commit and measured on the unmodified code.

Tests: SlimData.Tests **194 green** (176 existing + 18 new in `SlimDataQueueCommandTests`,
all green before and after the change), SlimFaas.Tests **1073 green**.

Measurement environment (a different machine from the sections above — only the
before/after pairs of this section are comparable with each other):

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200), .NET SDK 10.0.302, .NET 10.0.10 X64 RyuJIT x86-64-v3
Intel Core i7-10750H CPU 2.60GHz, 1 CPU, 12 logical and 6 physical cores
Job=ShortRunInProcess  InvocationCount=16  IterationCount=20  WarmupCount=5  DOTNET_TieredCompilation=0
```

The commands mutate queue elements in place, so every invocation consumes a pristine
state from a pool rebuilt in an unmeasured `[IterationSetup]`. A pool of 16 states keeps
the working set cache-resident (with 64+ fresh states per iteration the measurement was
dominated by cache misses and ±30 % variance). Tiered compilation is disabled because
each benchmark completes in ≈100 ms — before tier-1 code gets installed — so with
tiering on, the second benchmark of a run measured tier-0 code and the third tier-1.
Run with:

```bash
DOTNET_TieredCompilation=0 dotnet run -c Release --project benchmarks/SlimFaas.Benchmarks -- --filter '*SlimDataQueue*' --iterationCount 20 --warmupCount 5
```

**Design decision — the flat `ImmutableArray<QueueElement>` per key is kept.** A dequeue
has to visit every element anyway: whether an element is running, timed out or waiting
for a retry depends on the current time, so an id index or a tree structure would not
remove the O(N) scan, would slow down iteration, and would change the snapshot format.
What was wrong were the algorithms on top of the array, not the array.

| Method | QueueDepth | Before | After | Gain |
|---|---|---:|---:|---|
| ListRightPop (10 elements, mixed-state queue) | 50 | 3.10 µs / 3.97 KB | 1.88 µs / 1.95 KB | 1.6× faster, −51 % alloc |
| ListRightPop | 500 | 43.15 µs / 15.58 KB | 12.89 µs / 8.28 KB | **3.3× faster**, −47 % alloc |
| ListCallback (50 results) | 50 | 4.15 µs / 4.93 KB | 4.26 µs / 2.81 KB | time unchanged (within variance), −43 % alloc |
| ListCallback | 500 | 19.62 µs / 21.08 KB | 17.02 µs / 9.86 KB | −13 % time, −53 % alloc |
| ListCallbackBatch (50 results) | 50 | 9.44 µs / 23.82 KB | 3.43 µs / 2.81 KB | 2.8× faster, −88 % alloc |
| ListCallbackBatch | 500 | 93.19 µs / 375.41 KB | 16.84 µs / 9.86 KB | **5.5× faster, −97 % alloc** |
| ListLeftPushBatch (10 messages) | 50 | 9.24 µs / 10.03 KB | 7.54 µs / 9.65 KB | −18 % time |
| ListLeftPushBatch | 500 | 29.74 µs / 54.46 KB | 19.25 µs / 19.58 KB | 1.5× faster, −64 % alloc |

What changed:

- `QueueElementExtensions.GetState` classifies an element (Available / Running /
  WaitingForRetry / Finished) in **one evaluation**; the historical
  `IsFinished` → `IsRunning` → `IsWaitingForRetry` chain evaluated the timeout condition
  up to six times per element. `GetQueueAvailableElement` (the SlimFaas count path of
  theme 2) now relies on it.
- `DoListRightPopAsync`: **one pass** replaces five (timeout marking, finished selection,
  an O(N × F) reference-equality filter to drop the F finished elements, transaction-id
  scan, availability selection). The kept array is only rebuilt when an element is
  evicted, candidates are capped at `Count`, and the new try is appended with a single
  `ImmutableArray.Add` instead of a builder round-trip.
- `DoListCallbackAsync` / `DoListCallbackBatchAsync` share `ApplyCallbacks`: the M
  callbacks are indexed by identifier (allocation ∝ M, duplicates chained in command
  order) and the queue is walked once — **O(N + M)**. The batch command used to do a
  linear search per callback and a full `ToBuilder()` / `ToImmutable()` copy per
  eviction (O(N × M) time, one N-sized copy per evicted element — the 375 KB above).
- `DoListLeftPushBatchAsync`: new elements are appended with one `AddRange` instead of a
  builder copy followed by a `ToImmutable()` copy; the identifier set is pre-sized.

Side effect on theme 2 (`QueueCountBenchmarks.CountOnly`, the SlimFaas count path that
calls `GetQueueAvailableElement`), same environment and settings, `--filter '*QueueCount*'`:

| Method | QueueDepth | Before | After | Gain |
|---|---|---:|---:|---|
| CountOnly | 50 | 547.2 ns / 880 B | 329.8 ns / 880 B | 1.7× faster |
| CountOnly | 500 | 5,140.8 ns / 8080 B | 3,121.6 ns / 8080 B | 1.6× faster |

**Measured gain → commit kept.**
