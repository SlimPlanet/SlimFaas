# Architecture documentation

SlimFaas keeps its architecture documented next to the code, as Markdown pages with Mermaid diagrams. This folder holds the contract every contributor and AI agent follows, the map from code areas to pages, a template for new pages, and the pages that are not published on the website.

The system-level page is [`docs/how-it-works.md`](../how-it-works.md). Area pages are listed in the map below.

## Contract

1. **Same pull request.** Any change to components, workers, endpoints, request/queue/job flows, state machines, persistence or Raft behaviour, cross-project contracts (WebSocket protocol, MemoryPack commands, JSON serializer contexts) or public configuration updates the page mapped to that code area in the same pull request as the code.
2. **Mermaid, one block per concern.** `flowchart` for components and dependencies, `sequenceDiagram` for request and message flows, `stateDiagram-v2` for lifecycles (replica, job, queue item, Raft node), `erDiagram` or `classDiagram` only for data shapes. Prose explains responsibilities; do not paste code.
3. **Real names.** Diagram nodes and participants use the type names found in the code (`SlimWorker`, `ReplicasSynchronizationWorker`, `ToolProxyService`), so a reader can grep them. Renaming a type means updating the diagram.
4. **`Source:` line.** Every diagram is followed by a line starting with `Source:` that lists the code paths it covers, for example `Source: src/SlimFaas/Workers/SlimWorker.cs, src/SlimFaas/Data/`.
5. **`Last verified:` line.** Every architecture page ends with `Last verified: YYYY-MM-DD against <short commit SHA>`. When a change in a mapped area does not alter the architecture (rename, pure refactor, bug fix), re-read the page and bump this line anyway. This is the only accepted escape hatch: it keeps the page in the review diff.
6. **No page, create one.** When no page covers the code you change, copy [`TEMPLATE.md`](TEMPLATE.md) to `docs/architecture/<area>.md`, fill it, and add a row to the map below before finishing the pull request. Pages in this folder stay GitHub-only unless a maintainer registers them in `src/SlimFaasSite/src/lib/documentation-catalog.ts`.
7. **Incremental migration.** Existing pages without diagrams or without a `Last verified:` line are upgraded the first time their code area is touched. Unrelated pages are not rewritten.

The rule is enforced by `python .bin/check-architecture-docs.py`, which the pre-PR checklist and the Claude Code hook run before a pull request is opened. The workflow [`docs/sdlc/document-architecture.md`](../sdlc/document-architecture.md) (`/document-architecture` in Claude Code and GitHub Copilot) walks through an update.

## Code to page map

`.bin/check-architecture-docs.py` parses this table. Keep the format: one row per code area, code path globs in backticks separated by commas in the first column, pages in backticks separated by commas in the second column. When a row lists several pages, at least one of them must change when the code area changes. Globs are matched against repository-relative paths; `**` crosses directories, `*` does not.

| Code paths | Architecture pages |
|---|---|
| `src/SlimFaas/Endpoints/**`, `src/SlimFaas/Middleware/**`, `src/SlimFaas/Workers/**`, `src/SlimFaas/Jobs/**`, `src/SlimFaas/Kubernetes/**`, `src/SlimFaas/Database/**`, `src/SlimFaas/RateLimiting/**`, `src/SlimFaas/Security/**`, `src/SlimFaas/LogStreaming/**` | `docs/how-it-works.md` |
| `src/SlimFaas/PromQL/**` | `docs/autoscaling.md` |
| `src/SlimFaas/Data/**` | `docs/data-sets.md`, `docs/data-files.md` |
| `src/SlimData/**` | `docs/how-it-works.md`, `docs/slimdata-unified-batching.md`, `docs/slimdata-batch-modes.md` |
| `src/SlimFaas/Local/**` | `docs/local-orchestrator.md`, `docs/native-local-mode.md` |
| `src/SlimFaas/WebSocket/**`, `client/**` | `docs/clients.md` |
| `src/SlimFaas/ClientApp/**` | `docs/user-interface.md` |
| `src/SlimFaasKafka/**` | `docs/kafka.md` |
| `src/SlimFaasMcp/**` | `docs/architecture/slimfaas-mcp.md` |
| `src/SlimFaasPlanetSaver/**` | `docs/planet-saver.md` |

Changes limited to tests (`tests/**`, `*Tests/**`, `*.spec.*`, `*.test.*`), Markdown, lockfiles and `.github/**` do not trigger the check.

## Pages in this folder

| Page | Scope |
|---|---|
| [`slimfaas-mcp.md`](slimfaas-mcp.md) | SlimFaasMcp: OpenAPI to MCP tool generation and proxying |
| [`TEMPLATE.md`](TEMPLATE.md) | Skeleton for a new page |

## Checking locally

```bash
# Compare the current branch and working tree with origin/main (default)
python .bin/check-architecture-docs.py

# Compare with another base, or only staged changes
python .bin/check-architecture-docs.py --base main
python .bin/check-architecture-docs.py --staged

# Run the checker's own tests
python .bin/test-check-architecture-docs.py
```
