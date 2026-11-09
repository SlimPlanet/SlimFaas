# SlimData reliability campaigns

The [1.0 LTS reliability tracker](https://github.com/SlimPlanet/SlimFaas/issues/505)
records release evidence and outstanding scenarios. A successful short campaign
establishes its recorded invariants for that run; it does not certify LTS readiness.

The [8–9 November 2026 results](slimdata-reliability-results.md) include the executed
absence/API matrix, 2 MB versus 2 MiB diagnosis, fixes, failed campaigns and the
current LTS verdict. Compact evidence and raw archive hashes accompany the report.

## Run the real Local campaign

From the repository root, with .NET from `global.json` and Python 3.10+:

```bash
dotnet build src/SlimFaas/SlimFaas.csproj -c Release -p:SkipClientAppBuild=true
python .bin/test-local-chaos-harness.py
python .bin/test-slimdata-local-chaos.py \
  --output artifacts/local-chaos/quick --durations 1,5,10
```

The harness starts `SlimFaas local up` with three real runtime processes, a real
HTTP function and real jobs. It generates a block YAML manifest, uses persistent
per-node state, and keeps the entire run directory on success and failure. Pick a
new output directory for each run. It never supplies `--clean` or removes volumes.
Ports default to 35120–35180; `--port-base` reserves a different range.

Use an independently published runtime to test Native AOT or a preserved baseline:

```bash
dotnet publish src/SlimFaas/SlimFaas.csproj -c Release -r win-x64 \
  -p:SkipClientAppBuild=true -p:IlcGenerateDgmlFile=false \
  -p:IlcGenerateMstatFile=false -p:IlcSingleThreaded=true -o artifacts/native
python .bin/test-slimdata-local-chaos.py \
  --runtime artifacts/native/SlimFaas.exe \
  --output artifacts/local-chaos/native --durations 1,5,10
```

This Windows publication disables optional compiler diagnostics and serializes
code generation to limit build staging pressure; record these flags with the
runtime hash. The diagnostics are described in the [.NET Native AOT compiler
guide](https://github.com/dotnet/runtime/blob/main/src/coreclr/nativeaot/docs/troubleshooting.md).
Build failures and host-capacity interruptions are separate from runtime recovery
results. Run controlled measurements after compilation and other campaigns stop.

For a managed DLL, `--dotnet /absolute/path/to/dotnet` selects the host. Keep
binaries outside the build directory when rebuilding another version on Windows.
Resumed API exercises allocate fresh counter keys and retain previous completed
counter checkpoints for verification; they do not reset the retained values.

## Workloads and invariants

| Scenario | Checks |
|---|---|
| `payloads` | Concurrent Async bodies of 65,536, 262,144, 1,048,576, 2,000,000, 2,097,152 and 4,194,304 bytes, each with Content-Length and chunked transfer; function-side length and SHA-256 |
| `api` | Sync status/body integrity; Sets, HashSets, Files and retained Stream bodies on every node; concurrent atomic INCR/DECR, INCRBY/DECRBY/INCRBYFLOAT, TTL and explicit 1 MiB overflow rejection |
| `files` | Empty through 16 MiB uploads/downloads on every node and interrupted upload visibility |
| `wake` | A real function scales 0 → 1 → 0; a timed-out Sync response remains unknown and its later execution is observed |
| `websocket` | Optional official Python SDK: cross-node Sync streaming/Async/Streams, offline retained publications, reconnect through another owner, crashes and persistent restart |
| `retries` | A 2 MiB body across retry attempts 0/1/2, final-attempt flags, exhausted delivery, DLQ body checksum, repair/requeue and actual concurrent job processes |
| `startup-jobs` | Delay periodic configuration refresh for five minutes; run a declared job immediately and reject an undefined name before enqueue |
| `schedules` | Create a dynamic UTC cron schedule, read it through every node, preserve it across full persistent restart, observe a real scheduled process and delete it on every node |
| `streams` | Two real independent HTTP consumers, concurrent append order across paginated history, contiguous ACKs, length/age retention with explicit 410 boundaries and gap counters, then persistent restart |
| `websocket-timeout` | Suspend the owning node beyond an eight-second delivery timeout, keep the default retry statuses and verify a real 2 MiB delivery after owner recovery |
| `lease-burst` | Send a write burst, suspend one follower during another burst, pause the producer for one second and submit one unretried write while the majority remains healthy; retain its latency and verify the final value on every node |
| `chaos` | Independent producers for Sync, Async, Streams, Files, Sets, HashSets, atomic counters and jobs while a follower, leader or both followers are suspended |
| `restarts` | Successive abrupt follower/leader crashes with supervisor restart, followed by a full supervisor/cluster restart using original state |
| `partitions` | Linux packet drops on every Raft link while HTTP traffic continues, followed by quorum restoration and reconciliation |
| `latency` | Linux netem adds 25 ms to loopback packets for the longest requested duration while mixed traffic continues |
| `diskfull` | Exhaustion of a marked, separate 128 MiB filesystem; restore space by removing only the synthetic filler, then verify original state |
| `job-diskfull` | Hold an acknowledged real job, suspend Raft so cleanup cannot free space, exhaust the marked filesystem with progressively smaller writes, complete the job, then verify its terminal checkpoint survives recovery and full supervisor restart |
| `leader-route` | Warm a follower's leader HTTP route, suspend that leader and submit one unretried small mutation; require completion through the newly elected leader before the existing 15-second request budget |

`--scenarios` selects a comma-separated list. `--payload-sizes` accepts exact byte
counts, up to the fixture's 20 MiB ceiling; it does not change runtime limits.
The runtime's HTTP/body/file capacity limits still apply. `api` also checks TTL
and deletion for Sets, HashSets and Files, including deletion visibility on all nodes.

The optional WebSocket scenario uses the existing locked client dependencies:

```bash
uv run --locked --project client/python/slimfaas-client python .bin/test-slimdata-local-chaos.py \
  --output artifacts/local-chaos/websocket --scenarios websocket \
  --request-timeout 120 --recovery-timeout 300
```

Every mutation has an fsynced client intent and result. HTTP timeouts, disconnects
and server errors remain **unknown**, because commit may precede response loss.
Mutations are never automatically retried by the harness. Unique IDs and
independent, fsynced function/job journals reconcile confirmed acceptance with
actual execution. Repeated attempts are recorded; at-least-once delivery permits
duplicates. A received success proves execution, not that its queue acknowledgement
was durably committed before a subsequent fault.

After restoration, every confirmed immutable value/file/retained Stream body is
read from every node through the API and checked by checksum. Wrong values and
server errors fail immediately; only missing values may wait within a single
bounded convergence deadline. Confirmed Async/Stream publications and jobs must
execute. Counter responses must be unique and all nodes must converge to a value
between confirmed increments and confirmed plus unknown increments. Sequential
node reads can overlap a still-running unknown increment. The campaign journals
each observation and requires convergence within the existing phase deadline;
values below the highest confirmed increment response, out-of-bounds or regressing
values, duplicate confirmed results and server errors
fail immediately. A direct
write to the minority must not be acknowledged before quorum restoration.

Process suspension is a real stopped-process/communication outage. On POSIX it
uses `SIGSTOP`/`SIGCONT`; Windows uses the native process suspension APIs. It is
**not a network-only partition**, and it does not simulate a disk failure. Crash
tests use abrupt process termination. The harness only signals PIDs recorded by
its own Local supervisor.

The Linux fault wrappers require root, `unshare`, `iproute2`, `iptables`, ext4 and
loop-mount support. They isolate rules/mounts from the host. The disk wrapper
creates a new resource directory and retains the image after unmounting it:

```bash
sudo env "PATH=$PATH" "DOTNET_ROOT=$DOTNET_ROOT" \
  bash .bin/run-local-network-chaos.sh \
  --output artifacts/local-chaos/network --scenarios api,partitions,restarts
sudo env "PATH=$PATH" "DOTNET_ROOT=$DOTNET_ROOT" \
  bash .bin/run-local-disk-chaos.sh artifacts/local-chaos/disk-resources \
  --output artifacts/local-chaos/disk --scenarios api,diskfull,job-diskfull,restarts \
  --durations 15 --recovery-timeout 300
```

Keep the runtime, fixture and output on a native Linux filesystem when using WSL;
a private network namespace can lose access to Windows-backed 9p mounts. To
resume a failed campaign, keep its port base, select the remaining scenarios and
use `--resume`. The client ledger is reloaded and confirmed data is verified
before additional faults. Previous reports and all persistent state are retained.

## Endurance matrix

The `SlimData Local reliability` workflow runs quick checks on Linux and Windows
for relevant pull requests. Its manual `endurance` input adds three independent
Linux jobs, one per role. Each exercises 30, 60, 300, 900, 1,800 and 3,600 seconds
of absence, approximately two hours per role before verification overhead.

The same campaign can run locally:

```bash
python .bin/test-slimdata-local-chaos.py \
  --output artifacts/local-chaos/endurance-follower \
  --scenarios payloads,api,files,retries,chaos,restarts \
  --roles follower --durations 30,60,300,900,1800,3600 \
  --recovery-timeout 300 --traffic-interval 2
```

Repeat with `leader` and `quorum`, changing the output path. Several simultaneous
campaigns need disjoint port ranges. Do not run correctness load, compilation or
other campaigns alongside a performance comparison.

## Evidence to retain

- `parameters.json`: commit, platform, exact arguments and start time.
- `manifest.yaml`, `state/`: actual configuration, original WAL/snapshots and files.
- `client.jsonl`: intents, accepted/rejected/unknown responses, fault times and
  resource samples (CPU, RAM, managed heap, available GC metrics, WAL disk bytes
  and snapshot identities).
- `receipts/`: function checksums, attempt/element IDs and completed job arguments.
- `*.prom`, supervisor/node logs: raw diagnostics, commit/apply indices and
  snapshot/compaction evidence. Applying a skipped/corrupt command fails the run.
- `accounting.json`: repeated deliveries and final observations for unknown writes.
- `report.json`: passed phases, actual absence/election/recovery times and failures.

An unresolved unknown operation is not counted as confirmed loss or as proven
rejection. Preserve it for investigation. A stopped or failed campaign must not be
presented as successful endurance evidence.

The read-only evidence exporter writes under the repository's `artifacts/`
directory. It resolves paths and rejects traversal or symlinks escaping that
output root; it cannot overwrite repository source files through `--output`.
Choose a new JSON filename outside every input campaign directory. Existing
reports, client journals and other files are never overwritten by the exporter.

The command-format counters are a separate consensus gate even if application
checksums converge. A startup failure in a MemoryPack formatter must not be
converted to an incompatible-command skip. The generated-reader regression
models partially published formatter registration for batch, directory, Stream
and DLQ payloads; the retained network/restart campaigns exercise real replay.

## Performance comparisons

The dedicated GitHub Actions endurance workflow supports Linux managed and Windows
Native runtimes. Each selected platform runs the follower, leader and quorum roles
independently through 30/60/300/900/1800/3600-second absences, then crashes and a
full restart with its original volumes. Select `endurance_platforms=all`, `linux`
or `windows-native` when dispatching:

```bash
gh workflow run slimdata-local-chaos.yml --ref YOUR_BRANCH \
  -f endurance=true -f endurance_platforms=all
```

Run performance comparisons on a separate host or after local chaos work stops.

Reuse [the existing benchmarks](benchmarking.md), including
`.bin/slimfaas-local-benchmark.sh` and the `SlimFaasBenchmark compare` command.
`BENCHMARK_RUNTIME` selects a preserved native executable or managed DLL when
`BENCHMARK_SKIP_BUILD=true`; the same benchmark driver and manifest are used.
The manifest records the runtime path and SHA-256. `BENCHMARK_SLIMFAAS_DLL`
remains supported for existing callers.
Keep p50/p95/p99, throughput, errors, CPU/RAM, Raft diagnostics and raw samples.
Short or overlapping runs cannot support a performance non-regression verdict.

## Outstanding release gates

The tracker must distinguish implemented coverage from executed evidence for
every API and absence duration. Additional isolated campaigns are required for
asymmetric partitions, other network latency/loss profiles, targeted I/O faults,
targeted WAL/snapshot/compaction crash windows, advanced manual Stream checkpoints,
scheduling over prolonged outages, permanent
loss of a file's originating volume, and mixed-version rolling upgrades.

Files replicate metadata through Raft and transfer bytes separately; an upload's
metadata acknowledgement alone must not be mistaken for a verified durable copy
on a quorum of disks. Returning nodes retain their original volumes in this
campaign. Permanent-volume-loss guarantees need separate evidence.

Native AOT publication, complete tests and same-host baseline/candidate benchmark
results are separate acceptance criteria. The LTS verdict remains blocked until
the tracker contains successful evidence for the required matrix and any
confirmed defects are resolved.
