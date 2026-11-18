---
name: slimfaas-documentation
description: Update SlimFaas documentation, examples and the static documentation site when behavior, configuration, public routes or architecture change.
---

# Document SlimFaas changes

Write concise English Markdown. Update the relevant pages and sample commands
alongside user-facing changes. Keep new material discoverable through the
README or related pages. Add a README entry for a new major capability.
Use language tags for executable examples and paths relative to the Markdown
file for related pages, diagrams and assets.

| Change | Documentation |
| --- | --- |
| Deployment, prerequisites and environment variables | `docs/get-started.md` |
| Native local CLI, manifests, overlays and `debugUrl` | `docs/native-local-mode.md` |
| Function calls, thresholds, retries and timeouts | `docs/functions.md`, `docs/async-functions.md` |
| WebSocket clients or registration contracts | `docs/clients.md` and both SDK READMEs |
| Job scheduling, concurrency and retries | `docs/jobs.md` |
| Events and Streams | `docs/events.md`, `docs/streams.md` |
| Files APIs, retention and replication | `docs/data-files.md` |
| Key-value APIs, consistency and failover | `docs/data-sets.md` |
| Scaling policies or Kafka triggers | `docs/autoscaling.md`, `docs/kafka.md` |
| Runtime behavior, WAL settings and architecture | `docs/how-it-works.md` |
| Metrics, tracing and logging | `docs/opentelemetry.md` |
| Dashboard, Planet Saver or MCP | `docs/user-interface.md`, `docs/planet-saver.md`, `docs/mcp.md` |
| Benchmark methods and results | `docs/benchmarking.md` and the linked dated evidence |

For demo-facing changes, update the corresponding workloads, manifests,
Compose files and Bruno requests. When Planet Saver behavior changes, keep its
package README aligned. Verify executable examples and update screenshots for
visual UI changes where they clarify the new behavior.

## Site validation

The public documentation catalog is in
`src/SlimFaasSite/src/lib/documentation-catalog.ts`. Registered Markdown sources
are part of the public site. Technical notes linked from them can remain on
GitHub. Build locally with the repository's Node and pnpm versions.

```bash
cd src/SlimFaasSite
pnpm install --frozen-lockfile
pnpm test
pnpm build
```

For new public routes, verify that the static HTML contains the content and
that the sitemap and local link checks pass. The main workflow deploys the
generated site after a merge to `main`.
