# Benchmark exclusions and production analysis

Benchmark code and its orchestration/chaos/analysis programs are deliberately
excluded from static analysis. This differs from the previous coverage-only
policy. The [versioned inventory](../../../eng/sonar-benchmark-exclusions.txt)
lists narrow path patterns; it does not exclude all `.bin`, `tools` or reliability
documents. Reports remain eligible unless they belong to a benchmark fixture.

The unified benchmark project, CollectionProbe, MemoryLab and SocketMemoryProbe
also declare `SonarQubeExclude=true`. Their compilation, analyzers, NuGet audits
and CI execution remain enabled. The runtime, official SDKs, their tests and
production/release tools remain eligible for analysis. Only the Python SDK's
generic coverage report is imported after the benchmark polling script becomes
excluded; its tests still execute and produce their own coverage artifact.

The public pre-correction scan classified
`src/SlimData/QueueElementExtensions.cs` as `UTS` (test code). Production projects
reference a mocking assembly, directly or transitively. Sonar can infer test
classification from such references; the repository now explicitly derives
classification from the SDK's `IsTestProject` flag. No production rule or finding
is waived by this correction. See the
[scanner's categorization contract](https://docs.sonarsource.com/sonarqube-server/10.8/analyzing-source-code/dotnet-environments/specify-test-project-analysis).

Six boundary regressions cover broad exclusions reaching production/SDK code,
direct/nested diagnostic paths, preservation of release tools/evidence, missing
production sources and incorrect project classification. An MSBuild property
probe separately checks effective project classification and that compiler
warnings/analyzers remain enabled.

The CI verifier waits for the actual compute-engine task, checks submitted
project metadata, checks reported files and production/test classification, and
verifies imported C# and Python SDK coverage. Pull-request APIs omit unchanged
files; the verifier uses submitted project metadata for the full C# scope rather
than requiring every unchanged source to appear in that API. Windows PowerShell
and pwsh log encodings are supported. Inventory and completed-scan evidence are
retained as CI artifacts. **A passing local inventory is not a completed scan or
a passing external quality gate.** The first corrected CI scan remains pending.
