# Overnight macOS rerun after the supervisor repair

Updated 2026-11-17 20:15 CET. Campaign state: **in progress**.

Native supervisor smoke: **PASS**. An HTTP job without arguments succeeded, a malformed job failed individually, and the next job succeeded. Results agreed on all three nodes and the supervisor shut down cleanly.

Product candidate: `bbcf21477a78a973aec53f1f851f2dc3a557d1d8`. The .NET 11 reference is `f36084643a75d803051a6918d2950d1a4f674ada`.
Both products use macOS ARM64 Native AOT. Driver source: `bbcf21477a78a973aec53f1f851f2dc3a557d1d8`, SDK 11.0.100.
Preflight passed 2,504 .NET tests, 58 Python controller tests and 11 documentation tests.

The prior campaign and its unrepaired runtime results remain in the [initial campaign report](overnight-benchmarks.md).
This rerun uses fresh state and the repaired supervisor. Omitted arguments run the manifest command without additions. A job whose preparation fails follows its retry policy while other jobs continue.
The WebSocket async runner checks refreshed queue totals on all nodes after receipt reconciliation. File scenarios have an explicit byte budget within the 30 GiB disk reserve.

| Profile | Evidence status | Rows | Observed | Failed | Incomplete |
|---|---|---:|---:|---:|---:|
| jobs-rerun | OBSERVED | 4 | 4 | 0 | 0 |
| websocket-rerun | OBSERVED | 48 | 48 | 0 | 0 |

An observation means that the measured run supplied the expected integrity and resource evidence. Comparative acceptance comes from the ABBA verdicts.

## Comparisons

ABBA results are pending.

## Candidate observations

Throughput and CPU are medians across the listed repetitions. RAM is the highest sampled cluster working set among those repetitions during the load window. For Streams and Jobs, accepted throughput covers submission and completed throughput covers submission and drain. The table CPU and RAM values cover submission. Drain resources remain in the raw reports.

| Profile | Scenario | Bytes | C | Runs | Median rate | Unit | Completed/s incl. drain | CPU ms/op | Peak MiB |
|---|---|---:|---:|---:|---:|---|---:|---:|---:|
| jobs-rerun | jobs/http/fanout-1 | n/a | 1 | 2 | 131.27 | accepted job/s | 13.03 | 4.96 | 348.52 |
| jobs-rerun | jobs/http/fanout-1 | n/a | 16 | 2 | 1246.31 | accepted job/s | 13.09 | 0.96 | 352.75 |
| websocket-rerun | ws-async/dotnet/fanout-1 | 4096 | 1 | 2 | 80.74 | accepted message/s | 38.57 | 9.00 | 400.81 |
| websocket-rerun | ws-async/dotnet/fanout-1 | 4096 | 16 | 2 | 379.11 | accepted message/s | 41.03 | 2.32 | 429.84 |
| websocket-rerun | ws-async/dotnet/fanout-1 | 262144 | 1 | 2 | 31.16 | accepted message/s | 20.21 | 53.94 | 643.31 |
| websocket-rerun | ws-async/dotnet/fanout-1 | 262144 | 16 | 2 | 40.31 | accepted message/s | 20.24 | 40.73 | 1224.50 |
| websocket-rerun | ws-async/python/fanout-1 | 4096 | 1 | 2 | 124.31 | accepted message/s | 52.65 | 8.12 | 956.08 |
| websocket-rerun | ws-async/python/fanout-1 | 4096 | 16 | 2 | 945.74 | accepted message/s | 40.80 | 2.38 | 925.06 |
| websocket-rerun | ws-async/python/fanout-1 | 262144 | 1 | 2 | 37.35 | accepted message/s | 20.21 | 59.11 | 987.89 |
| websocket-rerun | ws-async/python/fanout-1 | 262144 | 16 | 2 | 54.18 | accepted message/s | 40.56 | 41.07 | 1273.84 |
| websocket-rerun | ws-events/dotnet/fanout-2 | 4096 | 1 | 2 | 192.03 | accepted message/s | 118.85 | 2.68 | 1187.81 |
| websocket-rerun | ws-events/dotnet/fanout-2 | 4096 | 16 | 2 | 626.04 | accepted message/s | 120.31 | 2.08 | 1208.97 |
| websocket-rerun | ws-events/dotnet/fanout-2 | 262144 | 1 | 2 | 134.79 | accepted message/s | 119.15 | 14.93 | 1206.12 |
| websocket-rerun | ws-events/dotnet/fanout-2 | 262144 | 16 | 2 | 487.55 | accepted message/s | 117.43 | 12.13 | 1142.78 |
| websocket-rerun | ws-events/python/fanout-2 | 4096 | 1 | 2 | 1218.02 | accepted message/s | 1719.99 | 2.14 | 1187.39 |
| websocket-rerun | ws-events/python/fanout-2 | 4096 | 16 | 2 | 3062.87 | accepted message/s | 1374.57 | 1.47 | 1147.72 |
| websocket-rerun | ws-events/python/fanout-2 | 262144 | 1 | 2 | 198.49 | accepted message/s | 365.39 | 17.17 | 1147.94 |
| websocket-rerun | ws-events/python/fanout-2 | 262144 | 16 | 2 | 780.17 | accepted message/s | 801.28 | 10.13 | 1658.00 |
| websocket-rerun | ws-sync/dotnet/fanout-1 | 4096 | 1 | 2 | 110.03 | request/s | n/a | 4.21 | 340.88 |
| websocket-rerun | ws-sync/dotnet/fanout-1 | 4096 | 16 | 2 | 118.68 | request/s | n/a | 4.12 | 353.19 |
| websocket-rerun | ws-sync/dotnet/fanout-1 | 262144 | 1 | 2 | 108.32 | request/s | n/a | 10.76 | 355.94 |
| websocket-rerun | ws-sync/dotnet/fanout-1 | 262144 | 16 | 2 | 121.74 | request/s | n/a | 10.77 | 407.34 |
| websocket-rerun | ws-sync/python/fanout-1 | 4096 | 1 | 2 | 832.77 | request/s | n/a | 2.78 | 972.81 |
| websocket-rerun | ws-sync/python/fanout-1 | 4096 | 16 | 2 | 1524.99 | request/s | n/a | 2.61 | 986.42 |
| websocket-rerun | ws-sync/python/fanout-1 | 262144 | 1 | 2 | 390.78 | request/s | n/a | 11.14 | 986.48 |
| websocket-rerun | ws-sync/python/fanout-1 | 262144 | 16 | 2 | 603.32 | request/s | n/a | 9.89 | 950.25 |

Full CSV, JSON, Markdown, resource samples and raw evidence remain in the local campaign directory. The final report includes completed comparisons and identifies profiles that did not finish.

## Run status

- **jobs-rerun**: OBSERVED.
- **websocket-rerun**: OBSERVED.
