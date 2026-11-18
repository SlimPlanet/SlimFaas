# Final-source managed kind quorum-loss long endurance

The dedicated kind quorum-loss role passes all six absences, successive crashes
and the complete original-volume restart: **230 confirmed operations, 8,940
separately retained unknown outcomes, zero confirmed missing operations, receipt
hash/length failures or successful repetitions**. Independent reconciliation
also finds no repeated confirmed job. The driver takes approximately 116.86
minutes. All three final-source kind long roles are now closed.

Production source `189826231fe43fe3069e8a1f7724f83589432c6f`, managed/trimmed
Linux ARM64 image71, per-pod assembly hashes, default copying policy zero,
strict 1 MiB offload threshold and resources match the
[follower](kind-follower-189.md) and [leader](kind-leader-189.md) roles. Two
follower processes are suspended to remove the majority. The direct minority
write probe checks that successful acknowledgement cannot precede restoration.
The generator, functions and real Jobs run inside the dedicated cluster.

| Quorum-loss absence | Ready after resume | Full recovery/reconciliation | Confirmed cumulative | Unknown cumulative |
|---|---:|---:|---:|---:|
| 30 s | 2.048 s | 2.569 s | 118 | 28 |
| 60 s | 2.329 s | 2.847 s | 124 | 97 |
| 300 s | 3.630 s | 5.519 s | 130 | 490 |
| 900 s | 3.617 s | 5.747 s | 135 | 1,691 |
| 1,800 s | 4.103 s | 8.265 s | 140 | 4,104 |
| 3,600 s | 2.738 s | 11.848 s | 145 | 8,935 |

Confirmed-count changes occur at the traffic/suspension boundaries; the direct
minority probe independently checks response timing against restoration. Client
requests remain bounded at 15 seconds and recovery/confirmed drain at 180
seconds. The protocol probe retains its separate historical 300-second startup
bound: 201.824 seconds from crash to listener, then 0.434 seconds for majority
readiness/write under the unchanged 15-second guard. The approximately
200-second startup limitation remains documented.

Independent export reconstructs accepted/unknown maps from the client journal,
checks confirmed async/stream receipts and job arguments, and verifies handler
SHA-256 and lengths. It counts 80 successful handler attempts, five deliberate
failed retry/DLQ attempts and 22 job receipts, including five jobs with unknown
client acceptance. No successful ID/function pair repeats. The harness also
reports no repeated job execution.

| Unknown client outcome after recovery | Count |
|---|---:|
| Handler executed | 14 |
| Job executed | 5 |
| Stored mutation committed on every node | 21 |
| Stored mutation absent at final all-node observation | 3,661 |
| Unresolved | 5,239 |

Later execution/commit does not turn an unknown response into confirmed client
acceptance. One additional deliberately truncated upload is intent-only and
checked absent after a write barrier. All **9,171 client intents** are accounted
for, including that separate negative control.

The [structured evidence and complete original-tree index](kind-quorum-189.json.gz)
has SHA-256 `5ccc83ea47ff5c71333de0c37dbe6462205bfe8e25d9eb2ed08874f0121dff82`.
It hashes **396 original files**, including state, WALs, snapshots and journals,
after all campaign writers stop. The [raw archive](kind-quorum-189-raw.tar.gz),
SHA-256 `0d500cd1ef688fe7972c63cbab14cbb8c3bb55d82f63b3fdb71e03e8c70ef56b`,
contains 184 independently verified campaign files, including two receipt
journals and the repair sentinel. Original state remains in the VM and is
indexed separately from the compact archive.

The [runtime resource summary](kind-quorum-runtime-resources-189.json) and
[696 raw observations](kind-quorum-runtime-resources-189.jsonl.gz) cover eight
observed container lifetimes and six pod UIDs. The maximum observed kernel
`memory.peak` is 200.81 MiB under a 1 GiB limit; observed per-lifetime memory-limit,
OOM/kill and socket-throttling counters remain zero. These are cgroup
observations, including file/kernel memory, rather than process RSS. Short
unobserved lifetimes, uncaptured final counter values and function/job resources
remain outside this scope.

Complete runtime-log lifetime coverage and healthy-window classification remain
separate audits. The workers share one VM filesystem, so the campaign does not
establish independent-storage hardware tolerance. Formal non-regression,
remaining SDK/demo/fault controls and tracked guarantees remain open; no merge
or LTS certification is claimed. Reproduce with the
[follower command](kind-follower-189.md#reproduce), choosing `--roles quorum` and
a fresh namespace/output, with the same image and unchanged deadlines.
