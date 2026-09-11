# Document the architecture of a change

Goal: the architecture page mapped to the code you changed describes the new reality, with Mermaid diagrams that name the real types, in the same pull request.

The contract and the code-to-page map live in [`docs/architecture/README.md`](../architecture/README.md). This workflow is how to satisfy them.

## Steps

1. **Find the page.** Look up the changed code paths in the map table of `docs/architecture/README.md`. `python .bin/check-architecture-docs.py` prints the pages it expects when it fails.
2. **Decide whether the architecture changed.**
   - New or removed component, worker, endpoint, message, state, storage or contract → update the diagrams and the prose.
   - Rename → update the names in the diagrams.
   - Pure refactor or bug fix with no structural change → re-read the page, confirm it is still true, and bump the `Last verified:` line only.
3. **Pick the diagram type** for what changed:

   | You changed | Use |
   |---|---|
   | Components, dependencies, who calls whom | `flowchart LR` (or `TB`) |
   | A request, message or job flow across components | `sequenceDiagram` |
   | A lifecycle with states and transitions (replica, job, queue item, Raft node) | `stateDiagram-v2` |
   | A data shape shared across components | `erDiagram` or `classDiagram` (sparingly) |

4. **Write the diagram.** Nodes and participants carry the type names from the code (`SlimWorker`, `ReplicasSynchronizationWorker`, `ToolProxyService`). Keep one concern per diagram; split rather than grow. Follow the block with a `Source:` line listing the files or folders it covers.
5. **Update the prose** around the diagram: responsibilities, failure paths worth knowing, configuration that changed. No pasted code.
6. **No page yet?** Copy [`docs/architecture/TEMPLATE.md`](../architecture/TEMPLATE.md) to `docs/architecture/<area>.md`, fill it, and add a row to the map table. Pages in that folder are GitHub-only unless registered in `src/SlimFaasSite/src/lib/documentation-catalog.ts`.
7. **Bump `Last verified:`** at the end of the page: `Last verified: YYYY-MM-DD against <short SHA>` (the SHA of the commit you verified against; the PR head is fine).
8. **Render-check.** Pages registered in the site catalog are rendered by `(cd src/SlimFaasSite && pnpm install --frozen-lockfile && pnpm test && pnpm build)`; GitHub-only pages render in the branch view on GitHub (a red "Unable to render" box means a Mermaid syntax error).
9. **Run the checker** from the repository root:

   ```bash
   python .bin/check-architecture-docs.py
   ```

## Example

Adding a `JobRetentionWorker` in `src/SlimFaas/Workers/` means: add the node to the components flowchart in `docs/how-it-works.md` (Components and responsibilities), add or extend the sequence for job execution and cleanup in "Jobs and schedules", update the `Source:` lines, describe the retention configuration, bump `Last verified:`.

## Related

- `AGENTS.md` – Architecture Golden Rule, Documentation Requirements
- [`docs/architecture/README.md`](../architecture/README.md), [`docs/how-it-works.md`](../how-it-works.md)
- Claude Code skill `/document-architecture`, GitHub Copilot prompt `/document-architecture`
