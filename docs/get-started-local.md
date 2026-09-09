# Get Started in Local

Run three SlimFaas nodes, four HTTP functions and jobs directly on your computer, then explore them in the dashboard. Choose how to start:

- [Run from a release bundle](#run-from-a-release-bundle): download the precompiled demo; no .NET or Node.js installation is required.
- [Run from a Git clone](#run-from-a-git-clone): build with npm and dotnet to test `main`, a feature branch or your own changes.

Both paths open the same dashboard and lead into the [Guided Tour](guided-tour.md). Docker and Kubernetes are not required.

## Run from a release bundle

The bundle includes SlimFaas and the sample applications, ready to execute.

> **Release availability:** this installation path requires a release containing `SlimFaas-Local-<rid>.zip` assets and their `.sha256` files. It becomes available when the first release with these new bundles is published. Older `SlimFaas-<rid>.zip` archives contain the server alone. Until the complete bundles are published, [run from a Git clone](#run-from-a-git-clone).

### Before you start

Use a Bash-compatible terminal with `curl`, `unzip`, and either `sha256sum` or `shasum`. On macOS these tools are normally present. On Linux, install missing tools through your distribution's package manager. On Windows, use Git Bash or WSL for installation; the extracted bundle also includes a PowerShell launcher.

| System | Bundle | Notes |
|---|---|---|
| Linux x64 | `linux-x64` | glibc distribution; Alpine/musl is not supported |
| Linux ARM64 | `linux-arm64` | glibc distribution, for example Ubuntu on ARM |
| macOS Intel | `osx-x64` | Use an Intel terminal/process architecture |
| macOS Apple Silicon | `osx-arm64` | Use a native ARM64 terminal to avoid selecting the Intel archive under Rosetta |
| Windows x64 | `win-x64` | Git Bash installer, then Bash or PowerShell launcher |
| Windows with WSL | Linux bundle matching WSL | Run the complete demo inside WSL |

The bundled sample applications include their own .NET runtime. Standard OS libraries are still needed (including ICU, OpenSSL and the C/C++ runtime on Linux); a missing-library error identifies what to install. Linux release builds use Ubuntu 24.04. The installer does not install system packages or request administrator privileges.

Keep ports `30020–30023`, `3262–3264` and `5000–5999` available. Stop other SlimFaas demos first because their ports overlap. Git, Docker and Kubernetes are not required.

### Download and validate

Run these commands in the directory where you want the demo installed:

```bash
curl -fsSLo install-local-demo.sh https://slimfaas.dev/downloads/install-local-demo.sh
sh install-local-demo.sh
cd slimfaas-demo
./start.sh --validate
```

The installer detects your OS and architecture, resolves the latest stable release once, verifies its SHA-256 checksum, and validates the extracted manifest. It installs into `./slimfaas-demo` without changing your shell profile or system tools. The archive and its checksum come from the same pinned release.

To choose a particular release or installation directory:

```bash
# Replace the tag with one listed on the Releases page and containing local bundles.
sh install-local-demo.sh --version YOUR_RELEASE_TAG --directory "./my SlimFaas demo"
```

Find available bundles on [GitHub Releases](https://github.com/SlimPlanet/SlimFaas/releases). Reinstalling the same version/platform preserves all existing files and state. To try another version, stop the old demo and choose a new directory; the installer refuses to overwrite an existing different installation.

Alternatively, download the ZIP and its matching `.sha256` file directly from the release, verify the checksum, and extract them into a new directory. On macOS/Linux, restore executable permissions if your extraction tool did not preserve them:

```bash
chmod +x start.sh runtime/SlimFaas functions/fibonacci/Fibonacci jobs/fibonacci-batch/FibonacciBatch
```

### Start the demo

From the extracted demo directory:

```bash
./start.sh
```

On Windows in PowerShell:

```powershell
.\start.ps1 -Validate
.\start.ps1
```

Leave this terminal running. The launcher validates the base manifest and its precompiled overlay, then starts three SlimFaas nodes. The four functions wake on demand and can scale to zero. No package restore or application compilation happens at startup.

The bundle contains `runtime/`, `functions/fibonacci/`, `jobs/fibonacci-batch/`, `demo/bruno-slimfaas-demo/`, and the two manifests. The overlay replaces source-build commands with packaged executables, configures a longer job retention for observation and disables automatic sample schedules; the tour creates its own schedules.

## Run from a Git clone

Use this path to try a branch before it has a release, or to develop the dashboard and runtime together. Install **Git**, the **.NET 10 SDK** (`10.0.103` or a newer .NET 10 SDK, as selected by `global.json`), and **Node.js 24 or later with npm**. Keep ports `30020–30023`, `3262–3264` and `5000–5999` available.

Clone the branch you want to test. Replace `main` with its name to build a feature branch:

```bash
git clone --branch main https://github.com/SlimPlanet/SlimFaas.git
cd SlimFaas
```

From the repository root, install the dashboard dependencies, build its assets and build SlimFaas:

```bash
npm ci --ignore-scripts --prefix src/SlimFaas/ClientApp
npm run build --prefix src/SlimFaas/ClientApp
dotnet build src/SlimFaas -p:SkipClientAppBuild=true
```

The npm build writes the dashboard to `src/SlimFaas/wwwroot`. `SkipClientAppBuild=true` avoids rebuilding those assets during the .NET build. No global SlimFaas installation is needed: the following commands run the runtime from this checkout.

Validate the manifest, then start the demo:

```bash
dotnet run --project src/SlimFaas --no-build -- local validate -f ../../slimfaas.local.yaml
dotnet run --project src/SlimFaas --no-build -- local up -f ../../slimfaas.local.yaml
```

These single-line commands also work in PowerShell. Run them from the repository root; the manifest path is relative to the `src/SlimFaas` working directory used by `dotnet run`. Leave `local up` running. The source manifest starts three nodes and builds sample functions and jobs with dotnet when they launch, so the first request can take longer. It also enables the sample job schedules configured in `slimfaas.local.yaml`.

Open the dashboard and run the checks below. To test a different branch or new edits, stop the demo with **Ctrl+C**, switch branches if needed, rerun the npm/.NET build commands, and start it again. `--no-build` uses the last build, so restart after rebuilding to see your changes. For overlays and IDE debugging, see the [Local Mode reference](native-local-mode.md).

## Open the dashboard

Open **http://127.0.0.1:30020/**. This is the shared entrypoint for the dashboard, application requests and WebSocket connections. Ports `30021–30023` belong to individual nodes.

In another terminal:

```bash
export BASE_URL=http://127.0.0.1:30020
curl -i "$BASE_URL/ready"
curl -fsS "$BASE_URL/status-functions"
curl -fsS "$BASE_URL/function/fibonacci1/hello/local"
```

Expect `200 READY`, four functions in the JSON list, and `Hello local!`. Retry readiness while the cluster starts. In **Overview**, find `fibonacci1` through `fibonacci4`. A successful synchronous request wakes its target and waits for readiness.

The demo exposes the data APIs for local exploration and includes the `fibonacci` and `fibonacci5` job configurations. Local processes share the host network and have no container resource isolation.

## Discover the features

Continue to the [Guided Tour](guided-tour.md), keeping the dashboard open. Run its commands from the extracted demo directory or the repository root: both contain the same `demo/` paths.

Open `demo/bruno-slimfaas-demo` in **Bruno Desktop** and select **Local**. This does not require Node.js. Alternatively, use the cURL commands with `jq`. The optional automated Bruno CLI requires Node on the machine running that test tool, independently of SlimFaas.

For developing your own functions, source builds, overlays and IDE debugging, use the [Local Mode reference](native-local-mode.md).

## Troubleshooting

| Symptom | What to check |
|---|---|
| Release has no downloadable local bundle | Choose a release containing `SlimFaas-Local-*` and its checksum. The server-only archive cannot supply the demo functions. |
| SHA-256 mismatch | Retry the download from the selected release. The installer stops before installing anything. |
| Destination already exists | Reuse the same installed version, or choose a new `--directory` for another release. |
| Address already in use | Stop the conflicting demo, or supply a manifest overlay with different ports. Update callback URLs if changing the entrypoint. |
| Function cannot start | Read its log in `.slimfaas/slimfaas-demo/logs`; verify executable permissions, platform and OS libraries. |
| macOS blocks an executable | Use the normal macOS Privacy & Security approval for a release you trust. The installer does not disable OS protection. |
| UI fails to load | Use the entrypoint port. A bundle must include `runtime/wwwroot`; in a Git checkout, rerun the npm build and the .NET build before restarting. |
| Some replicas are down | Idle scale-to-zero is expected. Click **Wake Up** or send a synchronous request. |
| `404` for `fibonacci4` | This function is private; caller classification differs on a shared host network. See the tour's private-access exercise. |

## Stop and reset

Press **Ctrl+C** in the terminal running the demo. SlimFaas stops its managed processes. Logs and persistent state remain below `.slimfaas/slimfaas-demo` inside the bundle directory or Git checkout. Restart with the same command to keep that state.

With a release bundle, to deliberately discard this demo's state and start again:

```bash
./start.sh --clean
```

PowerShell equivalent for a bundle: `.\start.ps1 -Clean`. From a Git checkout, use:

```bash
dotnet run --project src/SlimFaas --no-build -- local up -f ../../slimfaas.local.yaml --clean
```

To remove the demo, stop it and remove only its installation directory or checkout; no system service or global runtime was installed by these commands.
