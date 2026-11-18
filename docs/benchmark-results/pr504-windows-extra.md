# PR 504 additional final-runtime Windows campaigns

Baseline `999ca152` versus final runtime `dc7e4812`, same immutable JIT/NativeAOT publications through hard-linked aliases. Two fixed reverse-order rounds, no retries, no concurrent builds/compression. Every failed attempt remains included. Two repetitions give limited timing precision.

[All summaries, plans and hashes](pr504-windows-extra.json) · [Byte-exact original evidence and helper sources](pr504-windows-extra-evidence.zip)

Runtime CPU/RSS sum three nodes and exclude target/supervisor/driver. Process I/O includes cache/network traffic. Sampled GC/I/O cover first-to-last sample windows. The common .NET target drains full bodies and records IDs; it does not hash their contents. Earlier hash-verified diagnostics remain separate.

## Data loads

| Attempt | Scenario | Valid | Completed / failed | Reported op/s | p95 / p99 ms | CPU ms/op | Peak RSS MiB | GC 0/1/2 |
|---|---|---|---:|---:|---:|---:|---:|---|
| round-1-baseline-aot | slimdata-set | yes | 4332 / 0 | 72.06 | 340.35 / 770.74 | 12.82 | 384.7 | 35.0/7.0/4.0 |
| round-1-baseline-aot | slimdata-mixed | yes | 11491 / 0 | 191.32 | 212.42 / 421.08 | 8.34 | 401.9 | 32.0/8.0/3.0 |
| round-1-baseline-aot | files | NOT STARTED / no completed load report | — | — | — | — | — | — |
| round-1-baseline-jit | slimdata-set | yes | 6640 / 0 | 110.04 | 325.92 / 461.14 | 16.66 | 634.4 | 52.0/19.0/17.0 |
| round-1-baseline-jit | slimdata-mixed | yes | 13636 / 0 | 227.10 | 169.69 / 223.77 | 7.91 | 621.9 | 37.0/10.0/3.0 |
| round-1-baseline-jit | files | FAIL | 3824 / 10 | 63.23 (invalid) | 450.03 / 575.46 | no valid summary | — | 123.0/114.0/114.0 |
| round-1-candidate-aot | slimdata-set | yes | 7058 / 0 | 117.42 | 324.25 / 612.70 | 10.28 | 447.6 | 71.0/21.0/16.0 |
| round-1-candidate-aot | slimdata-mixed | yes | 13991 / 0 | 233.07 | 169.67 / 323.94 | 6.20 | 436.8 | 38.0/11.0/5.0 |
| round-1-candidate-aot | files | yes | 3025 / 0 | 50.15 | 463.29 / 555.27 | 37.03 | 420.2 | 113.0/105.0/105.0 |
| round-1-candidate-jit | slimdata-set | yes | 6693 / 0 | 111.50 | 346.47 / 469.46 | 18.82 | 631.8 | 52.0/19.0/15.0 |
| round-1-candidate-jit | slimdata-mixed | yes | 14321 / 0 | 238.42 | 157.79 / 213.11 | 8.01 | 617.4 | 39.0/10.0/3.0 |
| round-1-candidate-jit | files | yes | 2913 / 0 | 48.42 | 516.90 / 593.66 | 42.18 | 607.9 | 108.0/99.0/99.0 |
| round-2-baseline-aot | slimdata-set | yes | 7347 / 0 | 122.05 | 319.62 / 536.39 | 10.16 | 436.5 | 57.0/20.0/16.0 |
| round-2-baseline-aot | slimdata-mixed | yes | 13225 / 0 | 220.33 | 178.65 / 307.51 | 6.43 | 438.3 | 30.0/9.0/2.0 |
| round-2-baseline-aot | files | NOT STARTED / no completed load report | — | — | — | — | — | — |
| round-2-baseline-jit | slimdata-set | yes | 5670 / 0 | 94.41 | 331.94 / 454.35 | 24.81 | 625.4 | 42.0/13.0/8.0 |
| round-2-baseline-jit | slimdata-mixed | yes | 12726 / 0 | 211.97 | 174.47 / 262.99 | 9.24 | 618.1 | 34.0/10.0/3.0 |
| round-2-baseline-jit | files | FAIL | 4042 / 8 | 67.29 (invalid) | 408.02 / 512.26 | no valid summary | — | 135.0/125.0/125.0 |
| round-2-candidate-aot | slimdata-set | yes | 6927 / 0 | 114.80 | 319.78 / 545.49 | 9.77 | 440.3 | 56.0/21.0/15.0 |
| round-2-candidate-aot | slimdata-mixed | yes | 14358 / 0 | 239.17 | 146.29 / 271.21 | 6.31 | 437.9 | 33.0/8.0/2.0 |
| round-2-candidate-aot | files | yes | 2833 / 0 | 46.94 | 504.46 / 870.94 | 40.92 | 458.2 | 113.0/106.0/106.0 |
| round-2-candidate-jit | slimdata-set | yes | 6552 / 0 | 108.10 | 339.29 / 545.54 | 21.13 | 634.6 | 51.0/19.0/13.0 |
| round-2-candidate-jit | slimdata-mixed | yes | 13932 / 0 | 231.91 | 166.47 / 237.13 | 8.61 | 635.4 | 36.0/10.0/3.0 |
| round-2-candidate-jit | files | yes | 2879 / 0 | 47.61 | 524.83 / 727.95 | 41.02 | 661.9 | 107.0/99.0/99.0 |

