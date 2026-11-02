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
For the Bash smoke script, install cURL and jq.

On Windows, use Git Bash and check `command -v curl jq` first. Git Bash includes
cURL; install jq separately and add its directory to `PATH`. WSL is not required.

```bash
BASE_URL=http://127.0.0.1:30020 bash demo/smoke-tour.sh
```

For the optional DLQ repair and durable Streams tour, stop the demo first, then
restart with the shipped overlays (only in this isolated trusted demo):

```bash
export SlimFaas__DeadLetterQueue__EnableFront=true
export SlimFaas__DeadLetterQueue__DefaultVisibility=Public
./start.sh -f slimfaas.local.dlq.yaml -f slimfaas.local.streams.yaml -f slimfaas.local.tour.yaml
```

In PowerShell, set the same two `$env:` variables, then run
`.\start.ps1 -f slimfaas.local.dlq.yaml -f slimfaas.local.streams.yaml -f slimfaas.local.tour.yaml`.
From another Bash terminal, run `bash demo/durable-tour.sh`, or set
`DURABLE_TOUR=1` when running the core smoke script. Bruno's optional
**Tour / 09 Durable messaging** needs `durableTour=true` in the selected environment.
Public DLQ access permits body inspection and replay/deletion; never expose this
configuration to untrusted users. The sample handler is disabled without the tour
overlay. Each run's repair/completion keys expire after ten minutes; successful
cleanup leaves the shared `orders` history and subscriptions intact.

To observe scaling from one to several ready replicas with 800 async requests,
keep the dashboard open and run the optional bounded workload separately:

```bash
BASE_URL=http://127.0.0.1:30020 bash demo/async-scale-tour.sh
```

It waits for an idle baseline, checks every submission, then verifies scale-out,
queue drain and scale-down. Do not run another producer at the same time.
The tutorial uses one async request per replica and a function-wide limit of ten;
additional ready replicas can increase async processing capacity up to that limit.
Bruno provides the equivalent **Manual / Autoscaling** folder. Cancelling
submission leaves already accepted work queued; let it finish before stopping.

The source manifest is paired with `slimfaas.local.prebuilt.yaml`, which selects
the packaged executables and disables automatic sample schedules. The tour
creates its own schedules. Pass further `-f` overlays to the launcher to customize
the demo. Manifest paths are resolved relative to the base manifest. If an overlay
changes the entrypoint port or state location, use the dashboard URL and state
directory printed by SlimFaas at startup.

Linux bundles target glibc, not Alpine/musl. See the local installation guide
for platform prerequisites and troubleshooting:
https://slimfaas.dev/get-started/local
