# Headless DNS and established function connections

Both isolated #467 runs switch to the replacement function around five minutes
of connection age. Both source-189 references continue using the terminating but
still-alive old function through the 420-second post-delete observation. The
four runs reconcile **38,400 calls and successful receipts**, zero confirmed
missing calls, successful duplicates, SHA/length mismatches, unknown outcomes or
headers without operation completion. Actual HTTP is 1.1.

**The original series has a retained health failure.** During candidate three,
node 2 logs a NoLeader/protocol-unavailable error at 00:36:10.035 UTC. This is
inside the original JobStart-based audit window and 1.534 seconds before the
first C# intent at 00:36:11.570 UTC. The series stops before the final reference;
no build or other campaign runs before that missing reference is subsequently
completed with identical frozen code. A separate post-hoc audit, applied to all
four runs from first C# intent through last operation completion on the VM clock,
finds no runtime error. It does not replace the initial failed audit or establish
readiness before traffic. **General non-regression/operational acceptance remains
inconclusive**; this is a qualified connection diagnostic, not capacity evidence.

| Run | C# p95 ms | C# p99 ms | First new success s after delete | Last old success s | Backend connection tuples | Original JobStart health audit |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| hdls-c1-b1-01 | 5.431 | 7.224 | unobserved | 419.87 | 3 | pass |
| hdls-c1-p2-01 | 5.873 | 7.360 | 240.07 | 240.02 | 6 | pass |
| hdls-c1-p3-01 | 6.034 | 7.626 | 239.92 | 239.87 | 6 | pre-traffic error retained |
| hdls-c1-b4-01 | 5.724 | 7.702 | unobserved | 419.97 | 3 | pass |

The baseline/candidate order is 189–29–29–189; runtime sources are
`189826231fe43fe3069e8a1f7724f83589432c6f` and the dedicated #467 cherry-pick
`29daa097fd4562c39e4b362646c16ddd5f1cf7c0`. All use the same C# binaries from
`1b5552b8`, frozen adapter `bbf19e8e`, Linux ARM64 managed/trimmed compilation,
C1, 4 KiB and target 20 requests/second. Configuration is 60 seconds before
replacement and 420 seconds afterward, with the old process held for 600 seconds.
The controller's before timer starts when it observes Job startup; actual C#
intent/completion timestamps remain indexed. Host/VM alignment and uncertainty
are recorded before/after each case; cross-clock convergence is observational.

The function's headless Service omits unready/terminating endpoints, and its DNS
name remains stable while the published pod IP changes. Unlike the ClusterIP
Service case, this exercises changing DNS destinations. Receipts show which
UID/IP served each request and which TCP tuple it used; API snapshots and events
show pod/EndpointSlice transitions. No packet-level DNS query capture is claimed.
Pool expiry does not impose the separate 15-second cache-routing bound and does
not abort an active exchange. These cases leave the old process alive; they do
not establish behavior after complete process exit, C16/async retry safety or
final-source performance. Raw latencies and all observations are retained,
including the third candidate's p95 spread against the two references.

Reproduce with `.bin/run-kubernetes-routing.py --route headless --concurrency 1
--rps 20 --before-seconds 60 --after-seconds 420 --retire-delay 600`, the indexed
images and a fresh owned namespace. The full source/command/resource identities,
original and additional audit windows, measurements and
[raw hashes](kubernetes-headless-pool-lifetime.json) remain available alongside the
[complete file index](kubernetes-headless-pool-raw-index.jsonl.gz).
