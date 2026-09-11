---
mode: agent
description: Explain or execute the publishing of a SlimFaas version: what the (release), (alpha) and (beta) markers do, what CI publishes, and the maintainer checklist. Maintainers only; never adds marker words on its own.
---

# release

Open [docs/sdlc/release.md](../../docs/sdlc/release.md) and follow it step by step; this prompt only summarises it. Project rules come from [AGENTS.md](../../AGENTS.md).

- A squash commit on `main` containing `release` publishes `X.Y.Z` (tag, Docker `latest`, native archives on a GitHub Release, npm `latest`, changelog commit); `alpha` / `beta` publish pre-releases from any branch.
- The whole commit message is matched, and the squash body is the concatenation of every branch commit message.
- Version bump: `fix` → patch, `feat` → minor, `BREAKING` → major, otherwise patch.
- NuGet and PyPI clients are published separately with `workflow_dispatch`.

Never add a marker word or merge by yourself. Produce the maintainer checklist from `docs/sdlc/release.md` and the exact squash title to use.
