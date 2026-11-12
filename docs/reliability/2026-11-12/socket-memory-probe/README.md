# Raft socket-memory reproduction

This small Linux-only control exercises the actual internal
`RaftClientHandlerFactory`. It uses the existing test assembly identity to avoid
adding a public product API. `server` listens without accepting or reading;
`client` cancels 120 eight-MiB uploads in groups of ten, at concurrency four and
a 100 ms cancellation deadline, then reports cgroup v2 socket/total memory.
The buffers and deadline belong to this diagnostic, not to production defaults.

Publish both copies before running the control. To build the same diagnostic
against a preserved earlier implementation, pass an absolute `SlimDataProject`
path from its isolated worktree:

```bash
dotnet publish docs/reliability/2026-11-12/socket-memory-probe/SocketMemoryProbe.csproj \
  -c Release -r linux-musl-arm64 --self-contained true -p:PublishAot=false \
  -p:SlimDataProject=/absolute/path/before/src/SlimData/SlimData.csproj \
  -o artifacts/socket-memory/before
dotnet publish docs/reliability/2026-11-12/socket-memory-probe/SocketMemoryProbe.csproj \
  -c Release -r linux-musl-arm64 --self-contained true -p:PublishAot=false \
  -o artifacts/socket-memory/after
```

Copy each complete publication to its own directory on the Podman VM's native
Linux filesystem. Use a tools image inheriting the same production runtime
base. Run the receiver and sender in separate cgroups sharing one network
namespace, so the sender's socket charge remains visible:

```bash
variant=after
server=slimfaas-pr506-socket-server-$variant
client=slimfaas-pr506-socket-client-$variant
image=localhost/slimfaas-pr506:after-tools-sockets
# This path is on the VM, not a macOS bind mount.
probe_path=/var/home/core/slimfaas-pr506/socket-memory-$variant
podman run -d --name "$server" --user 1000:1000 --memory 256m \
  -v "$probe_path:/probe:ro,z" --entrypoint /probe/SlimData.Tests "$image" server
# Wait for LISTENING_WITHOUT_ACCEPT in this receiver's log before starting.
podman logs "$server"
podman run --name "$client" --user 1000:1000 --memory 512m \
  --network "container:$server" -v "$probe_path:/probe:ro,z" \
  --entrypoint /probe/SlimData.Tests "$image" client
podman inspect "$client" "$server" > "artifacts/socket-memory-$variant.json"
podman stop --time 1 "$server"
```

Retain stdout, inspections, source SHA and publication hashes. Use fresh names
for repeats. The receiver creates no runtime data, WAL or snapshots. Stopping
it releases this diagnostic's remaining network state. A sender OOM is a failed
bounded control and must be recorded rather than reported as a memory sample.

The original factory accumulated 316,686,336 socket bytes after 120 cancellations;
the corrected factory reported zero after every group. Total cgroup memory was
342,867,968 versus 26,030,080 bytes in those retained observations. These are
transport-resource controls, not throughput benchmarks or a durability proof.
