# Security Policy

## Supported versions

SlimFaas is released continuously from `main` (see [GOVERNANCE.md](GOVERNANCE.md#3-automated-semantic-versioning)). Security fixes are published as a new patch release on the latest release line; older versions are not patched.

| Version | Supported |
| --- | --- |
| Latest release (see [Releases](https://github.com/SlimPlanet/SlimFaas/releases)) | ✅ |
| Any earlier release | ❌ — upgrade to the latest release |

Container images are published on Docker Hub (`axaguildev/slimfaas`, `axaguildev/slimfaas-mcp`, `axaguildev/slimfaas-kafka`, `axaguildev/slimfaas-dotnet`). Only the tag matching the latest release, and `latest`, receive fixes.

## Scope

In scope: the SlimFaas runtime and its embedded dashboard, SlimData (Raft key-value store), SlimFaasMcp, the SlimFaasPlanetSaver npm package, the .NET and Python clients, the published container images and native release archives, and the CI/CD pipeline of this repository.

The `Fibonacci*` projects under `src/` and everything under `demo/` are demonstration workloads, not meant for production. Reports about them are still welcome but are handled with lower priority.

## Reporting a vulnerability

Please do **not** open a public issue for a security problem.

1. Preferred: use GitHub private vulnerability reporting: <https://github.com/SlimPlanet/SlimFaas/security/advisories/new>.
2. Otherwise e-mail the maintainers listed in [MAINTAINERS.md](MAINTAINERS.md); the primary contact is guillaume.chervet@axa.fr.

Include the affected component and version, a description of the impact, and steps or a proof of concept to reproduce it.

## What to expect

- Acknowledgement within 3 business days.
- An initial assessment (accepted, needs more information, or not a vulnerability) within 7 days.
- A fix released as a patch version, with a GitHub Security Advisory and a CVE when the issue qualifies. We ask reporters to keep details private until the fix is available and will credit them in the advisory unless they prefer otherwise.

## Further reading

- [Dependency management policy](docs/dependency-management.md)
- [OpenSSF Security Insights manifest](security-insights.yml)
- [OpenSSF Scorecard](https://scorecard.dev/viewer/?uri=github.com/SlimPlanet/SlimFaas) and [CLOMonitor](https://clomonitor.io/projects/cncf/slimfaas) reports
