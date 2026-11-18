# Agent guidelines for SlimFaas

## Repository layout

- `src/` contains the runtime, data, MCP, Kafka, Planet Saver and documentation site.
- `samples/` contains the workloads used by the demos, CI images and public examples.
- `benchmarks/SlimFaas.Benchmarks` provides the unified benchmark driver. `tools/` includes MemoryLab.
- Tests, SDKs, docs and deployments live in `tests/`, `client/`, `docs/` and `demo/`. Use the repository root as the Docker build context.

## Skills

Open the skill for the workflow that applies to the change.

| Skill | Use for |
| --- | --- |
| [Build and test](.agents/skills/slimfaas-build-test/SKILL.md) | SDK setup, .NET and SDK tests, dependency changes, CI and AOT validation |
| [Documentation](.agents/skills/slimfaas-documentation/SKILL.md) | Public docs, runnable examples, site changes and static export |
| [Local runtime and bundles](.agents/skills/slimfaas-local-release/SKILL.md) | Native local mode, Compose and Kubernetes demos, release bundles and smoke tests |
| [Benchmarks](.agents/skills/slimfaas-benchmarks/SKILL.md) | Performance measurement, recovery campaigns, result review and publication |

## Build quality

- Keep SlimFaas, SlimData and SlimFaasMcp Native AOT-compatible. Avoid runtime reflection in serialization, extend the existing source-generated `JsonSerializerContext` types and keep SlimData commands MemoryPackable.
- Inherit shared .NET settings from `Directory.Build.props` and `Directory.Build.targets`. Warnings are errors, with all analyzers, enforced code style and trim/AOT analysis. Scope justified exceptions to the smallest affected member or configuration section.
- Put NuGet versions in `Directory.Packages.props`. Project references use `PackageReference` without `Version` or `VersionOverride`. Audit direct and transitive dependencies. Pin a patched transitive version centrally when needed.
- Validate dependency and lockfile changes against the CNCF license policy and FOSSA. Check transitive and bundled licenses. Regenerate affected lockfiles and record any validated version hold with its reason in the PR.
- Add source-generated `LoggerMessage` methods in the logging type's `Foo.Log.cs` companion. Guard expensive arguments with `IsEnabled`.

## Web interfaces

Use BEM for new and modified application styles. Apply base and modifier classes together and update markup, styles and selectors together. Keep styles component-owned. Scope global resets, Markdown typography and third-party styles to their intended surfaces. Reuse the primary blue `#0000ff` token and verify responsive layout, keyboard navigation and dynamic content.

## Change validation

- Add focused regression tests for behavior changes and bug fixes. Cover success and failure, use isolated fixtures, and keep the full `dotnet test` suite passing.
- Update the relevant English documentation, samples and deployment examples for changes to public behavior, configuration or architecture. Keep runtime WebSocket contracts, official SDKs and their docs in sync.
- For changes to runtime, routing, data, jobs, queues, configuration or UI, verify the affected native local chain with the local runtime skill. Exercise the relevant performance controls for performance-sensitive changes in line with the requested measurement scope.
- Keep Kubernetes validation scoped to the intended cluster and namespace. Exercise real replica coordination and preserve persistent data compatibility.

## Release and security documentation

- Follow [the release guide](docs/releasing.md) for versioning, candidate builds, signing, publication and recovery. Validate release changes with `python3 .bin/test-release.py` and actionlint. Pin external actions by commit and release tools by checksum in `.bin/release-tools.json`.
- Keep [the dependency policy](docs/dependency-management.md), [security policy](SECURITY.md) and [Security Insights manifest](security-insights.yml) aligned with changes to maintenance, reporting and distribution.

See [CONTRIBUTING.md](CONTRIBUTING.md) for contribution requirements.
