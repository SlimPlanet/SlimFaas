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
| Duration | 300.08 s |
| Canvas draw rate | 59.96 frames/s |
| 95th percentile frame gap | 17.6 ms |
| JavaScript heap after GC, start / end | 17.9 / 19.9 MB |
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

The complete .NET suite passed 1,196 tests (SlimFaas 932, SlimData 176, MCP 79, Kafka 9). Dashboard and documentation tests each passed 8 tests; dashboard, Storybook and documentation builds succeeded. Run these checks with Node 24 on PATH:

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
