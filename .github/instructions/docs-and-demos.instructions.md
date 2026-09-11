---
applyTo: "docs/**,README.md,demo/**,docker-compose.yml,slimfaas.local*.yaml,src/SlimFaas/Local/**"
---

<!-- Mirror of .claude/rules/docs-and-demos.md, keep in sync (checked by .bin/check-agent-rules.py) -->
# Documentation, demos and native local mode

- Golden rule: every change to user-facing behaviour, configuration or architecture updates the documentation in the same pull request (`AGENTS.md`, Documentation Requirements).
- Only pages registered in `src/SlimFaasSite/src/lib/documentation-catalog.ts` are published on slimfaas.dev; `docs/sdlc/` and `docs/architecture/` stay GitHub-only. A registered page whose source is missing fails the site build: `(cd src/SlimFaasSite && pnpm install --frozen-lockfile && pnpm build)`.
- GitHub-flavored Markdown, fenced code blocks with language markers, images relative to the page, cross-links between pages; execute every example you write.
- Demo-facing changes (configuration, sample workloads, ports, images, annotations) update `demo/deployment-*.yml`, `docker-compose.yml` and `slimfaas.local*.yaml` together.
- After runtime, configuration, routing, data, queue, job or UI changes, run `dotnet run --project src/SlimFaas -- local validate -f ../../slimfaas.local.yaml` then `local up`, and check `http://127.0.0.1:30020/status-functions` and `/function/fibonacci1/hello/local`.
- The native local CLI and process orchestration live in `src/SlimFaas/Local/`; document them in `docs/native-local-mode.md` and `docs/local-orchestrator.md`, and keep the local demo bundle scripts in `.bin/` working.
