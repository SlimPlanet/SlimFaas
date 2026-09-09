# Dashboard validation — issue #338

This report records the checks for the canvas traffic map and metadata inventory. The browser workload is synthetic; it does not measure Kubernetes scheduling capacity or end-to-end HTTP throughput.

## Reference workstation

- Apple M4 Pro, 48 GiB RAM, macOS 26.5.2 (25F84), ARM64.
- Node 24.20.0, .NET SDK 10.0.300, Chromium 153.0.8010.12, headless at 1920 × 1080.
- The production Vite build is served over loopback. All 10,000 job executions and 10,000 replicas are recreated in a full status snapshot every second, with 100 activity events every 100 ms.

## Browser load

The five-minute run measured actual canvas background draws while continuously panning and zooming, with the map at instance detail. It also searched for and selected the final job execution and final replica, checked pause/resume, and verified a 390 × 844 mobile viewport and reduced-motion preference.

| Measure | Result |
|---|---:|
| Duration | 300.09 s |
| Canvas draw rate | 59.92 frames/s |
| 95th percentile frame gap | 18.1 ms |
| JavaScript heap after GC, start / end | 19.0 / 20.9 MB |
| Browser errors | 0 |
| Event journal capacity | 5,000 |
| Animated marker capacity | 200 |

The heap observations are consistent with bounded retention over this run, not a guarantee for every workload. The Node tests separately verify every instance identity and reserved group boundary, and feed 300,000 unique events plus duplicates through the bounded history.

Storybook's development runtime retained substantially more memory in the same full-snapshot scenario (approximately 49 → 863 MB after GC in five minutes). Clearing browser user-timing entries did not release it. Use the production harness below for memory/performance acceptance; Storybook remains the interactive visual fixture. No production code clears browser instrumentation or disables developer tools.

### Reproduce the production measurement

Build and start the synthetic SSE server:

```bash
cd src/SlimFaas/ClientApp
npm ci
npm test
npm run build
node scripts/serve-load.mts
```

In another terminal, use an independently installed Playwright Core and a local Chromium executable. This harness adds no dashboard dependency or lockfile change:

```bash
npm install --prefix /tmp/slimfaas-browser --no-save --ignore-scripts playwright-core
cd src/SlimFaas/ClientApp
PLAYWRIGHT_ROOT=/tmp/slimfaas-browser \
CHROMIUM_EXECUTABLE=/absolute/path/to/chromium \
node scripts/measure-load.mjs
```

