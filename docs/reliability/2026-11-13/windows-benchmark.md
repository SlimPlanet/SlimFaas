# PR #506: Windows large-message benchmark

The final Windows managed/AOT campaigns, the HTTP quick benchmark and the source
demo pass. The macOS and Linux campaigns remain separate. These Windows controls
do not establish LTS certification or comparative performance non-regression.

## Reproduction and cause

The Windows CI run on `a8b5a34e` passes the twelve SDK async size/encoding cases,
then times out all four concurrent 2 MiB Stream publications at the unchanged
120-second request bound. Its [original job log](https://github.com/SlimPlanet/SlimFaas/actions/runs/43463306251/job/132015700623)
and artifact `14057188880` remain authoritative for that failure.

Local Release controls use SDK 11.0.100 and the unchanged campaign. Both
`SharedMemory` and explicit `PrivateMemory` reproduce availability failures.
Windows direct I/O alone also fails. These failed attempts remain retained;
changing memory strategy is not the correction.

The first local diagnostics used Python 3.12.11; final controls use Python 3.11.5,
with the same locked websockets 16.1.1 dependency. An unmodified `a8b5a34e` binary
also passes all twelve short HTTP payload cases with Python 3.11.5 and the original
15-second bound on the idle host. The failure is intermittent and path-sensitive;
this successful control does not replace the failed CI Stream publication or
local SDK histories.

The default data chunk is the system page size: 4 KiB on this Windows host. A
2 MiB log entry spans more than 500 separately opened/flushed files. Diagnostic
stacks show mapped-page creation and pending per-page write-through I/O; the
Raft logs repeatedly show cancelled RPCs around their one-second deadline.
The [upstream options](https://github.com/dotnet/dotNext/blob/11bf3390134e69c414508a24d08dd6baa8e1e8bc/src/cluster/DotNext.Net.Cluster/Net/Cluster/Consensus/Raft/StateMachine/WriteAheadLog.Options.cs)
and [page implementation](https://github.com/dotnet/dotNext/blob/11bf3390134e69c414508a24d08dd6baa8e1e8bc/src/cluster/DotNext.Net.Cluster/Net/Cluster/Consensus/Raft/StateMachine/WriteAheadLog.Page.cs)
confirm the geometry and Windows synchronous mapped-page flushes. Repeated
cancellation prevents progress despite a healthy three-node host.

A diagnostic binary changes only the Windows data chunk size to 64 KiB. The
complete SDK scenario then passes, including initial Stream publication, offline
retention, reconnect, successive crashes and full original-volume restart:
306 confirmed operations, 11 unknown fault-time outcomes and no missing confirmed
operations. This exploratory run overlaps a failing storage control and is not
used as a latency comparison. Its deliberately unmarked experimental volumes
must only be reopened by that diagnostic binary.

## Correction and storage compatibility

New Windows WAL directories use 64 KiB data chunks. The runtime writes and
flushes `wal/slimdata-chunk-size` before constructing the WAL, then reads it on
every reopen, on all operating systems. Invalid or unreadable markers stop
startup. Existing unmarked WALs retain their historical system-page geometry;
new Linux and macOS WALs retain their existing defaults. The memory strategy
remains `SharedMemory`.

DotNext explicitly requires a fixed chunk size for existing WAL addresses. The
marker is part of persisted state: preserve it in backups, and never edit or
delete it. Older runtimes that do not understand the marker must not open marked
WALs. Existing Windows volumes are not silently migrated or rewritten, so their
old storage performance limitation can remain. This change introduces no request
retry, timeout increase, lower payload size, reduced concurrency, dependency
change or durability bypass.

Tests cover fresh Windows/non-Windows defaults, marker preservation and rejection,
and actual durable entry replay with both legacy and recorded geometry.

## Final Windows validation

The final binaries use `a8b5a34e` plus the retained runtime patch, built with SDK
11.0.100. Their source and binary hashes are recorded in the
[structured evidence](windows-benchmark.json). Final campaigns ran sequentially
after compilation and storage compression stopped. Default payload sizes,
concurrency and request/recovery deadlines remain unchanged.

| Campaign | Result | Confirmed operations | Unknown fault-time outcomes |
|---|---|---:|---:|
| Unmodified reference, twelve HTTP payload cases | Passed | 12 | 0 |
| Final managed SDK, Streams, reconnect, crashes and original-volume restart | Passed | 131 | 10 |
| Final AOT APIs, payloads, Files, retries, DLQ, Jobs, 1/5/10-second faults and restart | Passed | 301 | 11 |
| Final AOT SDK, Streams, reconnect, crashes and original-volume restart | Passed | 79 | 6 |
| Final AOT WebSocket owner timeout | Passed | 1 | 0 |
| Final AOT silent follower, stale leader route and protocol recovery | Passed | 45 | 0 |

All **557 final-candidate confirmed operations** reconcile without confirmed loss
or checksum corruption. The **27 unknown outcomes** remain separate. Every final
candidate node retains `slimdata-chunk-size=65536` after restart; the reference
has no marker. The four initial 2 MiB Stream publications complete in
**0.750/0.750/1.032/1.422 seconds** managed and
**0.391/0.860/0.875/1.281 seconds** AOT, within the unchanged 120-second bound.
Stale-leader routing recovers in 9.422 seconds within its 15-second bound. The
protocol control takes 2.891 seconds to listen after restart, then 2.906 seconds
for ready/write.

The AOT HTTP `quick` benchmark passes all **24 measured cases** (64 B, 4 KiB,
256 KiB and 2 MiB; concurrency 1/16; three-second measurements, one-second warmup,
one repetition). Request failures, missing async IDs and duplicate async IDs are
all zero. Scaling accepts all **200/200** messages, reaches four ready replicas
in **7.475 seconds**, and drains in **23.577 seconds**, within the 120-second
budget. This is a single candidate smoke test; comparison against another
SlimFaas revision still requires a separate controlled matrix.

The unchanged source manifest validates, `/status-functions` returns the demo
functions, and `/function/fibonacci1/hello/local` returns HTTP 200 and
`Hello local!`. Native publication succeeds with the existing MemoryPack
IL2104/IL3053 and ConfigurationManager IL2104 summaries retained. The documentation
production build passes: 23 pages, 1,672 local links/assets and 401 search entries.
The five Windows benchmark process/port/disk-reserve tests pass.

The 354 SlimData tests, including twelve new geometry/replay tests, pass. Two
complete solution attempts retain separate intermittent failures in the existing
`WebSocketPeerIntegrationTests.Early_completed_reply_cancels_upload_without_canceling_the_successful_response`
(loopback connection timeout) and
`LocalJobManagerTests.FailedProcess_HonorsBackoffLimitAndBecomesFailed`
(job still Running at its assertion deadline). These tests and deadlines were
not changed. A subsequent unchanged complete run passes **2,125 tests, zero
failed or skipped**. Its log and both earlier failed attempts remain retained.

## Retained evidence

[Structured reports and SHA-256 index](windows-benchmark.json) and the
[compact evidence archive](windows-benchmark-evidence.zip) retain the original
client ledgers, independent receipts, metrics, supervisor logs, manifests,
diagnostic stacks, source patch, validation commands and HTTP samples. Full
persisted WAL/snapshot directories and heap dumps remain local; they are not
included in this compact archive. No failed attempt is silently replaced.

## Reproduce on Windows

Use .NET SDK 11.0.100 and Python 3.11, with an otherwise idle host. Each output
directory must be new; retain failed attempts and do not retry mutations.

```powershell
dotnet build src/SlimFaas -c Release -p:SkipClientAppBuild=true
uv sync --frozen --python 3.11 --project client/python/slimfaas-client --no-build --no-install-project
$env:PYTHONPATH = 'client/python/slimfaas-client/src'
uv run --locked --no-sync --no-build --project client/python/slimfaas-client python .bin/test-slimdata-local-chaos.py --output artifacts/windows-validation/websocket --scenarios websocket --delivery-diagnostics --request-timeout 120 --recovery-timeout 300
python .bin/test-slimdata-local-chaos.py --output artifacts/windows-validation/quick --durations 1,5,10
```

For AOT, publish once before starting measurements and pass
`--runtime artifacts/windows-native/SlimFaas.exe` to the same campaign commands:

```powershell
dotnet publish src/SlimFaas -c Release -r win-x64 --self-contained true -p:SkipClientAppBuild=true -o artifacts/windows-native
$env:BENCHMARK_PROFILE = 'quick'
$env:BENCHMARK_SKIP_BUILD = 'true'
$env:BENCHMARK_RUNTIME = (Resolve-Path artifacts/windows-native/SlimFaas.exe).Path
$env:BENCHMARK_RUNTIME_MODE = 'aot'
& 'C:\Program Files\Git\bin\bash.exe' .bin/slimfaas-local-benchmark.sh
```

Run the same commands in a separate checkout of `a8b5a34e` to reproduce the
unmodified reference. Do not reuse a marked candidate WAL with that older binary.
