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

```
Linux x86_64 container, 4 vCPU (Intel Xeon @ 2.10GHz), 15 GB RAM, 20 000 open files (hard limit)
.NET SDK 10.0.112, Release builds without the dashboard (-p:SkipClientAppBuild=true)
2026-09-12, one session: v0.79.2 (b004682) and v0.84.6 (f1d97a8 + this document's benchmark files) for the end-to-end tier,
v0.74.0 (043a686) and v0.84.6 for the micro tier
```

Shared, virtualized CPUs: absolute numbers are not publishable, and the end-to-end matrix
runs the three SlimFaas nodes, the target and the driver on the same four cores. Only the
before/after pairs of this document are comparable with each other. Allocation counts are
exact; timings carry a few percent of noise (the BenchmarkDotNet standard deviations are in
the raw reports).

## Results

### End-to-end, standard profile (v0.79.2 → v0.84.6)

`--profile standard`, one repetition per case (10 s measured after 2 s warm-up, payloads
64 B / 4 KiB / 256 KiB / 2 MiB, closed-loop concurrency 1 and 16, scaling burst of 200
messages at concurrency 32). One repetition instead of three because v0.79.2 cannot
sustain the three-repetition matrix on this host (see *Resource usage* below). Raw
artifacts: `artifacts/perf-compare/e2e-standard-v0.79.2/`.

#### Synchronous proxy overhead (same target process called directly and through SlimFaas)

| payload | concurrency | added p50 v0.79.2 | added p50 v0.84.6 | reduction | throughput v0.79.2 | throughput v0.84.6 |
|---:|---:|---:|---:|---:|---:|---:|
| 64 B | 1 | 0.390 ms | 0.374 ms | −4 % | 1 875/s | 1 968/s |
| 64 B | 16 | 1.149 ms | 0.995 ms | −13 % | 10 799/s | 12 273/s |
| 4 KiB | 1 | 0.585 ms | 0.473 ms | −19 % | 1 183/s | 1 266/s |
| 4 KiB | 16 | 0.922 ms | 0.863 ms | −6 % | 7 070/s | 7 068/s |
| 256 KiB | 1 | 2.346 ms | 1.189 ms | **−49 %** | 91/s | 88/s |
| 256 KiB | 16 | 6.180 ms | 2.068 ms | **−67 %** | 1 593/s | 2 561/s (+61 %) |
| 2 MiB | 1 | 9.510 ms | 2.513 ms | **−74 %** | 60/s | 85/s (+42 %) |
| 2 MiB | 16 | 40.590 ms | 17.262 ms | **−57 %** | 318/s | 593/s (+86 %) |

The added p95 / p99 and throughput guardrails of `SlimFaasBenchmark compare` pass on
every case. The proxy overhead for bodies of 256 KiB and above is divided by 2 to 4; for
small bodies the gain is within 4–19 % (the median small-payload reduction, 9.8 %, is
below the 20 % the sync acceptance rule asks for, so the sync verdict of the comparison
is *FAIL* — the rule was written for a single sync optimization, not for a release
comparison, and is reported here as is).

#### Asynchronous ingress and delivery (HTTP 202 latency, and client send → handler start)

| payload | concurrency | HTTP p95 v0.79.2 | HTTP p95 v0.84.6 | arrival p95 v0.79.2 | arrival p95 v0.84.6 | messages/s v0.79.2 | messages/s v0.84.6 |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 64 B | 1 | 237.4 ms | 13.6 ms | 614.3 ms | 44.0 ms | 12.5 | 87 (×6.9) |
| 64 B | 16 | 240.1 ms | 17.5 ms | 703.9 ms | 51.1 ms | 68.8 | 1 221 (×17.7) |
| 4 KiB | 1 | 239.3 ms | 19.5 ms | 476.1 ms | 47.4 ms | 6.4 | 70 (×10.9) |
| 4 KiB | 16 | 244.0 ms | 27.0 ms | 715.0 ms | 70.1 ms | 68.3 | 901 (×13.2) |
| 256 KiB | 1 | 253.5 ms | 73.8 ms | 545.8 ms | 184.3 ms | 12.6 | 20 (×1.6) |
| 256 KiB | 16 | 762.0 ms | 2 279.5 ms | 10 814.2 ms | 5 578.8 ms | 19.8 (5 failed) | 31 (×1.6, 0 failed) |
| 2 MiB | 1 | 481.9 ms | 72.4 ms | 726.2 ms | 86.4 ms | 3.3 | 28 (×8.3) |
| 2 MiB | 16 | 485.7 ms | 146.5 ms | 912.7 ms | 386.7 ms | 33.3 | 154 (×4.6) |

