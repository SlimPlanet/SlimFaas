# Open a pull request

Goal: a pull request whose title is a correct squash-commit message, whose body follows the template, and whose CI checks pass.

## Title

Pull requests are squash-merged and **the title becomes the commit message on `main`**. That message drives automated versioning (`GOVERNANCE.md` §3), so the title is not cosmetic.

Format: `type(scope): imperative summary`

| Type | Use for | Version effect |
|---|---|---|
| `feat` | New capability or behaviour | minor |
| `fix` | Bug fix | patch |
| `perf`, `refactor`, `test`, `docs`, `chore`, `build`, `ci` | No behaviour change for users | patch (default) |
| Any type with `BREAKING` in title or body | Incompatible change | major |

Scopes seen in history are lower-case project or area names: `slimfaas`, `slimdata`, `mcp` (or `slimfaas-mcp`), `demo`, `doc`, `ci`, `build`, `otel`, `website`, `tests`; this lifecycle adds `sdlc`, `agents` and `architecture`. Capitalised variants (`SlimFaas`, `SlimData`) also exist; prefer lower-case.

Examples: `feat(slimfaas): add job retention policy`, `fix(slimdata): flush WAL before snapshot`, `docs(sdlc): describe the review checklist`.

**Do not** write the words `release`, `alpha` or `beta` anywhere in the title or in any commit message of the branch unless you intend to publish a version. The squash commit on `main` concatenates the branch's commit messages and the CI matches those substrings in the whole text; see [`release.md`](release.md).

## Body

Fill in `.github/pull_request_template.md`: summary and `Closes #N` (or `Part of #N, phase k` for a planned change), the title checklist, the checks you ran ([`pre-pr-checklist.md`](pre-pr-checklist.md)), the documentation checklist, the architecture documentation section, dependencies and licenses, UI screenshots.

With the GitHub CLI:

```bash
gh pr create --title "feat(SlimFaas): add job retention policy" --body-file pr.md
```

Claude Code runs `python .bin/check-architecture-docs.py` and `python .bin/check-agent-rules.py` automatically before `gh pr create` (hook in `.claude/settings.json`); with other tools run them yourself first.

## What CI runs on the pull request

| Check | Workflow | Notes |
|---|---|---|
| SonarCloud | `main.yml` | Skipped for forks and Dependabot |
| Unit Tests | `main.yml` | `dotnet test --collect "Code Coverage;Format=cobertura"`, Planet Saver coverage |
| tags (dry run) | `main.yml` | Computes the version that a merge would produce |
| Docker builds | `main.yml` → `Docker.yml` | Built, not pushed |
| Dashboard build and tests, documentation build | `dashboard.yml`, `SiteBuild.yml` | |
| FOSSA License Compliance | GitHub App | Required when dependencies or lockfiles changed |

Pull requests from forks run unit tests only; workflows that need secrets are skipped (`GOVERNANCE.md` §5).

## After opening

- Watch the checks: `gh pr checks <number> --watch`.
- Answer review comments in the thread and push fixes as new commits (they are squashed at merge).
- When a maintainer merges, the squash commit title is the PR title plus `(#number)` and the body is the list of branch commit messages; they remove marker words from it unless a publish is intended.

## Related

- `GOVERNANCE.md` §2, §3, §5
- [`release.md`](release.md), [`review.md`](review.md)
- Claude Code skill `/open-pr`, GitHub Copilot prompt `/open-pr`
