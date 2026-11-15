# Queued request body preparation probe

Use commit `e5883fa8e28ba2775c4dee8e5e6a31a850d22c82` or a descendant, SDK
11.0.100 and Node 26. Run from the repository root, outside any benchmark window:

```bash
mkdir -p artifacts/pr506/reproductions/queued-body-memory-03/probe artifacts/pr506/logs
cp docs/reliability/2026-11-15/queued-body-probe/Program.cs artifacts/pr506/reproductions/queued-body-memory-03/probe/
cp docs/reliability/2026-11-15/queued-body-probe/Probe.csproj.template artifacts/pr506/reproductions/queued-body-memory-03/probe/Probe.csproj
cp docs/reliability/2026-11-15/queued-body-probe/run-probe.py artifacts/pr506/reproductions/queued-body-memory-03/
python3 artifacts/pr506/reproductions/queued-body-memory-03/run-probe.py
```

The assembly name uses the existing internal test visibility. There is no new
dependency or runtime API. The script refuses to overwrite prior results; keep
an earlier result directory and use a fresh checkout for a separate reproduction.
It retains all three subprocess outcomes and build logs.

The probe creates 96 distinct serialized packets with 2 MiB inline bodies and
retains all packets and decoded requests. Its legacy mode calls the original
MemoryPack decoder in the same probe, rather than an unmodified full runtime.
With a 320 MiB GC heap limit, legacy decoding must report OOM, while borrowed
body streams must succeed. Aggregate body length and SHA-256 must match the
unbounded legacy control. The body hash is streamed without materializing
another full body. Heap readings after preparation are not peak RSS, and this
probe makes no application throughput or Kubernetes acceptance claim.

See the [recorded observations](../queued-body-memory-probe.json) and
[investigation report](../README.md).
