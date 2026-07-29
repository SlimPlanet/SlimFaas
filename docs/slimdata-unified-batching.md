# Unified ordered SlimData mutation batching

SlimData sends every mutation through one ordered path:

```text
public API request
  -> one FIFO adaptive batch queue per SlimFaas replica
  -> POST /SlimData/CommandBatch on the current leader
  -> one single-reader leader queue
  -> one composite ExecuteBatchCommand RAFT entry
  -> sequential state-machine application on every replica
```

The path covers key/value set and increments, key/value delete, hashset set and
delete, queue push and pop, callbacks, and TTL cleanup mutations. Reads stay
outside the batch and continue to wait for local application/leader lease where
required.

## Ordering model

Each SlimFaas process is a producer identified by its host name and SlimData
port. A process generation emits strictly increasing batch sequences. A batch
contains:

- `ProducerId`: stable replica identity;
- `GenerationId`: unique process lifetime;
- `Sequence`: monotonic producer-batch number;
- `RequestId`: stable identifier retained across transport retries;
- an ordered array of mutations, each with its own request ID.

The leader's single-reader queue determines the total RAFT log order. Several
producer batches can be coalesced into one composite log entry, but requests
and their mutations are applied in queue order.

The local FIFO uses a measured adaptive dequeue delay: 225 ms below 4,000
arrivals per minute and 100 ms above that threshold. This retains the low-load
memory profile while allowing the high-concurrency workload to form larger
composite batches. The delay changes batching cadence only; it never changes
the queue order.

`DateTime.UtcNow.Ticks` remains mutation data for TTL and queue timeout
semantics. It is never used to sort commands. Wall clocks from three replicas
cannot provide a safe total order because they can drift, repeat, or arrive
late. The observable guarantee is therefore the order accepted by the RAFT
leader, with FIFO order preserved independently for each producer.

## Retry and deduplication

The local FIFO worker retries the exact same serialized producer batch while a
leader or quorum is temporarily unavailable. It does not dequeue a later batch
first.

The state machine stores the last sequence, request ID, and response for each
producer under a reserved internal key. This metadata is replicated and
included in snapshots. If the response is lost after commit, replaying the
same batch returns the cached response without applying an increment, push, or
pop twice. A missing or out-of-order sequence is rejected and counted instead
of being reordered.

The three-node integration test also covers response loss followed by leader
change: it commits an increment, discards the acknowledgement, stops the
leader, waits for election, replays the same producer batch, and verifies a
cached duplicate response and a final value of exactly one on both survivors.

Protocol `SLDC/2` introduces the composite command. Nodes using `SLDC/1` and
`SLDC/2` refuse mutation traffic while mixed, preventing an older node from
silently skipping the new RAFT command during a rolling deployment.

## Benefits and costs

The unified path provides:

- one ordering rule for every mutation type;
- no direct delete/hash/pop path racing a key/value or queue batch;
- exact retry semantics across response loss;
- fewer RAFT entries for heterogeneous workloads because local and leader-side
  batches are composite;
- common backpressure and diagnostics.

Its costs are:

- a global leader sequencer can become a throughput bottleneck;
- unrelated keys share queueing delay and head-of-line blocking;
- low traffic can wait for the measured 225 ms local dequeue cadence, in
  addition to a 3 ms coalescing window and up to 2 ms at the leader;
- the last response retained per producer adds bounded replicated state;
- an unresolved producer batch intentionally blocks later batches from that
  producer to preserve order;
- `SLDC/2` requires all RAFT members to run a compatible binary before writes
  resume.

If measurements show throughput below 90% of the reference or p99 regression
above 20%, keep the single endpoint and composite command but replace the local
global FIFO with key-partitioned FIFO queues. Operations for the same key must
remain on one partition; cross-key ordering would then be explicitly
unsupported. The single-reader leader queue is still needed to serialize the
resulting producer batches into RAFT.

## Metrics

The following Prometheus gauges/counters expose the path:

- `slimdata_batch_queue_items` and `slimdata_batch_queue_bytes`;
- `slimdata_command_batch_queue_requests` and
  `slimdata_command_batch_queue_bytes`;
