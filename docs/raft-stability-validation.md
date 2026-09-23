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
| Progress, availability and readiness-warning tracking | 18 passed |
| Readiness: leader, consensus, warmup and protocol | 6 passed |
| Full .NET suite | 1,621 passed on the final source; the preceding 1,616-test run also built both embedded UIs |
| Documentation site | 21 pages, 1,393 local links/assets, 351 search entries |
| Native AOT publication | SlimFaas and standalone SlimData pass; existing MemoryPack IL2104/IL3053 and ConfigurationManager IL2104 warnings remain unchanged |
| Native rolling upgrade from 6.4.1 and 6.6.0 | Both pass; 187 final values verified on each of three nodes after full restart |
| Native local demo | Manifest valid; `/status-functions` succeeds and `/function/fibonacci1/hello/local` returns `Hello local!` |
| Comparative throughput, p99 and memory | FAIL: set/12 peak RSS +42.8%; set/48 throughput -30.2%; all 16 runs have zero request errors and pass state validation |
| Disposable Kubernetes Service/DNS validation | Blocked: local rootless provider lacks systemd Delegate=yes |
| CI and FOSSA | Unit tests and FOSSA passed on the first implementation commit; final-head checks remain required |

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

## Performance acceptance remains open

The comparison uses native main (`cc532ffe`, DotNext 6.6.0) and the 6.8.1
candidate, two repetitions per scenario, 5-second warm-up and 30-second measured
load, alternating execution order. No other local build, test or demo ran during
this matrix. These are workstation screening results, not production sizing.
All 16 runs complete: **452,275 measured operations, zero errors, and successful
set/hashset/counter checks on every node**.

| Scenario / concurrency | Baseline ops/s | Candidate ops/s | Throughput change | p99 change | Peak RSS change | Result |
|---|---:|---:|---:|---:|---:|---|
| Mixed / 12 | 197.69 | 198.81 | +0.6% | -0.5% | -15.6% | Pass |
| Mixed / 48 | 3131.23 | 3124.70 | -0.2% | +2.4% | -0.6% | Pass |
| Set / 12 | 70.84 | 123.91 | +74.9% | -3.9% | +42.8% | Fail: RSS |
| Set / 48 | 403.19 | 281.52 | -30.2% | +0.6% | +4.9% | Fail: throughput |

The unchanged gates require throughput >=90% of baseline, p99 <=120%, and peak
RSS <=115%. The overall verdict is **DO NOT ADOPT**. The measurements do not
establish a memory leak or isolate a dependency defect; further controlled
profiling is required before adopting this candidate. The thresholds are not
relaxed and no durability setting is disabled to obtain a passing result.

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