Balanced medians are reported only when both repetitions on each side pass phase validation:

| Mode | Scenario | Before → after op/s | Change |
|---|---|---:|---:|
| jit | slimdata-set | 102.23 → 109.80 | +7.40% |
| jit | slimdata-mixed | 219.54 → 235.16 | +7.12% |
| jit | files | not comparable: correctness failure / incomplete phase | — |
| aot | slimdata-set | 97.06 → 116.11 | +19.63% |
| aot | slimdata-mixed | 205.83 → 236.12 | +14.72% |
| aot | files | not comparable: correctness failure / incomplete phase | — |

## Common .NET HTTP target: 2 MiB / 16 clients

| Attempt | Route | HTTP completed / failed | HTTP req/s | HTTP p95 / p99 ms | Missing / duplicates | Observed |
|---|---|---:|---:|---:|---:|---:|
| round-1-baseline-aot | direct | 8283 / 0 | 413.48 | 60.11 / 72.54 | 0 / 0 | — |
| round-1-baseline-aot | sync | 2720 / 0 | 135.42 | 195.50 / 242.99 | 0 / 0 | — |
| round-1-baseline-aot | async | 648 / 0 | 31.76 | 684.44 / 744.77 | 0 / 0 | 648 |
| round-1-baseline-jit | direct | 6313 / 0 | 314.64 | 81.94 / 105.70 | 0 / 0 | — |
| round-1-baseline-jit | sync | 2373 / 0 | 118.30 | 192.13 / 220.47 | 0 / 0 | — |
| round-1-baseline-jit | async | 619 / 0 | 30.46 | 896.10 / 1328.76 | 0 / 0 | 619 |
| round-1-candidate-aot | direct | 12563 / 0 | 627.68 | 42.09 / 54.25 | 0 / 0 | — |
| round-1-candidate-aot | sync | 3638 / 0 | 181.25 | 159.42 / 223.03 | 0 / 0 | — |
| round-1-candidate-aot | async | 680 / 0 | 32.69 | 631.87 / 730.24 | 0 / 0 | 680 |
| round-1-candidate-jit | direct | 11019 / 0 | 550.50 | 50.83 / 63.59 | 0 / 0 | — |
| round-1-candidate-jit | sync | 3059 / 0 | 152.54 | 165.96 / 203.45 | 0 / 0 | — |
| round-1-candidate-jit | async | 626 / 0 | 30.59 | 825.53 / 909.83 | 0 / 0 | 626 |
| round-2-baseline-aot | direct | 14015 / 0 | 700.41 | 35.69 / 44.30 | 0 / 0 | — |
| round-2-baseline-aot | sync | 3275 / 0 | 163.25 | 178.91 / 222.04 | 0 / 0 | — |
| round-2-baseline-aot | async | 667 / 0 | 32.68 | 683.55 / 757.12 | 0 / 0 | 667 |
| round-2-baseline-jit | direct | 7150 / 0 | 356.88 | 72.65 / 86.91 | 0 / 0 | — |
| round-2-baseline-jit | sync | 2567 / 0 | 127.87 | 183.69 / 205.78 | 0 / 0 | — |
| round-2-baseline-jit | async | 239 / 0 | 11.51 | 8677.39 / 8811.19 | 0 / 0 | 239 |
| round-2-candidate-aot | direct | 13754 / 0 | 687.37 | 37.73 / 47.47 | 0 / 0 | — |
| round-2-candidate-aot | sync | 3387 / 0 | 168.78 | 174.95 / 232.53 | 0 / 0 | — |
| round-2-candidate-aot | async | 681 / 0 | 33.47 | 634.89 / 736.47 | 0 / 0 | 681 |
| round-2-candidate-jit | direct | 11461 / 0 | 572.74 | 48.60 / 60.71 | 0 / 0 | — |
| round-2-candidate-jit | sync | 2987 / 0 | 149.07 | 168.77 / 208.27 | 0 / 0 | — |
| round-2-candidate-jit | async | 639 / 0 | 31.20 | 778.45 / 1070.11 | 0 / 0 | 639 |

