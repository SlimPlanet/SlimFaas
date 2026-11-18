# Managed .NET 11 microbenchmarks: complete ABBA observations

All four runs complete with valid isolation: **74 parameterized cases per run,
296 case executions**, using reference–candidate–candidate–reference order.
These measurements show both gains and costs. Seven cases have a candidate mean
above 110% of the reference in every one of the four pairings; eighteen are
below 90% in every pairing. These descriptive ShortRun results do not establish
application non-regression or statistical significance.

The reference product source is `f36084643a75d803051a6918d2950d1a4f674ada`;
the candidate is `189826231fe43fe3069e8a1f7724f83589432c6f`. Both compile
the same thirteen benchmark/support source files, whose hashes are checked again
before analysis. Both drivers are optimized Release, framework-dependent,
untrimmed managed .NET 11 ARM64 on the same Apple M4 Pro. They use
BenchmarkDotNet 0.15.8 `ShortRunInProcess` with three warmup and three measured
iterations. These are separate from the macOS Native AOT HTTP measurements.

## Observed costs

The ratio below divides the mean of the two candidate run means by the mean of
the two reference run means. The structured evidence and CSV retain the four
individual means, every pair ratio, allocations and original statistics.
The 110% screen identifies follow-up work; it is not a newly introduced
acceptance criterion for the application.

| Case | Candidate/reference time | Allocated bytes/op, reference → candidate |
|---|---:|---:|
| RetryQueue, count 200 | 1.736 | 1,656 → 80 |
| RetryQueue, count 2,000 | 1.166 | 16,056 → 80 |
| RunningQueue, count 200 | 1.292 | 2,048 → 504 |
| PayloadOwnerReferences, 1 MiB | 1.126 | 240,265 → 240,265 |
| Running, depth 200, 100% running | 1.364 | 3,248 → 1,704 |
| Running, depth 200, 25% running | 1.249 | 2,048 → 504 |
| Running, depth 2,000, 25% running | 1.202 | 20,048 → 4,104 |

Lower allocations do not erase the latency costs. Ten cases have a candidate
allocation value above the largest reference value, including small per-call
increases in queue mutations. All observations remain in the evidence.

`ListLeftPushBatch`, depth 500 with DLQ disabled, has an average time ratio of
1.602 but its four pair ratios span 0.991–2.311. This variation is retained;
the averaged result does not support a stable improvement or a precise cost.

## Observed gains and remaining validation

`FirstReadyPodPorts` has time ratios of 0.260, 0.377 and 0.532 at function
counts 2, 20 and 200, and allocation falls from 176 to zero bytes/op.
`SearchFunction` has ratios of 0.528, 0.493 and 0.566 with allocation falling
from 24 to zero. `JobCallerLookup` improves to 0.642 and 0.615 for counts 20
and 200 while allocation falls from 5,408 to 2,208 and 48,608 to 18,048 bytes/op.
These are individual microbenchmark findings, not application throughput claims.

The HTTP/async/scaling and MemoryLab macOS/Podman/kind comparisons continue
serially on the frozen source189 runtime. Their formal performance verdicts
remain open. No production change is introduced during those measurements.
Queue selection/retry materialization is a candidate for a subsequent measured
optimization; correctness and the affected application comparisons must be
replayed before accepting a new runtime.

## Evidence and reproduction

The [analysis JSON](micro-abba-189.json) retains all 296 statistical records,
the four run hosts/isolation summaries, all ratios and a SHA-256/size index of
232 original result/log files. The [CSV](micro-abba-189.csv) provides all 74
case comparisons. Original files remain under
`artifacts/pr506/measurements/micro-net11-189-{1-before,2-after,3-after,4-before}`;
a compact raw export is pending the end of the timed campaign. The index
includes each complete BenchmarkDotNet JSON, CSV, Markdown and log output.
The [analysis script](analyze-micro-abba-189.py) can independently reconstruct
these results from the retained originals; use a fresh destination for its
`net11-156` output files.

The [driver build proof](managed-micro-driver-144.json) preserves the initial
`NETSDK1151` failure and the corrected framework-dependent build. Its reference
and candidate DLL SHA-256 values are respectively
`4f5e38ffe19f9f319325e4897eff69f04451272f71609202903c675d4b0c55a3`
and `414cbf5686313ee002709dbd33ce6731e1f7d0d09ca93da10b0a569ed14b646d`.
See the [benchmark guide](../../benchmarking.md) for the matching build setup.
Run each driver serially, preserve every run, and use new output directories:

```bash
repo_root="$PWD"
source artifacts/pr506/env-net11.sh
cd "$repo_root/artifacts/pr506/builds/drivers-net11-02/micro-before"
dotnet SlimFaas.Benchmarks.dll \
  --filter '*' --exporters json csv markdown --artifacts /absolute/fresh/reference-1
cd "$repo_root/artifacts/pr506/builds/drivers-net11-189/micro-after"
dotnet SlimFaas.Benchmarks.dll \
  --filter '*' --exporters json csv markdown --artifacts /absolute/fresh/candidate-2
dotnet SlimFaas.Benchmarks.dll \
  --filter '*' --exporters json csv markdown --artifacts /absolute/fresh/candidate-3
cd "$repo_root/artifacts/pr506/builds/drivers-net11-02/micro-before"
dotnet SlimFaas.Benchmarks.dll \
  --filter '*' --exporters json csv markdown --artifacts /absolute/fresh/reference-4
```

Run from the respective driver directory, with the dedicated SDK environment,
and keep the machine free of competing builds/tests. The original execution
also retains five-second compiler/test activity samples; all four runs have
no observed competing activity. Sampling is an observation, not continuous
profiling of every process.
