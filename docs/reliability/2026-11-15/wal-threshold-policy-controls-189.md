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

### Measured resource costs

The [resource summary](wal-threshold-resource-summary-189.json) derives these
observations from the archived reports. Each row is one sequential diagnostic;
it does not establish a statistically reliable geometry ranking. CPU and
allocation deltas span all three runtime processes, from before-send to the
first observed confirmed drain. Functions/generator are excluded. RSS and WAL
inventories are sampled aggregates; allocation/GC counters can lag their
telemetry cadence. The separate full 180-second window includes idle/cleanup
cost and is retained in the original reports.

| WAL | Acceptance/s | Delivery/s | Acceptance p99 | CPU ms/message | RSS MiB | WAL files | WAL logical MiB |
|---|---:|---:|---:|---:|---:|---:|---:|
| 4 KiB | 141.83 | 75.21 | 201.35 ms | 58.48 | 553.56 | 546 | 2.10 |
| 8 KiB | 143.41 | 76.29 | 210.19 ms | 61.71 | 571.13 | 303 | 2.25 |
| 16 KiB | 60.03 | 44.27 | 828.69 ms | 68.71 | 539.24 | 171 | 2.39 |
| 32 KiB | 63.05 | 46.29 | 766.80 ms | 72.48 | 564.01 | 92 | 2.27 |
| 64 KiB | 122.19 | 69.78 | 186.71 ms | 53.09 | 533.15 | 54 | 2.11 |

Increasing chunk size reduces the observed file count, but throughput and CPU
do not improve monotonically. In particular, this 64 KiB observation accepts
fewer messages per second than the 4/8 KiB observations. Counter allocation is
1.70–2.35 MiB/message and Gen2 collections are 0.145–0.165/message across rows.
Aggregate kernel writes are 616.58–631.54 MiB and runtime-pod transmitted traffic
is 610.25–614.03 MiB through drain. The sampled Files footprint is approximately
600.26 MiB in every row, consistent with three copies of the 200 MiB body set
after background replication. That aggregate footprint alone is not a per-file
durability proof; admission requires two confirmed copies.

Kernel I/O includes Files, WAL, snapshots and other runtime activity; pod traffic
includes probes/measurement and function traffic. These counters cannot isolate
WAL flush counts, mapping faults or synchronization time. Fewer WAL files alone
does not establish the cause of the latency differences or justify a new default.

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

| Threshold | CPU ms/message | Allocated MiB/message | Gen2/message | RSS MiB | WAL logical MiB | Kernel writes MiB | Verdict |
|---|---:|---:|---:|---:|---:|---:|---|
| 1 MiB | 89.80 | 31.80 | 0.405 | 2382.95 | 129.39 | 3236.96 | FAIL |
| 512 KiB | 61.99 | 1.88 | 0.155 | 569.13 | 2.11 | 620.68 | PASS |
| 256 KiB | 58.29 | 1.78 | 0.165 | 566.55 | 2.23 | 616.82 | PASS |
| 128 KiB | 61.62 | 2.06 | 0.150 | 558.22 | 2.23 | 626.32 | PASS |

The inline row records 18.19 acceptances/s and 13.42 first deliveries/s, with
1.702 s acceptance p99 and 13.331 s delivery p99. It remains an unhealthy result,
not an accepted performance baseline. Its sampled WAL contains 2,082 files and
no Files bodies, versus 54–56 WAL files and approximately 600.26 MiB of replicated
Files bodies for the offloaded rows. Runtime-pod transmitted traffic is
747.59 MiB inline and 611.77–612.12 MiB offloaded. The resource scopes and
counter/inventory limitations above apply to this table too.

These observations locate substantial allocation, WAL and memory pressure in
the inline path under this workload. They do not prove which mechanism causes
the unplanned leader change, runtime error and seven extra deliveries. The three
lower thresholds all exercise the same offloaded 1 MiB-body path; their numerical
differences do not rank the thresholds for other body sizes. A 512 KiB offload
profile is worth further controlled evaluation, while a production recommendation
and default change remain unverified.

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

To reproduce a fresh diagnostic, use an isolated checkout with the dedicated
kind cluster, explicit kubeconfig at `artifacts/pr506/kubernetes/kubeconfig`,
verified image71 imported into all four kind nodes and the [recorded toolchain
and setup](../../benchmarking.md). Extract the archive at the repository root
and restore the [variant configuration](wal-threshold-policy-variants-189.json).
Choose a new namespace on every execution; never reopen an existing diagnostic
directory or reuse its volumes for a new measurement.

```bash
tar -xzf docs/reliability/2026-11-15/wal-threshold-policy-raw-189.tar.gz
python3 - <<'PY'
import json
from pathlib import Path
variants = json.loads(Path('docs/reliability/2026-11-15/wal-threshold-policy-variants-189.json').read_text())
for profile, value in variants.items():
    (Path('artifacts/pr506/profiles') / profile / 'variants.json').write_text(json.dumps(value))
PY
python3 artifacts/pr506/profiles/wal-replay-net11-88/run-one.py \
  --namespace "slimfaas-pr506-wr88-repro-$(date +%s)" \
  --chunk-size 4096 --variant after --offload-threshold 524288 \
  --required-copies 2 --probes normal
python3 artifacts/pr506/profiles/offload-screen-net11-82/run-one.py \
  --namespace "slimfaas-pr506-os82-repro-$(date +%s)" \
  --chunk-size 65536 --variant after --offload-threshold 1048576 \
  --required-copies 2 --probes normal
```

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
