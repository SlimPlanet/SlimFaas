# Get Started in Local

Download a ready-to-run SlimFaas demo, open its dashboard, and explore three nodes, four HTTP functions and jobs directly on your computer. The bundle includes SlimFaas and the sample applications: **no .NET or Node.js installation is required**.

> **Release availability:** this installation path requires a release containing `SlimFaas-Local-<rid>.zip` assets and their `.sha256` files. It becomes available when the first release with these new bundles is published. Older `SlimFaas-<rid>.zip` archives contain the server alone. Until the complete bundles are published, use [the source-based local guide](native-local-mode.md#quick-start).

## Before you start

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

## Download and validate

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

## Start the demo

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

## Open the dashboard

Open **http://127.0.0.1:30020/**. This is the shared entrypoint for the dashboard, application requests and WebSocket connections. Ports `30021–30023` belong to individual nodes.

In another terminal:

```bash
export BASE_URL=http://127.0.0.1:30020
curl -i "$BASE_URL/ready"
curl -fsS "$BASE_URL/status-functions"
curl -fsS "$BASE_URL/function/fibonacci1/hello/local"
```

Expect `200 READY`, four functions in the JSON list, and `Hello local!`. Retry readiness while the cluster starts. In **Infrastructure Overview**, find `fibonacci1` through `fibonacci4`. A successful synchronous request wakes its target and waits for readiness.

The demo exposes the data APIs for local exploration and includes the `fibonacci` and `fibonacci5` job configurations. Local processes share the host network and have no container resource isolation.

## Discover the features

Continue to the [Guided Tour](guided-tour.md), keeping the dashboard open. Run its commands from the extracted demo directory: the bundle preserves the same `demo/` paths as the repository.

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
| UI fails to load | Use the complete bundle and entrypoint port. The runtime directory must include its `wwwroot` dashboard assets. |
| Some replicas are down | Idle scale-to-zero is expected. Click **Wake Up** or send a synchronous request. |
| `404` for `fibonacci4` | This function is private; caller classification differs on a shared host network. See the tour's private-access exercise. |

## Stop and reset

Press **Ctrl+C** in the launcher terminal. SlimFaas stops its managed processes. Logs and persistent state remain below `.slimfaas/slimfaas-demo` inside the installation directory. Restart with the same launcher to keep that state.

To deliberately discard this demo's state and start again:

```bash
./start.sh --clean
```

PowerShell equivalent: `.\start.ps1 -Clean`. To uninstall, stop the demo and remove only its installation directory; no system service or global runtime was installed.
