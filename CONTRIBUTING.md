# Contributing

Thank you for helping SlimFaas. This page is the short version; the complete development lifecycle, shared by human contributors, GitHub Copilot and Claude Code, lives in [`docs/sdlc/README.md`](docs/sdlc/README.md). Project rules (AOT constraints, BEM, licenses, documentation duties) live in [`AGENTS.md`](AGENTS.md).

## Getting started

```sh
git clone https://github.com/SlimPlanet/SlimFaas.git
cd SlimFaas
dotnet build src/SlimFaas/SlimFaas.csproj -p:SkipClientAppBuild=true
dotnet test
```

Prerequisites (SDK versions, Node, pnpm, uv) are listed in the "Running the Project" section of `AGENTS.md`.

## Development workflow

1. **Plan** non-trivial or multi-phase work in an issue using the *Implementation plan* form. See [`docs/sdlc/plan-issue.md`](docs/sdlc/plan-issue.md).
2. **Start work** on a branch. Maintainers use one git worktree per task under `.claude/worktrees/<slug>` on branch `claude/<slug>`; external contributors fork the repository. The Copilot coding agent works on `copilot/*` branches. See [`docs/sdlc/start-work.md`](docs/sdlc/start-work.md).
3. **Implement** following `AGENTS.md` and the path-scoped rules in `.claude/rules/` and `.github/instructions/`. Tests, documentation and architecture diagrams ship in the same pull request as the code. See [`docs/architecture/README.md`](docs/architecture/README.md).
4. **Run the pre-PR checklist** before opening the pull request. See [`docs/sdlc/pre-pr-checklist.md`](docs/sdlc/pre-pr-checklist.md).
5. **Open the pull request** with the template in [`.github/pull_request_template.md`](.github/pull_request_template.md). See [`docs/sdlc/open-pr.md`](docs/sdlc/open-pr.md).
6. **Review**: maintainers review with [`docs/sdlc/review.md`](docs/sdlc/review.md) and squash-merge.

## Pull request titles and releases

Pull requests are squash-merged, and the PR title becomes the commit message on `main`. That message drives automated versioning, so:

- Use Conventional Commits: `type(scope): imperative summary`, for example `feat(slimfaas): add job retention policy` or `fix(slimdata): flush WAL before snapshot`.
- `fix` bumps the patch version, `feat` bumps the minor version, `BREAKING` anywhere in the message bumps the major version.
- A commit message on `main` containing `(release)` publishes a stable version. `(alpha)` and `(beta)` publish pre-releases from any branch. Only maintainers merge to `main`. The squash commit concatenates every commit message of the branch, so do not use the words `release`, `alpha` or `beta` in titles or commit messages unless you intend to publish.

Details and pitfalls: [`docs/sdlc/release.md`](docs/sdlc/release.md) and [`GOVERNANCE.md`](GOVERNANCE.md) sections 2 and 3.

Pull requests from forks only run unit tests; workflows that need secrets are skipped (see `GOVERNANCE.md` section 5).

## Issues

Use the issue forms in [`.github/ISSUE_TEMPLATE/`](.github/ISSUE_TEMPLATE/): *Bug report*, *Feature request* or *Implementation plan*. Security problems go through [`SECURITY.md`](SECURITY.md), not public issues.

## Code of conduct

SlimFaas follows the [CNCF Code of Conduct](https://github.com/cncf/foundation/blob/main/code-of-conduct.md).