No message was lost or duplicated by either version. v0.79.2 dispatched the queue on a
polling fallback: its HTTP-202 and arrival latencies cluster around 240 ms and 470–710 ms
whatever the load. v0.84.6 (#311 signal-driven dispatch, #313, #343) accepts and
delivers in tens of milliseconds and sustains 7 to 18 times more messages per second for
bodies up to 4 KiB. The 256 KiB / concurrency 16 case is the one where both versions
saturate the host (see *Resource usage*): v0.79.2 failed 5 of 380 messages there,
v0.84.6 failed none but its 202 p95 is worse than the baseline's on this single
repetition.

#### Scaling burst (scale-to-zero → PromQL scale-out, 200 messages, 4 replicas)

| milestone from first send | v0.79.2 | v0.84.6 |
|---|---:|---:|
| first ready replica | 3.06 s | 4.21 s |
| 4 ready replicas | 8.72 s | 19.11 s |
| queue drained | 33.42 s | 29.62 s |

One observation each, on a host that was already saturated by the previous async cases:
the drain time (the user-visible end of the burst) improved, the time to 4 ready
replicas doubled. The scale-out policy changed in the window (#309 reactive PromQL
scaling with stabilization, #350 scale-down budgets), so this milestone deserves a
dedicated multi-repetition run before drawing a conclusion; it is reported, not
interpreted.

#### Resource usage of the SlimFaas nodes during the run

Sampled every 5 s from `/proc` (`resources-summary.md` of each run):

| | v0.79.2 | v0.84.6 |
|---|---:|---:|
| peak open file descriptors (any node) | **19 999 (limit hit)** | 19 168 |
| descriptors at the end of the run | 375 / 332 / 19 999 | 535 / 440 / 453 |
| peak resident memory (any node) | 646 MB | 657 MB |
| async messages failed | 5 | 0 |

The descriptor count of both versions climbs from ~300 to ~19 000 while the 256 KiB and
2 MiB async cases run and falls back afterwards; the errors logged by v0.79.2 name Raft
write-ahead-log files (`wal/data/<entry>`), so the growth follows the log. v0.79.2
crossed the 20 000 hard limit of the container and one node stayed at the limit until
the end (`Too many open files` on `wal/data/...`); with the three-repetition matrix the
same node exhausted its descriptors after ≈ 5 300 messages and the run had to be aborted
(`artifacts/perf-compare/aborted-e2e-async-full-matrix-v0.79.2/`). In this run v0.84.6 stayed 4 %
below the limit and released the descriptors afterwards; in the async-queue run below the
roles are reversed. **Large-body async bursts need a generous `ulimit -n` (or a bounded
number of open write-ahead-log files) on both versions.** This is the one finding of this
review that is not an improvement.

### End-to-end, async-queue profile (v0.79.2 → v0.84.6)

`--profile async-queue`, one repetition: the same latency matrix plus a paced scenario
(100 messages, one every 100 ms — how fast an almost idle queue wakes up) and a burst
(1 000 messages at concurrency 64 — coalescing and back-pressure). Raw artifacts:
`artifacts/perf-compare/e2e-async-v0.79.2/`.

| scenario | v0.79.2 | v0.84.6 | change |
|---|---:|---:|---:|
| paced — accepted messages/s | 4.9 | 9.1 | ×1.9 (the driver is the bottleneck at 10/s) |
| paced — HTTP 202 p95 | 152.0 ms | 15.2 ms | **−90 %** |
| paced — arrival p95 (send → handler start) | 614.4 ms | 25.7 ms | **−96 %** |
| burst — messages/s | 275.7 | 1 362.9 | **×4.9** |
| burst — HTTP 202 p95 | 276.4 ms | 58.2 ms | −79 % |
| burst — arrival p95 | 781.7 ms | 168.1 ms | −78 % |
| lost / duplicated messages (all scenarios) | 0 / 0 | 0 / 0 | — |

The async acceptance rules of `SlimFaasBenchmark compare --profile async` pass on the
latency reductions (median HTTP p95 −81.5 %, median arrival p95 −89.3 %, 2 MiB HTTP p50
−88.6 %) and fail on two guardrails, both explained by the host rather than the code:

- *256 KiB at concurrency 16*: −31.5 % messages/s and a 6.1 s arrival p95 for v0.84.6.
  This is the case where the candidate's nodes hit the 20 000-descriptor limit in this
  run (98 `Too many open files` errors on the Raft write-ahead log, see *Resource usage*
  below); in the standard-profile run above the same case had gone the other way.
- *Raft entries per message ×2.26*: v0.84.6 batches more commands per message on the
  paced scenario (3.6 vs 1.6 Raft entries per message) while spending 78 % less CPU per
  message overall; the ×2 guardrail was written for a single async optimization.

The sync matrix of this run confirms the standard-profile numbers (added p50 −8 to
−22 % for 64 B–4 KiB, −49 to −81 % for 256 KiB–2 MiB).

Resource usage in this run: v0.79.2 peaked at 16 161 descriptors (no error), v0.84.6 at
19 999 (98 errors, no lost message). Over the two end-to-end runs, each version crossed
the limit once and stayed under it once: **the descriptor growth of the write-ahead log
under large-body async load is a property of both versions, not something the window
fixed or broke.**

<!-- RESULTS-ASYNC -->

<!-- RESULTS-MICRO -->


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
