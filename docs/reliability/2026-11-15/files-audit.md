# Files admission and recovery audit

Source inspected: `e5883fa8e28ba2775c4dee8e5e6a31a850d22c82`.
These are code-inspection findings. Fault reproductions, corrections and measured
costs must be recorded separately before claiming a stronger guarantee.

## Current acceptance path

1. `AsyncFunctionEndpoints` passes a fixed 1,048,576-byte threshold to
   `FunctionEndpointsHelpers.InitCustomRequest`. Offload uses a strict `>`
   comparison. An exactly 1 MiB body remains inline. Unknown-length requests
   probe up to threshold + 1 before choosing the path. The helper's default
   threshold is separate from the endpoint's explicit value.
2. For an externalized body, the helper creates a unique internal file ID and
   tags it with the queue element ID and function name.
3. `ClusterFileSync.BroadcastFilePutAsync` takes the existing per-ID transfer
   lock/budget and calls `DiskFileRepository.SaveAsync`.
4. The repository hashes and streams the body into a temporary file with a
   pooled 128 KiB buffer, calls `FlushAsync`, moves the body into place, then
   writes and moves its MemoryPack metadata. Neither write requests
   `Flush(flushToDisk: true)`. There is no parent-directory persistence barrier.
   Linux page-cache advice is not a durability barrier.
5. The synchronizer sends an announce-only signal to remote members, requesting
   transport confirmation. The receiver puts an item in a bounded, 1,024-entry
   `DropOldest` mailbox and immediately returns. This confirms notification
   handling, rather than complete, hash-verified, persisted remote bytes.
   Announcement failures are logged and swallowed.
6. The helper commits `DataSetMetadata` through Raft, then the endpoint enqueues
   the request through the existing durable queue path and returns `202`.
   Committed metadata describes a body; it does not replicate that body.
7. Dispatch loads the Raft metadata and calls `PullFileIfMissingAsync`. Remote
   downloads reuse HEAD, HTTP Range, transfer budgets and post-download
   SHA-256/length verification. The worker owns and disposes the resulting stream.
8. Completion commits the queue outcome before scheduling local file cleanup.
   The convergent cleaner also handles TTL and orphan metadata/files. Retry and
   DLQ payload owner IDs already participate in its reference checks.

`POST /data/file` uses the same save/announce mechanism, then commits its metadata
and returns the file ID with HTTP 200. It permits overwrite. Its present response
does not certify two durable copies either. Overwrite races and version identity
need explicit coverage before reusing an old confirmation for a replacement.

## Current failure and repair limits

- A `202` can follow a successful local save even when every remote announcement
  fails, is evicted, or has not yet finished its download. Losing the only node
  and its volume can therefore leave committed metadata without recoverable
  bytes. A transport acknowledgement or a Raft metadata commit cannot establish
  the requested two-copy guarantee.
- Completed asynchronous writes and atomic renames support process-level
  publication, but the implementation does not request persistence of body,
  metadata and directory entries against a host restart. Hardware/storage
  guarantees must remain stated separately from OS flush requests.
- `ExistsAsync` compares the metadata hash and checks file existence. It does
  not verify actual length or body hash. A truncated or modified local body can
  therefore bypass on-demand repair. Pull verification currently happens after
  repository publication; concurrent readers/version changes need coverage.
- The announce worker attempts each received announcement once. Lost or evicted
  announcements have no periodic repair path based on durable Raft references.
  `ClusterFileSyncBootstrapper` only forces service construction and listener
  registration; its start method does not perform an inventory transfer.
- The cleaner requires three consecutive orphan observations, protecting an
  ordinary convergence window. This is not a durable admission reference and
  does not prove safety for an arbitrarily delayed upload/metadata/enqueue path.
  A delayed pull or delete must also be checked against current references so it
  cannot permanently resurrect a released file or remove a replacement version.

## Validation direction

Keep the existing repository, file IDs/metadata, membership, Range transfers,
SHA-256 checks, budgets, workers and on-demand pull. Reproduce the inspection
findings deterministically before changing their behavior. Evaluate a local
publication persistence barrier and confirmation of one complete persisted
remote copy before acceptance, followed by background repair of other members.
Define failure without a confirmed remote copy, membership/leader changes,
admission protection, overwrite identity, expiry and retry/DLQ retention.

Background reconciliation must scan durable live references incrementally after
usable Raft state is available. It must verify local bodies, repair missing or
invalid copies, survive missed announcements and resume interrupted transfers
without holding general readiness until the full history is copied. The same
reference authority must coordinate cleanup and admission.

Measure the acceptance and delivery cost of the stronger guarantee separately
from WAL geometry and threshold changes. Compare 4/8/16/32/64 KiB on fresh WALs,
then 1 MiB/512/256/128 KiB offload thresholds at fixed geometry. Do not change a
default before the targeted faults and repeated measurements support it. Two
stored copies do not imply exactly-once function execution.

## Admission-aware cleanup correction

The later admission component adds durable pending pins. The original cleaner
does not recognize them: its new regression fixture fails five of six cases
(four unsafe-deletion cases and one positive retirement control). The correction
retains pending/current owners and requires an authoritative Raft retirement
before deleting managed bytes. A committed pending index also protects a local
copy whose marker is absent. Managed metadata no longer uses the legacy orphan
heuristic or an unconditional stale-TTL delete. Expired public aliases are
removed only when they still match that admission's bytes.

Retirement and the persisted clock fence late commands/copies after collection.
Invalid persisted clocks fail closed. Seven cleanup regressions pass in the
combined 2,388-test full runtime suite; native/live fault validation remains
separate and pending. The opt-in HTTP path is connected by the runtime follow-up.

## Bounded Range continuation

Three regressions fail before this correction: a truncated range aborts rather
than continuing at the received offset; a zero-byte read incorrectly starts HTTP;
and the new bounded-retry control observes only the original attempt. The
stream now retries I/O/transport interruptions at its successfully returned
offset, at most twice for the complete stream. It does not retry cancellation
or chunk timeouts. The complete body still passes SaveVerified before publication.
The original four response-disposal/deadline controls remain green. Process
restart still starts an unfinished file again from zero; partial bytes are not
a separately durable resumable artifact.
