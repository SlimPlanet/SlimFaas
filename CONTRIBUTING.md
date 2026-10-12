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

Keep ESLint on 9 while `eslint-plugin-react` excludes ESLint 10 from its peer range. The documentation site keeps TypeScript 6 for its Next.js lint toolchain; the Vite dashboards use TypeScript 7. Vitest 5 and Mermaid 12 are separate major-version migrations. Recheck upstream peer requirements before lifting these constraints.

Use xUnit assertions in .NET client tests. FluentAssertions 8 requires a commercial license for commercial use and is not part of the CNCF license allowlist. Review direct and transitive package distributions and require a passing FOSSA License Compliance check before merging dependency updates; declared SPDX metadata alone is insufficient.

Validate updates with the full .NET suite, affected frontend checks and the native local demo. For example:

```bash
dotnet test -p:SkipClientAppBuild=true
(cd src/SlimFaas/ClientApp && npm ci && npm test && npm run build)
(cd src/SlimFaasMcp/ClientApp && npm ci && npm run build)
(cd src/SlimFaasPlanetSaver && pnpm install --frozen-lockfile && pnpm run coverage && pnpm build)
(cd src/SlimFaasSite && pnpm install --frozen-lockfile && pnpm test && pnpm build)
(cd samples/FibonacciReact && pnpm install --frozen-lockfile && pnpm lint && pnpm build)
```

See [How SlimFaas Works](docs/how-it-works.md#container-build-dependencies) for the container build baseline and [Native Local Mode](docs/native-local-mode.md) for the local smoke checks.

## Pull Request

Please respect the following [PULL_REQUEST_TEMPLATE.md](./PULL_REQUEST_TEMPLATE.md)

## Issue

Please respect the following [ISSUE_TEMPLATE.md](./ISSUE_TEMPLATE.md)
