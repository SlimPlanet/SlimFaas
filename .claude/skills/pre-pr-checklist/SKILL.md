---
name: pre-pr-checklist
description: Run the eleven pre-pull-request gates (tests, AOT publish, UI, clients, docs site, native local chain, benchmarks, documentation, licenses, rule mirror, architecture pages) and report which apply and which passed.
---

# pre-pr-checklist

Read `docs/sdlc/pre-pr-checklist.md` and follow it step by step; this skill only summarises it. Project rules come from `AGENTS.md` (imported by `CLAUDE.md`).

- Always: `dotnet test`, the documentation checklist of `AGENTS.md`, `python .bin/check-architecture-docs.py`.
- When AOT services changed: `dotnet publish -c Release`; when UI changed: dashboard npm and Planet Saver pnpm checks; when clients changed: client tests.
- When docs or the site changed: `(cd src/SlimFaasSite && pnpm install --frozen-lockfile && pnpm build)`; when runtime behaviour changed: `slimfaas local validate` / `local up` plus the two curls.
- When SlimData, data, queue or high-throughput paths changed: the `.bin/*benchmark*.sh` scripts with before/after numbers.

Run the applicable gates from the repository root, one at a time, and finish with a table gate / applies / result. Do not skip a failing gate.
