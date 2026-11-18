# Independent repeat of the bounded macOS benchmark campaign

Updated 2026-11-18 03:59 CET. Campaign state: **in progress**.

Product candidate: `bbcf21477a78a973aec53f1f851f2dc3a557d1d8`. The .NET 11 reference is `f36084643a75d803051a6918d2950d1a4f674ada`.
Both products use macOS ARM64 Native AOT. Driver source: `bbcf21477a78a973aec53f1f851f2dc3a557d1d8`, SDK 11.0.100.

The [previous campaign](supervisor-repair-benchmarks.md) measured the same frozen binaries. Every profile in this repeat starts with fresh state.
The disk reserve is 20 GiB, with a 1 GiB margin for stopping active workloads. Timed work is scheduled to stop by 08:45 CET.

This rerun uses fresh state and the repaired supervisor. Omitted arguments run the manifest command without additions. A job whose preparation fails follows its retry policy while other jobs continue.
The WebSocket async runner checks refreshed queue totals on all nodes after receipt reconciliation. File scenarios have an explicit byte budget.
The bounded HTTP comparison uses 5-second load windows, 1-second warmups, two repetitions, concurrency 1/16, and five payload sizes from 4 KiB through 4 MiB. It supplies a shorter ABBA comparison. Quiescent offload files are transparently compressed between roles, with their bytes and SHA-256 preserved.

