# SlimFaas performance benchmarks

This document tracks the methodology and results of the micro-benchmarks used to
validate the performance commits of the `claude/performance-improvements-9h83ah` branch.

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

## Results

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
