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

Seven boundary regressions cover broad exclusions reaching production/SDK code,
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
a passing external quality gate.**

The first updated scan (`81881c7a`) fails before analysis submission: automatic
TypeScript discovery reads a copied `ClientApp/tsconfig.json` below the benchmark
build output, where its `src` inputs do not exist. Excluding source files alone
does not constrain that discovery. The follow-up selects the three original
application TSConfigs explicitly and excludes generated `bin`/`obj` outputs.
The separate integration benchmark test is also included in the inventory.
Submitted C# metadata is captured before scanner completion so an unsuccessful
scan still retains its actual project classifications.

The same Windows coverage run independently records one membership-recovery
failure: `NotLeaderException` while removing an offline member. Its 2,432 passes
and this one failure are retained. This occurs after replacement begins and is
distinct from the previously corrected bootstrap-seed fixture. The ordinary
unit-test job succeeds. The membership failure remains under investigation;
recovery writes are not retried or their deadlines relaxed.
[Failure identities and log hash](sonar-818-failures.json).
Completed scan/scope/coverage verification remains required after the correction.

The `d0cd0d2f` scan is successfully uploaded. Its submitted metadata identifies
all five product/SDK projects as `Product`, and the unified benchmark as excluded.
C# coverage import now reports **301 main files with coverage** (543 files total),
and Python SDK coverage is imported for three files. The Windows job still fails:
the busy WAL test with shared memory reaches its unchanged 20-second deadline.
That independent concurrency failure remains open.

A read of the processed PR file index also catches three benchmark tests still
present as `UTS`: `sonar.exclusions` applies to main sources, so the same targeted
inventory must also be passed as `sonar.test.exclusions`. The rest of the runtime
and SDK tests remain analyzed. See the [scanner installation documentation](https://docs.sonarsource.com/sonarqube-cloud/analyzing-source-code/scanners/sonarscanner-for-dotnet/installing)
for the two scope properties. The next CI scan must verify the corrected server
index; the previous scan is not presented as a successful exclusion boundary.
The boundary verifier now runs after an actual scanner submission even when the
independent test step fails, while preserving that job's failure.


The follow-up scan at `9d1a610d` completes with the boundary verifier passing
against the actual processed analysis. No inventory-excluded benchmark/test/script
file remains in the returned PR file index; submitted metadata retains all five
product/SDK projects as analyzed production. C# coverage is imported for 301 main
files. All 2,446 tests pass in that Windows coverage job, including the membership
fixture. The earlier WAL deadline failure remains retained and is not explained
by this subsequent pass. **The external Sonar quality gate still fails**; successful
scope/coverage verification does not clear its findings.
[Completed task, scope, coverage and artifact hashes](sonar-completed-scope-9d.json).


At `1b5552b8`, the external check no longer lists coverage as a failed condition;
security E and reliability C remain. One reported blocker is `json:S6418` on the
numeric `authorized_at` DCO approval timestamp in the published provenance. This
is an approval record, not a credential. Its keys now use precise approval terms;
all values are preserved, with original Git blob and before/after hashes indexed
in [the clarification record](dco-provenance-key-clarification.json). No evidence
file is excluded and no security finding is suppressed. Conditional internal TLS
opt-ins, HTTP defaults, cancellation observations and other reported findings
remain subject to their own review; this metadata correction does not clear them.
