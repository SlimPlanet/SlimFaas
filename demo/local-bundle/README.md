# SlimFaas local demo

This bundle includes SlimFaas with its dashboard, four Fibonacci HTTP functions,
two job configurations and the Bruno collection. No .NET SDK, .NET installation,
Node.js, Docker or Kubernetes is needed. The sample applications include their
own .NET runtime; normal operating-system libraries are still required.

On Linux/macOS or a Windows Bash terminal:

```bash
./start.sh --validate
./start.sh
```

On Windows in PowerShell:

```powershell
.\start.ps1 -Validate
.\start.ps1
```

Open http://127.0.0.1:30020/ once `/ready` returns `200 READY`.
Keep ports 30020–30023, 3262–3264 and 5000–5999 available.
Press Ctrl+C in the launcher terminal to stop all managed processes. Logs and
state are in `.slimfaas/slimfaas-demo`. Restarting preserves state.
Use `./start.sh --clean` (PowerShell: `.\start.ps1 -Clean`) only to reset this demo.
Stop the demo before moving its directory or removing it.

Follow https://slimfaas.dev/guided-tour from this directory. Open
`demo/bruno-slimfaas-demo` in Bruno Desktop and select **Local**. Bruno Desktop
requires no Node installation; running its optional CLI requires Node.
For the Bash smoke script, install cURL and jq, then run:

```bash
BASE_URL=http://127.0.0.1:30020 bash demo/smoke-tour.sh
```

The source manifest is paired with `slimfaas.local.prebuilt.yaml`, which selects
the packaged executables and disables automatic sample schedules. The tour
creates its own schedules. Pass further `-f` overlays to the launcher to customize
the demo. Manifest paths are resolved relative to the base manifest.

Linux bundles target glibc, not Alpine/musl. See the local installation guide
for platform prerequisites and troubleshooting:
https://slimfaas.dev/get-started/local
