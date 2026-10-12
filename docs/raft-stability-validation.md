# SlimData Raft stabilization validation

Tracking: [SlimFaas #402](https://github.com/SlimPlanet/SlimFaas/issues/402) and
[draft PR #403](https://github.com/SlimPlanet/SlimFaas/pull/403).

## Candidate and scope

The candidate uses the published `DotNext.AspNetCore.Cluster` and
`DotNext.Net.Cluster` 6.8.1 packages. Their core, IO, threading and unsafe
packages resolve to 6.8.0. Baseline main uses 6.6.0; the additional migration
baseline is SlimFaas 0.81.0 with DotNext 6.4.1.

These are synthetic regression and compatibility checks. They do not establish
the cause of an operational incident or prove integrity of an existing deployment.
No production logs, data, addresses or configuration are used in these fixtures.
The PR remains a draft until compatibility, performance, CI and license checks
pass. No deployment, forced bootstrap or state deletion is included.

The application command protocol and snapshot payload format are unchanged.
Snapshot restoration runs before resolving the WAL. The AppendEntries commit-index
guard remains enabled. Forward rolling-upgrade tests do not validate rollback.

## Diagnostics

The five-second sampler tracks two independent conditions:

- Local entries pending application with no progress for 30 seconds:
  `slimdata_raft_progress_stalled`.
- A missing leader or unavailable consensus, even with an empty local backlog:
  `slimdata_raft_has_leader` and
  `slimdata_raft_consensus_unavailable_duration_seconds`.

Availability logs include term and last/committed/applied indexes on transitions
and every 60 seconds while unavailable. A leader without consensus does not reset
the outage timer. Readiness polling remains at 500 ms; identical warnings share
one 60-second budget across waiters. Monotonic-clock unit tests cover recovery,
short elections, missing leader, missing consensus, reminders and concurrent waiters.

## Validation record

Host: macOS ARM64, .NET SDK 10.0.300, Node 24.

| Check | Result |
|---|---|
| Dependency regressions, live member re-addition, legacy 6.6.0 compacted WAL | 8 passed on official 6.8.1 |
| Progress, availability and readiness-warning tracking | 17 passed after removing the unreachable negative-index case |
| Readiness: leader, consensus, warmup and protocol | 6 passed |
| Full .NET suite | 1,621 passed on the final source; the preceding 1,616-test run also built both embedded UIs |
| Documentation site | 21 pages, 1,393 local links/assets, 351 search entries |
| Native AOT publication | SlimFaas and standalone SlimData pass; existing MemoryPack IL2104/IL3053 and ConfigurationManager IL2104 warnings remain unchanged |
| Native rolling upgrade from 6.4.1 and 6.6.0 | Initial runs passed. Review rerun: 6.6.0 passes; 6.4.1 passes mixed-version upgrade and short outages, then two native processes crash during prolonged outage recovery; see below |
| Native local demo | Manifest valid; `/status-functions` succeeds and `/function/fibonacci1/hello/local` returns `Hello local!` |
| Comparative throughput, p99 and memory | Controlled rerun: 24 successful runs and all-node validations; set/12 throughput -18.9% and set/48 -16.7% fail unchanged gates; all memory/p99 gates pass |
| Disposable Kubernetes Service/DNS validation | Blocked: local rootless provider lacks systemd Delegate=yes |
| CI and FOSSA | Linux unit tests, Windows SonarCloud/test job and FOSSA pass on review code c5eaea6b; final documentation-head checks remain required |

In the native experiments, a pending write resumes 1.635 seconds after quorum
returns for the 6.6.0 baseline and 4.046 seconds for the 6.4.1 baseline, within
the unchanged 30-second bound. The prolonged idle outage produces a leader
gauge of zero and more than 65 seconds of unavailability with equal last,
committed and applied indexes; the local progress-stall gauge remains zero.

The Kubernetes example separates the governing discovery Service (headless,
publishes unready peers) from the application Service (filters unready peers).
It pins Raft DNS explicitly, preserving membership identities when another Service
selects the same pods. Probe and Service behavior still requires validation in a
disposable Kubernetes cluster before operational adoption. No host configuration
was changed to work around the local container-provider limitation.

## Previous performance screening (not conclusive)

The comparison uses native main (`cc532ffe`, DotNext 6.6.0) and the 6.8.1
candidate, two repetitions per scenario, 5-second warm-up and 30-second measured
load, alternating execution order. Other host workloads were subsequently
reported during this matrix, so these results cannot isolate a candidate
regression. The old harness also counted the state-verification tail as load in
memory reports and CPU deltas. These are workstation screening results, not
production sizing. All 16 runs complete: **452,275 measured operations and zero
errors**. The mixed scenarios checked sets/hashsets/counters on every node;
set-only runs did not yet include a final all-node state check.

| Scenario / concurrency | Baseline ops/s | Candidate ops/s | Throughput change | p99 change | Peak RSS change | Result |
|---|---:|---:|---:|---:|---:|---|
| Mixed / 12 | 197.69 | 198.81 | +0.6% | -0.5% | -15.6% | Pass |
| Mixed / 48 | 3131.23 | 3124.70 | -0.2% | +2.4% | -0.6% | Pass |
| Set / 12 | 70.84 | 123.91 | +74.9% | -3.9% | +42.8% | Fail: RSS |
| Set / 48 | 403.19 | 281.52 | -30.2% | +0.6% | +4.9% | Fail: throughput |

The unchanged gates require throughput >=90% of baseline, p99 <=120%, and peak
RSS <=115%. The historical script verdict was **DO NOT ADOPT**, but these
measurements are not conclusive evidence of a regression or a memory leak.
A fresh controlled comparison is required before adopting this candidate. The thresholds are not
relaxed and no durability setting is disabled to obtain a passing result.

## Controlled performance rerun

The review rerun compares the same native main baseline (`cc532ffe`, DotNext
6.6.0) with runtime source `4a326c7a` (6.8.1). Later review changes affect only
cluster tests and this record. Both variants use the same corrected harness:
three repetitions, 5-second warm-up, 30-second measured load, 5-second cooldown,
and alternating execution order. No other build, test or demo was run locally
during the matrix. These remain workstation measurements, not an isolated-host
or production benchmark.

All **24 runs pass, with 665,645 completed operations, zero request errors and
successful final state checks on all three members in every run**. Verification
now covers set-only scenarios too and is excluded from load RSS/CPU accounting.

| Scenario / concurrency | Baseline ops/s | Candidate ops/s | Throughput change | p99 change | Peak RSS change | Result |
|---|---:|---:|---:|---:|---:|---|
| Mixed / 12 | 200.37 | 199.62 | -0.4% | -0.8% | +1.1% | Pass |
| Mixed / 48 | 3045.19 | 3064.80 | +0.6% | +11.4% | -0.8% | Pass |
| Set / 12 | 91.46 | 74.21 | -18.9% | +6.7% | -10.8% | Fail: throughput |
| Set / 48 | 330.66 | 275.30 | -16.7% | +4.2% | +2.9% | Fail: throughput |

The previous +42.8% memory result is not reproduced. All current memory and p99
gates pass, but both set-only throughput medians remain below 90% of baseline.
Individual set-only runs vary substantially: at concurrency 48, the baseline
ranges from 272.36 to 513.25 ops/s and the candidate from 272.53 to 478.41 ops/s.
This is not sufficient to attribute the throughput difference to DotNext alone;
the acceptance result nevertheless remains **FAIL / DO NOT ADOPT**, without
changing thresholds or selecting favourable repetitions.

Across 38 host observations, CPU idle ranges from 14.61% to 74.13% (median 53.55%).
The system records no additional swap-outs during the observations; swap-ins do
occur. This context is retained rather than claiming complete host isolation.

Reproduce after publishing both variants with identical AOT options:

```bash
BEFORE_REF=cc532ffe \
BEFORE_PUBLISH_DIR=/path/to/native-baseline \
AFTER_PUBLISH_DIR=/path/to/native-candidate \
BENCHMARK_SKIP_AFTER_PUBLISH=1 \
BENCHMARK_RUN_ROOT=/path/to/fresh-comparison \
WARMUP_SECONDS=5 DURATION_SECONDS=30 REPETITIONS=3 COOLDOWN_SECONDS=5 \
  .bin/slimdata-benchmark.sh
```

## Review follow-up

The review against the earlier 6.7.2 candidate is addressed as follows:

- Keep 6.8.1 and the live member re-addition assertions. Legacy WAL tests remain
  enabled on all page sizes; the fixture now awaits its committed suffix.
- Exercise the 6.8.1 dependency-injection guard that rejects WAL resolution before
  restoration. Standalone startup disposes the host once, including restore failure.
- Bound each native-harness verification phase to 60 seconds, cap individual
  reads at five seconds, and distinguish a rejected write from an unsafe minority
  acknowledgement. Deterministic Python tests cover these failure paths in CI.
- Share the worker's monotonic sample, previous applied index and rewind decision
  with the progress detector. Sample batch statistics and Raft indexes once and
  use `CommittedLogIndex`/`AppliedLogIndex` consistently. Both transition signatures
  allow an unknown applied index when observation is lost.
- Keep the fixed diagnostic threshold in one code constant. No new tuning option
  or broader dependency upgrade is introduced. The minimum logging-abstractions
  pin follows the repository-wide central-version policy and also affects the
  published client; a client-specific override would violate that policy.
- Give private DotNext reflection bindings explicit failure messages, so a changed
  upstream API can be distinguished from a failed Raft behaviour assertion.
- Sort expected values once and validate members concurrently, including set-only
  scenarios. A separate validation phase excludes that work from measured RSS
  and captures CPU counters before verification. CLI regression tests check that
  a synthetic validation-memory spike does not change the reported load peak.
- Add elapsed phase diagnostics to the Windows-sensitive cluster test, bound its
  initial election, and stop only surviving hosts concurrently after failover.
  The 120-second test timeout and all membership assertions remain unchanged.

The updated .NET suite passes 1,621 tests locally; six Python/CLI regression
checks, both native AOT builds, the native local demo and the documentation build
pass. Linux CI and FOSSA pass. The Windows rerun now identifies a stale-leader
`ForceReplicationAsync` call after a successful deduplication replay; membership
removal/re-addition completes successfully. That redundant call is removed while
retaining bounded application checks on every survivor. Linux and Windows CI
both pass on that follow-up (`c5eaea6b`).

The native review rerun from 6.6.0 passes all phases. The 6.4.1 rerun verifies all
185 expected values after upgrade and both short quorum interruptions, but two
candidate processes exit with `ArgumentException` in `IPEndPoint.Create` /
`SocketAsyncEventArgs.FinishOperationSyncSuccess` when recovering from the prolonged
idle outage. Recovery fails, so this run is **not accepted**, despite the earlier
passing run. State and logs are retained; no retries conceal the failure. A similar
macOS accept-path failure is tracked in [dotnet/runtime #121848](https://github.com/dotnet/runtime/issues/121848);
matching exception signatures are a lead, not proof of the cause of this run.
A small socket control, run after the benchmark without SlimFaas or DotNext,
also observes missing peer-address metadata when accepting a reset connection on
an IPv6 listener, while the IPv4 listener returns that metadata. This supports
investigating the host/runtime path; a passing fault-recovery qualification is
still required.

All 14 inline review findings have been addressed with fixes or explicit scope
rationale. The PR remains a draft while throughput, native fault recovery,
Kubernetes validation or another acceptance gate is open.

## Reproduction

```bash
dotnet test -p:SkipClientAppBuild=true --filter FullyQualifiedName~SlimData
dotnet test

dotnet publish src/SlimFaas/SlimFaas.csproj -c Release -r osx-arm64 \
  -p:SkipClientAppBuild=true -o artifacts/raft-candidate
dotnet publish src/SlimData/SlimData.csproj -c Release -r osx-arm64

# Publish the historical versions in separate checkouts. Use a fresh output
# directory for each run. Only processes owned by this harness are signalled.
python3 .bin/test-slimdata-raft-recovery.py \
  --baseline /path/to/baseline/SlimFaas \
  --candidate artifacts/raft-candidate/SlimFaas \
  --output artifacts/raft-upgrade-unique-run
```

The first 6.4.1 experiment stopped on an immediate follower read returning 404,
before any upgrade. The historical version exposes eventual local reads. The
harness now bounds catch-up for missing values, records these observations and
still fails wrong values or other HTTP errors; writes are never retried. The
completed 6.4.1 run records two initially missing read observations in the
baseline and two while an old member remains. All later phases record zero.
The 6.6.0 experiment passes with immediate reads as well.

The native harness writes 180 values, crosses snapshot boundaries, checks every
value on every node, replaces followers before the leader, pauses one and two
followers, and restarts the complete cluster using the same state directories.
It also holds an idle quorum outage long enough to observe the 60-second reminder,
verifies `/health` remains 200 while `/ready` is 503, and waits for the availability
duration to reset after recovery. Process pauses model unresponsive peers; they do
not cover every asymmetric network partition.

## Dependencies and release gates

The changed DotNext packages and Microsoft runtime dependencies declare MIT.
`Microsoft.Extensions.Logging.Abstractions` remains centrally pinned at 10.0.12
to meet the new dependency minimum. Restore audits direct and transitive packages;
the final PR must also pass FOSSA's distribution-level license check. No license
exception or compiler-warning suppression is introduced.

Earlier experiments with 6.7.2 and private patched packages are historical, not
acceptance evidence for 6.8.1. In particular, prior memory-gate failures must not
be treated as resolved without a new comparison. Keep the existing benchmark
thresholds unchanged. Update this record with final results and keep the PR a
draft while any acceptance gate remains open.
