# Native performance screening — 8 November 2026

See [the reliability verdict](../../slimdata-reliability-results.md). This short same-host comparison cannot certify non-regression.

Baseline v0.88.0 and candidate fixes 1–10 use the same existing driver/manifest. Five-second measurements follow two-second warmups. Six sizes and concurrency 1/4 are tested; the scaling burst uses 200 messages/concurrency 32. No local compilation or other workload campaign ran concurrently.

ABBA was attempted. The second run completed its driver and scaling, but editing the launcher while Bash was reading it caused a post-driver syntax error. That run is excluded from the tables. A replacement candidate failed the 180-second queue-drain budget after Async warmup at 1 MiB/concurrency four. Its remaining cases and scaling were not run. The earlier contaminated comparison is also excluded; all failures are retained.

Values below use **two baseline runs and one complete candidate run**. They are medians of the available per-run values, including the percentile columns; they are not percentiles pooled across runs. Errors/missing/duplicates are totals. Raw results and file hashes are in [performance.json](performance.json).

## Sync response

| Bytes | C | Base req/s | Candidate req/s | Change | Base p50/p95/p99 ms | Candidate p50/p95/p99 ms |
|---:|---:|---:|---:|---:|---:|---:|
| 65,536 | 1 | 680.37 | 741.60 | +9.0% | 1.40/1.79/2.22 | 1.24/1.76/2.05 |
| 65,536 | 4 | 2084.05 | 2168.43 | +4.0% | 1.75/2.75/3.82 | 1.67/2.77/3.70 |
| 262,144 | 1 | 359.04 | 387.75 | +8.0% | 2.59/3.17/9.34 | 2.37/3.23/4.06 |
| 262,144 | 4 | 987.90 | 801.24 | -18.9% | 3.91/5.45/6.52 | 4.60/8.15/10.98 |
| 1,048,576 | 1 | 107.26 | 125.13 | +16.7% | 8.79/16.90/27.72 | 7.20/11.59/23.52 |
| 1,048,576 | 4 | 235.21 | 196.65 | -16.4% | 17.27/25.34/30.99 | 17.05/41.48/60.04 |
| 2,000,000 | 1 | 55.77 | 50.97 | -8.6% | 17.03/27.69/34.67 | 18.79/28.28/35.95 |
| 2,000,000 | 4 | 143.41 | 86.63 | -39.6% | 29.20/38.18/43.55 | 41.40/76.51/122.82 |
| 2,097,152 | 1 | 55.50 | 40.26 | -27.5% | 18.09/27.61/38.85 | 24.18/36.21/46.24 |
| 2,097,152 | 4 | 127.18 | 53.58 | -57.9% | 32.06/42.20/54.63 | 55.71/168.75/242.00 |
| 4,194,304 | 1 | 23.58 | 26.38 | +11.9% | 40.78/57.19/67.27 | 36.21/48.70/53.78 |
| 4,194,304 | 4 | 49.76 | 50.03 | +0.5% | 77.90/101.56/115.32 | 78.10/105.37/131.49 |

## Async HTTP acceptance

| Bytes | C | Base req/s | Candidate req/s | Change | Base p50/p95/p99 ms | Candidate p50/p95/p99 ms |
|---:|---:|---:|---:|---:|---:|---:|
| 65,536 | 1 | 14.26 | 15.32 | +7.5% | 70.48/82.39/103.61 | 65.75/81.10/140.49 |
| 65,536 | 4 | 22.45 | 20.86 | -7.1% | 164.80/259.83/301.86 | 200.87/314.11/348.39 |
| 262,144 | 1 | 5.09 | 4.70 | -7.7% | 191.63/228.20/229.66 | 214.48/249.34/258.82 |
| 262,144 | 4 | 2.35 | 1.40 | -40.4% | 1355.11/5379.81/5484.49 | 674.38/10453.94/10456.32 |
| 1,048,576 | 1 | 1.24 | 1.29 | +4.3% | 854.21/864.30/864.96 | 784.92/908.03/928.06 |
| 1,048,576 | 4 | 0.83 | 1.22 | +46.3% | 5086.02/6093.91/6250.45 | 3229.58/4508.49/4743.29 |
| 2,000,000 | 1 | 11.41 | 5.95 | -47.9% | 86.03/103.02/111.27 | 144.67/286.06/376.18 |
| 2,000,000 | 4 | 7.67 | 7.95 | +3.6% | 513.24/759.87/832.33 | 506.35/914.92/1054.08 |
| 2,097,152 | 1 | 10.84 | 9.33 | -14.0% | 93.26/108.92/114.90 | 101.00/124.83/250.98 |
| 2,097,152 | 4 | 16.89 | 16.35 | -3.2% | 232.14/325.65/350.10 | 238.22/354.74/477.01 |
| 4,194,304 | 1 | 7.25 | 7.59 | +4.6% | 138.56/176.31/194.94 | 127.41/156.25/217.09 |
| 4,194,304 | 4 | 11.60 | 12.87 | +11.0% | 322.13/581.36/650.74 | 285.75/458.41/574.68 |

## Async sampled resources by body size

Concurrency four; three runtime processes combined. CPU/message and sampled maximum RSS come from the existing driver and retain its sampling limitations.

| Bytes | Base CPU ms/message | Candidate CPU ms/message | Base sampled max RSS MiB | Candidate sampled max RSS MiB |
|---:|---:|---:|---:|---:|
| 65,536 | 75.12 | 92.45 | 386.82 | 377.04 |
| 262,144 | 1027.92 | 1513.89 | 506.18 | 506.12 |
| 1,048,576 | 3213.87 | 2039.93 | 626.66 | 713.90 |
| 2,000,000 | 371.24 | 428.20 | 650.44 | 723.57 |
| 2,097,152 | 185.32 | 126.32 | 590.43 | 616.13 |
| 4,194,304 | 176.82 | 147.06 | 564.24 | 581.36 |

