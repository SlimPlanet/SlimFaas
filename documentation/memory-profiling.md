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
concurrency. Scenarios can be `sync`, `async`, `set`, `files`, or `mixed`.
`mixed` uses a deterministic distribution of sync, async, set, and file
operations.

The script:

1. Publishes SlimFaas in the selected mode.
2. Starts `slimfaas-0`, `slimfaas-1`, and `slimfaas-2`.
3. Waits until all three Raft members report ready.
4. Warms up the selected scenario.
5. Generates load while sampling each process RSS every two seconds.
6. Marks load and idle-cooldown samples separately.
7. Writes logs, `memory.csv`, and a per-node load slope/cooldown report below
   `artifacts/memory-lab/`.

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
