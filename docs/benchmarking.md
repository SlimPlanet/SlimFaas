# Latency and Autoscaling Benchmark

SlimFaas includes a reproducible native-local benchmark for the warm synchronous
proxy path, asynchronous enqueue and delivery, and scale-to-zero followed by
PromQL scale-out.

## Run the benchmark

The quick profile is intended for a smoke test and takes a few minutes:

```bash
BENCHMARK_PROFILE=quick .bin/slimfaas-local-benchmark.sh
```

The standard profile runs every case three times with longer measurement and
warm-up windows:

```bash
.bin/slimfaas-local-benchmark.sh
```

The script builds SlimFaas without the dashboard, builds the benchmark driver,
validates `benchmarks/slimfaas.local.benchmark.yaml`, starts a clean three-node
cluster with `slimfaas local`, runs the matrix, and shuts down every managed
process. Docker and Kubernetes are not required.

It requires the repository's .NET 10 SDK plus Bash and `curl`. Ports 31020-31023,
3162-3164, 31080, and the 32000-32015 process pool must be free.

Results are written below
`artifacts/slimfaas-local-benchmark/<profile>-<UTC timestamp>/`:

- `summary.md`: tables for sync overhead, async latency, and scaling milestones;
- `results.json`: structured settings and results;
- `latency-samples.csv`: one row per measured HTTP request;
- `scaling-timeline.csv`: observed replicas and queue depths over time;
- `benchmark-manifest.txt`: commit, dirty-worktree flag, SDK, host, and matrix;
- `slimfaas-local.log`: control-plane and managed-process output.

## Compare an optimization with its baseline

Run the standard profile before changing the runtime, then run the exact same
matrix after the change. Compare the two structured reports with:

```bash
dotnet run --project src/SlimFaasBenchmark -- compare \
  --baseline artifacts/slimfaas-local-benchmark/baseline/results.json \
  --candidate artifacts/slimfaas-local-benchmark/candidate/results.json \
  --output artifacts/slimfaas-local-benchmark/comparison
```

The command rejects reports whose duration, warm-up, repetitions, payloads,
concurrency, or scaling settings differ. It writes:

- `comparison.md`: human-readable before/after latency, overhead, throughput,
  async, scaling, and acceptance verdicts;
- `comparison.json`: the same comparison as structured data.

The command exits with a non-zero status when the candidate records errors or
timeouts, when the median added p50 reduction is below 20% for 64 B–4 KiB or
40% for 256 KiB–2 MiB, or when added p95/p99 or the throughput ratio regresses
by more than 10%. Async latency and scaling timings are reported for context;
only their errors and timeouts affect the verdict.

## What is measured

### Warm synchronous overhead

The benchmark target is one minimal HTTP/1.1 server. The driver calls that exact
process in two ways:

1. directly at `http://127.0.0.1:31080/echo`;
2. through `/function/benchmark-latency/echo`.

For each payload size and concurrency, the report includes throughput and HTTP
p50, p95, p99, and maximum latency. It then subtracts the direct percentile
from the SlimFaas percentile in milliseconds and percent. Calling the same
process on both paths isolates the warm routing/proxy overhead from application
work.

The standard matrix uses:

- payloads: 64 B, 4 KiB, 256 KiB, and 2 MiB;
- closed-loop concurrency: 1 and 16;
- warm-up: 2 seconds;
- measurement: 10 seconds;
- repetitions: 3.

The report uses the median percentile across repetitions and the mean throughput.
The driver alternates direct/proxy order between cases to reduce systematic CPU
frequency and temperature bias.

### Asynchronous latency

Each async case reports two different clocks:

- **ingress latency**: client send until SlimFaas returns HTTP 202 after the
  request has been durably enqueued;
- **delivery latency**: client send until the target handler starts, including
  enqueue, queue wait, dispatch, and request-body transfer.

The target also records completion latency in `results.json`. The target and
driver are local processes on the same clock, so UTC ticks can be compared
without a network clock-synchronization error. The benchmark waits for every
accepted message to be observed by the target and for queue metrics to remain at
zero before starting another case.

