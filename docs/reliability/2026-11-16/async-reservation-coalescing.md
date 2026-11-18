# Async reservation ownership and delivery tracing

**A deterministic queue-boundary reproduction fails before the correction.** A
dispatch snapshot can report an ID as running, then a later dequeue timestamp
can reserve its next try while the original local HTTP exchange or completion
still owns that ID. The old dispatcher overwrites its local owner and starts a
second HTTP exchange. Completing the old exchange during dequeue reproduces
the same problem. Both cases make two sends where one is expected on source
`358539344c76220d6872737cb3f5e20de12b6e15`.

This establishes a scheduling defect at the worker boundary. It does **not** yet
establish the cause of the seven historical healthy duplicate executions. In
particular, producer operation timestamps are captured before Raft submission:
time waiting for apply alone does not advance the dequeue timestamp. Real
per-ID traces, stored state and application replays are still required.

## Correction and regression boundaries

The worker keeps its local HTTP owner and holds a newer reservation without
starting a parallel send. A known terminal result completes the ID once. For a
nonterminal result (transport failure, configured retry status or HTTP 202), the
worker can resume the **already reserved** next try after the original exchange
settles. It does not dequeue again or apply the older failure callback to the
newer reservation. While a reservation is held, that function does not consume
additional dequeue attempts.

The held try requires the current leader, a ready function and an unexpired
original reservation. Scale-zero and unready snapshots defer it; expiration
leaves it to existing Raft retry/DLQ policy. Losing leadership prevents that
worker from sending the retry or acknowledging the newer try with an older
failure. Clearing a dispatch generation also clears held reservations. This
preserves the existing distributed at-least-once limits: an unknown transport
outcome or leadership change can still permit another execution.

Nine new worker cases cover pending/completed original success, HTTP 500/202,
leadership loss, scale-zero, unready functions and expiry. The first two cases
fail on the old dispatcher. Additional RED phases caught retry-budget and
readiness/deadline defects during implementation; their results remain in the
[structured evidence](async-reservation-coalescing.json). The final focused
worker/transport/admission group passes 42 tests. Full validation passes
**2,477 .NET tests, zero failures/skips**, plus 12 routing, 47 platform, 32 chaos,
7 Sonar-boundary and 37 official Python SDK tests. Analyzers and dependency
audits remain active; no dependency, lockfile or persistence format changes.

## Per-ID diagnostic chain

The opt-in Kubernetes `--delivery-trace` profile joins these boundaries:

1. The common C# journal records caller ID and the response's `QueueElementId`.
2. Admission records queue element/file identity and commit request/outcome.
3. Queue batching records producer generation/sequence, operation and batch
   request IDs, retry batch attempt and validated reservation details.
4. Local Raft apply records committed index/term, operation IDs and duplicate
   batch results. Followers/replay may have no local request context; use their
   retained WAL rather than interpreting a missing trace line as non-application.
5. Worker and HTTP transport records identify local generation, queue try,
   reserved destination, actual URL, transport entry and real response headers.
6. Callback records distinguish the request from confirmation of its command.
   Command confirmation alone does not establish that it matched a running try.

Transport entry is not proof of transmission or execution. The existing network
exception wrapper can return a synthetic HTTP 500; that result is now explicitly
distinguishable from real response headers. Sync remains one application send.
Tests preserve producer identity on a retried batch and exercise actual WAL
commit, duplicate apply and reopen/replay. Trace is disabled by default, records
no payload or credentials, and adds I/O when enabled. Keep forensic runs separate
from ordinary performance acceptance.

Apply the [test-only reproduction patch](async-reservation-reproduction.patch)
to a clean source `35853934` checkout and run:

```bash
dotnet test tests/SlimFaas.Tests/SlimFaas.Tests.csproj -c Release \
  -p:SkipClientAppBuild=true -nr:false --filter FullyQualifiedName~AsyncQueueWorkerTests
```

The evidence retains test/log SHA-256 values and the exact changed-source hashes
validated before publication. These unit tests do not validate a new binary's
performance or endurance. New Native AOT/container publications, actual
Kubernetes replacement with per-ID tracing and affected long recovery campaigns
remain required. Historical successful campaigns remain attached to source189.
