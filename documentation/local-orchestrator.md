# Running a three-node SlimFaas cluster locally

The `Local` orchestrator runs a real multi-process SlimFaas cluster without a
Kubernetes cluster, Docker daemon, or Podman daemon. It is intended for local
development, integration tests, diagnostics, and performance investigations.
It is not a production orchestrator.

## What is real and what is simulated?

Each SlimFaas process uses `LocalKubernetesService` to discover the same
deterministic topology:

```text
                              local function
                         http://127.0.0.1:5050
                          ^        ^        ^
                          |        |        |
client --> :30021 [slimfaas-0]     |     [slimfaas-2] :30023
                      :3262 <---- HTTP Raft ----> :3264
                                   |
                              [slimfaas-1]
                           :30022       :3263
```

The following behavior is real:

- three independent SlimFaas operating-system processes;
- DotNext HTTP Raft elections, replication, WAL, and snapshots;
- synchronous and asynchronous function routing;
- async queues and workers;
- SlimData sets, TTLs, and counters;
- file storage and cross-node file transfer;
- readiness and Prometheus metrics.

The following behavior is simulated:

- the Kubernetes discovery snapshot;
- one function pod advertised at `127.0.0.1:5050`;
- function scale state, limited to `0` or `1`.

`LocalKubernetesService` does not start the other SlimFaas processes or the
function. It does not create containers, Jobs, Deployments, or StatefulSets.
Its Job operations are no-ops, and scaling changes only the local discovery
metadata.

## Fastest option: use the memory laboratory

From the repository root, the supplied script publishes SlimFaas, starts the
function and all three nodes, waits for the Raft cluster, generates traffic,
collects RSS samples, and stops every process:

```bash
.bin/memory-lab.sh trimmed mixed 300 12
.bin/memory-lab.sh aot mixed 300 12
```

The arguments are:

1. publication mode: `trimmed` or `aot`;
2. scenario: `sync`, `async`, `set`, `files`, or `mixed`;
3. measured duration in seconds;
4. load-generator concurrency.

For the generated artifacts and memory-analysis workflow, see
[Reproducing SlimFaas memory workloads locally](memory-profiling.md).

## Start an interactive local cluster manually

Use this option when the cluster must remain available while developing a
function, exercising an API, inspecting metrics, or testing failover.

### Prerequisites

- the .NET 10 SDK;
- `curl`;
- ports `3262` to `3264`, `30021` to `30023`, and `5050` must be free.

The commands below use a new state directory for every run. Run them from the
repository root in the same Bash or Zsh session so the process ID variables
remain available for cleanup.

### 1. Build SlimFaas and the sample function

This creates a framework-dependent, untrimmed publication for a short local
development cycle:

```bash
repo_root="$PWD"
publish_dir="$repo_root/artifacts/local-cluster/publish"
run_dir="$repo_root/artifacts/local-cluster/run-$(date -u +%Y%m%dT%H%M%SZ)"
lab_project="$repo_root/tools/SlimFaas.MemoryLab/SlimFaas.MemoryLab.csproj"
lab_dll="$repo_root/tools/SlimFaas.MemoryLab/bin/Release/net10.0/SlimFaas.MemoryLab.dll"

mkdir -p "$publish_dir" "$run_dir"

dotnet build "$lab_project" -c Release --nologo
dotnet publish "$repo_root/src/SlimFaas/SlimFaas.csproj" \
  -c Release \
  -o "$publish_dir" \
  -p:PublishAot=false \
  -p:PublishTrimmed=false \
  -p:SkipClientAppBuild=true \
  --nologo
```

For a trimmed or Native AOT cluster, prefer `.bin/memory-lab.sh`; it selects the
correct runtime identifier and publication options for the current host.

### 2. Start a local function

The memory-lab function accepts common HTTP methods on every path, consumes the
request body, and returns `204 No Content`. It is sufficient for proxy and queue
tests:

```bash
dotnet "$lab_dll" function --port 5050 >"$run_dir/function.log" 2>&1 &
function_pid="$!"

curl --fail http://127.0.0.1:5050/health
```

A real development function can be used instead. Set
`SlimFaas__Local__FunctionHost`, `SlimFaas__Local__FunctionPort`, and
`SlimFaas__Local__FunctionName` consistently on all three nodes.

### 3. Start the three SlimFaas nodes

Every process must have a unique `HOSTNAME` matching the advertised node name.
All other topology settings must be identical:

