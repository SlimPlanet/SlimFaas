# perf: event-driven Kubernetes sync via watch streams (−91 % API LIST calls) and jobs N+1 fix

## Summary

This PR replaces SlimFaas's fixed-cadence full-list polling of the Kubernetes API with **watch-driven synchronization** ("watch-as-signal"): watch events on pods/deployments/statefulsets/jobs/cronjobs only signal *that* something changed — the existing LIST-based synchronization then runs unchanged, so the synchronized state is byte-identical to polling, just triggered by events, with a periodic resync as a safety net. It also fixes the jobs N+1 pod listing.

An end-to-end integration test proves the absence of regression, and its benchmark quantifies the gain.

### Measured results (integration benchmark, same activity window in both modes)

| Mode    | LIST requests | Propagation latency |
|---------|--------------:|--------------------:|
| polling |           273 |               18 ms |
| watch   |            24 |               62 ms |

**−91 % of Kubernetes API LIST requests for identical activity** (per resource: cronjobs 39→0, pods 79→9, deployments/statefulsets/services 40→5 each, jobs 39→4). In real time per replica on an idle cluster: functions sync ~80 → ~8 LIST/min, jobs 60·(1+N) → ~4 calls/min, CronJob config 60 → 1 LIST/min. Worst-case reaction latency to a real change drops from 3 s to ~300 ms (debounce window).

### Commits

1. `perf: fix N+1 pod listing in ListJobsAsync` — 1 job LIST + 1 pod LIST **per job**, sequential, every second → 2 concurrent calls with a label-exists selector; `long.TryParse` hardening on job labels. Test asserts exactly 2 HTTP requests regardless of job count.
2. `feat: add Kubernetes watch-as-signal infrastructure` — `KubernetesWatchOptions` (`SlimFaas:KubernetesWatch`), version-counter change signals, AOT-safe `Utf8JsonReader` watch-line parser (the `KubernetesClient.Aot` package ships no `Watcher<T>`), and `KubernetesWatcherWorker`: five long-lived watch streams over the same hand-rolled authenticated HTTP pattern as `ScaleAsync`, with debounced pulses, bookmark-based resourceVersion continuity across 60 s rotations, jittered exponential backoff, forced pulse after an errored reconnect, 410 reset, and 403/404 degradation to resync-only.
3. `perf: drive deployments synchronization from the functions watch signal` — 4 unfiltered LISTs every 3 s → event-driven + 30 s resync.
4. `perf: drive jobs and jobs-configuration sync from watch signals` — jobs list sync only on signal/resync (the 1 s loop still drives the SlimData job queues); CronJob config sync pulse-driven with a 60 s resync (it cannot be master-gated: every replica serves the enqueue image-whitelist checks).
5. `test: end-to-end integration test and benchmark of the watch-driven sync` — `FakeKubernetesCluster` (in-memory API server behind the real k8s client: LIST + streaming watch + synthetic ADDED replay + request counters); the same six-checkpoint scenario (scale-up, pod readiness, job lifecycle, CronJob config) produces **identical state snapshots** in polling and watch modes; the benchmark asserts watch mode issues at most 25 % of the polling LIST requests.
6. `docs: document the Kubernetes watch requirements and configuration` — RBAC drift fix in `docs/get-started.md` (`batch: jobs` → `jobs, cronjobs`), watch verb requirement, `SlimFaas:KubernetesWatch` reference.

### Safety

- **No `IKubernetesService` interface change**: Docker/Local/Process orchestrators are untouched and never start the watcher.
- **Escape hatch**: `SlimFaas:KubernetesWatch:Enabled=false` (or any non-Kubernetes orchestrator) restores the exact legacy polling cadences — no pulse ever fires and the signal waits degrade to the historical `Task.Delay`.
- Services are intentionally not watched (RBAC does not grant them; the existing 403-latch behavior is preserved).
- Validation: `SlimFaas.Tests` **892/892 green**, `SlimData.Tests` **175/175 green**; the watch code uses only `Utf8JsonReader` (AOT/trim-safe).

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01Ei1iNqZCxJtmaXvRMGspLi
