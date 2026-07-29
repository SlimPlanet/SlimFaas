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
from the canonical SlimFaas repository.

## Develop

From this directory:

```bash
pnpm install
pnpm dev
```

Open [http://localhost:3000](http://localhost:3000).

## Validate and export

```bash
pnpm lint
pnpm build
```

The static export is written to `out/`. A missing documentation source fails
the build instead of producing an empty page.
