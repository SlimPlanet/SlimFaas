# SlimFaas cross-version performance benchmark (July → September 2026)

This document is the objective before/after measurement of the performance work merged
into SlimFaas between v0.74.0 (2026-07-09, the last release before the two-month window)
and v0.84.6 (2026-09-12). Unlike [performance-benchmarks.md](performance-benchmarks.md),
which records the numbers reported by each performance pull request on the machine of its
author, every number below was produced **in one session, on one machine, by one benchmark
harness, against two builds of SlimFaas that differ only by their git commit**.

## What changed in the window

Performance-related commits between v0.74.0 and v0.84.6 (`git log 043a686..f1d97a8`):

| Date | PR | Area | Summary |
|---|---|---|---|
| 2026-07-17 | [#290](https://github.com/SlimPlanet/SlimFaas/pull/290) | SlimData / RAM | Raft memory diagnostics, snapshot and health tuning |
| 2026-07-23 | [#298](https://github.com/SlimPlanet/SlimFaas/pull/298) | Metrics / RAM | Bounded in-memory metrics store, PromQL evaluator memory limits |
| 2026-07-24 | [#299](https://github.com/SlimPlanet/SlimFaas/pull/299) | Async / RAM | Large async bodies offloaded to cluster files, queue worker memory |
| 2026-07-25 | [#300](https://github.com/SlimPlanet/SlimFaas/pull/300) | Memory leak | PromQL series rewrite, port and options lifetime fixes |
| 2026-07-26 | [#302](https://github.com/SlimPlanet/SlimFaas/pull/302) + `a0d4469` | SlimData | Command batching, multi-rate adaptive batcher, low-latency mode |
| 2026-08-02 | [#309](https://github.com/SlimPlanet/SlimFaas/pull/309) | Autoscaling | Reactive PromQL scaling, stabilization from the last high recommendation |
| 2026-08-04 | [#310](https://github.com/SlimPlanet/SlimFaas/pull/310) | Sync proxy | Sync request path optimization + native-local latency benchmark driver |
| 2026-08-07 | [#311](https://github.com/SlimPlanet/SlimFaas/pull/311) | Async | Queue worker rewrite (signal-driven dispatch instead of polling), async benchmark profile |
| 2026-08-14 | [#313](https://github.com/SlimPlanet/SlimFaas/pull/313) | Hot paths | Zero-allocation snapshot reads, count-only queue counts, per-request allocations, schedule / PromQL caches, snapshot overheads |
| 2026-08-21 | [#317](https://github.com/SlimPlanet/SlimFaas/pull/317) | SlimData | Raft recovery under load, per-request route prefix allocations |
| 2026-09-02 | [#329](https://github.com/SlimPlanet/SlimFaas/pull/329) | SlimData | Linearizable key-value reads (correctness; adds a read barrier) |
| 2026-09-10 | [#340](https://github.com/SlimPlanet/SlimFaas/pull/340) | Kubernetes | Watch-driven synchronization, jobs LIST N+1 fix |
| 2026-09-12 | [#343](https://github.com/SlimPlanet/SlimFaas/pull/343) | SlimData | Single-pass queue dequeue, O(N + M) callbacks, push without double copy |

## Methodology

### Tooling

`benchmarks/compare-versions.sh` measures two git refs with two tiers:

- **Micro tier** — the BenchmarkDotNet suite of the current checkout
  (`benchmarks/SlimFaas.Benchmarks`) is compiled twice, against a git worktree of the
  baseline and against the candidate (`-p:SlimFaasSourceRoot=<worktree>/`). The same
  benchmark code therefore measures both commits. Checkouts older than PR #313 are compiled
  with `-p:BaselineApi=true`, which removes the one benchmark whose subject did not exist
  yet (`SlimDataStateSnapshot.PayloadBytes`) and adapts three signatures that changed since
  (`DeploymentsInformations`, `JobService`, `IJobConfiguration`); the harness also adds an
  `InternalsVisibleTo("SlimFaas.Benchmarks")` entry to the baseline worktree, which changes
  no behavior. Runs use `DOTNET_TieredCompilation=0` so every benchmark measures fully
  optimized code, 3 warm-up and 10 measured iterations, `MemoryDiagnoser` for exact
  allocation counts. `benchmarks/compare-microbenchmarks.py` merges the two brief JSON
  exports into the tables below.
- **End-to-end tier** — the three-node native-local cluster of each commit is started with
  the current checkout's manifest (`benchmarks/slimfaas.local.benchmark.yaml`), benchmark
  target and load driver (`src/SlimFaasBenchmark`, see [benchmarking.md](benchmarking.md)).
  Only the `SlimFaas.dll` under test changes between the two runs; the driver, the target
  process, the ports and the matrix are identical. `SlimFaasBenchmark compare` produces the
  before/after tables and applies the acceptance rules documented in benchmarking.md.

```bash
# micro tier, July code vs working tree
benchmarks/compare-versions.sh --baseline v0.74.0 --skip-e2e
# end-to-end tier (sync + async + scaling, then the async-queue profile)
benchmarks/compare-versions.sh --baseline v0.79.2 --skip-micro --profile standard
benchmarks/compare-versions.sh --baseline v0.79.2 --skip-micro --profile async-queue
```

Every run writes a `manifest.txt` (commits, dirty flags, SDK, host, matrix) next to its
results under `artifacts/perf-compare/`.

### Baselines

- **Micro tier: v0.74.0 (`043a686`, 2026-07-09)** — the last release before the window;
  every benchmarked API already exists there.
- **End-to-end tier: v0.79.2 (`b004682`, 2026-07-31)** — the first release whose native
  local mode can run the benchmark manifest (local mode itself was introduced on
  2026-07-28 by #303/#304 and the auxiliary-process support the manifest needs on
  2026-07-30). Before that the only orchestrators were Kubernetes and Docker, which the
  driver cannot start on a bare host. The end-to-end tier therefore covers #309, #310,
  #311, #313, #317, #329, #340 (its non-Kubernetes fallback) and #343, but **not** the
  July memory and batching work (#290–#302); those are covered only where a micro
  benchmark reaches them.
- **Candidate: v0.84.6 (`f1d97a8`, 2026-09-12)**, built from this checkout (the benchmark
  files added by this document do not change the SlimFaas binaries).

### Environment

<!-- ENVIRONMENT -->

Shared, virtualized CPUs: absolute numbers are not publishable, and the end-to-end matrix
runs the three SlimFaas nodes, the target and the driver on the same four cores. Only the
before/after pairs of this document are comparable with each other. Allocation counts are
exact; timings carry a few percent of noise (the BenchmarkDotNet standard deviations are in
the raw reports).

## Results

<!-- RESULTS -->

## Reproducing

```bash
git fetch --tags origin                       # release tags; a shallow clone also needs --unshallow
benchmarks/compare-versions.sh --baseline v0.74.0 --skip-e2e --output artifacts/perf-compare/micro
benchmarks/compare-versions.sh --baseline v0.79.2 --skip-micro --profile standard --output artifacts/perf-compare/e2e-standard
benchmarks/compare-versions.sh --baseline v0.79.2 --skip-micro --profile async-queue --output artifacts/perf-compare/e2e-async
```

Any pair of refs works (`--candidate <ref>` defaults to the working tree, so an
uncommitted optimization can be measured against `HEAD` or against a release). The
`--profile quick` end-to-end matrix takes a few minutes and is meant as a smoke test; keep
the standard matrix and compare medians for a publishable before/after.
