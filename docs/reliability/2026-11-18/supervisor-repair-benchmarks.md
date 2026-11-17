# Overnight macOS rerun after the supervisor repair

Updated 2026-11-17 22:49 CET. Campaign state: **in progress**.

Native supervisor smoke: **PASS**. An HTTP job without arguments succeeded, a malformed job failed individually, and the next job succeeded. Results agreed on all three nodes and the supervisor shut down cleanly.

Product candidate: `bbcf21477a78a973aec53f1f851f2dc3a557d1d8`. The .NET 11 reference is `f36084643a75d803051a6918d2950d1a4f674ada`.
Both products use macOS ARM64 Native AOT. Driver source: `bbcf21477a78a973aec53f1f851f2dc3a557d1d8`, SDK 11.0.100.
Preflight passed 2,504 .NET tests, 58 Python controller tests and 11 documentation tests.

The operator set the minimum free disk reserve to 20 GiB for subsequent blocks. The initial phases used the original 30 GiB cutoff. Historical run statuses retain the cutoff that applied at the time.

The prior campaign and its unrepaired runtime results remain in the [initial campaign report](overnight-benchmarks.md).
This rerun uses fresh state and the repaired supervisor. Omitted arguments run the manifest command without additions. A job whose preparation fails follows its retry policy while other jobs continue.
The WebSocket async runner checks refreshed queue totals on all nodes after receipt reconciliation. File scenarios have an explicit byte budget.
The original HTTP reference stopped at the disk reserve while testing large bodies. The bounded HTTP rerun uses 5-second load windows, 1-second warmups, two repetitions, concurrency 1/16, and five payload sizes from 4 KiB through 4 MiB. It supplies a shorter ABBA comparison. Quiescent offload files are transparently compressed between roles, with their bytes and SHA-256 preserved.

| Profile | Evidence status | Rows | Observed | Failed | Incomplete |
|---|---|---:|---:|---:|---:|
| data-rerun | OBSERVED | 36 | 36 | 0 | 0 |
| files-bounded-files | OBSERVED | 12 | 12 | 0 | 0 |
| files-bounded-files-download | OBSERVED | 12 | 12 | 0 | 0 |
| files-bounded-files-unique | OBSERVED | 12 | 12 | 0 | 0 |
| files-bounded-files-upload | OBSERVED | 12 | 12 | 0 | 0 |
| files-retry | INCONCLUSIVE | 2 | 2 | 0 | 0 |
| http-bounded-abba | FAIL | 448 | 448 | 0 | 0 |
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

A and B refer to the first and second variants, respectively. For code comparisons, A is the reference and B is the candidate. For settings comparisons, A uses the current settings and B uses the measured change. Each table includes matched cases with evidence across all four roles of the attempt. The displayed throughput, p95 and CPU are medians across repetitions. RAM is the highest sampled cluster working set. The verdicts above use the comparison criteria for the corresponding profile.

<details><summary>http-bounded-abba / http-attempt-1 (FAIL)</summary>