```bash
node_pids=()

for index in 0 1 2; do
  (
    cd "$publish_dir"
    exec env \
      HOSTNAME="slimfaas-$index" \
      SlimFaas__Orchestrator="Local" \
      SlimFaas__Namespace="local" \
      SlimFaas__BaseSlimDataUrl='http://{pod_ip}:{pod_port_0}' \
      SlimFaas__BaseFunctionUrl='http://{pod_ip}:{pod_port}' \
      SlimFaas__BaseFunctionPodUrl='http://{pod_ip}:{pod_port}' \
      SlimFaas__EnableFront="false" \
      SlimFaas__WebSocketPort="0" \
      SlimFaas__Local__NodeCount="3" \
      SlimFaas__Local__NodeNamePrefix="slimfaas-" \
      SlimFaas__Local__SlimDataPortBase="3262" \
      SlimFaas__Local__HttpPortBase="30021" \
      SlimFaas__Local__FunctionName="memory-function" \
      SlimFaas__Local__FunctionHost="127.0.0.1" \
      SlimFaas__Local__FunctionPort="5050" \
      SlimFaas__Local__NumberParallelRequest="64" \
      SlimData__Directory="$run_dir/state" \
      SlimData__AllowColdStart="true" \
      Data__DefaultVisibility="Public" \
      Logging__LogLevel__Default="Warning" \
      dotnet "$publish_dir/SlimFaas.dll"
  ) >"$run_dir/slimfaas-$index.log" 2>&1 &

  node_pids+=("$!")
done
```

`SlimData__Directory` is a shared base path. SlimFaas automatically creates one
subdirectory per node, for example `state/slimfaas-0`.

### 4. Wait for the Raft cluster

Readiness is stricter than liveness: a node returns `200` only after it has
completed its initial synchronization and is compatible with the leader.

```bash
ready=0
for attempt in $(seq 1 120); do
  ready=0
  for port in 30021 30022 30023; do
    if curl --silent --fail "http://127.0.0.1:$port/ready" >/dev/null; then
      ready=$((ready + 1))
    fi
  done

  if [ "$ready" -eq 3 ]; then
    break
  fi

  sleep 1
done

test "$ready" -eq 3
```

The public node URLs are:

| Node | Public API | SlimData/Raft |
|---|---|---|
| `slimfaas-0` | `http://127.0.0.1:30021` | `http://127.0.0.1:3262` |
| `slimfaas-1` | `http://127.0.0.1:30022` | `http://127.0.0.1:3263` |
| `slimfaas-2` | `http://127.0.0.1:30023` | `http://127.0.0.1:3264` |

## Exercise the cluster

### Synchronous and asynchronous function calls

```bash
curl -i -X POST \
  --data-binary 'synchronous payload' \
  http://127.0.0.1:30021/function/memory-function/echo

curl -i -X POST \
  --data-binary 'asynchronous payload' \
  http://127.0.0.1:30022/async-function/memory-function/echo
```

The synchronous request is proxied immediately. The asynchronous endpoint
returns `202 Accepted`, persists the request in the distributed queue, and a
SlimFaas worker dispatches it to the function.

### Write a set on one node and read it from another

```bash
curl --fail -X POST \
  --data-binary '{"ready":true}' \
  'http://127.0.0.1:30021/data/sets/local-demo?ttl=60000'

curl --fail \
  http://127.0.0.1:30022/data/sets/local-demo
```

### Upload a file and pull it through another node

```bash
curl --fail -X POST \
  -H 'Content-Type: application/octet-stream' \
  --data-binary 'local file payload' \
  'http://127.0.0.1:30021/data/files?id=local-file&ttl=60000'

curl --fail \
  http://127.0.0.1:30023/data/files/local-file
```

Set metadata is replicated through Raft. File metadata is replicated, while the
binary is stored on disk and pulled from a peer on demand. A cross-node read
immediately after a write is eventually observable; retry briefly if it first
returns `404`.

### Generate repeatable load without restarting the cluster

```bash
dotnet "$lab_dll" load \
  --scenario mixed \
  --duration 60 \
  --concurrency 12 \
  --first-port 30021 \
  --nodes 3
```

Replace `mixed` with `sync`, `async`, `set`, or `files` to isolate one path.

### Inspect health and metrics

```bash
curl --fail http://127.0.0.1:30021/health
curl --fail http://127.0.0.1:30021/ready
curl --silent http://127.0.0.1:30021/metrics
```

