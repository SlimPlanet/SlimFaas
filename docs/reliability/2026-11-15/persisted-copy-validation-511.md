# Persisted-copy application validation on source511

Runtime source: `511630668ffad507f14d6ce7d42c941bb224ad84`. This report separates
functional recovery from the cost screen. Product defaults are unchanged. It does
not establish final performance acceptance or LTS certification.

## Executables and compilation modes

All three macOS ARM64 Native AOT publications succeed. Their local-persistence
control completes with 201 confirmed operations, 15 unknown outcomes, no confirmed
missing operations, 43 successful handler deliveries and five intentional failed
retry attempts. No successful ID/function pair repeats. The control covers
protocol/leader recovery, API, six Content-Length/chunked payload sizes, Files
through 16 MiB, retries/DLQ, real jobs, Streams and full original-state restart.
Native Local shares one declared root and is tested with local persistence;
it cannot supply two distinct declared-volume receipts.

| Native executable | SHA-256 |
| --- | --- |
| SlimFaas | `b60a9a1b1ba9ddc19f73220ea5469c63f3fb1a696b9c0fd7f43f85f1ab53c7f0` |
| SlimData | `b2d88493e0b1a13309dbd231e20dc090c00498cfb8630303e11e01aeaa51eeff` |
| SlimFaasMcp | `f4d5e8cb2fdd8ea83a5321371724a608b423d13dfc713484294c79fcf24fdbaa` |

Linux uses the managed/trimmed, self-contained musl ARM64 build. The image is
`localhost/slimfaas-pr506:after-tools-net11-33`, configuration SHA-256
`c4e0eeadd6f829ef284afe1de7b6a150ff6721b2b00a1f5de488eec40d9b1519`.
It was imported into all four owned kind nodes. Actual apphost, SlimFaas/SlimData
DLL and frozen common-driver hashes are in the [evidence](persisted-copy-application-evidence-02.json).
These results are not compared across managed and Native AOT modes.

## Targeted storage and recovery controls

Each volume fault submits one 1 MiB request with two-copy admission, a fresh
64 KiB WAL and a 512 KiB strict offload threshold. The third runtime is paused
before upload. After acceptance the ingress is killed and all its data-volume
contents are quarantined outside active mounts, never restored. The paused
runtime is killed to discard old notifications and returns on its retained state.
The ingress subsequently returns with fresh storage and a new volume identity.
Local-only HEAD precedes GET, distinguishing background repair from demand pulls.

| Request / ingress | Acceptance seconds | Healthy-majority readiness seconds | Returned-node repair after readiness | Fresh ingress repair after readiness |
| --- | ---: | ---: | ---: | ---: |
| async / follower | 2.612 | 2.026 | 3.067 | 4.066 |
| file / follower | 2.599 | 2.018 | 3.072 | 3.052 |
| async / leader | 5.106 | 1.010 | 3.868 | 4.059 |
| file / leader | 5.088 | 1.011 | 3.872 | 3.871 |

All four complete the full 180-second post-delivery observation: one confirmed
operation each, zero unknowns, missing/corrupt bodies or extra deliveries. Files
are read and hash-verified on all three nodes; their closed VM indices retain
all three active body copies after the observation. Complete current/previous
logs cover the injected restarts, with zero managed OOM occurrences. Error-level
no-leader messages during recovery remain counted in the raw audits. These are
fault controls, not an error-free healthy-load log claim.

The first corrected Files replay (`fa34`) repairs the bytes and completes the
observation, then fails report construction on an async-only `missing` field.
That original remains a fixture FAIL. Profile38 records explicit verified File
nodes and completes fresh replays; no original verdict was overwritten.

The no-remote control denies writes only to both remote Files repositories while
leaving Raft writable. Async and Files both return `503`; a separate set operation
still commits. The ledger retains one confirmed set and **two unknown upload
outcomes**, rather than promoting 5xx to proven rejection. No delivery appears,
the public alias remains absent, both permissions are restored, and all local
unpublished bodies are cleaned within the full 180-second observation. Process
identities remain unchanged and normal probes are active.

The original source49 Files failure is also replayed from a SHA-verified,
metadata-preserving copy in its original namespace. Every state/init mount and
the control directory point to that copy. Original volumes remain unmodified;
all original file hashes are reverified afterwards. The acknowledged public file
is recovered by background repair and verified on all three nodes using source511,
without resubmitting the mutation, restoring quarantined bytes or forcing a cold
bootstrap. The original failure and its old index remain valid evidence.

All workers share one VM filesystem. These tests demonstrate logical volume-content
loss and repair; independent backing hardware is still a deployment precondition.

## Durability cost screen

Two complete sequences run policy **0–1–2–2–1–0**, each pass sending 200 × 1 MiB
at C16 with normal probes, fresh 64 KiB WAL, 512 KiB offload and the full 180-second
window. Every one of the twelve passes reconciles all 200 confirmed messages,
with zero unknowns, extra/missing/corrupt deliveries, restarts, managed OOM or
error-level workload logs. Their final indices contain no retained body files.

