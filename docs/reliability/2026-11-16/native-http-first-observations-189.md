# First final-source macOS HTTP observations

The first candidate passage completes all **216 latency cases, six focused
async cases and the 200-message scaling control**. Its reported client/delivery
counters have zero HTTP failures, missing messages or duplicates. However, a
separate supervisor-log audit finds **34 error-level headers during the workload
without injected faults**. This is an open runtime-health failure, despite the
benchmark process returning success. It must not be reported as an overall pass.

The first reference is incomplete: at **2,097,152 bytes, concurrency 4,
repetition 2**, it observes **845/848 unique deliveries, zero duplicates**, then
reaches the unchanged **180-second** drain deadline. Its original state and logs
remain retained. Three messages missing at this observation bound do not
establish permanent loss. No complete `results.json` is produced for this
reference. Formal HTTP performance acceptance is therefore **INCONCLUSIVE** for
this observation; remaining ABBA passages and the other matrix blocks continue.

## Frozen configuration and first candidate execution

The product sources remain reference
`f36084643a75d803051a6918d2950d1a4f674ada` and candidate
`189826231fe43fe3069e8a1f7724f83589432c6f`, both macOS ARM64 Native AOT,
using the same frozen SDK11/common HTTP driver and standard unchanged defaults.
This report covers only `http-attempt-1/1-before` and `2-after` in
`artifacts/pr506/measurements/macos-net11-189-serial-01` as observed through
16 November 2026. It is not the completed three-environment report.

The first candidate runs from 16:43:01.312 to 17:40:47.546 UTC. It reports
142,570 completed measured async requests across the 72 async latency cases,
3,300 accepted focused paced/burst requests, and 200 accepted scaling requests.
These counts are separate from the independently reconciled long-endurance
ledgers and must not be added to their totals as if they shared the same proof.
All 78 async resource boundaries cover the three runtime nodes. Both raw and
normalized synchronous metrics remain in the original result file.

| Candidate control | Observation |
|---|---|
| Latency/focused client failures | 0 |
| Reported async missing/duplicate counts | 0 / 0 |
| Scaling accepted/failed | 200 / 0 |
| First replica ready | 3.100 s |
| Four replicas ready | 6.190 s |
| Scaling queue drained | 19.076 s |
| Last 4 MiB/C16 repetition, async enqueue p99 | 7,175.078 ms |
| Workload-time runtime error headers | **34, open failure** |

The long tail and runtime errors remain visible. Completed delivery does not
clear performance guards or certify runtime health.

## Runtime errors to reproduce and investigate

The closed supervisor log has 39 error-level headers: three before the reported
workload starts, 34 during it, and two after it ends. All timestamps parse. The
34 workload-time headers comprise:

- 16 `SlimFaas.SendClient` headers around **17:30:07 UTC**, reporting
  `InvalidOperationException: Not port or IP available` while routing to the
  benchmark function;
- 18 `SlimFaas.Database.SlimDataService` headers during the later large-body
  workload, reporting unavailable Raft leader/lease or read-barrier failures.
  The original log also records an unplanned leader election around
  **17:36:56 UTC**.

Counts are log headers, not necessarily distinct exceptions; some headers share
timestamps. There are no deliberate runtime suspensions or crashes in this
HTTP passage. The first reference separately retains `FileNotFoundException`
stacks when reading offloaded body files before its drain timeout. These
observations identify reproductions and diagnostic leads; they do not prove a
common underlying cause. No deadline, guard, default or logging rule is relaxed.
Product corrections require a cause/regression and affected-control replay.

## Host maintenance qualification

Disk inspection observes about 20.9 GiB free without crossing the 20 GiB hold
threshold in the retained observations. Two global open-file scans find no
reference to the regenerable NuGet package cache. Its preemptive removal takes
**3.496 seconds, 17:12:10.987–17:12:14.482 UTC**. Space has already recovered
to 26.69 GiB immediately before removal and reaches 28.44 GiB afterward.
No source, session, publication, state/WAL/snapshot or campaign proof is removed.

This maintenance overlaps the **1 MiB/C1/repetition3 async resource window**
(17:12:03.072–17:12:18.021 UTC). That window includes the resource observation
and does not alone establish exact load boundaries. Its timing/resource values
are qualified, and this ordinal must not be described as perfectly isolated.
The original compiler-only isolation records are preserved as their narrower
observation. A clean replay is needed before using affected timing for acceptance;
it would not repair the independent incomplete-reference limitation.
No performance acceptance is claimed from this candidate passage.

## Evidence

The [first-reference evidence](native-http-reference1-189.json) retains the
timeout, exit/status and hashes of the original reports/logs. The
[candidate execution snapshot](native-http-candidate2-189.json) retains its
complete counts, scaling, three-node resource-boundary checks and maintenance
qualification. The [supervisor-log audit](native-http-candidate2-log-audit-189.json)
and [error contexts](native-http-candidate2-error-context-189.json) retain the
error timestamps/stacks and original log hash. The [maintenance proof](native-http-cache-maintenance-189.json)
records exact paths, timing and the open-file checks. Full raw exports and final
matrix comparisons remain pending closure of the timed campaigns.

The [benchmark guide](../../benchmarking.md) gives the matrix reproduction;
the frozen input is `artifacts/pr506/configurations/macos-net11-189.json`.
Use a fresh output directory and the same prepared reference/candidate binaries
and common drivers. Preserve failed outputs and the 180-second drain bound.