The default duration is 300 seconds. `DASHBOARD_LOAD_RESULTS` selects the output directory (default: the system temporary directory's `slimfaas-dashboard-load`). `DASHBOARD_LOAD_URL`, `DASHBOARD_LOAD_PORT` and `DASHBOARD_LOAD_SECONDS` allow local overrides. The harness fails below 30 actual draws/s, on browser errors, or when post-GC heap growth exceeds 30 MB.

For interactive scenarios, run `npm run storybook` and open **Dashboard / Live**. Traffic contains the dense workload; Data Files covers persistent, expiring and unknown-size entries.

## Integration validation

The complete .NET suite passed 1,214 tests (SlimFaas 950, SlimData 176, MCP 79, Kafka 9). Dashboard and documentation tests each passed 8 tests; dashboard, Storybook and documentation builds succeeded. Run these checks with Node 24 on PATH:

```bash
dotnet test
dotnet publish src/SlimFaas/SlimFaas.csproj -c Release -r osx-arm64
(cd src/SlimFaas/ClientApp && npm test && npm run build && npm run build-storybook)
(cd src/SlimFaasSite && pnpm install --frozen-lockfile && pnpm lint && pnpm test && pnpm build)
```

Use the native RID of the build host. The dashboard MSBuild target refreshes the web content items after Vite changes its hashed filenames, including on an incremental build.

## SlimData comparison with inventory open

The baseline is `ffff804f` (current main when the branch was created). Both variants use native AOT, three nodes, the same adaptive batch defaults and `EnableFront=true`. The candidate additionally holds one metadata stream open throughout warmup, load and cooldown. Each variant ran three alternating repetitions: 2 s warmup, 5 s measured writes, concurrency 12, 4 KiB payloads, then 1 s cooldown.

| Variant | Write rates across runs (ops/s) | Median ops/s | Median p95 (ms) | Median peak node RSS (MiB) | Errors |
|---|---|---:|---:|---:|---:|
| Main baseline | 76.72, 52.31, 53.12 | 53.12 | 244.32 | 138.83 | 0 |
| Candidate + inventory | 51.97, 124.21, 52.32 | 52.32 | 237.45 | 138.48 | 0 |

Median throughput changed by −1.5%; median p95 improved by 2.8%. These short screening runs have high throughput variance and do not establish statistical equivalence. All writes succeeded. Each observer received nine frames, never more than 100 entries per frame, with 312–320 keys in the shared inventory. No stream disconnected during measured load.

Reproduce each variant with its own publication directory; alternate the baseline and candidate to reduce ordering bias:

```bash
MEMORY_LAB_ENABLE_FRONT=true MEMORY_LAB_DATA_STREAM=0 \
MEMORY_LAB_PUBLISH_DIR=/absolute/path/to/baseline-publish \
MEMORY_LAB_SKIP_PUBLISH=1 WARMUP_SECONDS=2 COOLDOWN_SECONDS=1 \
.bin/memory-lab.sh aot slimdata-set 5 12

MEMORY_LAB_ENABLE_FRONT=true MEMORY_LAB_DATA_STREAM=1 \
MEMORY_LAB_PUBLISH_DIR=/absolute/path/to/candidate-publish \
MEMORY_LAB_SKIP_PUBLISH=1 WARMUP_SECONDS=2 COOLDOWN_SECONDS=1 \
.bin/memory-lab.sh aot slimdata-set 5 12
```

## Native demo and browser behavior

The `osx-arm64` AOT executable validated the native demo manifest and ran its three-node chain with an ephemeral test overlay. `/status-functions` and `/function/fibonacci1/hello/local` returned 200. A temporary Python job sent synchronous and asynchronous requests through its signed local gateway; the collected events retained the execution identity and associated `fibonacci1` target/queue.

The live API checks created set keys and files, observed a short TTL expire, changed a persistent key's TTL, checked exact sizes (2,500,000; 98,297; 128 bytes), and paged 105 prefix-matching keys as 100 + 5. The browser checks covered dialog keyboard navigation and focus restoration, wake failure feedback, restricted metadata without an automatic retry loop, copying a key, retaining stale rows after stream EOF, reconnecting, and mobile overflow. The screenshots use synthetic data in that native demo.

Kubernetes IP attribution is covered by endpoint/helper tests; no live Kubernetes cluster was used. AOT dependency warnings from MemoryPack and System.Configuration are also present on the baseline. The full test suite initially encountered an existing adaptive-batcher timing timeout under concurrent builds; its isolated rerun and the complete subsequent run passed.

## Address privacy follow-up

The browser workload above was repeated with full-length opaque address tokens after the privacy change. Fifteen new backend cases cover IPv4, mapped IPv6, IPv6 scope IDs, loopback callers, unchanged workload names and the actual SSE wire output. Initial/periodic state, history, individual activity and batches expose matching tokens; a second subscriber receives consistent tokens. Raw deployment caches and access-controlled inter-node activity retain their internal addresses.

The native AOT demo and browser follow-up passed Fibonacci, signed job sync/async calls through a successfully completed job, identifier search, replica selection and mobile layout. All 55 captured browser SSE frames omitted literal loopback addresses, the old `Ip` property and the `ip_` prefix; there were no browser errors. Existing Node topology tests also cover opaque-token correlation and stable selection identities after token rotation. Dashboard, Storybook, documentation and native AOT builds passed again. The public replica field is `Identity` (replacing `Ip`) and tokens use the generic `id_` prefix. SSE tests explicitly reject the old `Ip` JSON property; consumers must migrate to `Identity` and treat it as opaque.

After renaming the wire field to `Identity`, a further 60-second production smoke run with the same 20,000-instance workload passed at 59.86 draws/s (p95 gap 17.6 ms), with post-GC heap 19.0 → 20.6 MB and no browser errors. Native publication, the complete .NET suite, dashboard/Storybook/site builds and browser integration checks were rerun for the renamed contract.

## Reactive Traffic follow-up

The follow-up fixes the first ignored activity batch, server-clock-based animation expiry and missing WebSocket dispatch events. Native integration also exposed an existing routing error: peer activity was queried on the first advertised port, which is the Raft port in local mode. Peer reads now select an application port, with tests for both native and Kubernetes port ordering. Default peer interval and initial delay are 500 ms, explicit overrides are preserved, and at most four peer reads run concurrently. Overview and Data request state-only streams and do not activate peer activity polling.

The complete .NET suite passed **1,232 tests** (SlimFaas 968, SlimData 176, MCP 79, Kafka 9), and the dashboard passed **20 Node tests**. Regressions cover first/delayed receipts, duplicates, identical timestamps, peer restart with a backward clock, history-free bootstrap, concurrent reads, disconnect cleanup, selection/filter changes, buffered arrivals during pause, reconnect sessions, function power states and bounded marker aggregation. The WebSocket tests cover successful streaming, abort, send failure and publication fanout while preserving job identity. The client wire protocol and public `Identity` contract are unchanged.

Dashboard and Storybook production builds passed with Node 24. Documentation lint, eight tests and the static export passed: 21 pages, 1,306 local links/assets and 324 search entries. Native AOT publication remains compatible; generated Storybook output is excluded from the runtime's content items. No dependencies or lockfile versions changed.

The `osx-arm64` AOT demo ran on three isolated nodes. Twelve synchronous HTTP requests and three publications to four ready HTTP subscribers produced **93 events: 93 received, no duplicates** on a stream pinned to one node. A signed local job completed four rounds of synchronous requests, queued requests and publications; its **84 events** retained the execution identity and fanout targets. Two native WebSocket clients on another node served four synchronous requests and a publication to both clients: **21 emitted, 21 received, no duplicates**. Refeeding these exact receipts through the playback model represented all 21 as six aggregate markers, with none omitted.

The production dashboard browser observed the remote WebSocket function despite its absence from the local inventory, searched its identity, selected both the function and an observed replica, retained global traffic during selection, and displayed active markers. Captured public frames omitted literal loopback addresses and the old `Ip` property. Storybook browser checks cover the isolated first event, deliberately old server timestamps, publications, an empty observed queue, all three persisted speeds, explicit isolation, pause/resume, reduced motion, keyboard canvas controls and a 390 px mobile viewport. See **Dashboard / Live / Reactive Traffic** for the interactive fixture.

![Reactive traffic states, queue symbols and message legend](images/dashboard/traffic-reactive.png)

![Reactive traffic on mobile](images/dashboard/traffic-reactive-mobile.png)

### Peer polling cost and idle shutdown

Both measurements use the same corrected native binary, three otherwise idle nodes, information-level node logging, one browser-equivalent observer, three seconds of warmup and twenty seconds per view. The baseline explicitly overrides the interval to 2,000 ms; the candidate uses the 500 ms default. Nodes are restarted between variants. CPU and peak RSS are summed across the three runtime processes; the local controller and this Python observer are excluded.

| Interval | View | Successful peer reads | Failed reads | Node CPU (s) | Peak node RSS (MiB) |
|---|---|---:|---:|---:|---:|
| 2,000 ms | Traffic | 20 | 0 | 2.952 | 326.9 |
| 2,000 ms | Overview | 0 | 0 | 2.841 | 362.1 |
| 500 ms | Traffic | 80 | 0 | 2.562 | 330.9 |
| 500 ms | Overview | 0 | 0 | 2.109 | 366.7 |

Polling performs four times as many successful peer reads at the new default and stops without Traffic subscribers. These short single runs demonstrate polling frequency and shutdown, not a statistically significant CPU improvement or a throughput benchmark. RSS includes normal process growth between sequential views. No activity was generated during these cost measurements.

With a native local demo running, reproduce from the repository root (substitute its HTTP ports if using an overlay):

```bash
python3 .bin/status-stream-benchmark.py \
  --nodes http://127.0.0.1:30021 http://127.0.0.1:30022 http://127.0.0.1:30023 \
  --interval-label 500
```

For the comparison, restart the same demo with `SlimFaas__StatusStream__PeerSyncIntervalMilliseconds=2000` and change the label to `2000`. The label is descriptive and does not configure the server. The harness fails if any peer read returns a non-success response.

Eight benchmark unit tests cover metric deltas, failed reads, stream parsing and its 5,000-event bound, readiness errors, cleanup and CLI validation. They run in the dashboard check and emit a Sonar generic coverage report in the analysis job. Python's standard-library `trace` records executed lines (including the reader threads); compiled line tables provide the executable-line denominator. The local result is **88/89 lines (98.9%)**, with no additional dependency. Only the test runner itself is excluded from coverage; the measured benchmark remains included and the quality gate is unchanged.

```bash
python3 .bin/test-status-stream-benchmark.py --coverage /tmp/status-stream-coverage.xml
```

### Five-minute reactive map workload

The production harness above was rerun on the same reference workstation with the new Fast animation, global traffic during selection, grouped markers, icons and status badges. The workload again contained **10,000 job executions + 10,000 replicas, 1,000 events/s, and a full new state every second**.

| Measure | Result |
|---|---:|
| Duration | 300.08 s |
| Canvas draw rate while panning/zooming | 59.86 frames/s |
| 95th percentile frame gap | 17.5 ms |
| JavaScript heap after GC, start / end | 20.1 / 21.9 MB |
| Browser errors | 0 |
| Journal / marker capacities | 5,000 / 200 |

Search and selection of the final job execution and replica, a stable paused journal, live resumption, reduced motion and mobile overflow checks passed. The Node tests verify non-overlapping reserved groups and all 20,000 identities, independently of viewport culling. These results describe this synthetic rendering workload; they do not measure function latency or lossless production telemetry.
