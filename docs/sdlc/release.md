# Publish a version

Goal: understand exactly what publishes a SlimFaas version today, so that nothing is published by accident and a deliberate publish does what maintainers expect. The mechanism itself is being reconsidered in [#363](https://github.com/SlimPlanet/SlimFaas/issues/363).

## What triggers what

The `tags` job in `.github/workflows/main.yml` runs after the unit tests on every push to `main` and every pull request. It reads the **entire last commit message** and classifies it with substring matches. On a pull request that is the last commit of the branch; on `main` it is the squash commit, which the repository settings build from the PR title plus the **concatenated messages of every commit in the branch** (`squash_merge_commit_message: COMMIT_MESSAGES`).

| Message contains | Branch | Result | Version |
|---|---|---|---|
| `release` | `main` | stable publish | `X.Y.Z` |
| `alpha` | any | pre-release | `X.Y.Z-alpha.<run>` |
| `beta` | any | pre-release | `X.Y.Z-beta.<run>` |
| none of them | `main` | dry run | `X.Y.Z-dev.<run>` |
| none of them | PR | dry run | `X.Y.Z-pr.<pr><run>` |

The convention is to append the marker in parentheses to the PR title at merge time, for example `fix(dashboard): order request animations (#355) (release)`, but the match is on the substring, so parentheses and position do not matter.

The next version is computed by `mathieudutour/github-tag-action` from the same message: `fix` → patch, `feat` → minor, `BREAKING` anywhere → major, otherwise patch (`GOVERNANCE.md` §3).

## What a stable publish does

1. `tags` creates the tag `X.Y.Z` (no `v` prefix).
2. Docker images for SlimFaas, SlimFaasMcp, the Kafka connector and the demo apps are built for `linux/amd64` and `linux/arm64` and pushed as `:X.Y.Z` and `:latest`.
3. Native AOT archives (SlimFaas for five RIDs, SlimFaasMcp for four) are built and attached to a GitHub Release named `X.Y.Z`.
4. Planet Saver is published to npm with the `latest` dist-tag; the website is deployed.
5. The `change_log` job regenerates `CHANGELOG.md` with `.bin/generate-changelog.sh`, commits `[skip ci] Generate changelog to version X.Y.Z` and force-pushes it to `main` together with the tag.

The .NET client (NuGet) and the Python client (PyPI) are **not** published by this flow: their workflows listen for `v*` tags that are never created, so maintainers run them with `workflow_dispatch` and an explicit version.

## Pre-releases

Any commit message containing `alpha` or `beta`, on any branch including a pull request, publishes `X.Y.Z-alpha.<run>` or `X.Y.Z-beta.<run>` images and npm packages with the matching dist-tag. Use it deliberately from a branch to hand a build to a tester; do not leave the word in the branch's last commit afterwards.

## Pitfalls

- **Innocent words publish.** "update release notes", "alphabetical", "beta feature flag" anywhere in the squash message trigger the match, and that message includes every commit message of the branch. Keep marker words out of all branch commits, and before confirming a squash-merge, edit the body so that only the intended marker remains.
- **The marker is read from the last commit.** On a PR the dry run uses the last commit of the PR, on `main` the squash commit. Rebasing or amending changes what is matched.
- **`[skip ci]` commits and tags come from a bot force-push** with `secrets.GIT_TOKEN`; do not push to `main` while a release is running.
- **Only maintainers** merge to `main` and therefore publish stable versions (`GOVERNANCE.md` §2.2).

## Checklist for a maintainer publishing a stable version

1. The PR is approved, CI is green, and the tags dry run shows the expected next version.
2. Set the squash title to the Conventional title plus ` (release)`; check the pre-filled body (the branch's commit messages) and remove any other marker word.
3. Merge. Watch `main.yml` on `main`: `tags`, the Docker jobs, `build_slimfaas_aot`, `build_slimfaas_mcp_aot`, `publish_native_release`, `build_slimfaas_planet_saver`, `deploy_website`, `change_log`.
4. Verify: the tag and GitHub Release exist, Docker Hub shows `:X.Y.Z` and `:latest`, npm shows the new version under `latest`, `CHANGELOG.md` has the new section.
5. If the .NET or Python client changed, run `publish-dotnet-nuget.yml` and `publish-python-pypi.yml` with `workflow_dispatch` and the chosen version.
6. Announce in the community channels when the change is user-visible.

## Related

- `.github/workflows/main.yml` (`tags`, `change_log`, `publish_native_release`), `.github/workflows/Docker.yml`
- `GOVERNANCE.md` §2, §3; [`open-pr.md`](open-pr.md); issue [#363](https://github.com/SlimPlanet/SlimFaas/issues/363)
- Claude Code skill `/release`, GitHub Copilot prompt `/release`
