# SlimData Raft stabilization validation

Tracking: [SlimFaas #402](https://github.com/SlimPlanet/SlimFaas/issues/402).
SlimFaas PR: [#403](https://github.com/SlimPlanet/SlimFaas/pull/403).
Upstream issue and fixes: [DotNext #299](https://github.com/dotnet/dotNext/issues/299),
[DotNext #300](https://github.com/dotnet/dotNext/pull/300).
Status: **draft, awaiting a published corrected dependency and validation gates; no rollout or merge**.

## Scope and incident evidence

Smartguide staging used SlimFaas 0.84.8 or 0.84.9. Pods remained alive while the
service stopped progressing during normal operation; incident logs are unavailable.
No recent commit has been established as the incident's cause.

Before this change, 210 SlimData tests passed. Three native three-node mixed-load
runs processed 520,257 measured operations with no reported failures, including
temporary follower pauses. Their final state validations passed. Those original
checks rotated nodes; the updated memory lab checks **every expected set, hashset
and counter on every node**.

The candidate upgrades DotNext 6.6.0 to 6.7.2 and adds progress diagnostics. It
preserves application command IDs, serialization, protocol and the AppendEntries
commit-index guard. Host startup now awaits snapshot restoration before resolving
the WAL or starting hosted services.

## Confirmed upstream fixes

`DotNextRaftRegressionTests` exercise the installed dependency rather than copying
its algorithms. The replication test fills a capacity-one queue without starting
the consumer; the second replication barrier must complete as unavailable. The
election tests use actual persisted log terms, including a shorter candidate log
whose last term is newer.

| Dependency | Full replication queue | Newer-term, shorter candidate | Other election cases |
|---|---|---|---|
| 6.6.0 | Fails: barrier remains unresolved | Fails: candidate rejected | 4 pass |
| 6.7.2 | Pass | Pass | 4 pass |

Upstream references: [queue fix](https://github.com/dotnet/dotNext/commit/bedd8b5eae5d88d89e802d127c862e2269754143),
[election fix](https://github.com/dotnet/dotNext/pull/298).

## Release blockers found during validation

### Re-adding a removed live member

The existing `RaftClusterTests.MessageExchange` passes on the unmodified 6.6.0
baseline but repeatedly fails with 6.7.2. Addition, replication and removal work;
after re-addition, the third member stays outside the applied configuration.
Incoming AppendEntries requests return HTTP 500 with `QuorumUnreachableException`.

The upstream `Leader` setter reads `electionEvent.Task.Result` after the task has
been faulted when the local member was removed. The exception prevents processing
the later replication request. The existing test remains enabled and unchanged
in its assertions. The candidate must pass this test before review readiness.

### WAL metadata page size on macOS ARM64

A rolling 6.6.0 → 6.7.2 experiment verified 180 sets on each node, then restarted
one follower with the candidate and the same state directory. Startup failed with
`WriteAheadLog.InternalException: WAL page 0 doesn't exist on the disk`.

DotNext changes metadata page sizing from 4096 to
`max(4096, Environment.SystemPageSize)`. On this host the system page size is
16384, so a persisted metadata index now maps to a different file. Restoring the
snapshot first is necessary but does not correct this separate incompatibility.
See the pinned [WAL constructor](https://github.com/dotnet/dotNext/blob/50fe09e16d66526a080e7f7fba14deedd0d4c638/src/cluster/DotNext.Net.Cluster/Net/Cluster/Consensus/Raft/StateMachine/WriteAheadLog.cs).

The compact [6.6.0 fixture](../tests/SlimData.Tests/Snapshots/README.md) preserves
the reproduction. Its restoration test passes on 6.6.0 and fails on 6.7.2 on this
16 KiB-page host. No state migration, deletion or restart workaround is applied
automatically. The original published 6.7.2 package cannot complete this upgrade on the affected host.

### Legacy Raft HTTP headers

After the two preceding fixes, native mixed-version validation exposed a third
blocker: new followers reject 6.6.0 requests without `X-Raft-State-Version`.
New leaders also reject old AppendEntries responses without `X-Raft-Last-Index`.
These are dependency HTTP protocol changes; SlimData's application commands did
not change.

The upstream PR treats an absent state version as zero and an absent last-index
hint as the request's preceding index, preserving one-entry backtracking on
rejection. Malformed explicit headers remain rejected. Nine HTTP parser cases
include three failures before this fix and all pass afterward.

### Upstream correction and package provenance

DotNext PR #300 targets `develop` and includes the live rejoin fix, validation
and preservation of existing WAL metadata page sizes, and legacy HTTP defaults.
New WALs retain DotNext's current default page size. Invalid or inconsistent
existing sizes fail before WAL files are opened or resized; no state migration
or deletion is performed.

The complete upstream suite passes locally: **2,443 tests, 4 existing skips,
0 failures**. Private packages `6.7.2-slimdata.402.2` were built from commit
`750c57f` in an isolated downstream worktree, based on upstream `develop` at
`f10ace6` as required by its contribution policy. They also include the existing
`develop` changes since the published 6.7.2 release; comparative measurements do
not isolate the cost of these three fixes. They are used only for experiments,
not published or referenced by the SlimFaas PR. The PR still references official
6.7.2 and must wait for an upstream release containing the corrections.

## Checks completed locally

Host: macOS ARM64, .NET SDK 10.0.300; documentation built with Node 24.18.0.
Baseline: `origin/main` at `fe3b08e7`, DotNext 6.6.0. The branch subsequently
incorporates `b7cdd8b1` (central NuGet package management); the DotNext version now
lives in `Directory.Packages.props`. Native evidence below retains its original
commit and executable provenance.

| Check | Result |
|---|---|
| Monotonic progress tracker | 9 cases pass: idle, short delay, 30-second stall, progress, unavailable/crossing/rewound indexes |
| SlimFaas tests | 1,215 pass |
| SlimFaasMcp / Kafka / .NET client | 79 / 11 / 23 pass |
| SlimData tests, before adding the legacy WAL fixture | 215 pass, 1 failure (member re-addition) |
| Native AOT publish, SlimFaas and standalone SlimData | Pass with the baseline third-party warnings below |
| Documentation site | Pass: 21 pages, 1,383 local links/assets, 349 search entries |
| Native local demo | Manifest valid; `/status-functions` succeeds; `/function/fibonacci1/hello/local` returns `Hello local!` |
| Three native 6.7.2 nodes, restarts and quorum faults | Pass, data checked on every node after each phase |
| Published 6.7.2 rolling upgrade from 6.6.0 | Blocked at first follower by WAL metadata page-size failure |
| Locally patched dependency, complete .NET suite | 1,545 tests pass, including all 217 SlimData tests; repeated after centralization with the embedded UI builds enabled |
| Locally patched native rolling upgrade from 6.6.0 | Pass: 180 initial and 186 final values checked on each of 3 nodes |

The native 6.7.2-only experiment writes 180 initial sets and one marker per phase.
It verifies all values after restarting each follower, restarting the leader,
pausing one follower, pausing both followers, and restarting the complete cluster.
With both followers paused, the minority does not acknowledge the pending write.
After six seconds of quorum loss, it completes 1.644 seconds after the followers
resume, within a 30-second bound. All 186 final sets match on all three nodes.

The same native scenario passes with the private corrected packages, starting
from genuine 6.6.0 snapshots and WALs and replacing followers before the leader.
Every phase validates every expected value on all three nodes. After six seconds
without quorum, the pending write completes **1.938 seconds** after followers
resume; all 186 values remain correct after the full cluster restart. This
validates the tested forward rolling upgrade, not rollback to 6.6.0 or arbitrary
application state-version changes. After incorporating central package management
and its Logging.Abstractions minimum, a second full native rolling-upgrade run
also passes (186 values per node; quorum recovery in 3.063 seconds). Both SlimFaas
and standalone SlimData AOT publications pass with those central package settings.

Both baseline and candidate AOT publications report MemoryPack.Core IL2104 and
IL3053, plus System.Configuration.ConfigurationManager IL2104. These are the
existing third-party summary warning exceptions in `Directory.Build.targets`;
no new suppression is introduced.

## Comparative performance

The baseline executable is SlimFaas `fe3b08e7` with DotNext 6.6.0. Each native
three-node run starts with fresh state and checks every expected set, hashset and
counter on each node afterward. These are workstation screening measurements,
not a production capacity estimate. The benchmark intentionally uses its existing
`WarmupRounds=10000` and disabled low-load fast path; the recovery experiment uses
the runtime defaults instead.

An initial short run overlapped a test using the same Raft ports and is excluded.
The first separate official 6.7.2 matrix (5-second warm-up, 20-second measurement,
one repetition) completed all eight runs with zero errors, but failed the
set/concurrency-12 throughput threshold:

| Scenario / concurrency | 6.6.0 ops/s | Official 6.7.2 ops/s | Throughput change | p99 change | Verdict |
|---|---:|---:|---:|---:|---|
| Mixed / 12 | 214.31 | 200.47 | -6.5% | -0.4% | Pass |
| Mixed / 48 | 2488.55 | 2557.36 | +2.8% | -12.0% | Pass |
| Set / 12 | 127.34 | 81.08 | -36.3% | +2.8% | Fail |
| Set / 48 | 391.78 | 510.79 | +30.4% | -1.8% | Pass |

The final corrected-package matrix uses two repetitions, 30-second measurements
and a 5-second warm-up, alternating execution order. **All 16 runs completed: 389,372 measured operations, zero errors, and successful
state validation on every node. The overall benchmark verdict remains FAIL.** No additional build or test launched by this work
runs alongside this final matrix. The host is shared, and the candidate includes
existing upstream development-branch changes in addition to the proposed fixes.

| Scenario / concurrency | 6.6.0 median ops/s | Corrected median ops/s | Throughput change | p99 change | RSS change | Verdict |
|---|---:|---:|---:|---:|---:|---|
| Mixed / 12 | 201.56 | 199.92 | -0.8% | +3.0% | +21.2% | Fail: RSS |
| Mixed / 48 | 2568.70 | 2493.50 | -2.9% | +6.7% | -0.1% | Pass |
| Set / 12 | 87.37 | 111.89 | +28.1% | +12.7% | +17.4% | Fail: RSS |
| Set / 48 | 347.62 | 474.24 | +36.4% | +0.2% | -4.2% | Pass |

The earlier set/concurrency-12 throughput loss does not repeat in this matrix,
but median maximum resident memory exceeds the unchanged +15% acceptance limit
in two cases. Memory varies substantially between repetitions (for example,
baseline mixed/12 reaches 268.3 and 409.8 MiB). These measurements do not establish
a memory leak or isolate a cause. Memory acceptance must be resolved before
adoption; the threshold is not relaxed to make the run pass.

The retained GC metrics help narrow the follow-up: mixed/12 node 2 has about
303 MiB committed to the managed heap in both candidate runs and in the second
baseline run, versus 161 MiB in the first baseline run. That baseline run also
records an extra generation-0 collection. Across the three nodes, total allocated
bytes are similar between variants. This suggests GC timing contributes to the
short-run RSS variability; a longer steady-state comparison is still needed to
clear the memory gate.

## Reproduction commands

```bash
dotnet test tests/SlimData.Tests/SlimData.Tests.csproj \
  -p:SkipClientAppBuild=true --filter FullyQualifiedName~DotNextRaftRegressionTests
dotnet test tests/SlimData.Tests/SlimData.Tests.csproj \
  -p:SkipClientAppBuild=true --filter FullyQualifiedName~MessageExchange
dotnet test tests/SlimData.Tests/SlimData.Tests.csproj \
  -p:SkipClientAppBuild=true --filter FullyQualifiedName~Restores_snapshot_and_compacted_wal
dotnet test -p:SkipClientAppBuild=true

# Supply separately published baseline and candidate executables. The output
# directory must be new. Ports 32021–32023 and 3382–3384 must be free.
python3 .bin/test-slimdata-raft-recovery.py \
  --baseline /path/to/baseline/SlimFaas \
  --candidate /path/to/candidate/SlimFaas \
  --output artifacts/raft-recovery-new-run
```

Use the candidate executable for both arguments to exercise recovery independently
of the rolling-upgrade blockers. Only processes created by the script are signalled;
it resumes paused nodes during cleanup and retains state, metrics, logs and results.

## Dependency licensing and remaining gates

The resolved dependency diff changes the six DotNext packages from 6.6.0 to 6.7.2,
and System.Configuration.ConfigurationManager, System.IO.Hashing,
System.Runtime.Caching and System.Security.Cryptography.ProtectedData from 10.0.9
to 10.0.12. Each declares MIT, an approved license under the
[CNCF policy](https://github.com/cncf/foundation/blob/main/policies-guidance/allowed-third-party-license-policy.md).
Package metadata alone does not prove distribution-wide compliance: the PR's
FOSSA License Compliance passed on SlimFaas commit `0e9b2b9b`; it and CI must
pass again on the final dependency update. Central package management also
requires `Microsoft.Extensions.Logging.Abstractions` 10.0.12 (MIT), matching
DotNext's minimum; leaving the central 10.0.11 pin produces NU1109 even where
framework pruning ultimately removes it from runtime assets. No package hold or
license exception is introduced, and no .NET lockfiles are used in this repository.

Obtain a published upstream release containing all three corrections, update the
SlimFaas dependency, rerun validation, and obtain green CI/FOSSA before marking the
SlimFaas PR ready. Resolve the two benchmark memory failures as well. The
upstream full suite also passes locally **with coverage**, but CI build 131614
timed out in the macOS coverage step; Linux and Windows passed. New WAL tests
now have a 60-second deadline and macOS CI reports detailed test progress. The
rerun must pass; an earlier timeout on the unmodified `develop` branch does not
prove this failure has the same cause. The upstream CLA bot also requires the contributor to review and
accept the agreement personally; that action is still pending. A maintainer must
apply the upstream `ai_assisted` label because the contributor account cannot do so. Keep #402 open until staging confirms the incident is resolved.
