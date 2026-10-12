# Releasing SlimFaas

SlimFaas uses one version for the runtime, native local bundles, container images,
Planet Saver, and the .NET and Python clients. Stable releases are approved by
reviewing and merging the release PR maintained by Release Please. The release
PR updates `version.txt`, package metadata and `CHANGELOG.md` together. Its merge
starts the automated build, verification and publication process.

This process applies after the migration is activated. Historical releases and
tags remain available, but do not retroactively acquire signatures or SBOMs.
The migration starts at the published version `0.84.16` and preserves the existing
changelog. New tags are `X.Y.Z`, without a `v` prefix.

## Daily use

Use a Conventional Commit PR title and squash merge reviewed PRs:

| Title | Next stable version |
| --- | --- |
| `fix: correct request routing` | Patch |
| `feat: add a trigger` | Minor |
| `feat!: remove an old API` or a `BREAKING CHANGE:` footer | Major, including before 1.0 |
| `docs: explain releases`, `test: ...`, `ci: ...` | No release by themselves |

The words `release`, `alpha` or `beta` in a commit do not publish anything.
`Release-As:` overrides are rejected. Maintainers review the proposed changelog
and common version in the release PR; merging it is the usual human approval.
Do not create a second release while an earlier release is incomplete.

To request a preview, run **Actions → Release → Run workflow**, select `main`
and `alpha` or `beta`. The version is the next calculated stable version followed
by `-alpha.<run number>` or `-beta.<run number>`. For example, `0.85.0-alpha.42`
becomes `0.85.0a42` on PyPI (PEP 440). Preview builds never change `latest`.
Other builds are retained as GitHub Actions artifacts only.

```bash
gh workflow run release.yml --ref main -f mode=validate
gh workflow run release.yml --ref main -f mode=alpha
```

The second command publishes a real preview. Only run it after completing the
registry setup below. Every candidate is built from the workflow's exact commit
SHA; an arbitrary tag, branch or SHA cannot be passed as a publication source.

## Pipeline and artifacts

1. `ci.yml` runs release failure tests, actionlint, .NET and Python tests, dashboard
   tests, native browser tests, Planet Saver coverage and the documentation build.
2. `release-build.yml` builds the native RID matrix, smoke-tests the standalone
   bundles, builds all existing multi-platform images as OCI archives, and builds
   and installs npm, NuGet and Python packages in clean consumers. PRs, including
   forks, use these same build jobs without publishing credentials.
3. Syft scans each distribution together with its resolved dependency evidence.
   Native AOT distributions include generated NuGet `packages.lock.json` files
   and embedded frontend lockfiles. Local bundles also include the sample services'
   restore graphs. Images retain these graphs in `/usr/share/slimfaas/dependencies`.
   SPDX and CycloneDX documents are generated per artifact and image platform.
   Lockfile scans include build dependencies; they are not claims that every listed
   development tool executes in the deployed application.
4. `release.yml` signs `SHA256SUMS` with keyless Cosign and generates GitHub build
   provenance. The signed index covers `release.json`, all payloads, dependency
   evidence and SBOMs. Verification binds them to the expected SHA and workflow.
5. After CI, DCO and FOSSA checks pass, the signed candidate is attached to a draft
   GitHub release. Registry jobs verify it again, publish the existing bytes, and
   check existing versions for collisions. Docker images are signed by digest.
6. Finalization verifies registry contents and image signatures, promotes the npm
   and Docker channels, and publishes the GitHub release. Stable releases become
   `latest`; previews update only their `alpha` or `beta` channel.

The distribution matrix is in [release-products.json](../.github/release-products.json).
Each file has `<filename>.sbom.spdx.json` and `<filename>.sbom.cdx.json`; image
SBOM names also include `linux-amd64` or `linux-arm64`. Native ZIP names remain
compatible with the installer. NuGet symbol packages (`.snupkg`) are signed release
assets on GitHub; the pipeline publishes the `.nupkg` to NuGet.org. NuGet may add
its repository signature to the package: verification compares all original ZIP
entries while allowing only this additional signature.

