# SlimData batch modes and LocalKubernetes benchmark

SlimData mutations still use the single `SlimData/CommandBatch` endpoint and the
same durable `ProducerId + GenerationId + Sequence + RequestId` deduplication
protocol. Reads remain outside the mutation queues.

## Configuration

```json
{
  "SlimData": {
    "BatchMode": "Global",
    "BatchPartitionCount": 8,
    "LowLoadFastPath": true,
    "LowLoadRequestsPerSecond": 10
  }
}
```

`BatchMode` accepts `Global` or `PartitionedByKey`. Partition counts are limited
to 2, 4, 8 or 16. The adopted default is `Global` with
`LowLoadFastPath=true`. Set it explicitly to `false` to retain the former
225/100 ms local cooldown behaviour under low load.

The fast path measures local mutation arrivals over a rolling five-second
window. At or below the configured threshold, local cooldown and local
coalescence are zero. Above it, the existing 225 ms cooldown is used; above
4,000 requests/minute, it becomes 100 ms. To prevent small scheduling bursts at
the threshold from disabling the fast path, hysteresis leaves low-load mode only
above 120% of the threshold and returns at or below the configured threshold.
The leader-side 2 ms coalescence and the RAFT commit are unchanged.

`PartitionedByKey` hashes each key to an independent local queue with its own
producer sequence. It preserves local order for a key, but deliberately does
not define a global order between different keys. The leader remains the
authority that orders batches arriving from different replicas.

## Metrics

The `/metrics` endpoint exposes:

- `slimdata_batch_mode`
- `slimdata_batch_partition_count`
- `slimdata_batch_local_requests_per_second`
- `slimdata_batch_low_load_fast_path_enabled`
- `slimdata_batch_low_load_fast_path_active`
- `slimdata_batch_local_delay_milliseconds`
- `slimdata_batch_local_coalesce_milliseconds`
- `slimdata_batch_queue_items` and `slimdata_batch_queue_bytes`, labelled by
  global queue or partition

The existing leader batch, RAFT, WAL, process CPU, heap and RSS metrics remain
available and are collected by the benchmark.

## Reproduction

Run the screening and official matrix with one Native AOT binary:

```bash
.bin/slimdata-batch-modes-benchmark.sh
```

The default run performs:

- screening of 2/4/8/16 partitions at c48/c96/c192;
- automatic selection of the safest partition count with the highest median
  throughput gain;
- paced `slimdata-set` runs at 1/3/10 op/s per replica (3/9/30 op/s for the
  three-node cluster);
- unpaced `slimdata-set` and `slimdata-mixed` runs at c12/c48/c192;
- three alternating repetitions, fresh three-node state per run, 30 seconds of
  warm-up and 120 seconds of measurement.

Useful scoped commands:

```bash
BENCHMARK_PHASE=screening .bin/slimdata-batch-modes-benchmark.sh

BENCHMARK_PHASE=official \
SELECTED_PARTITIONS=8 \
.bin/slimdata-batch-modes-benchmark.sh

BENCHMARK_PHASE=official \
OFFICIAL_SCOPE=low \
OFFICIAL_LOW_TARGETS="1 3 10" \
.bin/slimdata-batch-modes-benchmark.sh
```

For a repeatable quick smoke test:

```bash
BENCHMARK_PHASE=screening \
SCREENING_DURATION_SECONDS=10 \
SCREENING_WARMUP_SECONDS=5 \
.bin/slimdata-batch-modes-benchmark.sh
```

Each run stores its manifest, raw operation CSV/JSON, metrics before and after,
diagnostics time series, memory samples, WAL inventory, logs and leader identity.
`comparison.md` reports the automatic SLO and regression verdict. A partitioned
candidate is recommended only when it stays within all safety limits and adds at
least 10% median high-load throughput.

## LocalKubernetes result

The official Darwin arm64 run used one Native AOT binary, three SlimFaas
processes, fresh state per run, 30 seconds of warm-up, 120 seconds of
measurement and three alternating repetitions. Its report is stored in
`artifacts/slimdata-benchmark/batch-modes-final-20260726T130725Z/official/comparison.md`.

- `GlobalLowLatency`: **ADOPT**. Every low- and high-load point passes.
- `PartitionedLowLatency-p4`: **DO NOT ADOPT**. Its median high-load throughput
  gain is -19.7%, with large regressions at c12 and c48.
- At 1 op/s per replica, the adopted mode measures p50 6.9 ms, p95 11.2 ms and
  p99 22.9 ms.
- At 10 op/s per replica, it measures p50 6.2 ms, p95 10.8 ms and p99 17.3 ms.
- All HTTP and business validations passed. All collected duplicate and sequence
  gap counters remained zero.

The automatic overall result means “at least one candidate is adoptable”.
Individual rejected experiments remain visible as `FAIL` rows without turning a
successful adoption into a global failure.