| Profile | Evidence status | Rows | Observed | Failed | Incomplete |
|---|---|---:|---:|---:|---:|
| data-rerun | OBSERVED | 36 | 36 | 0 | 0 |
| files-bounded-files-upload | OBSERVED | 12 | 12 | 0 | 0 |
| jobs-rerun | OBSERVED | 4 | 4 | 0 | 0 |
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
| data-rerun | decrement | n/a | 1 | 2 | 39.37 | operation/s | n/a | 8.63 | 498.02 |
| data-rerun | decrement | n/a | 16 | 2 | 119.77 | operation/s | n/a | 2.13 | 502.88 |
| data-rerun | delete | 4096 | 1 | 2 | 212.27 | operation/s | n/a | 4.73 | 503.02 |
| data-rerun | delete | 4096 | 16 | 2 | 129.52 | operation/s | n/a | 2.06 | 492.94 |
| data-rerun | get | 4096 | 1 | 2 | 4813.39 | operation/s | n/a | 0.55 | 452.92 |
| data-rerun | get | 4096 | 16 | 2 | 19991.84 | operation/s | n/a | 0.33 | 485.30 |
| data-rerun | hashdelete | 4096 | 1 | 2 | 218.22 | operation/s | n/a | 4.46 | 415.66 |
| data-rerun | hashdelete | 4096 | 16 | 2 | 158.37 | operation/s | n/a | 1.85 | 473.05 |
| data-rerun | hashget | 4096 | 1 | 2 | 4924.99 | operation/s | n/a | 0.53 | 587.12 |
| data-rerun | hashget | 4096 | 16 | 2 | 19633.99 | operation/s | n/a | 0.31 | 456.59 |
| data-rerun | hashset | 4096 | 1 | 2 | 40.43 | operation/s | n/a | 8.74 | 503.42 |
| data-rerun | hashset | 4096 | 16 | 2 | 98.23 | operation/s | n/a | 3.12 | 531.50 |
| data-rerun | increment | n/a | 1 | 2 | 39.88 | operation/s | n/a | 8.42 | 474.44 |
| data-rerun | increment | n/a | 16 | 2 | 105.50 | operation/s | n/a | 2.24 | 486.45 |
| data-rerun | set | 4096 | 1 | 2 | 34.41 | operation/s | n/a | 11.38 | 473.94 |
| data-rerun | set | 4096 | 16 | 2 | 140.08 | operation/s | n/a | 3.43 | 496.80 |
| data-rerun | slimdata-set | 4096 | 1 | 2 | 12.88 | operation/s | n/a | 16.69 | 358.52 |
| data-rerun | slimdata-set | 4096 | 16 | 2 | 112.42 | operation/s | n/a | 2.92 | 447.27 |
| files-bounded-files-upload | files-upload | 262144 | 1 | 2 | 105.64 | operation/s | n/a | 16.03 | 345.84 |
| files-bounded-files-upload | files-upload | 262144 | 4 | 2 | 39.95 | operation/s | n/a | 17.52 | 382.08 |
| files-bounded-files-upload | files-upload | 1048576 | 1 | 2 | 89.70 | operation/s | n/a | 31.65 | 385.80 |
| files-bounded-files-upload | files-upload | 1048576 | 4 | 2 | 43.08 | operation/s | n/a | 30.05 | 396.59 |
| files-bounded-files-upload | files-upload | 4194304 | 1 | 2 | 41.77 | operation/s | n/a | 80.41 | 398.56 |
| files-bounded-files-upload | files-upload | 4194304 | 4 | 2 | 94.89 | operation/s | n/a | 64.15 | 399.81 |
| jobs-rerun | jobs/http/fanout-1 | n/a | 1 | 2 | 140.98 | accepted job/s | 13.15 | 5.29 | 338.91 |
| jobs-rerun | jobs/http/fanout-1 | n/a | 16 | 2 | 1403.81 | accepted job/s | 13.16 | 1.00 | 341.44 |
| streams-rerun | streams/http/fanout-1 | 4096 | 1 | 2 | 122.96 | accepted message/s | 4.01 | 7.62 | 378.91 |
| streams-rerun | streams/http/fanout-1 | 4096 | 16 | 2 | 606.54 | accepted message/s | 4.00 | 2.03 | 443.02 |
| streams-rerun | streams/http/fanout-2 | 4096 | 1 | 2 | 94.94 | accepted message/s | 7.99 | 9.18 | 467.08 |
| streams-rerun | streams/http/fanout-2 | 4096 | 16 | 2 | 570.47 | accepted message/s | 7.99 | 2.10 | 485.27 |
| websocket-rerun | ws-async/dotnet/fanout-1 | 4096 | 1 | 2 | 94.83 | accepted message/s | 37.03 | 7.52 | 401.88 |
| websocket-rerun | ws-async/dotnet/fanout-1 | 4096 | 16 | 2 | 450.29 | accepted message/s | 40.89 | 1.80 | 428.92 |
| websocket-rerun | ws-async/dotnet/fanout-1 | 262144 | 1 | 2 | 34.29 | accepted message/s | 20.12 | 44.44 | 538.75 |
| websocket-rerun | ws-async/dotnet/fanout-1 | 262144 | 16 | 2 | 45.86 | accepted message/s | 20.18 | 34.20 | 868.25 |
| websocket-rerun | ws-async/python/fanout-1 | 4096 | 1 | 2 | 114.15 | accepted message/s | 41.04 | 8.38 | 936.48 |
| websocket-rerun | ws-async/python/fanout-1 | 4096 | 16 | 2 | 1004.53 | accepted message/s | 40.83 | 1.63 | 917.94 |
| websocket-rerun | ws-async/python/fanout-1 | 262144 | 1 | 2 | 42.46 | accepted message/s | 20.10 | 50.22 | 967.78 |
| websocket-rerun | ws-async/python/fanout-1 | 262144 | 16 | 2 | 60.58 | accepted message/s | 41.77 | 38.41 | 1108.94 |
| websocket-rerun | ws-events/dotnet/fanout-2 | 4096 | 1 | 2 | 222.05 | accepted message/s | 125.28 | 2.54 | 862.78 |
| websocket-rerun | ws-events/dotnet/fanout-2 | 4096 | 16 | 2 | 688.43 | accepted message/s | 128.02 | 1.94 | 885.69 |
| websocket-rerun | ws-events/dotnet/fanout-2 | 262144 | 1 | 2 | 127.35 | accepted message/s | 127.89 | 12.83 | 885.67 |
| websocket-rerun | ws-events/dotnet/fanout-2 | 262144 | 16 | 2 | 436.58 | accepted message/s | 127.97 | 11.42 | 850.42 |
| websocket-rerun | ws-events/python/fanout-2 | 4096 | 1 | 2 | 1307.98 | accepted message/s | 1798.63 | 2.10 | 1038.88 |
| websocket-rerun | ws-events/python/fanout-2 | 4096 | 16 | 2 | 3026.69 | accepted message/s | 1362.94 | 1.59 | 982.02 |
| websocket-rerun | ws-events/python/fanout-2 | 262144 | 1 | 2 | 204.67 | accepted message/s | 374.83 | 17.39 | 900.48 |
| websocket-rerun | ws-events/python/fanout-2 | 262144 | 16 | 2 | 814.51 | accepted message/s | 817.22 | 10.08 | 1368.23 |
| websocket-rerun | ws-sync/dotnet/fanout-1 | 4096 | 1 | 2 | 121.48 | request/s | n/a | 3.97 | 333.31 |
| websocket-rerun | ws-sync/dotnet/fanout-1 | 4096 | 16 | 2 | 128.71 | request/s | n/a | 4.15 | 339.31 |
| websocket-rerun | ws-sync/dotnet/fanout-1 | 262144 | 1 | 2 | 113.73 | request/s | n/a | 11.51 | 349.28 |
| websocket-rerun | ws-sync/dotnet/fanout-1 | 262144 | 16 | 2 | 129.20 | request/s | n/a | 11.31 | 408.27 |
| websocket-rerun | ws-sync/python/fanout-1 | 4096 | 1 | 2 | 781.26 | request/s | n/a | 2.85 | 891.94 |
| websocket-rerun | ws-sync/python/fanout-1 | 4096 | 16 | 2 | 1422.17 | request/s | n/a | 2.65 | 913.81 |
| websocket-rerun | ws-sync/python/fanout-1 | 262144 | 1 | 2 | 410.40 | request/s | n/a | 10.91 | 913.98 |
| websocket-rerun | ws-sync/python/fanout-1 | 262144 | 16 | 2 | 609.77 | request/s | n/a | 9.88 | 925.80 |

Full CSV, JSON, Markdown, resource samples and raw evidence remain in the local campaign directory. The final report includes completed comparisons and identifies profiles that did not finish.

## Queue and routing microbenchmarks

Status: **queued**.

## Run status

- **jobs-rerun**: OBSERVED.
- **websocket-rerun**: OBSERVED.
- **streams-rerun**: OBSERVED.
- **data-rerun**: OBSERVED.
- **files-bounded-files-upload**: OBSERVED.
