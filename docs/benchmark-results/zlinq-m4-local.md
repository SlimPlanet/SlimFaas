# Local JIT / Native AOT before-and-after measurements

Function dispatch capacity: 16 globally and per target replica.

All cells are medians across independent clean-cluster repetitions. CPU and RSS sum the three runtime nodes.
RSS is the median sampled peak, not managed bytes allocated per operation. Data rows count workload operations.

Completed repetitions per revision: JIT 3; Native AOT 2. Original planned rounds: 3.
Stop Native AOT retries after two unsuccessful heavy-body attempts. Use every completed repetition with balanced before/after counts within each runtime mode. Planned three rounds remain in the original manifest; three primary failed attempts are retained separately.

Archived unsuccessful attempts: 3. Performance statistics cover completed repetitions only and do not establish reliability.

| Runtime | N per revision | Workload | Bytes | C | Before op/s | After op/s | Rate Δ | Before p95 ms | After p95 ms | Before CPU ms/op | After CPU ms/op | Before RSS MiB | After RSS MiB |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| jit | 3 | async | 64 | 1 | 109.21 | 108.51 | -0.6% | 10.730 | 10.764 | 11.5733 | 13.5925 | 905.8 | 907.8 |
| jit | 3 | async | 64 | 16 | 1464.38 | 1488.86 | +1.7% | 13.790 | 13.553 | 4.1425 | 4.1833 | 1106.0 | 1109.7 |
| jit | 3 | async | 4096 | 1 | 104.86 | 107.19 | +2.2% | 11.385 | 11.528 | 12.7224 | 13.2663 | 1101.3 | 1108.8 |
| jit | 3 | async | 4096 | 16 | 1129.43 | 1128.96 | -0.0% | 17.941 | 17.644 | 4.6668 | 4.6389 | 2392.2 | 2252.3 |
| jit | 3 | async | 2097152 | 1 | 44.85 | 46.65 | +4.0% | 27.585 | 27.937 | 77.9161 | 72.5568 | 2594.3 | 2380.7 |
| jit | 3 | async | 2097152 | 16 | 130.62 | 158.93 | +21.7% | 115.157 | 259.041 | 41.0248 | 36.3661 | 2504.9 | 2289.4 |
| jit | 3 | files | 262144 | 16 | 384.18 | 365.39 | -4.9% | 81.200 | 100.843 | 6.3636 | 6.3316 | 1651.7 | 1557.5 |
| jit | 3 | slimdata-mixed | 4096 | 16 | 573.57 | 578.53 | +0.9% | 90.475 | 91.198 | 2.0118 | 1.7418 | 1886.4 | 1850.2 |
| jit | 3 | slimdata-set | 4096 | 16 | 344.79 | 317.77 | -7.8% | 108.322 | 115.472 | 2.4042 | 2.3541 | 2469.3 | 2342.7 |
| jit | 3 | streams | 4096 | 16 | 3.99 | 4.00 | +0.1% | 20.108 | 18.258 | 80.7072 | 79.0260 | 1529.2 | 1440.0 |
| jit | 3 | sync | 64 | 1 | 3312.07 | 3310.91 | -0.0% | 0.334 | 0.337 | 0.4875 | 0.4891 | 740.5 | 739.6 |
| jit | 3 | sync | 64 | 16 | 10220.11 | 10329.06 | +1.1% | 1.707 | 1.687 | 0.5146 | 0.5065 | 982.7 | 986.0 |
| jit | 3 | sync | 4096 | 1 | 2942.22 | 2947.34 | +0.2% | 0.373 | 0.384 | 0.5607 | 0.5689 | 1099.7 | 1094.4 |
| jit | 3 | sync | 4096 | 16 | 7064.42 | 7090.89 | +0.4% | 2.634 | 2.614 | 0.7303 | 0.7224 | 1107.8 | 1116.9 |
| jit | 3 | sync | 2097152 | 1 | 217.36 | 219.03 | +0.8% | 4.978 | 4.946 | 12.9385 | 13.0744 | 2515.2 | 2348.4 |
| jit | 3 | sync | 2097152 | 16 | 225.01 | 225.46 | +0.2% | 74.726 | 73.768 | 18.9567 | 19.3147 | 2506.7 | 2259.8 |
| aot | 2 | async | 64 | 1 | 96.67 | 98.01 | +1.4% | 12.461 | 12.046 | 9.1007 | 8.7993 | 790.5 | 821.4 |
| aot | 2 | async | 64 | 16 | 1386.90 | 1393.97 | +0.5% | 14.507 | 14.325 | 3.1735 | 3.0870 | 1018.3 | 1007.6 |
| aot | 2 | async | 4096 | 1 | 91.87 | 97.22 | +5.8% | 13.631 | 12.751 | 11.2704 | 10.0806 | 858.5 | 862.7 |
| aot | 2 | async | 4096 | 16 | 1091.34 | 1090.44 | -0.1% | 18.338 | 18.536 | 3.8605 | 3.7975 | 2080.5 | 2067.0 |
| aot | 2 | async | 2097152 | 1 | 41.20 | 45.15 | +9.6% | 30.027 | 28.282 | 54.3843 | 54.8999 | 2200.0 | 2129.0 |
| aot | 2 | async | 2097152 | 16 | 186.44 | 192.38 | +3.2% | 98.750 | 108.268 | 34.3872 | 33.1599 | 2125.5 | 1970.9 |
| aot | 2 | files | 262144 | 16 | 363.89 | 272.87 | -25.0% | 108.500 | 166.557 | 5.4994 | 5.9814 | 1216.2 | 1193.2 |
| aot | 2 | slimdata-mixed | 4096 | 16 | 570.89 | 557.61 | -2.3% | 90.848 | 91.053 | 1.5833 | 1.6141 | 1579.9 | 1422.4 |
| aot | 2 | slimdata-set | 4096 | 16 | 368.91 | 351.00 | -4.9% | 95.116 | 109.055 | 1.9011 | 1.9945 | 2097.3 | 1794.7 |
| aot | 2 | streams | 4096 | 16 | 4.00 | 3.89 | -2.7% | 19.152 | 22.023 | 64.4309 | 64.0626 | 1142.6 | 1131.3 |
| aot | 2 | sync | 64 | 1 | 3447.56 | 3356.46 | -2.6% | 0.316 | 0.326 | 0.3489 | 0.3608 | 667.4 | 667.1 |
| aot | 2 | sync | 64 | 16 | 10107.30 | 10136.82 | +0.3% | 1.713 | 1.715 | 0.4773 | 0.4568 | 1074.1 | 1060.1 |
| aot | 2 | sync | 4096 | 1 | 3017.86 | 2998.56 | -0.6% | 0.363 | 0.366 | 0.4299 | 0.4368 | 851.4 | 854.6 |
| aot | 2 | sync | 4096 | 16 | 7116.86 | 7218.99 | +1.4% | 2.640 | 2.616 | 0.6529 | 0.6275 | 855.0 | 860.0 |
| aot | 2 | sync | 2097152 | 1 | 217.96 | 217.81 | -0.1% | 4.940 | 4.936 | 10.2926 | 10.2928 | 2157.0 | 2120.4 |
| aot | 2 | sync | 2097152 | 16 | 218.90 | 218.88 | -0.0% | 75.738 | 75.823 | 16.7542 | 16.8744 | 2089.0 | 1943.9 |