| Scenario | Bytes | C | Unit | Rate A → B | Completed/s A → B | p95 ms A → B | CPU ms/op A → B | Peak MiB A → B |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| async | 4096 | 1 | accepted message/s | 102.92 → 97.18 | 83.88 → 76.00 | 11.77 → 12.68 | 7.66 → 10.29 | 397.17 → 411.09 |
| async | 4096 | 16 | accepted message/s | 1107.69 → 1087.77 | 883.77 → 852.91 | 20.40 → 20.57 | 1.78 → 1.79 | 498.17 → 491.61 |
| async | 262144 | 1 | accepted message/s | 38.86 → 37.23 | 29.86 → 29.22 | 39.80 → 41.87 | 50.62 → 51.02 | 687.84 → 630.67 |
| async | 262144 | 16 | accepted message/s | 51.81 → 51.84 | 42.20 → 42.01 | 503.72 → 476.33 | 37.25 → 40.05 | 1655.31 → 1009.00 |
| async | 1048576 | 1 | accepted message/s | 11.89 → 12.55 | 9.34 → 9.95 | 127.43 → 124.80 | 158.44 → 165.24 | 1563.48 → 927.17 |
| async | 1048576 | 16 | accepted message/s | 13.04 → 13.96 | 10.86 → 11.25 | 1701.24 → 1593.42 | 131.48 → 130.90 | 3262.16 → 2029.48 |
| async | 1048577 | 1 | accepted message/s | 40.99 → 44.96 | 35.73 → 35.88 | 28.78 → 26.43 | 45.45 → 46.16 | 3084.91 → 1896.52 |
| async | 1048577 | 16 | accepted message/s | 256.55 → 279.69 | 219.13 → 225.17 | 76.79 → 78.18 | 18.10 → 19.81 | 2818.17 → 1205.67 |
| async | 4194304 | 1 | accepted message/s | 36.23 → 35.58 | 30.85 → 28.37 | 31.34 → 31.28 | 91.51 → 98.46 | 2161.72 → 801.11 |
| async | 4194304 | 16 | accepted message/s | 92.35 → 82.44 | 68.97 → 65.53 | 231.38 → 334.33 | 61.55 → 65.90 | 1905.91 → 652.75 |
| async-burst | 4096 | 64 | accepted message/s | 2126.47 → 2104.64 | 861.32 → 537.36 | 46.90 → 49.29 | 1.33 → 1.48 | 1503.27 → 628.97 |
| async-paced | 4096 | 1 | accepted message/s | 9.24 → 8.91 | 8.33 → 7.91 | 13.34 → 17.57 | 38.80 → 48.99 | 1488.00 → 603.66 |
| direct | 4096 | 1 | request/s | 9729.34 → 9972.11 | n/a → n/a | 0.12 → 0.11 | 0.01 → 0.01 | 377.22 → 391.61 |
| direct | 4096 | 16 | request/s | 23444.60 → 22865.84 | n/a → n/a | 0.74 → 0.75 | 0.00 → 0.00 | 482.38 → 491.52 |
| direct | 262144 | 1 | request/s | 2784.36 → 2790.47 | n/a → n/a | 0.43 → 0.43 | 0.03 → 0.03 | 622.67 → 601.62 |
| direct | 262144 | 16 | request/s | 5964.13 → 5976.21 | n/a → n/a | 2.83 → 2.84 | 0.01 → 0.01 | 1406.28 → 874.72 |
| direct | 1048576 | 1 | request/s | 677.26 → 597.43 | n/a → n/a | 2.05 → 2.13 | 0.12 → 0.13 | 1655.31 → 916.30 |
| direct | 1048576 | 16 | request/s | 1811.17 → 1836.27 | n/a → n/a | 9.39 → 9.34 | 0.05 → 0.05 | 2781.22 → 1799.19 |
| direct | 1048577 | 1 | request/s | 627.28 → 582.87 | n/a → n/a | 2.09 → 2.09 | 0.12 → 0.13 | 3264.44 → 2031.27 |
| direct | 1048577 | 16 | request/s | 1820.51 → 1872.48 | n/a → n/a | 9.33 → 9.23 | 0.05 → 0.05 | 2717.34 → 1384.72 |
| direct | 4194304 | 1 | request/s | 240.17 → 227.37 | n/a → n/a | 6.66 → 7.05 | 0.36 → 0.37 | 2162.66 → 815.12 |
| direct | 4194304 | 16 | request/s | 463.33 → 460.89 | n/a → n/a | 37.33 → 37.73 | 0.29 → 0.20 | 2039.30 → 686.59 |
| sync | 4096 | 1 | request/s | 3015.76 → 3001.76 | n/a → n/a | 0.36 → 0.37 | 0.52 → 0.55 | 395.28 → 401.34 |
| sync | 4096 | 16 | request/s | 7808.82 → 7754.39 | n/a → n/a | 2.36 → 2.39 | 0.47 → 0.47 | 500.05 → 505.91 |
| sync | 262144 | 1 | request/s | 1170.83 → 1190.55 | n/a → n/a | 0.96 → 0.94 | 1.67 → 1.63 | 620.98 → 599.97 |
| sync | 262144 | 16 | request/s | 1594.86 → 1602.27 | n/a → n/a | 10.51 → 10.48 | 1.89 → 1.89 | 1406.20 → 955.22 |
| sync | 1048576 | 1 | request/s | 383.68 → 383.21 | n/a → n/a | 2.81 → 2.81 | 5.32 → 5.42 | 1655.81 → 949.64 |
| sync | 1048576 | 16 | request/s | 447.95 → 447.98 | n/a → n/a | 37.06 → 37.17 | 6.74 → 6.84 | 2779.78 → 1859.94 |
| sync | 1048577 | 1 | request/s | 381.42 → 381.24 | n/a → n/a | 2.82 → 2.83 | 5.33 → 5.37 | 3266.22 → 2032.05 |
| sync | 1048577 | 16 | request/s | 448.65 → 448.20 | n/a → n/a | 37.25 → 36.82 | 6.63 → 6.73 | 2719.64 → 1384.92 |
| sync | 4194304 | 1 | request/s | 104.34 → 104.94 | n/a → n/a | 10.35 → 10.27 | 19.60 → 19.64 | 2162.67 → 824.83 |
| sync | 4194304 | 16 | request/s | 111.79 → 111.56 | n/a → n/a | 150.67 → 151.76 | 25.48 → 25.85 | 2046.56 → 686.67 |

