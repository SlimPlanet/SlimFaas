# Overnight macOS rerun after the supervisor repair

Updated 2026-11-17 21:26 CET. Campaign state: **in progress**.

Native supervisor smoke: **PASS**. An HTTP job without arguments succeeded, a malformed job failed individually, and the next job succeeded. Results agreed on all three nodes and the supervisor shut down cleanly.

Product candidate: `bbcf21477a78a973aec53f1f851f2dc3a557d1d8`. The .NET 11 reference is `f36084643a75d803051a6918d2950d1a4f674ada`.
Both products use macOS ARM64 Native AOT. Driver source: `bbcf21477a78a973aec53f1f851f2dc3a557d1d8`, SDK 11.0.100.
Preflight passed 2,504 .NET tests, 58 Python controller tests and 11 documentation tests.

The prior campaign and its unrepaired runtime results remain in the [initial campaign report](overnight-benchmarks.md).
This rerun uses fresh state and the repaired supervisor. Omitted arguments run the manifest command without additions. A job whose preparation fails follows its retry policy while other jobs continue.
The WebSocket async runner checks refreshed queue totals on all nodes after receipt reconciliation. File scenarios have an explicit byte budget within the 30 GiB disk reserve.
The original HTTP reference stopped at the disk reserve while testing large bodies. The bounded HTTP rerun uses 5-second load windows, 1-second warmups, two repetitions, concurrency 1/16, and five payload sizes from 4 KiB through 4 MiB. It supplies a shorter ABBA comparison. Quiescent offload files are transparently compressed between roles, with their bytes and SHA-256 preserved.

| Profile | Evidence status | Rows | Observed | Failed | Incomplete |
|---|---|---:|---:|---:|---:|
| data-rerun | OBSERVED | 36 | 36 | 0 | 0 |
| files-bounded-files-download | OBSERVED | 12 | 12 | 0 | 0 |
| files-bounded-files-upload | OBSERVED | 12 | 12 | 0 | 0 |
| files-retry | INCONCLUSIVE | 2 | 2 | 0 | 0 |
| http-reference-comparison | INCONCLUSIVE | 0 | 0 | 0 | 0 |
| jobs-rerun | OBSERVED | 4 | 4 | 0 | 0 |
| memory-reference-comparison | INCONCLUSIVE | 0 | 0 | 0 | 0 |
| streams-rerun | OBSERVED | 8 | 8 | 0 | 0 |
| websocket-rerun | OBSERVED | 48 | 48 | 0 | 0 |

An observation means that the measured run supplied the expected integrity and resource evidence. Comparative acceptance comes from the ABBA verdicts.

## Receiver reconciliation

| Profile | Requests | Expected receipts | Completions | Missing | Duplicates | Corrupt |
|---|---:|---:|---:|---:|---:|---:|
| jobs-rerun | 800 | 800 | 800 | 0 | 0 | 0 |
| streams-rerun | 1600 | 2400 | 2400 | 0 | 0 | 0 |
| websocket-rerun | 9600 | 12800 | 12800 | 0 | 0 | 0 |

## Comparisons

ABBA results are pending.

## Candidate observations

Throughput and CPU are medians across the listed repetitions. RAM is the highest sampled cluster working set among those repetitions during the load window. For Streams and Jobs, accepted throughput covers submission and completed throughput covers submission and drain. The table CPU and RAM values cover submission. Drain resources remain in the raw reports.