## Scaling milestones

| Run | First ready replica ms | Four ready replicas ms | Queue drained ms |
|---|---:|---:|---:|
| paired-native-final/01-baseline | 4248.4 | 7474.7 | 23824.0 |
| paired-native-final/03-candidate | 3167.7 | 6345.7 | 22333.7 |
| paired-native-final/04-baseline | 4314.6 | 8634.2 | 25479.6 |

## Correctness and resources

- `paired-native-final/01-baseline`: wrapper exit 0; 0 HTTP errors, 0 missing Async, 0 duplicate Async; scaling failed=0, timed_out=False.
- `paired-native-final/02-candidate`: wrapper exit 2; 0 HTTP errors, 0 missing Async, 0 duplicate Async; scaling failed=0, timed_out=False.
- `paired-native-final/03-candidate`: wrapper exit 0; 0 HTTP errors, 0 missing Async, 0 duplicate Async; scaling failed=0, timed_out=False.
- `paired-native-final/04-baseline`: wrapper exit 0; 0 HTTP errors, 0 missing Async, 0 duplicate Async; scaling failed=0, timed_out=False.
- `paired-native-confirmation/01-candidate`: wrapper exit 1; **failed**, 1 MiB/concurrency 4 Async warmup queue did not drain within 180 seconds. Partial raw CSV and metrics retained; no final JSON or successful scaling verdict.

| Variant/run | Cluster peak RSS MiB | CPU seconds | Allocated GiB | GC collections | GC pause seconds | Max node WAL since snapshot MiB |
|---|---:|---:|---:|---:|---:|---:|
| paired-native-final/01-baseline | 654.8 | 485.8 | 3.53 | 193 | 0.971 | 28.0 |
| paired-native-final/03-candidate | 723.6 | 463.5 | 3.74 | 212 | 1.117 | 31.9 |
| paired-native-final/04-baseline | 707.6 | 492.2 | 3.81 | 212 | 1.063 | 29.3 |

These whole-run metrics include warmups, draining and scaling, whose durations differ. They cannot be interpreted as normalized cost per request. Per-size Async CPU/message, peak working set, Raft entries/message and actual function-arrival percentiles are preserved in the JSON. Gauges update at a coarser cadence than some short phases. CPU/RSS cover the three runtime processes, excluding the driver and function processes. The failed state archive retains 197,070 files / 815,372,782 logical bytes, including logs; this is a final footprint observation, not a measured growth rate or root cause.

## Existing MemoryLab data workloads

Each binary has two real three-node persistent Local runs. Each scenario uses 4 KiB values, 16 keys/worker, concurrency 12/48, two-second warmup and ten-second measurement, with `--validate true`. This is a separate sequential comparison.

The initial setup attempt is excluded: the existing driver hardcodes `memory-function`, which was missing from that first manifest. It returned ten Async 404s while KV validation reported no errors. The corrected fresh comparison declares that real function, verifies every distinct delivered Async element ID and its 4 KiB checksum, and retains original failures.

| Scenario | C | Base ops/s | Candidate ops/s | Change | Base p50/p95/p99 ms | Candidate p50/p95/p99 ms | Errors base/candidate |
|---|---:|---:|---:|---:|---:|---:|---:|
| slimdata-set | 12 | 76.36 | 66.57 | -12.8% | 138.37/322.51/464.51 | 171.41/345.41/396.52 | 0/0 |
| slimdata-set | 48 | 145.57 | 144.95 | -0.4% | 271.77/597.30/1161.80 | 299.39/620.72/1012.22 | 0/0 |
| slimdata-mixed | 12 | 130.83 | 121.97 | -6.8% | 57.42/224.81/322.46 | 59.05/248.67/333.90 | 0/0 |
| slimdata-mixed | 48 | 290.09 | 315.27 | +8.7% | 127.49/314.22/803.99 | 116.65/318.59/756.74 | 0/0 |

Raw CSV/JSON/logs, runtime/driver hashes and per-phase CPU/RAM/allocation/GC and WAL/snapshot samples are retained. `performance.json` indexes each measured load and its sampled resources. Phase resource windows also include driver validation. Wire replication bandwidth was **not measured**. Neither byte payload rate nor a resettable WAL gauge is a substitute for it.

These small unequal Native samples and the two-per-binary MemoryLab repetitions on a shared developer laptop show substantial variance; individual tail regressions remain visible. These measurements do not establish a performance non-regression guarantee. Longer controlled repeats and Windows large-Stream availability must be resolved before LTS acceptance.

## Existing normalized comparison

The existing `SlimFaasBenchmark compare` command was also run against each baseline and the complete candidate. Both return exit 1. Its optimization targets additionally demand a latency improvement, which is not this reliability change's objective; importantly, **its non-regression guardrails also fail**.

| Baseline | Sync 2 MiB / C4 change in proxy-to-direct throughput ratio | Guardrails |
|---|---:|---|
| [native-comparison-1.json](native-comparison-1.json) | -53.8% | Failed |
| [native-comparison-4.json](native-comparison-4.json) | -43.1% | Failed |

These observed regressions are not waived because the samples are short or noisy. They require investigation and controlled repeats, alongside the separate 180-second queue-drain failure. No causal attribution to a particular fix or to AOT alone is established.
