# Contributing

To get started with the repository:

```sh
git clone https://github.com/SlimPlanet/SlimFaas.git

```
You are now ready to contribute!

## Build quality

Warnings are errors and every .NET analyzer is enabled for the whole repository (`Directory.Build.props`, `.editorconfig`, `eng/*.globalconfig`). New code must build clean; suppressions are scoped and justified. NuGet versions live only in `Directory.Packages.props`: a `PackageReference` in a csproj never carries a `Version` attribute, and adding a package means adding one `PackageVersion` line there. See the "Build quality" section of [AGENTS.md](./AGENTS.md) for the rules.

## Dependency updates

Update NuGet versions centrally and regenerate each affected JavaScript lockfile with its package manager: npm for the SlimFaas and MCP dashboards; pnpm for Planet Saver, the documentation site and FibonacciReact. Docker dashboard builds use `npm ci`, and pnpm builds use `--frozen-lockfile`, so commit the manifest and lockfile together.

Keep ESLint on 9 while `eslint-plugin-react` excludes ESLint 10 from its peer range. The documentation site keeps TypeScript 6 because its Next.js TypeScript ESLint toolchain requires TypeScript below 6.1; the Vite dashboards use TypeScript 7. Recheck upstream peer requirements before lifting these constraints.

Planet Saver uses Vitest 5 with the matching coverage provider. Keep Mermaid on 11: Mermaid 12 adds `elkjs` under EPL-2.0, which is outside the CNCF license allowlist. Revisit this hold only when a compliant dependency distribution or an explicit CNCF exception is available.

The dashboard and FibonacciReact demo override `serve`'s `compression` dependency to the patched 1.8.2 release because `serve` 14.2.6 pins vulnerable 1.8.1 (GHSA-vc2v-76pw-4v95). Remove these scoped overrides once `serve` includes the fix itself.

The documentation site overrides Mermaid's `katex` dependency to 0.19.1 to address GHSA-238p-pmpm-9mq7; recheck mathematical diagram rendering when changing this override. It excludes Next.js's optional `sharp` dependency and its LGPL-licensed libvips binaries: this site exports static pages with `images.unoptimized: true`, so it does not use the native image optimizer. Revisit this exclusion if image optimization is enabled.

Use xUnit assertions in .NET client tests. FluentAssertions 8 requires a commercial license for commercial use and is not part of the CNCF license allowlist. Review direct and transitive package distributions and require a passing FOSSA License Compliance check before merging dependency updates; declared SPDX metadata alone is insufficient.

Validate updates with the full .NET suite, affected frontend checks and the native local demo. For example:

```bash
dotnet test -p:SkipClientAppBuild=true
(cd src/SlimFaas/ClientApp && npm ci && npm test && npm run build)
(cd src/SlimFaasMcp/ClientApp && npm ci && npm test && npm run build)
(cd src/SlimFaasPlanetSaver && pnpm install --frozen-lockfile && pnpm run coverage && pnpm build)
(cd src/SlimFaasSite && pnpm install --frozen-lockfile && pnpm test && pnpm build)
(cd samples/FibonacciReact && pnpm install --frozen-lockfile && pnpm lint && pnpm build)
```

See [How SlimFaas Works](docs/how-it-works.md#container-build-dependencies) for the container build baseline and [Native Local Mode](docs/native-local-mode.md) for the local smoke checks.

## Pull Request

Please respect the following [PULL_REQUEST_TEMPLATE.md](./PULL_REQUEST_TEMPLATE.md)

## Issue

Please respect the following [ISSUE_TEMPLATE.md](./ISSUE_TEMPLATE.md)
