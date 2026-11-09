# ZLinq on Apple M4 Pro: JIT and Native AOT comparison

## Scope and host

The [PR 504 follow-up](performance-pr504-followup.md) investigates additional
count/projection costs and reliability on Windows, using this PR's final head
as its incremental baseline. Its results are separate from this M4 comparison.

This campaign rebuilds and measures the baseline on the new machine. Historical Windows/Ryzen results in [the earlier report](performance-benchmarks.md) are not used as its baseline.

- Baseline: `496e8739e5f7b4326f64520fe4caa4e0e23f8d06` (PR base, without ZLinq).
- Candidate: the runtime changes accompanying this report. Executable and assembly SHA-256 hashes identify the measured publications.
- Apple M4 Pro, 14 logical processors, 48 GiB RAM, macOS 26.7.1, connected to AC power.
- .NET SDK 10.0.403 and runtime/Native AOT compiler 10.0.14, `osx-arm64`, Release.
- Four isolated self-contained publications: before/after under untrimmed JIT, and before/after under the product Native AOT settings.
- All runtime nodes launch through `slimfaas local`; no Docker or Kubernetes. The common driver and function targets remain JIT in all variants.
- Publications have identical empty dashboard directories. Skipping the dashboard build alone can copy pre-existing assets, so those assets were excluded from benchmark-only publication copies.
- Builds finish before timed runs. Variants run sequentially, reverse order on the second round and rotate order on the third round. Every repetition gets a fresh ephemeral three-node cluster.

## Retained changes

Explicit value enumeration extends the existing PR to queue-state selections, key/value batch projections, queue result DTOs, batch statistics, state metrics, file-transfer peer selection and Stream pagination. Pending Stream counters use a single pass without a temporary array. Existing efficient loops and scalar LINQ paths remain in place.

