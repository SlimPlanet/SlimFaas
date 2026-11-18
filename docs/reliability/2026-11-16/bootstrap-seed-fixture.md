# Preserve single-attempt recovery checks while stabilizing bootstrap setup

The [Windows coverage job on documentation head02bff599](https://github.com/SlimPlanet/SlimFaas/actions/runs/43871528315/job/133313689367)
fails before any pod replacement: the initial `before-replacement` write loses
leadership between leader selection and replication. DotNext reports
`NotLeaderException` with an inner cancellation. The scanner later uploads its
analysis successfully, but the failed test makes the job exit with code one.
The original failure remains retained; it is separate from the external
SonarCloud quality gate.

The fixture's bootstrap predicate cannot reserve leadership for the following
direct write. Only its logical key/value setup seed now retries that specific
leadership exception, using the same key/value and the existing **90-second
fixture deadline**. Other exceptions and an expired outer deadline propagate.
The request/RPC bounds, theory timeout, recovery assertions and every
post-bootstrap application write remain unchanged and single-attempt.
No failed attempt is treated as definitely uncommitted or converted into a
confirmed response.

Per-node warning/error diagnostics are buffered and emitted when bootstrap
reselects a leader or the fixture fails. The retry therefore does not silently
hide node exceptions behind the fixture's former disabled logging. Product
logging, APIs, dependencies, AOT publications and image71 remain unchanged.
The six completed long roles still test production source189.

Four deterministic regressions cover leadership loss with inner cancellation
while the outer deadline remains live, unrelated failures, an already expired
deadline, and cancellation during a rejected seed attempt. Before correction,
the first case reproduces the exception: **one failed, three passed**. After
correction, the full solution passes **2,425 tests, zero failures or skips**;
the two membership-recovery scenarios also pass with code coverage enabled.
The suite includes 1,787 SlimFaas, 466 SlimData, 109 MCP, 52 official .NET SDK
and eleven Kafka tests. Standard analyzers, warnings-as-errors and restore
audits remain active.

Compilation starts only after all long campaign writers stop and the complete
quorum original-state index is frozen. These are test-fixture changes; neither
the frozen runtime binaries nor their performance comparison settings change.
The original historical post-election failure remains documented separately;
this correction does not add retries to that recovery stage.

[Structured validation](bootstrap-seed-fixture-142.json) retains exact commands,
exit codes, source hashes and TRX counters. The
[31-file raw reproduction](bootstrap-seed-fixture-142-raw.tar.gz), SHA-256
`f83f519ee507e29e6fda67e6144546ecb3bcce4028d88263b9a6a01eacc3f0da`,
contains the original CI log, original fixture, RED/GREEN sources, test logs,
TRX reports and instrumented coverage. Archive members are independently
checked against their hashes after export.

To reproduce, use an isolated checkout at `02bff599` with the recorded .NET11
toolchain, extract the archive, and copy its `reproduction/red-source/tests`
files into that checkout. The focused command must report one failed case:

```bash
dotnet test tests/SlimFaas.Tests/SlimFaas.Tests.csproj \
  -p:SkipClientAppBuild=true -nr:false \
  --filter FullyQualifiedName~SlimFaas.Tests.Integration.RaftMembershipBootstrapTests
```

Replace the helper with the archived
`reproduction/RaftMembershipBootstrap.green.cs` at
`tests/SlimFaas.Tests/Integration/RaftMembershipBootstrap.cs`, then validate:

```bash
dotnet test -p:SkipClientAppBuild=true -nr:false
dotnet test tests/SlimFaas.Tests/SlimFaas.Tests.csproj \
  --no-build --no-restore -p:SkipClientAppBuild=true -nr:false \
  --filter FullyQualifiedName~SlimFaas.Tests.Integration.RaftMembershipRecoveryTests \
  --collect 'Code Coverage;Format=cobertura'
```

Corrected Windows CI, the external quality gate and formal non-regression remain
separate checks. No rule, finding, timeout or acceptance criterion is waived.