The manual example disables the dashboard activity synchronization and skips
the frontend build to reduce background noise. To develop the dashboard, build
the client assets, omit `-p:SkipClientAppBuild=true`, and set
`SlimFaas__EnableFront=true`.

## Configuration reference

Set `SlimFaas__Orchestrator=Local`. .NET configuration maps the double
underscore in environment variables to the colon used in configuration files.

| Configuration setting | Default | Purpose |
|---|---:|---|
| `SlimFaas:Local:NodeCount` | `3` | Number of advertised SlimFaas nodes |
| `SlimFaas:Local:NodeNamePrefix` | `slimfaas-` | Prefix used to create stable node identities |
| `SlimFaas:Local:SlimDataPortBase` | `3262` | Raft port of node 0; following nodes increment it |
| `SlimFaas:Local:HttpPortBase` | `30021` | public port of node 0; following nodes increment it |
| `SlimFaas:Local:FunctionName` | `memory-function` | Name of the single advertised function |
| `SlimFaas:Local:FunctionHost` | `127.0.0.1` | Host of the advertised function |
| `SlimFaas:Local:FunctionPort` | `5050` | Port of the advertised function |
| `SlimFaas:Local:NumberParallelRequest` | `64` | Maximum concurrent async dispatches to the function |

`SlimFaas:BaseSlimDataUrl` must be
`http://{pod_ip}:{pod_port_0}` for this topology. `{pod_port_0}` selects the
first port advertised for each node, which is its unique Raft port.

## Useful local use cases

The Local orchestrator is particularly useful for:

- reproducing Raft election, replication, WAL, snapshot, and memory issues;
- running API and integration tests without a container daemon;
- debugging synchronous proxying separately from asynchronous queue dispatch;
- validating set replication, TTL expiration, counters, and file transfer;
- testing client code against stable local node URLs;
- simulating leader loss by stopping one exact node process and observing a new
  election while the two remaining members preserve quorum;
- comparing framework-dependent, fully trimmed, and Native AOT behavior under
  the same deterministic traffic;
- testing restart and recovery with a dedicated persisted state directory;
- developing observability against real readiness and Prometheus endpoints.

It is not suitable for validating:

- Kubernetes annotations, RBAC, DNS, Services, network policies, or service
  meshes;
- real Deployment, StatefulSet, or Job creation;
- container startup latency and image-pull behavior;
- Kubernetes Horizontal Pod Autoscaler behavior;
- real function process creation or deletion during scale-to-zero.

Use Docker Compose or Kubernetes when the test depends on those
infrastructure-specific behaviors.

## Failover experiment

After all three nodes are ready, stop one node by its recorded process ID:

```bash
kill "${node_pids[0]}"
```

If that node was the leader, the other two members elect a replacement. Calls
through ports `30022` and `30023` should recover after the election timeout.
This tests SlimFaas and Raft failover, not Kubernetes pod replacement: the Local
orchestrator does not restart the stopped process.

Start a fresh three-node run to test clean recovery. Reusing `run_dir/state`
tests persisted WAL recovery; choosing a new `run_dir` creates a clean cluster.

## Stop the manual cluster

Stop only the processes recorded by the current shell:

```bash
kill "${node_pids[@]}" "$function_pid" 2>/dev/null || true
wait "${node_pids[@]}" "$function_pid" 2>/dev/null || true
```

Logs and node state remain under `$run_dir`, so the run can be inspected after
the processes stop.

## Troubleshooting

- **A node never becomes ready:** confirm that the three `HOSTNAME` values are
  exactly `slimfaas-0`, `slimfaas-1`, and `slimfaas-2`, and inspect
  `$run_dir/slimfaas-<index>.log`. A three-member cluster needs at least two
  live members for quorum.
- **A process reports an address already in use:** stop the exact process that
  owns the conflicting port or choose different, non-overlapping port bases for
  all nodes.
- **Function calls return `503`:** verify
  `http://127.0.0.1:5050/health` and keep the function name, host, and port
  identical on all nodes.
- **Data endpoints return `403`:** set
  `Data__DefaultVisibility=Public` for local command-line testing.
- **A cross-node set or file read briefly returns `404`:** retry after a short
  delay while Raft metadata propagation or the peer file pull completes.
- **A previous run affects the next one:** use a new `run_dir` to obtain
  isolated state rather than reusing the previous state directory.

