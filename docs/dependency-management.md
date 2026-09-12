# Dependency Management Policy

This page describes how SlimFaas selects, updates, pins and retires third-party dependencies. It is referenced from the project's [OpenSSF Security Insights manifest](../security-insights.yml) and complements the [Security Policy](../SECURITY.md).

## Ecosystems

| Ecosystem | Where it is declared | Lockfile |
| --- | --- | --- |
| .NET / NuGet | every `*.csproj` under `src/`, `tests/`, `client/dotnet/` | none (versions are explicit in the project files); the SDK is pinned by `global.json` |
| npm / pnpm | `src/SlimFaasPlanetSaver`, `src/SlimFaasSite`, `src/FibonacciReact` | `pnpm-lock.yaml` |
| npm | `src/SlimFaas/ClientApp` (embedded dashboard) | `package-lock.json` |
| Python | `client/python/slimfaas-client` | `uv.lock` |
| Container base images | every `Dockerfile` | image tags, updated by Dependabot |
| GitHub Actions | `.github/workflows/*.yml` | action versions, updated by Dependabot |

## Selecting a dependency

- Prefer packages with an active maintainer, a recent release and a public issue tracker.
- The licence must be on the [CNCF allowed third-party licence list](https://github.com/cncf/foundation/blob/main/policies-guidance/allowed-third-party-license-policy.md) and pass the FOSSA License Compliance check that runs on every pull request. Check direct **and** transitive dependencies; FOSSA can flag differently licensed vendored or example files inside a distribution. `GPL-2.0-only`, `GPL-3.0-only` and other copyleft licences are not accepted in runtime code without an explicit CNCF exception.
- Runtime projects (SlimFaas, SlimData, SlimFaasMcp) are compiled with Native AOT and trimming. A dependency must be AOT and trim compatible; reflection-based serialisation or dynamic code generation is a blocker.
- Keep the dependency surface small: a new runtime dependency needs a justification in the pull request description.

## Updating dependencies

- [Dependabot](../.github/dependabot.yml) opens grouped pull requests every Monday for each ecosystem (minor and patch updates grouped, major updates separate).
- Every update runs the full CI: unit tests, coverage, SonarCloud, FOSSA, Docker builds and the native AOT publish for the runtime projects.
- Major updates and base-image changes are reviewed by a maintainer and tested with the local end-to-end demo (`dotnet run --project src/SlimFaas -- local validate -f ../../slimfaas.local.yaml`) before merge.
- After any change, regenerate every affected lockfile and commit it together with the manifest change.

## Pinning

- Lockfiles are committed. CI installs JavaScript packages with `pnpm install --frozen-lockfile` or `npm ci`; the Python client keeps `uv.lock` committed and installs with `uv sync`.
- `global.json` pins the .NET SDK used by CI and contributors.
- When a newer version of a dependency is rejected (licence, AOT regression, security), pin the last accepted version with an explicit direct constraint and a comment explaining why, so a future lockfile refresh cannot silently reintroduce it. Record the hold and its reason in the pull request summary.
- GitHub Actions are referenced by major version tag today; pinning them to commit SHAs is planned as part of the [supply-chain hardening work](https://github.com/SlimPlanet/SlimFaas/issues/367).

## Vulnerabilities in dependencies

- Dependabot security alerts are enabled on the repository. Maintainers triage new alerts within 7 days.
- A vulnerability that affects SlimFaas at runtime is fixed by upgrading the dependency and publishing a patch release; a CVE in a base image is fixed by rebuilding on the updated image through the Dependabot Docker updates.
- If no fixed version exists, the maintainers evaluate a replacement or a mitigation and document the decision in the tracking issue.
- Vulnerabilities in SlimFaas itself follow the [Security Policy](../SECURITY.md).

## Software Bill of Materials

SBOMs for the runtime and its container images are generated from the lockfiles and project files. See the [SBOM section of the README](../README.md#software-bill-of-materials-sbom) for how to obtain or generate one.

## Removing a dependency

A dependency is removed or replaced when it becomes unmaintained, fails the licence policy, breaks AOT compatibility, or is no longer needed. The removal is a normal pull request with the lockfile regenerated.
