# Reproducing SlimFaas memory workloads locally

SlimFaas includes a deterministic local orchestrator and memory workload for
investigating retained memory without a Kubernetes or Docker dependency. It
starts three real SlimFaas processes. Their embedded SlimData instances form a
three-member HTTP Raft cluster; only the orchestrator discovery API is
implemented locally.

For interactive startup, configuration, limitations, and development use cases,
see [Running a three-node SlimFaas cluster locally](local-orchestrator.md).

## Run the complete laboratory

From the repository root:

```bash
# Fully trimmed, self-contained .NET runtime
.bin/memory-lab.sh trimmed mixed 300 12

# Native AOT
.bin/memory-lab.sh aot mixed 300 12
```

Arguments are build mode, scenario, measured duration in seconds, and
concurrency. Scenarios can be `sync`, `async`, `set`, `files`, `mixed`,
`slimdata-set`, or `slimdata-mixed`.
`mixed` uses a deterministic distribution of sync, async, set, and file
operations. `slimdata-set` measures 4 KiB key/value writes. `slimdata-mixed`
covers set, delete, increment, hashset, hash delete, and asynchronous calls
that exercise queue push, pop, and callback.

The script:

1. Publishes SlimFaas in the selected mode.
2. Starts `slimfaas-0`, `slimfaas-1`, and `slimfaas-2`.
3. Waits until all three Raft members report ready.
4. Warms up the selected scenario.
5. Generates load while sampling each process RSS and SlimData diagnostics
   every two seconds.
6. Marks load and idle-cooldown samples separately.
7. Writes logs, raw per-operation CSV/JSON, `memory.csv`, `diagnostics.csv`,
   before/after Prometheus snapshots, leader identities, WAL sizes, and a
   per-node load slope/cooldown report below `artifacts/memory-lab/`.

For the ordered command architecture and the complete alternating Native AOT
A/B matrix, see [Unified ordered SlimData mutation batching](slimdata-unified-batching.md).

The warm-up and cooldown default to 20 seconds and can be changed without
editing the script:

```bash
WARMUP_SECONDS=60 COOLDOWN_SECONDS=60 \
  .bin/memory-lab.sh aot files 600 8
```

After the first publication, it can be reused for another scenario:

```bash
MEMORY_LAB_SKIP_PUBLISH=1 \
  .bin/memory-lab.sh aot set 600 8
```

Do not reuse a publication after changing application source or dependencies.
On macOS, `MEMORY_LAB_MALLOC_STACKS=1` also enables native allocation
backtraces for short diagnostic runs with `heap` and `malloc_history`; it has
significant overhead and should not be used for the reference performance run.

## Reading the results

The report computes its slope from the final half of the load samples.
`cooldown_end_mb` is the median of the final samples. RSS includes
managed heap capacity, reusable pools, and memory-mapped WAL pages; it is not a
measurement of live objects by itself.

Correlate it with these existing `/metrics` gauges:

- `slimdata_process_managed_heap_bytes`
- `slimdata_state_payload_bytes`
- `slimdata_wal_bytes_since_snapshot`
- `slimdata_snapshot_in_progress`

With a fixed set of keys and queue items, a heap that oscillates while RSS
settles is normal pool/GC warm-up. A continuing RSS slope together with bounded
state and bounded WAL needs a managed dump or a native allocation backtrace.

## Simulate OpenShift pod churn

The normal three-node laboratory deliberately uses stable IP addresses. That is
useful for steady-state analysis, but it does not reproduce the cardinality
created when OpenShift replaces function or SlimFaas pods.

The `http-cardinality` command accelerates several days of pod turnover without
requiring a cluster. Each request uses a new destination host while an
in-memory HTTP handler avoids DNS and network noise:

```bash
dotnet run --project tools/SlimFaas.MemoryLab -c Release -- \
  http-cardinality --mode default --hosts 10000

dotnet run --project tools/SlimFaas.MemoryLab -c Release -- \
  http-cardinality --mode bounded --hosts 10000
```

The original global `prometheus-net` HttpClient instrumentation used the
destination `host` as a label. Its registry is append-only, and its two
histograms have 16 buckets. A historical pod IP therefore remained reachable
after the pod disappeared and added 40 exposition lines.

An accelerated run produced:

| Historical hosts | Original live managed delta | Original HTTP metric lines | Bounded live managed delta | Bounded HTTP metric lines |
|---:|---:|---:|---:|---:|
| 1,000 | 4.56 MiB | 40,000 | 0.34 MiB | 40 |
| 5,000 | 20.97 MiB | 200,000 | 0.35 MiB | 40 |
| 10,000 | 41.85 MiB | 400,000 | 0.35 MiB | 40 |
| 20,000 | 83.30 MiB | 800,000 | 0.34 MiB | 40 |

The measurements are taken after a forced compacting GC. This distinguishes
live collector children from temporary allocation and GC heap capacity.