Registries do not share a transaction. A failed run can leave some versions
published, or some channel pointers updated. The GitHub release stays a draft
until all checks and promotions have succeeded. Never delete/recreate a published
version or move its tag to recover. Roll forward with a new patch when necessary.

## One-time activation

The workflows default to publishing disabled (`RELEASES_ENABLED` is unset).
Configuration outside this repository is required; merging the migration alone
does not establish registry trust.

1. Wait for old publication runs to finish. Merge the migration after the required
   checks and maintainer review. Use squash merges with the PR title as the commit
   title; keep the DCO sign-off in the commit body. Do not enable old tag-based or
   keyword-based publishers in parallel.
2. Create a dedicated GitHub App, installed only on `SlimPlanet/SlimFaas`, with
   repository **Contents**, **Pull requests**, and **Issues** read/write and Metadata
   read. Store its ID as `RELEASE_APP_ID` and its private key as
   `RELEASE_APP_PRIVATE_KEY`. Set `signoff` in `release-please-config.json` to the
   bot's verified name/email identity and confirm the bot's first PR passes DCO.
   The App must not bypass branch protection. A maintainer merges its release PR.
3. Create the environments `release`, `npm`, `nuget`, `pypi`, and `docker`. Restrict
   deployment branches to `main`; do not add another mandatory approval, because
   the release PR already requires human review. Move Docker credentials into the
   `docker` and `release` environments (`DOCKER_USERNAME`, `DOCKER_PASSWORD`) and
   scope the token to the nine Docker Hub repositories listed in the product matrix.
   Remove repository-wide copies once the environment secrets are in place.
