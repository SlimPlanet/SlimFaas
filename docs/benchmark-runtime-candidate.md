# Queue selection candidate: running / loop

This isolated candidate starts from benchmark tooling commit `7d79b66c` and changes
only `GetQueueRunningElement` to a direct loop with a List buffer.
It preserves predicates, element identity, order, timeout/retry boundaries and
immutable result representation. The other selection paths remain the control.

No performance measurement has been run for this candidate. This branch is not
an adopted optimization. Use the same prebuilt driver and ABBA reference brackets
for comparison, then apply the existing latency/throughput/resource/integrity
budgets. Keep offload, WAL geometry and persisted copies fixed. The production
defaults are unchanged. See [benchmark methodology](benchmarking.md).

Validation command (unit tests only):

```bash
dotnet test tests/SlimData.Tests/SlimData.Tests.csproj -c Release
```
