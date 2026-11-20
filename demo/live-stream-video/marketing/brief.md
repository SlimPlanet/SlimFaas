# SlimFaas marketing film brief

**Zero idle replicas. One HTTP call wakes your API.**

The audience is a platform lead paying for a fleet of intermittently used HTTP services, and the developer who maintains one of those services. An internal API used during office hours is a good example. Its callers still need a working endpoint after hours, even when keeping a worker running all night would serve no one.

**For the platform lead.** Free cluster capacity between calls and add workers when a queue builds. Guillaume Chervet (AXA France) reported that his team cut its infrastructure costs by **more than 60%**. Attribute the figure to that deployment. The financial return for another team depends on how much capacity sits idle and how it is billed. SlimFaas and its backing cluster still consume resources. [Devoxx Belgium 2025](https://m.devoxx.com/events/dvbe25/talks/11002/i-lost-weight-on-kubernetes-with-slimfaas)

**For the developer.** Keep your HTTP handler. For an interactive request, SlimFaas wakes the worker and returns your handler’s response. For a long calculation, change the route prefix from `/function/` to `/async-function/` and let the caller continue once the work is durably queued. You can exercise both paths on a laptop without a Kubernetes cluster. [HTTP functions](../../../docs/functions.md) · [Async calls](../../../docs/async-functions.md) · [Local demo](../../../docs/get-started-local.md)

## Dashboard cut

| Problem | Outcome for the viewer | Evidence |
| --- | --- | --- |
| An internal API sits idle overnight, but someone may still call it. | Release its last pod after the idle timeout. The next HTTP call starts a worker and receives the computed result. The first caller waits for readiness. | The filmed request wakes one pod and receives Fibonacci(10) = 55. [Recording](../media/dashboard.mp4) |
| Thirty requests arrive while the worker is asleep. | The caller gets a receipt for queued work. Workers share the burst, finish it and give the capacity back. | 30 accepted, 30 completed, four ready workers at peak, zero after the idle timeout. [Recorded observations](../observed.json) |
| A customer asks why a request is still waiting. | Inspect the queue and the ready pods together to see whether work is waiting for a worker or the existing workers are busy. | The same dashboard recording shows queue depth and replica count. [Live Stream guide](../../../docs/user-interface.md) |

The strongest sequence is the sleeping API, the call that gets an answer, then the burst. Hold the closing dashboard frame long enough to read **30 completed · 0 workers**. Give the cost result its own on-screen attribution so viewers can distinguish the customer deployment from the Fibonacci demo. Close with one invitation, **Try it with one HTTP endpoint**.

A speed figure belongs in the technical follow-up. The three-node macOS ARM64 Native AOT benchmark measured a median **7,605 successful warm HTTP requests/s** for 4 KiB payloads at concurrency 16. The comparison missed its overall HTTP non-regression criteria. For a buyer whose priority is first-request latency, discuss the cold start and the option to wake a service before opening time. [Benchmark conditions and results](../../../docs/benchmarking.md#measured-macos-native-aot-results) · [Scheduled wake-up](../../../docs/autoscaling.md)

## House film

Open with **Reduce infrastructure spend by up to 60%** and credit the team result on the same frame. Keep the windows and front door in place as the house becomes an application diagram. Follow a single meter from eight idle pods through scale-to-zero. When incoming work wakes the house, widen it into a tower and then the Palace of Versailles. The scale figures and pod-hour tariff are illustrative.

Close on the ice with three outcomes. A developer can try an existing HTTP app locally in five minutes and keep its handler. An agent can move files, reuse results, fan out events, replay streams, queue work and trace requests through HTTP. A platform team saving 60% on a €10,000 monthly bill would free €6,000 each month. [Local trial](../../../docs/get-started-local.md) · [Files](../../../docs/data-files.md) · [Cache](../../../docs/data-sets.md) · [Pub/sub](../../../docs/events.md) · [Streams](../../../docs/streams.md) · [Queued work](../../../docs/async-functions.md) · [Observability](../../../docs/opentelemetry.md)

## Visual direction

Use the site's SlimFaas symbol beside the wordmark, white backgrounds, ink `#15263f`, primary blue `#0000ff` and its system sans-serif typography. Keep **Functions. Less infrastructure.** as the brand signature. Give each shot one outcome. Use recorded footage in the dashboard cut and a continuous illustrated façade in the house film. Keep captions clear of the queue and replica counts. [Official mark](../../../src/SlimFaasSite/public/slimfaas.svg) · [Design tokens and typography](../../../src/SlimFaasSite/src/styles/index.scss) · [Brand signature](../../../src/SlimFaasSite/src/components/NavBar.tsx)
