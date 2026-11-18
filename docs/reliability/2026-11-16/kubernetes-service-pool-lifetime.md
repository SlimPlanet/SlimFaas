# Established function connections: isolated PR #467 Service comparison

Both reference runs keep all calls on the deleted but still-running function pod
through the full 420-second replacement observation. Both isolated #467 runs
start using its replacement around 240 seconds after deletion, corresponding to
the five-minute connection age after 60 seconds of pre-replacement traffic.
All four runs complete 9,600 calls each: **38,400 matching successful receipts**,
zero confirmed missing calls, successful duplicates, SHA/length mismatches,
unknown client outcomes or headers without operation completion. The complete
three-node workload log audits pass; bootstrap observations remain retained.

The order is reference–candidate–candidate–reference. Runtime sources are
`189826231fe43fe3069e8a1f7724f83589432c6f` and the dedicated #467 cherry-pick
`29daa097fd4562c39e4b362646c16ddd5f1cf7c0`. All runs use identical C# generator
binaries from `1b5552b8`, the frozen Kubernetes adapter at `bbf19e8e`, and Linux
ARM64 **managed/trimmed** runtime images. There are no concurrent local builds
or other campaigns. Runtime probes are normal; the function preStop retains its
process for 600 seconds. Bodies are 4 KiB, C1, target 20 requests/second, actual
HTTP/1.1, with 60 seconds before and 420 seconds after replacement.

| Run | C# p95 ms | C# p99 ms | First new success s after deletion | Last old success s | Backend connection tuples | Approx. runtime CPU ms/call | Peak sampled aggregate RSS MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| svc-c1-b1-01 | 5.837 | 7.490 | unobserved | 419.61 | 3 | 9.83 | 485.2 |
| svc-c1-p2-01 | 5.582 | 7.051 | 239.88 | 239.83 | 6 | 10.05 | 488.9 |
| svc-c1-p3-01 | 5.656 | 7.152 | 239.97 | 239.92 | 6 | 9.83 | 501.9 |
| svc-c1-b4-01 | 5.754 | 7.151 | unobserved | 419.86 | 3 | 9.90 | 500.3 |

The three reference backend tuples establish connection reuse through all three
SlimFaas nodes. Each candidate has six tuples: the three original connections and
three replacement connections. The client journal, function UID/IP receipts,
actual runtime send URLs, EndpointSlices and Kubernetes events are retained.
Host/VM clock-offset estimates and round-trip uncertainty bracket every case;
reported cross-clock convergence values are observational, not sub-millisecond
precision. CPU counters include background consensus/probe work and cover a
slightly shorter sampler interval than the 480-second load; they are descriptive.

This demonstrates #467's real effect with established connections. It does not
establish immediate failover: connection retirement happens after the active
exchange finishes. Stable Service DNS keeps the same ClusterIP in this test;
headless DNS address changes need a separate comparison. A terminating process
still able to answer is distinct from a fully stopped or absent destination.
The separate 15-second routing-cache budget does not shorten the five-minute
pool lifetime. This paced diagnostic is not throughput capacity, final-source
non-regression acceptance, proof of the historical production incident, or an
explanation of the seven healthy large-body duplicates.

Reproduce each case with `.bin/run-kubernetes-routing.py`, a fresh
`slimfaas-pr506-routing-*` namespace, the indexed runtime/tools images and
`--route service --concurrency 1 --rps 20 --before-seconds 60 --after-seconds 420
--retire-delay 600`. The C# load Job has no application-level request retry.
Stop owned writers and preserve their volumes before exporting/reconciling.
[Parameters, source/image/executable hashes, exact controller commands and results](kubernetes-service-pool-lifetime.json)
and the [SHA-256 index of the original raw files](kubernetes-service-pool-raw-index.jsonl.gz)
retain all four observations; runtime volumes and originals also remain on the VM.
