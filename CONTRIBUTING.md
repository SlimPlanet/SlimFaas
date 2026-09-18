# Contributing

To get started with the repository:

```sh
git clone https://github.com/SlimPlanet/SlimFaas.git

```
You are now ready to contribute!

## Build quality

Warnings are errors and every .NET analyzer is enabled for the whole repository (`Directory.Build.props`, `.editorconfig`, `eng/*.globalconfig`). New code must build clean; suppressions are scoped and justified. NuGet versions live only in `Directory.Packages.props`: a `PackageReference` in a csproj never carries a `Version` attribute, and adding a package means adding one `PackageVersion` line there. See the "Build quality" section of [AGENTS.md](./AGENTS.md) for the rules.

## Pull Request

Please respect the following [PULL_REQUEST_TEMPLATE.md](./PULL_REQUEST_TEMPLATE.md)

## Issue

Please respect the following [ISSUE_TEMPLATE.md](./ISSUE_TEMPLATE.md)
