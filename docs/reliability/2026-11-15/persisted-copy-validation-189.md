# Corrected-source persisted-copy application validation

Source `189826231fe43fe3069e8a1f7724f83589432c6f` passes the five targeted
Kubernetes fault/refusal controls and the complete bounded same-guarantee ABBA
comparison. **All four numerical comparisons pass the existing guards.** This
is a 200 × 1 MiB/C16 diagnostic at two durable copies, not the final timed
HTTP/MemoryLab matrices, long endurance or LTS acceptance.

The [root-formatter investigation](admission-root-registration.md) retains the
earlier unhealthy source511 reference: 199 confirmed requests, one unknown HTTP
500 and an unhandled exception. Its original state is unchanged; its verdict
remains inconclusive. That failed source-f13 comparison did not continue. The
new comparison uses the corrected candidate source and fresh namespaces/volumes.
Neither reference in this new complete series fails, and no repeat is needed.

## Compilation, integrity and fault controls

The three macOS ARM64 Native AOT publications pass, together with API/payload,
Files, retry/DLQ, Streams, real-job and persistent-restart controls. Native local
mode uses one-copy persistence because its shared declared storage root cannot
satisfy the two-distinct-root contract. Its fault campaign records **213
confirmed operations and ten unknown outcomes**, with no confirmed missing
operation or successful repeated ID/function pair. The 48 successful handler
attempts and five deliberately failed retries remain separate.

Linux/kind uses the same **managed/trimmed, self-contained Linux musl ARM64**
image `localhost/slimfaas-pr506:after-tools-net11-71`, configuration hash
`0203a61d16dbba284fb9ad4d6321cd40adc0000a4b460b519b958f3670e389f0`.
Its runtime DLL hashes and all three native hashes are retained in the evidence.
The image is verified in every dedicated kind node. Both variants inherit the
same benchmark/MemoryLab drivers and backlog-128 function fixture.

Four fresh two-copy controls permanently remove the ingress volume contents
after confirmed acceptance. A third node, suspended before upload, loses its
socket/mailbox state and returns without historical announcements. Durable
reference reconciliation repairs it, then repairs the ingress on empty storage.
All SHA-256/length checks and the full 180-second observation pass. Each case has
one confirmed request, zero unknowns and no confirmed missing operation.

| Control | Acceptance | Absent-node repair after ready | Fresh-ingress repair after ready |
|---|---:|---:|---:|
| Files / follower | 2.593 s | 2.861 s | 4.064 s |
| Files / leader | 5.096 s | 3.869 s | 3.863 s |
| Async / follower | 2.598 s | 4.077 s | 3.859 s |
| Async / leader | 5.129 s | 2.865 s | 4.062 s |

The normal-probe no-remote control also passes: both uploads return 503 while a
Raft Set returns 200. The two body outcomes remain unknown. There is no handler
receipt or public alias; temporary bodies are cleaned during the full window.
Original runtime identities remain unchanged. Current/previous runtime logs are
retained by pod UID and restart generation, with zero observed managed OOM.
Errors during deliberate faults remain available separately from healthy logs.
kind's volumes share one VM filesystem; this validates logical content loss,
not independent-host hardware failure tolerance.

## Same-guarantee diagnostic comparison

Order is source511–source189–source189–source511. Every pass uses a fresh
64 KiB WAL, strict 512 KiB body offload, two durable copies, normal probes,
200 × 1 MiB requests at C16 and the full 180-second post-send window. Every pass
has 200 confirmed and delivered IDs, zero unknowns, successful repetitions,
healthy-workload runtime errors, container restarts or observed managed OOM.

| Ordinal | Source | Acceptance/s | Delivery/s | Acceptance p99 | Delivery p99 | CPU ms/message | Sampled RSS MiB | Raft batches/message |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| A1 | 51163066 | 138.55 | 74.68 | 204.63 ms | 1596.61 ms | 63.32 | 578.42 | 1.345 |
| B2 | 18982623 | 152.31 | 80.71 | 186.15 ms | 1528.54 ms | 61.21 | 555.93 | 1.235 |
| B3 | 18982623 | 154.55 | 79.48 | 175.46 ms | 1636.07 ms | 54.63 | 572.35 | 1.305 |
| A4 | 51163066 | 152.65 | 79.17 | 222.07 ms | 1552.24 ms | 54.37 | 579.10 | 1.280 |

Both candidates pass against both enclosing references: throughput ≥90%,
acceptance/delivery p95 and p99 ≤110%, CPU/message ≤120%, sampled RSS ≤110%,
and Raft batches/message ≤200%. CPU includes all three runtime processes from
before-send through the first observed confirmed drain; generator/functions are
excluded. Final 180-second CPU includes idle/cleanup and remains a separate
original measurement. RSS is sampled every second. Raft uses the existing batch
append metric summed across the runtimes. Allocation-counter cadence and sampled
resources limit precision; raw counters and process/network I/O remain available.

This combines pooled verification and the required serializer correction.
It does not isolate the contribution of each change. The
[earlier policy-cost series](persisted-copy-validation-511.md) compares copying
policies within source511; its two-copy failures against legacy acceptance are
not erased by this same-guarantee result. Defaults remain unchanged.

## Validation and remaining work

The corrected source passes 2,421 .NET tests without failures/skips, 37 Python SDK
tests, 28 chaos-harness tests, 41 platform tests, two MemoryLab report tests and
the documentation build. Full UI builds, analyzers and audits remain active.
Main, dashboard/documentation and reliability workflows pass on this source,
as does FOSSA. The separate Sonar gate remains independently tracked; successful
scanner/workflow execution is not gate acceptance.

Fresh WAL geometry/replay and threshold checks follow. Formal three-environment
HTTP/MemoryLab comparisons, final-source long endurance, remaining fault coverage
and the older original-state-copy recoveries remain open. No merge or LTS
certification is requested. See the [benchmark protocol](../../benchmarking.md).

The first fresh 4 KiB WAL diagnostic completes its healthy 200-message window,
then fails a separate replay fixture: generic `campaign.ready()` waits for the
function deliberately kept behind the delivery gate. Replay is unverified, and
the separately confirmed async seed remains pending behind that gate. Its
[original failure and state index](wal-replay-gated-readiness-failure.json) remain
preserved; runtime-only readiness and verified-copy seed recovery follow. This
fixture failure does not change the closed same-guarantee ABBA verdict above.

[Complete application evidence](persisted-copy-application-evidence-189.json),
[raw index](admission-root-application-index-04.json.gz) (625 local files, nine
retained VM roots), [per-ID journals](admission-root-per-id-ledgers-04.json.gz)
(4,076 rows from 31 originals) and the
[frozen reproducer](admission-root-application-reproducer-189.tar.gz) retain the
exact sources, executable/image hashes, configurations and all raw observations.