4. Configure trusted publishers for owner `SlimPlanet`, repository `SlimFaas`:

   | Registry | Package | Workflow / environment |
   | --- | --- | --- |
   | npm | `@axa-fr/slimfaas-planet-saver` | `main.yml` and `release.yml`, environment `npm` for publish |
   | npm | same package | `main.yml` and `release.yml`, environment `release` for dist-tag management |
   | PyPI | `slimfaas-client` | caller workflows `main.yml` and `release.yml`, environment `pypi` |
   | NuGet | `SlimFaasClient` | caller workflows `main.yml` and `release.yml`, environment `nuget` |

   Register both caller paths because stable releases call the reusable release
   workflow from `main.yml`, while previews are dispatched directly. Restrict trust
   to `main` where the registry exposes that setting. For npm, explicitly permit
   direct publishing for the `npm` identities and dist-tag operations for the
   `release` identities (npm CLI 11.21.0 is pinned). Set `NUGET_USER` to the NuGet
   account that owns the trusted publishing policy. No npm, PyPI or NuGet API key
   is read by these workflows. See the official [npm](https://docs.npmjs.com/trusted-publishers/),
   [PyPI](https://docs.pypi.org/trusted-publishers/adding-a-publisher/), and
   [NuGet](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) setup guides.
5. Configure `main` protection after observing the new checks: forbid force pushes
   and deletion, require one approval and CODEOWNER review, enforce the rules for
   administrators, and require `CI / required`, `DCO`, and `License Compliance`.
   Bind each check to its actual GitHub App. Keep SonarCloud required where it runs;
   the aggregate CI check permits its absence for fork and Dependabot PRs only.
   Do not globally require a secret-dependent Sonar check that cannot run on forks.
6. Enable [immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases)
   in repository Settings → General → Releases. All assets are attached while the
   release is a draft; publication locks the tag and assets. The activation helper
   below prepares the GitHub settings from checks actually observed on a merged PR.
7. Run `validate` on `main`, configure `RELEASES_ENABLED=true`, then explicitly run
   an alpha. Check its installations and signatures and confirm every `latest`
   pointer is unchanged. Only then merge the first stable release PR. Remove old
   `GIT_TOKEN`, `NPM_TOKEN`, `NUGET_API_KEY`, and `PYPI_API_TOKEN` secrets after
   confirming no other workflow uses them.

```bash
# Read-only inspection; --apply is allowed only after the migration is on main.
python3 .bin/configure-release-repository.py --pr 368
python3 .bin/configure-release-repository.py --pr 368 --apply
```

The helper does not create the GitHub App, change registry account settings, move
secret values, or enable publication. These require their respective account owners.

## Verification for consumers

Choose the expected version and full commit SHA from the reviewed release. Obtain
the verification script from a trusted checkout, not from an unverified archive.
Use GitHub CLI and the pinned Cosign version from `.bin/release-tools.json`:

```bash
VERSION=0.85.0-alpha.42
SOURCE_SHA=replace_with_the_reviewed_40_character_commit_sha
gh release download "$VERSION" --repo SlimPlanet/SlimFaas --dir artifacts/release
python3 .bin/install-release-tools.py cosign regctl
export PATH="$PWD/artifacts/release-tools:$PATH"
python3 .bin/release.py verify --version "$VERSION" --sha "$SOURCE_SHA"

# Verify an individual image, using its digest from the verified release.json.
cosign verify axaguildev/slimfaas@sha256:REPLACE_WITH_VERIFIED_DIGEST \
  --certificate-identity-regexp 'https://github\.com/SlimPlanet/SlimFaas/\.github/workflows/(main|release)\.yml@refs/heads/main' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

The script checks all file hashes and sizes, the complete artifact matrix, image
digests, the checksum index signature, the GitHub Actions issuer, the release
workflow identity and the exact source digest in the provenance. A checksum alone
does not authenticate the publisher. Pin image digests when deploying.

## Testing and recovery

Local checks:

```bash
python3 .bin/test-release.py
python3 .bin/install-release-tools.py actionlint
artifacts/release-tools/actionlint
dotnet test
python3 .bin/build-release.py packages --version 0.84.16 --sha "$(git rev-parse HEAD)"
python3 .bin/build-release.py native --product SlimFaas --rid osx-arm64 \
  --version 0.84.16 --sha "$(git rev-parse HEAD)"
```

Use the current `version.txt` and host RID. Package building requires Node 24,
pnpm 10.14.0, .NET 10, Python 3.12+ and uv 0.9.0. Preview stamping changes package
metadata only in the build checkout; run preview builds in a disposable checkout.
The native bundle test runs the runtime, functions and jobs with an empty PATH.
On a source checkout, also validate `slimfaas.local.yaml`, run `local up`, and call
`/status-functions` and `/function/fibonacci1/hello/local` as described in
[native local mode](native-local-mode.md).

The failure tests cover version parsing, PEP 440, disallowed refs, tag mismatches,
path traversal, missing/tampered files, signature failures, registry errors and
collisions, and refusal to promote channels after failure. The `validate` mode
uses the production builders and cryptographic verification in GitHub Actions,
retaining the signed candidate without creating tags/releases or writing registries.
Only an explicit alpha can prove the real registry permissions work.

If a publication fails, use **Re-run failed jobs** on the original Actions run.
Once the draft has been staged, the equivalent manual command is:

```bash
gh workflow run release.yml --ref main -f mode=resume -f tag=0.85.0-alpha.42
```

Resume verifies the draft, reads its signed producer run ID, and requests failed
jobs of that original run. This preserves its OIDC source SHA; it never starts a
new build for an already-published candidate. Successful publications are compared
before being skipped. Different bytes or digests fail closed. Before the draft
exists, rerun failed jobs directly from Actions. Unsigned artifacts expire after
7 days, signed candidates after 30 days; staged draft assets persist. Recover
promptly within GitHub's workflow rerun window. If that window expires, retain
the failed draft for audit and prepare a new version instead of rebuilding it.

These controls follow OpenSSF/Sigstore supply-chain practices used by CNCF
projects; they do not constitute a CNCF certification or a claimed SLSA level.