| Profile | Scenario | Bytes | C | Runs | Median rate | Unit | Completed/s incl. drain | CPU ms/op | Peak MiB |
|---|---|---:|---:|---:|---:|---|---:|---:|---:|
| data-rerun | decrement | n/a | 1 | 2 | 39.68 | operation/s | n/a | 8.38 | 501.91 |
| data-rerun | decrement | n/a | 16 | 2 | 107.46 | operation/s | n/a | 2.33 | 507.81 |
| data-rerun | delete | 4096 | 1 | 2 | 206.06 | operation/s | n/a | 4.31 | 501.28 |
| data-rerun | delete | 4096 | 16 | 2 | 189.25 | operation/s | n/a | 1.71 | 508.41 |
| data-rerun | get | 4096 | 1 | 2 | 4635.59 | operation/s | n/a | 0.56 | 510.38 |
| data-rerun | get | 4096 | 16 | 2 | 19815.14 | operation/s | n/a | 0.33 | 498.42 |
| data-rerun | hashdelete | 4096 | 1 | 2 | 213.35 | operation/s | n/a | 4.32 | 430.30 |
| data-rerun | hashdelete | 4096 | 16 | 2 | 168.10 | operation/s | n/a | 1.74 | 492.73 |
| data-rerun | hashget | 4096 | 1 | 2 | 4985.17 | operation/s | n/a | 0.54 | 619.56 |
| data-rerun | hashget | 4096 | 16 | 2 | 19437.91 | operation/s | n/a | 0.32 | 462.00 |
| data-rerun | hashset | 4096 | 1 | 2 | 40.05 | operation/s | n/a | 8.69 | 513.73 |
| data-rerun | hashset | 4096 | 16 | 2 | 126.61 | operation/s | n/a | 2.68 | 589.31 |
| data-rerun | increment | n/a | 1 | 2 | 37.61 | operation/s | n/a | 7.81 | 500.88 |
| data-rerun | increment | n/a | 16 | 2 | 104.34 | operation/s | n/a | 2.38 | 501.67 |
| data-rerun | set | 4096 | 1 | 2 | 23.61 | operation/s | n/a | 13.94 | 462.42 |
| data-rerun | set | 4096 | 16 | 2 | 142.55 | operation/s | n/a | 2.93 | 496.91 |
| data-rerun | slimdata-set | 4096 | 1 | 2 | 12.62 | operation/s | n/a | 14.60 | 366.89 |
| data-rerun | slimdata-set | 4096 | 16 | 2 | 159.64 | operation/s | n/a | 1.92 | 433.44 |
| files-bounded-files-download | files-download | 262144 | 1 | 2 | 591.12 | operation/s | n/a | 2.13 | 333.72 |
| files-bounded-files-download | files-download | 262144 | 4 | 2 | 1143.44 | operation/s | n/a | 2.30 | 359.08 |
| files-bounded-files-download | files-download | 1048576 | 1 | 2 | 239.12 | operation/s | n/a | 5.61 | 364.03 |
| files-bounded-files-download | files-download | 1048576 | 4 | 2 | 466.57 | operation/s | n/a | 5.98 | 361.30 |
| files-bounded-files-download | files-download | 4194304 | 1 | 2 | 85.25 | operation/s | n/a | 17.08 | 364.75 |
| files-bounded-files-download | files-download | 4194304 | 4 | 2 | 136.50 | operation/s | n/a | 18.24 | 366.81 |
| files-bounded-files-upload | files-upload | 262144 | 1 | 2 | 105.33 | operation/s | n/a | 17.86 | 346.66 |
| files-bounded-files-upload | files-upload | 262144 | 4 | 2 | 44.98 | operation/s | n/a | 17.54 | 373.45 |
| files-bounded-files-upload | files-upload | 1048576 | 1 | 2 | 86.26 | operation/s | n/a | 31.84 | 380.88 |
| files-bounded-files-upload | files-upload | 1048576 | 4 | 2 | 42.65 | operation/s | n/a | 29.18 | 386.11 |
| files-bounded-files-upload | files-upload | 4194304 | 1 | 2 | 48.15 | operation/s | n/a | 78.90 | 389.14 |
| files-bounded-files-upload | files-upload | 4194304 | 4 | 2 | 89.86 | operation/s | n/a | 69.43 | 389.66 |
| files-retry | files-upload | 262144 | 1 | 2 | 106.45 | operation/s | n/a | 16.21 | 344.77 |
| jobs-rerun | jobs/http/fanout-1 | n/a | 1 | 2 | 131.27 | accepted job/s | 13.03 | 4.96 | 348.52 |
| jobs-rerun | jobs/http/fanout-1 | n/a | 16 | 2 | 1246.31 | accepted job/s | 13.09 | 0.96 | 352.75 |
| streams-rerun | streams/http/fanout-1 | 4096 | 1 | 2 | 134.28 | accepted message/s | 4.00 | 6.10 | 382.86 |
| streams-rerun | streams/http/fanout-1 | 4096 | 16 | 2 | 796.35 | accepted message/s | 4.01 | 1.93 | 442.55 |
| streams-rerun | streams/http/fanout-2 | 4096 | 1 | 2 | 98.96 | accepted message/s | 8.02 | 6.75 | 471.08 |
| streams-rerun | streams/http/fanout-2 | 4096 | 16 | 2 | 1072.85 | accepted message/s | 8.02 | 1.88 | 478.30 |
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
- **streams-rerun**: OBSERVED.
- **data-rerun**: OBSERVED.
- **http-reference-comparison**: INCONCLUSIVE (30 GiB disk reserve).
- **files-retry**: INCONCLUSIVE (30 GiB disk reserve).
- **memory-reference-comparison**: INCONCLUSIVE (30 GiB disk reserve).
- **threshold-512kib**: deferred (30 GiB disk reserve).
- **wal-64kib**: deferred (30 GiB disk reserve).
- **files-bounded-files-upload**: OBSERVED.
- **files-bounded-files-download**: OBSERVED.
