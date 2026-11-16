# Current-source WAL, threshold and copying-policy controls

Runtime source `189826231fe43fe3069e8a1f7724f83589432c6f` passes all five
fresh-WAL replay controls. The unchanged strict 1 MiB offload boundary still
fails a separate healthy-traffic screen with seven extra successful deliveries.
The copying-policy cost screen also fails after its single permitted complete
repeat. These are bounded diagnostics, not formal HTTP/MemoryLab acceptance.
Defaults, resource limits and observation deadlines remain unchanged.

All observations use the same managed/trimmed Linux musl ARM64 image
`localhost/slimfaas-pr506:after-tools-net11-71`, in the dedicated kind cluster.
The [application report](persisted-copy-validation-189.md) records its source,
DLL/image hashes, successful fault controls and separate same-guarantee ABBA.
Those passing comparisons do not erase the costs or failures below.

## WAL replay

Each geometry uses fresh original volumes, two required durable copies,
strict 512 KiB offload, normal probes and 200 × 1 MiB messages at C16. Every
healthy window lasts 180 seconds after sending. All five confirm and deliver
200 unique requests, with zero unknowns, missing IDs, extra deliveries, healthy
runtime errors, observed managed OOM or unplanned container restarts.

A separate fault phase confirms one gated async request plus Set/Files seeds,
stops all three runtime containers behind one barrier, and releases their
restarts only after every original process is absent. Pod UIDs, volume/storage
identities and persisted WAL geometry are retained; bootstrap permission is
already consumed. Runtime readiness is checked while the function stays gated;
function readiness follows gate release. Another full 180-second window checks
all 203 original confirmed operations, without resubmission or successful
repetitions. Current/previous runtime logs cover both container generations.

| WAL chunk size | Replay recovery | Confirmed operations after replay | Verdict |
|---|---:|---:|---|
| 4 KiB | 7.863 s | 203 | PASS |
| 8 KiB | 7.362 s | 203 | PASS |
| 16 KiB | 8.165 s | 203 | PASS |
| 32 KiB | 8.406 s | 203 | PASS |
| 64 KiB | 6.132 s | 203 | PASS |

Recovery is a bounded application observation, not an isolated storage timing
or a statistically established ranking. These logical volumes share one VM
filesystem and do not certify independent-host hardware resilience. The earlier
[failed gated-readiness fixture and verified-copy recovery](wal-gated-seed-recovery-evidence.json)
remain independent, preserved observations.

## Offload threshold

The sequential screen holds fresh WALs at 64 KiB and the copying policy at two,
using the same 200 × 1 MiB/C16 workload, normal probes and full 180-second window.

| Strict offload threshold | Confirmed | Unknown | Confirmed missing | Extra successful deliveries | Verdict |
|---|---:|---:|---:|---:|---|
| 1 MiB | 200 | 0 | 0 | 7 | FAIL |
| 512 KiB | 200 | 0 | 0 | 0 | PASS |
| 256 KiB | 200 | 0 | 0 | 0 | PASS |
| 128 KiB | 200 | 0 | 0 | 0 | PASS |

At the strict 1 MiB boundary, exactly 1 MiB bodies remain inline. Seven IDs each
execute successfully twice during healthy traffic, and one runtime error is
recorded in that window. All four windows have zero observed container restarts
or managed OOM; logs and duplicate IDs are retained.
The three passing offloaded cases do not establish a universally safe threshold:
other sizes, chunked transfer and concurrency boundaries require separate checks.
The default remains 1 MiB. Longer endurances take priority over the remaining
boundary screen at the user's request.

## Copying-policy cost

Two complete sequences use the same source and settings: policies
0–1–2–2–1–0, fresh 64 KiB WAL, strict 512 KiB offload, normal probes,
200 × 1 MiB/C16 and full 180-second windows. All twelve observations deliver
their 200 confirmed requests without unknowns, confirmed missing IDs or extras.

The first sequence is INCONCLUSIVE because policy-one ordinal two passes
against the closing reference but narrowly fails acceptance throughput against
the opening reference (89.989%, minimum 90%). The complete sequence is repeated
once; both original sequences remain published. The repeat has no contradictory
bracket verdicts and is **FAIL**. Both policy-two observations fail against both
policy-zero references. Acceptance throughput is 84.34–89.16% of reference;
acceptance tails and some CPU/delivery-tail checks also fail. Policy-one ordinal
two passes both references in the repeat; its later ordinal five fails both.
No additional repetition is used to seek a passing result.

CPU includes all three runtime processes from before-send through confirmed
drain; generator/function CPU is excluded. Final observation-window CPU remains
separate. RSS is sampled, and Raft batches/message use the existing append metric
summed across runtimes. These six-pass policy comparisons measure the cost of
different guarantees, while the earlier source511–189 ABBA compares identical
two-copy guarantees. Neither substitutes for the final standard-default matrix.

## Evidence and continuation

[Structured evidence](wal-threshold-policy-evidence-189.json) retains the complete
policy comparator rows, enclosing-reference checks and all individual verdicts.
The [frozen raw archive](wal-threshold-policy-raw-189.tar.gz) contains 921 closed
local files, including reports, UID/restart-specific logs, configuration, driver
sources and per-file indices for 21 original VM roots. Archive SHA-256:
`ef9f98795c3bb755721dfd55bf87057897dd302f524dadf579f675e15cf641ee`.
All original volumes, WALs, snapshots and journals remain retained.

On 2026-11-16 the final-source long macOS endurance started, with Kubernetes
endurance and general validation queued serially afterward. This is a launch
record, not a completed endurance verdict. Each environment runs follower,
leader and quorum absences of 30/60/300/900/1,800/3,600 seconds with mixed traffic,
successive crashes and a complete original-volume restart. Recovery remains
bounded at 180 seconds and the separate leader-routing guard at 15 seconds.
Final reconciliation, raw indices, three-environment formal comparisons and
remaining tracked guarantees stay open. No merge or LTS certification is claimed.

See the [benchmark protocol](../../benchmarking.md) for the complete acceptance
criteria and the [root-formatter validation](admission-root-registration.md) for
the 2,421-test .NET run, Python/client/harness checks and AOT publication evidence.