The 2 MiB case is intentionally above SlimFaas' 1 MiB async body-offload
threshold. This makes the matrix cover both inline queue payloads and the
cluster-file path.

### Scaling speed

`benchmark-scale` starts at zero replicas. The driver sends a burst of 200 async
messages at concurrency 32; each target request takes 250 ms and each replica is
limited to one concurrent request. The following durations are measured from
the first client send:

- first and last HTTP 202;
- desired replicas changing from 0 to at least 1;
- first ready replica;
- desired replicas reaching 4;
- all 4 replicas becoming ready;
- the ready, in-flight, and retry queues returning to zero.

The first delivered `/work` request makes the benchmark target expose
`slimfaas_benchmark_scale_pressure 4` for 30 seconds. SlimFaas scrapes it every
second and evaluates
`max_over_time(slimfaas_benchmark_scale_pressure[30s])`. This deterministic
demand signal exercises metric discovery, scraping, the internal metric store,
PromQL evaluation, scale policy application, local process launch, and health
readiness. Queue metrics are observed independently to measure backlog and drain
time.

## Configure the matrix

The wrapper accepts environment variables without changing tracked files:

```bash
BENCHMARK_PROFILE=quick \
BENCHMARK_DURATION_SECONDS=5 \
BENCHMARK_WARMUP_SECONDS=1 \
BENCHMARK_REPETITIONS=2 \
BENCHMARK_PAYLOAD_BYTES=64,4096,1048576,2097152 \
BENCHMARK_CONCURRENCY=1,8,32 \
BENCHMARK_SCALE_MESSAGES=500 \
BENCHMARK_SCALE_CONCURRENCY=64 \
.bin/slimfaas-local-benchmark.sh
```

Available variables are:

| Variable | Standard default | Purpose |
|---|---:|---|
| `BENCHMARK_PROFILE` | `standard` | `quick` or `standard` defaults |
| `BENCHMARK_DURATION_SECONDS` | `10` | measured seconds per route and case |
| `BENCHMARK_WARMUP_SECONDS` | `2` | unrecorded warm-up seconds |
| `BENCHMARK_REPETITIONS` | `3` | repetitions per payload/concurrency case |
| `BENCHMARK_PAYLOAD_BYTES` | `64,4096,262144,2097152` | comma-separated body sizes |
| `BENCHMARK_CONCURRENCY` | `1,16` | comma-separated closed-loop concurrency |
| `BENCHMARK_SCALE_MESSAGES` | `200` | messages in the scaling burst |
| `BENCHMARK_SCALE_CONCURRENCY` | `32` | burst enqueue concurrency |
| `BENCHMARK_SCALE_TIMEOUT_SECONDS` | `120` | maximum scaling observation window |
| `BENCHMARK_ASYNC_DRAIN_TIMEOUT_SECONDS` | `180` | maximum async delivery/drain wait |
| `BENCHMARK_RUN_ROOT` | timestamped artifacts path | exact output directory |
| `BENCHMARK_SKIP_BUILD` | `false` | reuse existing Release binaries for repeated runs on the same checkout |

## Interpret the result correctly

- Subtract percentiles in milliseconds first. A sub-millisecond direct baseline
  can make a small absolute delta look large as a percentage.
- Async HTTP 202 is enqueue latency, not execution latency. Use target delivery
  percentiles for end-to-end dispatch behavior.
- Native local scaling measures SlimFaas plus operating-system process startup.
  It deliberately excludes Kubernetes scheduling, admission, image pulls, CNI,
  and container runtime time. Run the same driver against a Kubernetes
  deployment when those costs are part of the question.
- Function status endpoints have short caches. Scaling times are externally
  observed milestones with that polling resolution, not internal timestamps.
- Avoid unrelated workloads, power-saving mode, thermal throttling, and debug
  builds. Keep the same host, SDK, commit, and matrix for before/after comparisons.
- A single run is exploratory. Use the standard repetitions, compare medians,
  and retain the raw manifest and samples for a publishable result.

See [Native Local Development Mode](native-local-mode.md) for the process
orchestrator and [Autoscaling](autoscaling.md) for the production scaling model.