A pre-deployment safety run with 100,000 rotating destinations in bounded mode
still produced one request series, zero `host` labels, 40 HTTP metric lines,
and only a 0.345 MiB live managed-memory delta after compacting GC.

SlimFaas now publishes the same `httpclient_*` metric families with only
bounded labels: `client`, plus `code` where the response status is known.
The ephemeral `host` and caller-controlled `method` labels are intentionally
omitted. Queries or dashboards that grouped `httpclient_*` metrics by `host` or
`method` must remove that grouping.

After deploying the fixed image, verify one pod:

```bash
curl --silent http://<slimfaas-pod>:<port>/metrics \
  | grep '^httpclient_' \
  | grep 'host="'
```

The command must return no line. A process running the old implementation
cannot release those label children with a GC; a rollout is required to start
with a new registry.

The fixed, fully trimmed build was also exercised with the three-node `mixed`
scenario for five measured minutes:

| Result | Value |
|---|---:|
| Operations | 42,019 |
| Failed operations | 0 |
| Throughput | 140.0/s |
| HTTP metric lines, node 0 | 120 before / 120 after |
| HTTP metric lines, nodes 1 and 2 | 199 before / 199 after |
| Destination `host` labels | 0 |
| Managed heap after 60 seconds idle | 44.4 / 53.8 / 49.1 MiB |

RSS can remain above its startup value even when the managed heap drops during
the idle phase. The .NET GC retains committed heap segments for reuse, and RSS
also includes pools and memory-mapped WAL pages. For that reason, a rising RSS
during a short load is not sufficient evidence of a live-object leak.

### Final Native AOT pre-deployment run

The current source was republished as Native AOT and exercised on three nodes
with concurrency 24, a three-minute warm-up, 30 measured minutes, and a
five-minute idle cooldown:

| Result | Value |
|---|---:|
| Operations | 1,181,502 |
| Failed operations | 0 |
| Throughput | 656.38/s |
| Latency p50 / p95 / p99 | 23.810 / 109.979 / 136.195 ms |
| Tail RSS slope, nodes 0 / 1 / 2 | -0.085 / +0.014 / +0.375 MiB/min |
| Cooldown RSS, nodes 0 / 1 / 2 | 373.70 / 403.09 / 367.45 MiB |
| Tail managed-heap slope, nodes 0 / 1 / 2 | -0.466 / +0.411 / +0.267 MiB/min |
| HTTP metric lines per node | 238 before / 238 after |
| Destination `host` labels | 0 |
| Queue items at cooldown end | 0 / 0 / 0 |

The small positive coefficients are not shared across nodes and are tiny
relative to the normal GC ranges. Node 2, for example, ended its measured load
at 372.42 MiB RSS and cooled down to 367.45 MiB despite its +0.375 MiB/min
regression coefficient. This run did not reproduce an unbounded memory trend.

On macOS, prevent laptop sleep during a reference run because the .NET timeout
uses a monotonic clock while the CSV contains wall-clock timestamps:

```bash
caffeinate -dimsu .bin/memory-lab.sh aot mixed 900 12
```

SlimFaas must remain on DotNext 6.4.1 or newer. DotNext 6.4.1 removed the
leader replication-loop allocation that occurred on every heartbeat timeout.
SlimFaas also bypasses the operating-system proxy for its internal SlimData,
function-pod, and cluster-file clients. This avoids per-request PAC evaluation;
on macOS that evaluation retained `CFRunLoopSource`/`CFError` objects under
sustained traffic.

## Local orchestrator configuration

Set `SlimFaas:Orchestrator` to `Local`. The following settings have deterministic
defaults:

| Setting | Default | Purpose |
|---|---:|---|
| `SlimFaas:Local:NodeCount` | `3` | Number of advertised SlimFaas nodes |
| `SlimFaas:Local:NodeNamePrefix` | `slimfaas-` | Stable node identity prefix |
| `SlimFaas:Local:SlimDataPortBase` | `3262` | First Raft/SlimData port |
| `SlimFaas:Local:HttpPortBase` | `30021` | First public SlimFaas port |
| `SlimFaas:Local:FunctionName` | `memory-function` | Advertised test function |
| `SlimFaas:Local:FunctionHost` | `127.0.0.1` | Test function host |
| `SlimFaas:Local:FunctionPort` | `5050` | Test function port |
| `SlimFaas:Local:NumberParallelRequest` | `64` | Maximum concurrent async dispatches |

Each process must receive a matching `HOSTNAME` (`slimfaas-0`, `slimfaas-1`,
or `slimfaas-2`). Use
`SlimFaas:BaseSlimDataUrl=http://{pod_ip}:{pod_port_0}` so each node resolves
the correct local Raft port.

The `Local` implementation is intended for diagnostics and integration tests.
It does not create workloads or jobs, and it must not be used as a production
orchestrator.
