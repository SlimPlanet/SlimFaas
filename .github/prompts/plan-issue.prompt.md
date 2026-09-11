---
mode: agent
description: Plan a multi-phase or cross-project change as a GitHub issue with phases, documentation impact and verification, before writing code.
---

# plan-issue

Open [docs/sdlc/plan-issue.md](../../docs/sdlc/plan-issue.md) and follow it step by step; this prompt only summarises it. Project rules come from [AGENTS.md](../../AGENTS.md).

- Read `AGENTS.md` and the architecture pages mapped to the code areas involved before proposing a design.
- Split the work into phases that each fit one reviewable pull request and leave `main` releasable.
- For each phase, name the `docs/*.md` pages, demo manifests and architecture pages to update, and how it will be verified.
- Create the issue with the *Implementation plan* form and keep it current as pull requests merge.

Draft the plan in chat first; create the issue with the *Implementation plan* form or `gh issue create` once the user confirms.