Filtered queue results use `Where(...).ToArray()` followed by `ImmutableCollectionsMarshal.AsImmutableArray`. The array belongs to the result; queue elements keep their original identities. This avoids copying a builder sized to the whole queue, and avoids the growing immutable-builder fallback for dense results. [ZLinq 1.6.1 provides a specialized filtered-array path](https://github.com/Cysharp/ZLinq/blob/1.6.1/src/ZLinq/Linq/ToArray.cs). Tests cover sparse and dense ownership, result ordering, empty selections and Stream counter filtering.

File-tag sorting and active Stream counts retain their original LINQ implementations: exploratory replacements were slower or saved negligible allocation. No dependency version changes were needed beyond the PR's existing ZLinq 1.6.1 dependency.

## Collection measurements

BenchmarkDotNet runs in-process under JIT, with six warmup and twelve measured iterations targeting 500 ms. Queue mutation benchmarks retain sixteen fresh states per iteration; their short iteration times make mutation timings exploratory. Default BenchmarkDotNet outlier handling applies. There are 56 baseline and 56 retained-candidate cases, including the additional empty/sparse/dense queue matrix.

The table reports observed mean method time and managed bytes allocated per operation. It is not a process RSS measurement or a direct measurement of AOT allocation. Small timing changes can reflect shared-host variation; unchanged controls are retained in the raw results.

| Operation | Population | Before ns | After ns | Time delta | Before B/op | After B/op | Allocation delta |
|---|---|---:|---:|---:|---:|---:|---:|
| CollectionPipelineBenchmarks.JobCallerLookup | Count=200 | 12987.39 | 7659.21 | -41.0% | 48616 | 18048 | -62.9% |
| CollectionPipelineBenchmarks.ProjectRequestHeaders | Count=200 | 1762.69 | 1700.76 | -3.5% | 9896 | 9832 | -0.6% |
| CollectionPipelineBenchmarks.ProjectDetailedStatuses | Count=200 | 21793.87 | 20971.16 | -3.8% | 149133 | 134659 | -9.7% |
| CollectionPipelineBenchmarks.FilterPeerHeaders | Count=200 | 5311.66 | 4946.75 | -6.9% | 23536 | 23288 | -1.1% |
| CollectionPipelineBenchmarks.ProjectVirtualDeployment | Count=200 | 3440.61 | 3366.58 | -2.2% | 45432 | 45176 | -0.6% |
| CollectionPipelineBenchmarks.StageManagedQueue | Count=200 | 7268.48 | 4099.15 | -43.6% | 46640 | 36992 | -20.7% |
| RoutingQueryBenchmarks.SearchFunction | FunctionCount=200 | 516.00 | 264.64 | -48.7% | 24 | 0 | -100.0% |
| RoutingQueryBenchmarks.FirstReadyPodPorts | FunctionCount=200 | 557.41 | 262.41 | -52.9% | 176 | 0 | -100.0% |
| RoutingQueryBenchmarks.StatusReadyCount | FunctionCount=200 | 25.55 | 22.88 | -10.5% | 96 | 96 | +0.0% |
| DataFeatureBenchmarks.RunningQueue | Count=2000 | 6297.63 | 4828.88 | -23.3% | 20048 | 4112 | -79.5% |
| DataFeatureBenchmarks.RetryQueue | Count=2000 | 13867.37 | 10541.37 | -24.0% | 16056 | 88 | -99.5% |
| DataFeatureBenchmarks.StreamCounters | Count=2000 | 515844.83 | 411040.53 | -20.3% | 1076568 | 1060366 | -1.5% |
| DataFeatureBenchmarks.StreamRunning | Count=2000 | 145607.60 | 132318.12 | -9.1% | 356218 | 356218 | +0.0% |
| DataFeatureBenchmarks.FileTags | Count=2000 | 328772.62 | 324281.93 | -1.4% | 803922 | 803925 | +0.0% |
| QueueSelectionBenchmarks.Running | Depth=0&RunningPercent=0 | 5.28 | 2.26 | -57.1% | 56 | 24 | -57.1% |
| QueueSelectionBenchmarks.Running | Depth=0&RunningPercent=25 | 4.92 | 2.28 | -53.5% | 56 | 24 | -57.1% |
| QueueSelectionBenchmarks.Running | Depth=0&RunningPercent=100 | 4.95 | 2.23 | -54.9% | 56 | 24 | -57.1% |
| QueueSelectionBenchmarks.Running | Depth=200&RunningPercent=0 | 206.22 | 136.48 | -33.8% | 1656 | 88 | -94.7% |
| QueueSelectionBenchmarks.Running | Depth=200&RunningPercent=25 | 471.58 | 291.04 | -38.3% | 2080 | 512 | -75.4% |
| QueueSelectionBenchmarks.Running | Depth=200&RunningPercent=100 | 1006.32 | 506.23 | -49.7% | 3280 | 1712 | -47.8% |
| QueueSelectionBenchmarks.Running | Depth=2000&RunningPercent=0 | 2448.17 | 1938.09 | -20.8% | 16056 | 88 | -99.5% |
| QueueSelectionBenchmarks.Running | Depth=2000&RunningPercent=25 | 5147.71 | 3653.25 | -29.0% | 20080 | 4112 | -79.5% |
| QueueSelectionBenchmarks.Running | Depth=2000&RunningPercent=100 | 10036.83 | 5600.41 | -44.2% | 32080 | 16112 | -49.8% |

Zero-percent running selections return no elements: their large percentage allocation reduction describes that specific sparse case. Empty-queue timing is close to harness overhead; its allocation count is the useful result. The unchanged `StreamRunning` and `FileTags` rows expose variation unrelated to the retained code changes.

## End-to-end protocol

The comparison profile caps benchmark-function async dispatch at 16 globally and per replica, through a generated local overlay applied identically to all four variants. The original 256-dispatch stress profile is recorded separately below.

The planned matrix uses 64 B, 4 KiB and 2 MiB request bodies; concurrency 1 and 16; five seconds of warmup and twenty seconds of load; and three repetitions per variant. After two unsuccessful third-candidate AOT attempts, AOT retries stop. The analyzed counts are **three completed JIT repetitions per revision and two completed Native AOT repetitions per revision**, balanced before/after within each mode. Every completed repetition is retained. The smaller AOT sample limits conclusions. This makes 180 completed direct/proxy/async HTTP cases. Every completed cluster also runs the paced and 1,000-message async scenarios and the 200-message scale-to-zero/scale-out scenario.

Each variant repetition adds three 60-second, 16-worker data workloads: sets; mixed set/delete/increment/hashset/async; and 256 KiB upload/read file cycles. File cycles reuse one ID per worker (16 IDs), upload to one node and read from the next. They measure warm repeated-file access and check HTTP success/body draining; they do not compare file bytes or represent append-only unique-file ingestion. The 2 MiB async cases exercise unique offloaded bodies. A 200-publication durable Stream burst checks target IDs, duplicate counts, committed ACKs, zero lag and zero pending deliveries. Set/hashset/counter state is checked on all three nodes.

CPU and sampled peak RSS sum the three runtime nodes, excluding the supervisor, target functions and load generator. RSS is sampled every 500 ms and includes native/shared pages; it is not exclusive physical memory. Async and Stream CPU windows include delivery and drain. Data resources include driver startup before load and stop before report serialization/state validation. Data operations can include multiple HTTP calls. Stream throughput includes delivery/drain, while its HTTP p95 measures append acceptance.

The mixed data workload records async acceptance alongside data mutations; it does not separately trace those messages to the target or include a final drain in its CPU window. Delivery/duplicate assertions and arrival percentiles come from the dedicated HTTP async and Stream scenarios.

### Final measurements

All statistics below are conditional on the completed repetitions: JIT **N=3** per revision; Native AOT **N=2** per revision. The 13 primary attempts produced ten completed profiles and three unsuccessful heavy-body attempts. The completed HTTP matrix contains **21,038,318 successful requests**, the sustained data workloads contain **754,850 completed operations**, and the Stream bursts contain **2,000 observed and acknowledged publications**. Failed attempts are retained separately below.

The [complete tables](benchmark-results/zlinq-m4-local.md) retain every payload/concurrency case, direct-target controls, arrival latency, scaling and readiness. The [machine-readable measurements](benchmark-results/zlinq-m4-measurements.json) include all 112 collection cases, every analyzed repetition, ranges, runtime/driver hashes and the unsuccessful-attempt ledger.

Selected 16-client results cover each major workload. CPU and RSS cells are before -> after; RSS is the median sampled peak across the three nodes.

| Runtime | Workload | Before op/s | After op/s | Rate delta | CPU ms/op | RSS MiB |
|---|---|---:|---:|---:|---:|---:|
| JIT | Async 4 KiB | 1129.43 | 1128.96 | -0.0% | 4.667 -> 4.639 | 2392.2 -> 2252.3 |
| JIT | Files 256 KiB | 384.18 | 365.39 | -4.9% | 6.364 -> 6.332 | 1651.7 -> 1557.5 |
| JIT | Mixed 4 KiB | 573.57 | 578.53 | +0.9% | 2.012 -> 1.742 | 1886.4 -> 1850.2 |
| JIT | Sets 4 KiB | 344.79 | 317.77 | -7.8% | 2.404 -> 2.354 | 2469.3 -> 2342.7 |
| JIT | Streams 4 KiB | 3.99 | 4.00 | +0.1% | 80.707 -> 79.026 | 1529.2 -> 1440.0 |
| JIT | Sync 4 KiB | 7064.42 | 7090.89 | +0.4% | 0.730 -> 0.722 | 1107.8 -> 1116.9 |
| AOT | Async 4 KiB | 1091.34 | 1090.44 | -0.1% | 3.860 -> 3.797 | 2080.5 -> 2067.0 |
| AOT | Files 256 KiB | 363.89 | 272.87 | -25.0% | 5.499 -> 5.981 | 1216.2 -> 1193.2 |
| AOT | Mixed 4 KiB | 570.89 | 557.61 | -2.3% | 1.583 -> 1.614 | 1579.9 -> 1422.4 |
| AOT | Sets 4 KiB | 368.91 | 351.00 | -4.9% | 1.901 -> 1.994 | 2097.3 -> 1794.7 |
| AOT | Streams 4 KiB | 4.00 | 3.89 | -2.7% | 64.431 -> 64.063 | 1142.6 -> 1131.3 |
| AOT | Sync 4 KiB | 7116.86 | 7218.99 | +1.4% | 0.653 -> 0.627 | 855.0 -> 860.0 |

### What the changes establish

**Managed allocation falls substantially in the large-collection cases. Whole-service CPU, throughput and RSS do not improve uniformly.** Small sync throughput changes are approximately -2.6% to +1.4% across JIT/AOT. The unchanged direct target also varies: its 2 MiB / single-client median moves -9.2% in the JIT comparison and +10.0% in the AOT comparison. Small timing deltas cannot be assigned confidently to ZLinq.

The mixed JIT workload has a 13.4% lower median CPU cost (2.012 -> 1.742 ms/operation), but its per-repetition CPU ranges overlap (1.684-2.044 before, 1.658-1.842 after). Native mixed CPU rises 1.9%. Set throughput falls 7.8% under JIT and 4.9% under AOT. These unfavorable rows are retained.

Warm-file throughput falls 4.9% under JIT and 25.0% under AOT; AOT append/read p95 rises 108.5 -> 166.6 ms and CPU cost rises 8.8%. AOT file rates are 351.6/376.1 operations/s before and 183.5/362.2 after: the low first candidate result strongly affects the two-value median. This is an observed regression with substantial variation, not proof that the peer-list query caused it. No file-path speedup is claimed.

For 2 MiB / 16-client async acceptance, completed JIT runs rise 130.6 -> 158.9/s (+21.7%), while HTTP p95 worsens 115.2 -> 259.0 ms. The separate failures make these conditional results unsuitable for a reliability or sustainable-throughput claim. Dedicated burst arrival p95 changes only 1,784.8 -> 1,735.2 ms in JIT and 1,793.6 -> 1,756.6 ms in AOT; 202 acceptance is not execution latency.

Data-workload median sampled RSS falls about 1-14%, while several HTTP cases increase. Repetition ranges and different heap/cache histories limit causal attribution. Fewer temporary managed objects reduce allocation pressure; the process working set also contains retained heap capacity, caches, stacks, native code and shared pages. A 79.5% reduction in query B/op is not a 79.5% reduction in application RAM.

### Native AOT versus conventional JIT

The most consistent footprint difference is before load: candidate AOT uses **322.1 MiB** across the three nodes versus **494.2 MiB** under JIT (-34.8%). The baseline already shows the same advantage (320.9 versus 489.9 MiB). ZLinq itself does not reduce this pre-load footprint: 489.9 -> 494.2 MiB in JIT and 320.9 -> 322.1 MiB in AOT.

With the candidate, 4 KiB / 16-client sync traffic measures 7,091/s in JIT versus 7,219/s in AOT, CPU 0.722 versus 0.627 ms/request, and sampled peak RSS 1,117 versus 860 MiB. Conversely, 64 B / 16-client sync peak RSS is 986 MiB in JIT versus 1,060 MiB in AOT. Native AOT therefore helps several measured CPU/footprint cases, while its smaller sample and mixed throughput results prevent a blanket "faster and less RAM everywhere" conclusion. Readiness includes Raft election and the shared JIT target, so it is not a pure application-startup measurement.

## Stability limits and unsuccessful attempts

The original 256-dispatch stress profile encountered a Raft quorum interruption on the **baseline Native AOT binary without ZLinq**: 2,545 of 2,555 accepted IDs were observed within 300 seconds, with no duplicates. A superseded JIT candidate using the growing immutable-builder selection path had separately observed 2,485 of 2,491 accepted IDs within 300 seconds, also without duplicates. Three fresh targeted baseline JIT runs at the same payload/concurrency completed without missing deliveries. The incident is intermittent, not evidence of a ZLinq-specific failure.

After a targeted baseline AOT run with dispatch capacity 16 passed the 2 MiB / 16-client case, paced/burst traffic and scaling, that capacity was chosen uniformly for the full comparison. It limits delivery concurrency, not the number of benchmark clients.

The main capacity-16 campaign also encountered a failure on its second **baseline JIT** repetition: only 2,233 of 2,248 accepted IDs were observed after 300 seconds, with no duplicates. Logs record internal offloaded-body `FileNotFoundException` errors and a quorum interruption followed by a new election. There was still 23 GiB of free disk space. These observations do not establish the root cause. The failed attempt is archived, its counts/log hashes are retained, and the whole repetition is restarted with fresh state.

The third **retained-candidate Native AOT** repetition attempt also failed this case: 3,714 of 3,729 accepted IDs were observed after 300 seconds, again without duplicates, with offloaded-body file errors and new elections. Its whole repetition is also restarted. Failures occur before and after the changes; the experiment provides no evidence that ZLinq fixes this issue.

That Native AOT restart also failed: 2,064 of 2,066 accepted IDs were observed within 300 seconds, without duplicates, following another quorum interruption. Native AOT retries then stop. The report uses the two completed repetitions from each AOT revision, keeps all three unsuccessful primary attempts in its ledger, and completes the three JIT repetitions per revision. `analysis.json` records these balanced mode-specific counts and the reason, without rewriting the original three-round manifest.

Performance medians describe completed, correctness-checked repetitions and are conditional on successful completion. Unsuccessful attempts are reported separately, rather than treated as slow successful runs. Neither dispatch capacity establishes reliability for this sustained 2 MiB / 16-client workload. No product quorum or offload fix is claimed.

## Reproduction and artifacts

See [the benchmark instructions](benchmarking.md#compare-jit-and-native-aot-on-a-new-machine) for publishing both revisions, the four-variant runner, resumable execution and strict summary validation. The measured runtime source is commit `e58f8ffa0d4cc13afec83c54bbae369fd4919322`.

For collection measurements, apply the same benchmark instrumentation to the baseline worktree, leaving its product sources unchanged:

```bash
git worktree add --detach ../SlimFaas-zlinq-baseline 496e8739e5f7b4326f64520fe4caa4e0e23f8d06
git archive e58f8ffa0d4cc13afec83c54bbae369fd4919322 benchmarks/SlimFaas.Benchmarks \
  | tar -x -C ../SlimFaas-zlinq-baseline
```

Run the documented extended filters in each checkout. The local campaign command is:

```bash
caffeinate -i python3 .bin/slimfaas-zlinq-benchmark.py \
  --publish-root artifacts/zlinq-m4 \
  --output artifacts/zlinq-m4/campaign-quiet \
  --rounds 3 --duration 20 --warmup 5 \
  --payloads 64,4096,2097152 --concurrency 1,16 \
  --data-duration 60 --stream-messages 200 --dispatch-parallelism 16
python3 .bin/summarize-zlinq-benchmark.py artifacts/zlinq-m4/campaign-quiet
```

The initial command plans three rounds. Resume interrupted attempts with `--resume`; the documented `analysis.json` selects the final balanced JIT=3/AOT=2 counts and records why AOT retries stopped. The original manifest and unsuccessful directories remain unchanged.

`caffeinate` is a macOS idle-sleep assertion; omit it on other operating systems. Binary hashes, exact settings, driver logs, compressed per-request samples, data validation, resource samples and every repetition remain under the campaign directory. Exploratory runs have separate directories.

## Validation

- Documentation site: frozen pnpm install and production build with Node 24.22.2 pass; 23 pages, 1,650 local links/assets and 393 search entries checked.
- Managed local CLI validation succeeds for the four-function/three-job/three-node demo; MemoryLab CLI help and its Release build pass with zero warnings.

- Actual three-node native Fibonacci demo: status, `Hello local!`, async 202, committed Stream ACKs for both subscribers and a successful Fibonacci batch job. Ephemeral/prebuilt overlay; scheduled demo jobs disabled.
- Summary checks accept complete reports and reject missing/duplicate HTTP cases, missing deliveries, invalid or unbalanced analysis counts; unsuccessful-attempt ledgers remain visible.

- Full .NET suite on the retained source: 1,986 passed, zero failed.
- Release builds and both JIT/Native AOT publications succeed. Native publication retains the baseline MemoryPack IL2104/IL3053 and System.Configuration.ConfigurationManager IL2104 summary warnings; no new ZLinq warning is observed.
- Native AOT executable: 56,158,232 -> 56,290,792 bytes (+132,560 bytes, +0.236%). This is disk size, not RAM usage.