## Unchanged direct-target control

| Runtime | Bytes | C | Before direct/s | After direct/s | Δ |
|---|---:|---:|---:|---:|---:|
| jit | 64 | 1 | 11448.6 | 11696.1 | +2.2% |
| jit | 64 | 16 | 32768.2 | 32890.0 | +0.4% |
| jit | 4096 | 1 | 10006.9 | 10094.9 | +0.9% |
| jit | 4096 | 16 | 22492.0 | 22693.8 | +0.9% |
| jit | 2097152 | 1 | 378.1 | 343.3 | -9.2% |
| jit | 2097152 | 16 | 929.9 | 939.0 | +1.0% |
| aot | 64 | 1 | 11748.6 | 11522.0 | -1.9% |
| aot | 64 | 16 | 33155.0 | 32940.0 | -0.6% |
| aot | 4096 | 1 | 10146.7 | 10006.9 | -1.4% |
| aot | 4096 | 16 | 22682.1 | 22683.3 | +0.0% |
| aot | 2097152 | 1 | 384.8 | 423.4 | +10.0% |
| aot | 2097152 | 16 | 963.0 | 931.1 | -3.3% |

## Fixed-count async scenarios

Rates and HTTP p95 measure acceptance; arrival p95 includes queueing and dispatch.

| Runtime | Shape | Before accepted/s | After accepted/s | Before HTTP p95 ms | After HTTP p95 ms | Before arrival p95 ms | After arrival p95 ms |
|---|---|---:|---:|---:|---:|---:|---:|
| jit | burst | 2645.88 | 2822.12 | 46.131 | 35.814 | 1784.751 | 1735.157 |
| jit | paced | 9.38 | 9.41 | 7.913 | 8.515 | 17.728 | 17.795 |
| aot | burst | 2560.80 | 2897.92 | 49.892 | 38.777 | 1793.622 | 1756.557 |
| aot | paced | 9.02 | 9.34 | 13.649 | 9.421 | 25.282 | 19.551 |

## Scaling

| Variant | First ready s | Four ready s | Drained s |
|---|---:|---:|---:|
| baseline-aot | 4.209 | 6.299 | 20.031 |
| baseline-jit | 3.149 | 6.291 | 19.668 |
| candidate-aot | 2.601 | 5.724 | 19.284 |
| candidate-jit | 3.134 | 5.213 | 18.855 |

## Cluster readiness and memory before load

Readiness includes the local supervisor, Raft and the shared JIT target. RSS sums the runtime nodes after five seconds of settling.

| Variant | Median ready s | Median pre-load RSS MiB |
|---|---:|---:|
| baseline-aot | 3.119 | 320.9 |
| baseline-jit | 4.394 | 489.9 |
| candidate-aot | 3.872 | 322.1 |
| candidate-jit | 4.379 | 494.2 |