| Sequence / ordinal | Copies | Delivery/s | Acceptance p99 seconds | Delivery p99 seconds | Sampled RSS MiB | CPU ms/message to drain |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| First / 1 | 0 | 74.21 | 0.1745 | 1.6899 | 555.9 | 53.09 |
| First / 2 | 1 | 74.49 | 0.1410 | 1.7034 | 595.9 | 57.74 |
| First / 3 | 2 | 67.19 | 0.2087 | 1.7454 | 577.5 | 67.53 |
| First / 4 | 2 | 73.14 | 0.1890 | 1.7386 | 575.9 | 57.96 |
| First / 5 | 1 | 71.68 | 0.1662 | 1.8026 | 563.0 | 57.18 |
| First / 6 | 0 | 80.25 | 0.1208 | 1.7477 | 524.7 | 52.13 |
| Repeat / 1 | 0 | 79.76 | 0.1254 | 1.6611 | 552.6 | 52.62 |
| Repeat / 2 | 1 | 74.13 | 0.1367 | 1.6581 | 563.4 | 53.38 |
| Repeat / 3 | 2 | 67.50 | 0.2036 | 1.7261 | 575.5 | 70.45 |
| Repeat / 4 | 2 | 72.01 | 0.1971 | 1.7380 | 578.0 | 63.08 |
| Repeat / 5 | 1 | 78.16 | 0.1341 | 1.7517 | 554.2 | 54.34 |
| Repeat / 6 | 0 | 79.63 | 0.1300 | 1.6980 | 558.2 | 51.42 |

The diagnostic budgets retain 90% minimum acceptance/delivery rates, 110% maximum
acceptance/delivery p95/p99, 120% CPU/message, 110% RSS and twice the Raft
batch-append counter/message. Every candidate is compared with both surrounding
references. The first sequence has contradictory checks, triggering its one full
allowed repeat. The repeat remains contradictory: **overall INCONCLUSIVE**.
The two-copy observations fail one or more budgets against each surrounding
reference in both sequences. These costs do not pass a non-regression claim.
No further repetition is used to select a favorable result.

CPU is the delta of all three runtime processes from before sending through the
first confirmed-drain resource snapshot, including Raft/Files/proxy/telemetry and
observer cadence. Generator, function and agent CPU are excluded. Original reports
also retain final-180-second CPU, which includes idle polling/cleanup and must not
replace the active-window metric to hide overhead. Allocations/GC use exported
runtime counters; their update cadence limits short-window precision. RSS is a
one-second sampled aggregate, not a continuous peak. Kernel I/O is per runtime;
pod network includes functions/probes/measurement traffic. Inventories separate
retained WAL/Files bytes and counts, but kernel writes are not attributed between
them. The Raft metric uses the formal comparer's three-runtime counter sum.

This bounded batch has no steady-state warmup and does not replace the required
formal timed HTTP/MemoryLab matrices. Quantiles from 200 requests and the changing
references limit its statistical strength.

## Unchanged default boundary control and remaining work

A separate current-source strict-1-MiB/legacy-acceptance control, at 200 × 1 MiB
C16 and 64 KiB WAL, passes its full observation with 200 confirmed operations and
zero unknowns, missing/extra/corrupt deliveries or runtime errors. Delivery rate
is 27.775/s, acceptance p99 0.6615 s, delivery p99 5.3127 s, sampled RSS 2,033.38 MiB
and active-window CPU 72.97 ms/message. This single pass does not establish the
cause or correction of the earlier sourcef14 failure with 54 extra deliveries
and two errors. Those observations remain attached to their original source.

All three source511 workflows, DCO and FOSSA pass. The separate external Sonar
quality gate still fails: 5.7% new-code coverage, security E and reliability D.
The original source49 Windows Sonar-job peer-connect failure is preserved; it
is distinct from source511's passing workflow and failing external gate.

Representative threshold boundaries with/without Content-Length, selected WAL
mechanism/replay checks, the remaining fault combinations, final-source formal
comparisons and renewed long endurance remain open. No new default or performance
recommendation follows from these results. A subsequent pooled-verification
optimization has separate micro/component evidence and requires application
comparison before acceptance.

## Raw records and reproduction

The [full observations](persisted-copy-application-evidence-02.json) include exact
sources, executable/image hashes, configurations, guard checks and limits.
The [closed raw index](persisted-copy-application-index-02.json.gz) and
[index hashes](persisted-copy-application-index-summary-02.json) cover 1,051 local
files and twenty VM roots. The [per-ID client/receipt export](persisted-copy-per-id-ledgers-02.json.gz)
contains 11,108 original rows from 35 journals with writer/pod identities and
source paths; its [hashes](persisted-copy-per-id-ledgers-summary-02.json) permit
independent verification. Missing historical identifiers are not reconstructed.

The [source511 fault profile archive](persisted-copy-fault-reproducer-511.tar.gz)
contains profile38 and the no-remote profile36. Follow the
[build/reproduction method](file-admission-fault-reproducer.md), substituting the
recorded source511 image and actual rebuilt hashes, then use fresh `fa38-*` or
`fa36-*` namespaces. Always pass the dedicated kubeconfig/context, retain old
namespaces/volumes and run builds, loads and fault campaigns serially.
