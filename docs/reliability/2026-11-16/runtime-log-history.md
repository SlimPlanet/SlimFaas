# Detecting incomplete Kubernetes log history

The source-f2 direct-IP async replacement diagnostic accepts **2,880 messages**
and reconciles 2,880 successful length/SHA-256 receipts, zero successful duplicates
or confirmed missing deliveries. Its detailed leader history is nevertheless
**inconclusive**. The last log snapshot starts at 06:16:21 UTC, after the
06:14:36 startup, and contains only twelve transport-stage records. Kubelet
rotation discarded an earlier part of that container's output. The pod UID and
restart count did not change, so the old auditor incorrectly called coverage
complete. Original files and the earlier audit are retained; missing trace
records are not reconstructed from successful receipts.

A regression on the previous collector fails: disjoint initial/final snapshots
with the same UID/restart incorrectly pass. The correction compares the selected
canonical snapshot with earlier successfully captured prefixes, checks their
hashes and reports missing prefixes as incomplete history. A growing-log control
still passes. Fifty platform tests pass, including the actual MemoryLab endpoint
checks restored from its exact source-f2 publication after cache reclamation;
routing, chaos/accounting and Sonar-boundary tests also pass.

The in-cluster chaos backend now reuses the host collector through its existing
Kubernetes API transport. Both retain immutable pod UID/restart/current/previous
snapshots, rather than duplicating collection logic. Capturing before successive
intentional kills preserves the generation that would otherwise disappear from
kubelet's single `previous` slot. Current runtime binaries, probes, retry policy
and the 15/180-second scenario deadlines are unchanged.

While all campaign writers were stopped, the four dedicated kind nodes were
configured with `containerLogMaxSize: 128Mi`. Original node configurations are
retained and each effective setting was verified through `configz`. An initial
read immediately after restarting kubelet gets connection refused; that failed
preflight remains recorded before bounded readiness polling succeeds. This is
separate from runtime performance and recovery results. New comparisons must use
the same retention configuration for both variants. The initial trace diagnostic
remains incomplete and needs a fresh run; increasing retention does not repair
its history or prove the absence of other gaps.

The [structured evidence](runtime-log-history.json) records the actual prefix gap,
source identities, node settings, reproduction hash and validation. Other
source-f2 replacement diagnostics retain their sync/mixed fault-time HTTP 503s.
The first C1 case also retains its original post-budget audit failure: its
failing request starts during replacement and returns 503 after its existing
15-second timeout. C1 cannot observe cache convergence while that request is
blocked; its first replacement success at 15.26 seconds is not a passing
15-second convergence measurement. C16 termination instead reconciles all
2,694 calls, with first replacement success at 3.90 seconds and no old-pod receipt
after fifteen seconds. Neither result attributes the historical seven healthy
duplicate executions or establishes final performance/LTS acceptance.