HTTP acceptance throughput is distinct from delivery. A missing/duplicate delivery invalidates an end-to-end correctness/performance claim. Per-case CPU, RSS, arrival/completion latencies, focused 4 KiB paced/burst and scaling results remain in the full JSON; global HTTP GC/I/O samples cover the entire driver, not one route.

## Native thread-state diagnostic

A separate four-attempt set-only diagnostic samples Windows ProcessThread states/wait reasons and cumulative thread CPU. Snapshots include idle threads, are not exact wait durations, and contain no native stacks. WPR is installed but the current token is not elevated; no elevation was attempted.

| Attempt | Phase | Frames | Thread-state samples | Wait-reason samples | Query errors |
|---|---|---:|---|---|---:|
| round-1-baseline-aot | slimdata-set | 111 | {"Ready": 63, "Running": 86, "Standby": 3, "Wait": 9185} | {"EventPairLow": 4169, "Executive": 15, "Unknown": 6, "UserRequest": 4995} | 0 |
| round-1-candidate-aot | slimdata-set | 110 | {"Ready": 104, "Running": 130, "Standby": 6, "Wait": 8853} | {"EventPairLow": 3864, "Executive": 25, "Unknown": 7, "UserRequest": 4957} | 0 |
| round-2-baseline-aot | slimdata-set | 113 | {"Ready": 43, "Running": 72, "Standby": 2, "Wait": 9166} | {"EventPairLow": 4042, "Executive": 24, "Unknown": 8, "UserRequest": 5092} | 0 |
| round-2-candidate-aot | slimdata-set | 109 | {"Ready": 125, "Running": 125, "Standby": 7, "Wait": 8913} | {"EventPairLow": 4035, "Executive": 22, "Unknown": 10, "UserRequest": 4846} | 0 |

The original evidence archive contains 688 byte-verified files; SHA-256 `034214c41ad03544de3c7db0bd9ef5498959f8621fd3859614c319643d0f7927`.

The earlier M4 and Windows campaigns, all failed/negative controls, and the original evidence ZIP remain unchanged. No Linux production benchmark or native wait-stack attribution is established by these Windows measurements.
