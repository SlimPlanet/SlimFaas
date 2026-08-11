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