</details>


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
| files-bounded-files | files | 262144 | 1 | 2 | 71.98 | operation/s | n/a | 23.28 | 333.55 |
| files-bounded-files | files | 262144 | 4 | 2 | 48.94 | operation/s | n/a | 18.76 | 378.77 |
| files-bounded-files | files | 1048576 | 1 | 2 | 41.97 | operation/s | n/a | 41.58 | 383.80 |
| files-bounded-files | files | 1048576 | 4 | 2 | 68.29 | operation/s | n/a | 33.05 | 393.06 |
| files-bounded-files | files | 4194304 | 1 | 2 | 20.48 | operation/s | n/a | 101.60 | 396.28 |
| files-bounded-files | files | 4194304 | 4 | 2 | 56.53 | operation/s | n/a | 88.37 | 397.09 |
| files-bounded-files-download | files-download | 262144 | 1 | 2 | 591.12 | operation/s | n/a | 2.13 | 333.72 |
| files-bounded-files-download | files-download | 262144 | 4 | 2 | 1143.44 | operation/s | n/a | 2.30 | 359.08 |
| files-bounded-files-download | files-download | 1048576 | 1 | 2 | 239.12 | operation/s | n/a | 5.61 | 364.03 |
| files-bounded-files-download | files-download | 1048576 | 4 | 2 | 466.57 | operation/s | n/a | 5.98 | 361.30 |
| files-bounded-files-download | files-download | 4194304 | 1 | 2 | 85.25 | operation/s | n/a | 17.08 | 364.75 |
| files-bounded-files-download | files-download | 4194304 | 4 | 2 | 136.50 | operation/s | n/a | 18.24 | 366.81 |
| files-bounded-files-unique | files-unique | 262144 | 1 | 2 | 140.44 | operation/s | n/a | 17.89 | 330.75 |
| files-bounded-files-unique | files-unique | 262144 | 4 | 2 | 56.21 | operation/s | n/a | 16.56 | 373.42 |
| files-bounded-files-unique | files-unique | 1048576 | 1 | 2 | 63.08 | operation/s | n/a | 34.29 | 380.16 |
| files-bounded-files-unique | files-unique | 1048576 | 4 | 2 | 98.79 | operation/s | n/a | 25.87 | 389.77 |
| files-bounded-files-unique | files-unique | 4194304 | 1 | 2 | 30.99 | operation/s | n/a | 89.03 | 393.36 |
| files-bounded-files-unique | files-unique | 4194304 | 4 | 2 | 102.04 | operation/s | n/a | 59.39 | 390.06 |
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

## Queue and routing microbenchmarks

Status: **queued**.

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
- **files-bounded-files**: OBSERVED.
- **files-bounded-files-unique**: OBSERVED.
- **http-bounded-abba**: FAIL (30 GiB disk reserve).
