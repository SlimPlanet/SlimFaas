# Final-source managed kind leader long endurance

The dedicated kind leader role passes the recovery/integrity harness with
**22,885 confirmed operations, 31 separately retained unknown outcomes, zero
confirmed missing operations and zero receipt hash/length failures**. All six
absences, successive process crashes and the complete original-volume restart
pass. One unknown client outcome has an additional successful delivery during
an injected suspension; it remains counted below. There is no repeated confirmed
job execution. The driver takes approximately 119.41 minutes.

Source `189826231fe43fe3069e8a1f7724f83589432c6f`, the managed/trimmed Linux
ARM64 image71, per-pod assembly hashes, resources, default copying policy zero
and strict 1 MiB offload threshold match the [kind follower](kind-follower-189.md).
Three dedicated workers host three runtime replicas; functions, real Jobs and
the generator execute inside the cluster. Endurance probes permit deliberate
suspension. Normal probes, stronger storage-copying policies and independent
hardware remain separate validation scopes.

| Leader absence | New leader observed | Ready after resume | Full recovery/reconciliation | Confirmed cumulative | Unknown cumulative |
|---|---:|---:|---:|---:|---:|
| 30 s | 4.733 s | 0.050 s | 0.788 s | 181 | 4 |
| 60 s | 4.888 s | 0.344 s | 1.037 s | 342 | 10 |
| 300 s | 4.881 s | 0.174 s | 2.612 s | 1,350 | 11 |
| 900 s | 4.879 s | 0.777 s | 7.023 s | 4,405 | 15 |
| 1,800 s | 4.895 s | 0.452 s | 15.689 s | 10,536 | 21 |
| 3,600 s | 4.500 s | 1.015 s | 44.408 s | 22,848 | 25 |

Requests and the leader-change guard remain bounded at 15 seconds; recovery
and confirmed drain remain bounded at 180 seconds. The independent protocol
probe uses the restored historical 300-second startup bound: 201.755 seconds
from crash to listener, followed by 0.646 seconds for majority readiness/write.
The approximately 200-second startup limitation and its
[earlier failed control](protocol-startup-budget.md) remain documented.

Independent export reconstructs the confirmed/unknown maps, verifies confirmed
async/stream receipts and job arguments, and checks receipt SHA-256 and lengths.
It finds 8,424 successful handler attempts, five deliberate failed retry/DLQ
attempts and 3,195 job receipts. No confirmed operation is missing; no job ID
has repeated execution in the harness accounting.

| Unknown client outcome after recovery | Count |
|---|---:|
| Handler executed | 3 |
| Stored mutation committed on every node | 2 |
| Stored mutation absent at final all-node observation | 8 |
| Unresolved | 18 |

One additional deliberately truncated upload is intent-only and checked absent
after a write barrier. All **22,917 client intents** remain accounted for.
Later commit/execution does not convert an unknown response into confirmed
client acceptance.

## Fault-associated repetition

The [retained per-ID timeline](kind-leader-fault-repetition-189.json) records
one 64 KiB async intent with a 15.017-second client timeout during the
60-second leader-suspension scenario. It never receives confirmed acceptance.
The same queue element executes successfully twice, with attempts zero and one;
both completions occur during the following 300-second suspension, 0.092 and
9.831 seconds after that fault begins. Both receipts have matching body length
and SHA-256. There is one client intent and no client resubmission.

This is **one extra successful ID/function delivery associated with an injected
fault**, retained under the plan's fault-time repetition allowance. The request
stays unknown. It is not reported as exactly-once delivery or silently removed
from the successful-attempt count. The timeline alone does not prove the
internal callback/retry cause.

## Evidence and remaining limits

The [structured evidence and complete original-tree index](kind-leader-189.json.gz)
has SHA-256 `ba139a5abb369f3b97531a57cbe94ee69dfdf9f6a3ae28163d9736c599d289cc`
and covers **9,529 original files**, including state, WALs, snapshots and
journals, hashed after all campaign writers stop. The [raw archive](kind-leader-189-raw.tar.gz),
SHA-256 `5c62b68ae1f7a61a3c62c99c4d6ccc778b39b525987f84b8d866c83b6a59aebd`,
contains 184 independently verified files: campaign reports, configuration,
captured logs/metrics, two receipt journals and the repair sentinel. Original
state remains in the VM and is indexed separately from the compact archive.

The [runtime resource summary](kind-leader-runtime-resources-189.json) and
[711 raw observations](kind-leader-runtime-resources-189.jsonl.gz) cover nine
observed container lifetimes and six pod UIDs. The maximum observed kernel
`memory.peak` is 682.23 MiB under a 1 GiB limit; observed per-lifetime memory-limit,
OOM/kill and socket-throttling event counters remain zero. These are cgroup
observations, including file/kernel memory, not process RSS or proof of each
counter's uncaptured final value. Short unobserved lifetimes and function/job
resources remain outside this scope.

Runtime logs retain pod UID/restart generations, with supplementary previous-
container captures stored separately. Complete lifetime coverage and healthy-
window error classification still need their own audit; harness PASS does not
establish them. The quorum role is running. Formal HTTP/MemoryLab comparisons,
remaining SDK/demo/fault controls and other tracked guarantees remain open.
Historical unhealthy inline/policy-cost observations remain unchanged. No merge,
global non-regression acceptance or LTS certification is claimed.

Reproduce with the [follower command](kind-follower-189.md#reproduce), choosing
`--roles leader` and a fresh namespace/output. Keep the same runtime/image,
300-second protocol startup, 180-second recovery and 15-second request bounds.

At documentation head `e0793e66`, the
[main CI](https://github.com/SlimPlanet/SlimFaas/actions/runs/43853481486),
[reliability workflow](https://github.com/SlimPlanet/SlimFaas/actions/runs/43853481310),
[dashboard/documentation workflow](https://github.com/SlimPlanet/SlimFaas/actions/runs/43853481424)
and FOSSA License Compliance pass. The external SonarCloud quality gate remains
failed and separate from scanner-job success. Latest-head checks remain
authoritative; no rule or finding is waived.
