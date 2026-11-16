# Observed Kubernetes runtime errors and remaining history limits

An independent audit combines the three closed in-driver raw archives with the
separate previous-container captures from observer120. It finds **592, 691 and
7,255 distinct timestamped error-level headers** in the follower, leader and
quorum roles. Every observed header falls inside initialization or an explicitly
recorded injected-fault/recovery interval. These are headers, not distinct
exceptions. Repeated captures of the same `(pod UID, timestamped line)` count
once.

This does **not** certify zero healthy runtime errors over the entire campaign.
Retired/rotated log history is not fully demonstrated, and the in-driver
`before-stop` captures contain pod UIDs without container restart indices.
The complete-history/healthy-window verdict remains **INCONCLUSIVE**. Receipt
reconciliation and successful recovery do not remove that limitation.

| Role | Observed unique error headers | Startup | Protocol probe | Long suspensions/recovery | Successive crashes/full restart | Outside these recorded windows |
|---|---:|---:|---:|---:|---:|---:|
| Follower | 592 | 3 | 546 | 10 | 33 | 0 observed |
| Leader | 691 | 3 | 543 | 107 | 38 | 0 observed |
| Quorum | 7,255 | 3 | 546 | 6,678 | 28 | 0 observed |

The audit uses client wall-clock fences, not calibrated distributed traces.
Each suspension window ends at the corresponding post-reconciliation capture;
the crash/restart window ends at full-restart readiness. Classification near
these boundaries remains observational.

## Request exceptions during faults

The original logs retain six unique messages saying that an unhandled exception
occurred **while executing a request**: three in follower and three in quorum.
Every stack starts with Kestrel `BadHttpRequestException: Unexpected end of
request content` while DotNext consumes a Raft `AppendEntries` body through
`DurableWriteAheadLog.AppendAsync`.

| Role | UTC timestamp | Recorded fault/recovery window |
|---|---|---|
| Follower | 08:41:27.575253925 | 30-second follower suspension |
| Follower | 08:42:28.192269634 | 60-second follower suspension |
| Follower | 10:32:55.244031998 | 3,600-second follower suspension |
| Quorum | 13:04:32.866597123 | 300-second quorum suspension |
| Quorum | 13:19:38.402719019 | 900-second quorum suspension |
| Quorum | 13:49:44.177417431 | 1,800-second quorum suspension |

All timestamps are on 16 November 2026. These request-boundary messages do not
by themselves establish process-fatal exceptions. They remain visible and are
not suppressed or reclassified as successful requests. Their client outcomes
and the independent confirmed/unknown ledgers remain unchanged. The examined
captures contain no `OutOfMemoryException` strings or critical-level headers;
this is an observation about the captures, not proof covering uncaptured history.

## Generation coverage and evidence

The observer and pod snapshots imply ten runtime container generations per role,
including the original three, four injected process crashes and three replacement
pods at the complete restart. Explicit restart-index metadata maps captures to
seven follower, seven leader and eight quorum generations. Each role also has
six `before-stop` files with UID-only attribution. These retained files may
represent additional generations, but this audit does not promote that inference
into a complete lifetime log index.

The [audit JSON](kind-runtime-log-audit-189.json) indexes all 111 examined captures
with hashes, timestamp bounds, generation attribution and missing metadata. The
[request-exception detail](kind-unhandled-request-triage-189.json) retains all six
stacks and their source paths. The [audit script](audit-kind-endurance-logs-189.py)
reconstructs the classification from the published role raw archives and the
retained observer120 directory. Original role archives and state remain unchanged.
The supplementary previous-container raw export is pending completion of the
timed matrices; original paths and hashes remain in the audit.

The [endurance rollup](README.md) and [cgroup evidence](kind-quorum-runtime-resources-189.json)
retain separate integrity/recovery and sampled resource observations. None of
these observations constitutes complete log/OOM coverage or LTS certification.
