# Comparison and evidence rules

## Comparison design

Publish reference and candidate first, then run reference–candidate–candidate–
reference serially with identical common drivers, resources and settings.
Compare each candidate with both enclosing references. If their guardrail
verdicts disagree, repeat the complete block once and retain both observations.
Use the existing matrix's resume mechanism after inspecting completed/failed
ordinals; never overwrite an original failed result to manufacture a pass.

Treat Native AOT, managed JIT and managed/trimmed container measurements
as separate modes. Use identical source-built images under Podman and kind
when comparing the same runtime. Freeze the driver and settings before each
comparison. Use the benchmark guide and the dated campaign configuration for
the exact payloads, concurrency, windows and reference commit.

## Verdicts and resources

Preserve HTTP throughput/tail limits of 10%, CPU/message +20%, RSS +10% and
Raft entries/message ×2. MemoryLab requires throughput >=90% of reference,
p99 <=120%, RSS <=115%, no errors and successful independent reconciliation.
Use implemented comparators and their exact scopes rather than reinterpreting
budgets from a summary. Missing/incomplete references or mandatory metrics make
that comparison INCONCLUSIVE; a separately observed candidate defect remains a
FAIL. Raw and normalized sync results must both stay visible.

Collect initial/final process and Raft boundaries on every runtime node. Counter
reset, stale exporter data and missing nodes invalidate resource acceptance.
State the CPU denominator (accepted, delivered or completed operation), sampler
window, background consensus/probe work, and idle/drain inclusion. Distinguish
sampled peak RSS, aggregate node RSS, managed heap/allocations and container
memory pressure. Lower allocated bytes alone proves neither lower CPU nor RSS.
FD/socket metrics need process identity. TIME_WAIT/interface bytes may cover
the whole pod namespace, including sidecars/probes. Paced C1 routing diagnostics
do not establish saturation capacity or a global CPU/RAM improvement.

## Integrity and faults

Keep accepted, rejected and unknown client outcomes distinct. Reconcile each
ID/function pair against durable independent receipts, SHA-256 and length,
including Content-Length/chunked paths, retries/DLQ, Files/Streams, official
SDKs and real jobs. Preserve successful duplicate receipts, including those
belonging to unknown client outcomes. Zero confirmed loss is narrower than
zero unknown outcomes or exactly-once execution.

Keep synchronous dispatch at one application attempt. For async distinguish
pre-transmission failure, transport entry with unknown execution, actual
response headers, successful receipt and persisted acknowledgment. Join queue
ID, worker generation/try, producer sequence, batch/operation ID and Raft
index/term. An acknowledgment trace alone does not prove the running attempt
matched. Missing logs do not prove non-application. Forensic Trace adds I/O;
measure ordinary performance separately.

Long roles are follower, leader and quorum for Native macOS and kind, with
30/60/300/900/1800/3600-second absences, mixed traffic, successive crashes and
full restart of the original volumes. Preserve the 180-second async recovery/
drain and 15-second leader-route budgets. The distinct Kubernetes protocol
startup allowance is 300seconds; Job wall-time is another independent limit.
Keep probes, namespace and process-scoped interruption controls explicit.
Use a small separate Linux filesystem for disk-full faults.

## Evidence and disk

Keep source/runtime/driver/controller hashes, mode, exact argv/env, image ID and
archive digest, host/resources, UTC windows, clock uncertainty, process/pod UID,
Kubernetes events, all-node counters and isolation observations. Compare actual
HTTP versions and connection reuse/retirement, including established exchanges.

Capture timestamped current/previous container generations and logs before
intentional kill; check earlier-prefix continuity even if UID/restart is
unchanged. A successful last `kubectl logs` is not proof of complete history.
Preserve failed coverage audits separately. Export/hash volumes only after
labelled writers have stopped; retain original WAL/snapshots and state.

Keep at least 20 GiB free on the Mac and on the Podman VM when the benchmark
uses it. Sample free space before every phase and during workloads. Allow
headroom to stop owned writers at a safe boundary before reaching 20 GiB.
Size the shutdown margin from observed writes during shutdown and raise it
if an interruption falls below the reserve.
Resume with several stable readings above 20 GiB and enough room for the next
phase. Keep the heartbeat current with the phase, owner, free space and
deadline. Document time gaps when a comparison resumes after maintenance.

When space is needed, clean up between timed phases. Inspect candidates and
remove only verified inactive, regenerable items within the authorized scope.
This includes temporary build and benchmark artifacts, unused Podman build
cache and images, and temporary Codex or Copilot files. Verify any archive and
its contents before removing a reproducible extracted copy. Preserve raw
measurements, logs, journals, original state, source, sessions, credentials
and tooling used by active jobs. Preserve Podman volumes and containers used
by workloads. Record the removed paths or resource IDs, timestamps and
measured disk space reclaimed. Clear a disk hold only after the stable
readings and headroom checks pass.
