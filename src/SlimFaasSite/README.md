# SlimFaas documentation site

This Next.js application builds the static site published at
[slimfaas.dev](https://slimfaas.dev).

## Documentation sources

Public pages are rendered from the repository's `docs/*.md` files. The
route-to-file mapping, labels, titles, and descriptions are defined in
`src/lib/documentation-catalog.ts`. The static build reads the checked-out
files directly, so a branch or pull request always renders its own
documentation rather than the version from the default branch.

Relative links between public documents become site routes. Links to technical
references become GitHub links, and relative documentation assets are served
from the checked-out repository into the static export.

## Develop

From this directory:

```bash
pnpm install --frozen-lockfile
pnpm dev
```

Open [http://localhost:3000](http://localhost:3000).

## Validate and export

```bash
pnpm lint
pnpm typecheck
pnpm test
pnpm build
pnpm check:export
```

The static export is written to `out/`, including a generated `sitemap.xml`.
A missing documentation source fails the build instead of producing an empty
page.

## Navigation, search and downloads

Use Node.js 24 or later. The catalogue defines page sources and metadata; `DOCUMENTATION_GROUPS` defines navigation and previous/next order. Keep every public document except the homepage in exactly one group.

`dev` and `build` first run `scripts/prepare-assets.mts` to generate `public/search-index.json`, copy local documentation images, and package `demo/bruno-slimfaas-demo` into `/downloads/slimfaas-demo.zip`. These generated files are ignored by Git. Restart the dev server to refresh search/downloads after editing Markdown or Bruno files. The build uses Node's built-in APIs and existing dependencies, with no external search service or ZIP package.

Markdown headings receive deterministic unique anchors. The page loader exposes headings for the responsive table of contents. Mermaid retains source text before client rendering and renders each diagram independently after route changes. Code-copy buttons use the browser clipboard and announce success/failure.

Tests cover anchors, link rewriting, search behavior, navigation, ZIP integrity and route coverage against the actual C# route registrations. `check:export` validates local links, search destinations, assets and the absence of the retired MCP page in the output.

## Branding and CSS

The primary SlimFaas blue is `#0000ff`, centralized as `--color-primary`. Use BEM
for component styles and generated Markdown wrappers; see the repository's
[agent guidelines](../../AGENTS.md). Mermaid/Highlight.js own their generated
subtrees. The homepage community content lives in `docs/home.md`.

The build also copies `.bin/install-local-demo.sh` to
`/downloads/install-local-demo.sh`. Release bundles are built by
`.bin/package-local-demo.py`, tested by `.bin/test-local-bundle.py` and published
by the native release workflow. The site must retain the availability explanation
while the latest release has no `SlimFaas-Local-*` assets.