- `slimdata_command_batch_raft_total`;
- `slimdata_command_batch_producer_total`;
- `slimdata_command_batch_operations_total`;
- `slimdata_command_batch_duplicates_total`;
- `slimdata_command_batch_sequence_gaps_total`;
- `slimdata_command_batch_last_raft_latency_milliseconds`;
- `slimdata_command_batch_last_max_queue_wait_milliseconds`;
- `slimdata_command_batch_last_producer_batches`,
  `slimdata_command_batch_last_operations`, and
  `slimdata_command_batch_last_payload_bytes`.

Together with the existing committed/applied indices, WAL, snapshot, state,
RSS, managed-heap, and CPU metrics, these values allow calculation of RAFT
entries per mutation and batch wait/commit costs.

## Native AOT LocalKubernetes A/B benchmark

The benchmark uses the reference Native AOT binary from commit
`4d836f83c4e3c7df865cb2869c10a85108a6ed43` and the current Native AOT binary.
Both are driven by the same current `SlimFaas.MemoryLab` executable.

Run the complete matrix:

```bash
.bin/slimdata-benchmark.sh
```

Defaults are the official protocol: 30-second warm-up, 120-second measurement,
concurrency 12 and 48, scenarios `slimdata-set` and `slimdata-mixed`, and three
alternating before/after repetitions. Every run creates fresh state and
distributes requests uniformly over ports 30021 to 30023.

For a short validation of the automation:

```bash
WARMUP_SECONDS=2 DURATION_SECONDS=5 REPETITIONS=1 \
  .bin/slimdata-benchmark.sh
```

Each run writes:

- raw per-request `operations.csv` and `operations.json`;
- operation throughput and p50/p95/p99/maximum latency;
- `memory.csv` and `diagnostics.csv`;
- metrics and leader identity before/after load;
- WAL file sizes, state sizes, node logs, and queue validation;
- the exact run manifest.

The matrix root contains `comparison.md` with individual results, median A/B
ratios, and automatic evaluation of the throughput, latency, memory, and error
criteria.

WAL values in that report are net directory-size deltas. A negative value
means that snapshot compaction removed more WAL bytes than the run appended;
the raw before/after file listings and snapshot sizes remain in each run
directory.

## Official Darwin arm64 result (2026-07-25)

The complete 24-run matrix is stored under
`artifacts/slimdata-benchmark/official-final-20260725`. Binary identities:

- reference: `f9361766a527616ccd17f3ec2df9ec3bd4cc0a9de85973581359ca95fbb30461`;
- unified batch:
  `71dab88f61d942ffcc0e183e6fb06dad34a0b9dc925934486c214756f6ab3670`.

Median results:

| Scenario | C | Before ops/s | After ops/s | Throughput | Before/after p95 ms | Before/after p99 ms | Before/after RSS MiB | After errors |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `slimdata-set` | 12 | 137.52 | 131.00 | 95.3% | 231.44 / 230.85 | 244.77 / 246.13 | 455.0 / 449.1 | 0 |
| `slimdata-set` | 48 | 366.25 | 744.55 | 203.3% | 259.45 / 108.58 | 276.67 / 236.74 | 399.8 / 322.4 | 0 |
| `slimdata-mixed` | 12 | 102.82 | 143.24 | 139.3% | 299.57 / 216.55 | 312.02 / 236.64 | 423.5 / 284.9 | 0 |
| `slimdata-mixed` | 48 | 359.10 | 1102.41 | 307.0% | 299.72 / 90.15 | 318.64 / 134.28 | 487.9 / 325.7 | 0 |

All twelve unified-batch runs completed with zero HTTP, RAFT, sequence, queue,
or business-validation error. The centralized queue was empty after drainage.
The set-only configurations pass every acceptance criterion.

The report's overall verdict is intentionally `FAIL`: the immutable reference
binary does not mount its public hashset routes, producing 17,176 errors at
concurrency 12 and 58,225 at concurrency 48. It also reports successful
deletes that are absent from the final replicated state. The unified-batch
variant has zero such failures. Changing the reference binary to make the
mixed baseline pass would invalidate the commit-exact A/B comparison.

Composite RAFT entries per mutation fall from a median 0.2016 to 0.0830
(-58.8%) for mixed concurrency 12 and from 0.0938 to 0.0222 (-76.3%) for mixed
concurrency 48. Set-only is already batched by the reference implementation,
so it does not show an entry-count reduction (approximately 0.0844 vs 0.0898
at concurrency 12 and 0.0313 vs 0.0319 at concurrency 48).
