---
name: slimfaas-build-test
description: Build and test SlimFaas, SlimData, SlimFaasMcp, SlimFaasKafka and official SDKs. Use for Native AOT, analyzer, NuGet, license, CI or regression-test work.
---

# Build and test SlimFaas

Use the .NET SDK in `global.json`, Node 26 for embedded dashboards and site
builds, and the pnpm version declared by each web package. The Python SDK uses
Python 3.10 or newer and `uv`. Use the host RID for Native AOT publication.

## .NET validation

```bash
dotnet build
dotnet build -p:SkipClientAppBuild=true
dotnet test
dotnet test --collect 'Code Coverage;Format=cobertura'
dotnet publish src/SlimFaas/SlimFaas.csproj -c Release
```

Use `dotnet test <project> --filter 'FullyQualifiedName~<case>'` for targeted
feedback. `SkipClientAppBuild=true` skips the SlimFaas and SlimFaasMcp embedded
dashboard builds during backend verification. Run the relevant dashboard or
site workflow when editing a client application. The test projects inherit
extra analyzer configuration from `eng/tests.globalconfig`. Coverage outputs
live under `TestResults/`.

- Use source generation for JSON and logging, and keep MemoryPack contracts
  compatible. Exercise AOT publication when changing dependencies, reflection
  usage, native calls or serialization contracts. Verify P/Invoke on the
  affected platforms.
- Keep package versions centralized. For NU1901–NU1904, update the affected
  package or centrally pin the patched transitive dependency. Preserve the
  audit and analyzer checks and apply scoped exclusions with a justification
  where required.
- Check the CNCF
  [allowed license policy](https://github.com/cncf/foundation/blob/main/policies-guidance/allowed-third-party-license-policy.md)
  and the PR's FOSSA result after changing dependencies. Verify bundled files
  and transitive licenses. Prefer a compliant replacement. When a version
  needs a temporary hold, pin the highest version already validated by FOSSA
  and record the reason and review trigger in the PR. A CNCF Governing Board
  exception is required for licenses outside the allowlist. Regenerate all
  affected lockfiles and rerun the relevant tests.
- Use isolated HTTP and Kubernetes fixtures and verify both successful and
  failing paths. Prefer explicit readiness and completion signals to fixed
  delays.

## SDK and web checks

```bash
dotnet test client/dotnet/SlimFaasClient/tests/SlimFaasClient.Tests/SlimFaasClient.Tests.csproj
(cd client/python/slimfaas-client && uv sync --extra dev && uv run pytest)
(cd src/SlimFaasPlanetSaver && pnpm install --frozen-lockfile && pnpm run coverage)
```

WebSocket contract changes need matching runtime and SDK checks for
registration, callbacks, sync streaming and publish/subscribe. Keep the
runtime and both SDKs aligned. Function names must be distinct from Kubernetes
Deployment names. Clients sharing a name use identical configuration.

## CI review

Read the affected workflow under `.github/workflows/` for its entry points.
Main CI validates unit tests, Sonar, persistent local restart, AOT builds and
release packaging. The dashboard workflow covers documentation and client
contracts. Package and container publishing use their dedicated workflows.
Check the external SonarCloud quality gate alongside the scanner job, and
attribute each CI, FOSSA and DCO result to the exact PR head.
