# Run your functions. Let SlimFaas handle the rest.

Bring an HTTP application, call it when you need it, and let idle workloads scale down. SlimFaas provides routing, wake-up, asynchronous queues, events, jobs and temporary data APIs in a native .NET application.

- [Get Started with **Kubernetes**](get-started-kubernetes.md)
  Deploy beside your workloads with a persistent three-node cluster.
- [Get Started **in Local**](get-started-local.md)
  Develop with native processes, without Docker or Kubernetes.
- [Get Started with **Docker Compose**](get-started-docker-compose.md)
  Explore a complete container-based demo on your computer.

## CNCF & Community

![CNCF logo](https://www.cncf.io/wp-content/uploads/2022/07/cncf-stacked-color-bg.svg)

**Proud CNCF Landscape Project.** SlimFaas is part of the Cloud Native Computing Foundation landscape. Meet the community, ask questions and help shape the project.

- [CNCF Landscape](https://landscape.cncf.io)
- [CNCF Slack](https://cloud-native.slack.com/archives/C08CRC77VDE)
- [Community Meeting Calendar](https://calendar.google.com/calendar/embed?src=be1dd72d18650490580a7d5d96a45a6eebe0fc4c9fe8adce630754cbb6121cca%40group.calendar.google.com&ctz=Europe%2FParis)
- [Subscribe to the calendar (ICS)](https://calendar.google.com/calendar/ical/be1dd72d18650490580a7d5d96a45a6eebe0fc4c9fe8adce630754cbb6121cca%40group.calendar.google.com/public/basic.ics)
- [Code of Conduct](https://github.com/cncf/foundation/blob/main/code-of-conduct.md)
- [Contribute to SlimFaas](../CONTRIBUTING.md)

## See your requests move

Open the built-in dashboard, send a request and watch the target function wake. Follow asynchronous work through a queue, observe job executions, and inspect replicas as they start and stop.

The [Guided Tour](guided-tour.md) takes you through the core features with copyable cURL commands and an executable Bruno collection. Each step explains the expected result and what to look for in the UI.

## Build with the features you need

| Your application needs to… | Start with |
|---|---|
| Call an HTTP service and wait for its response | [Synchronous functions](functions.md) |
| Accept work now and process it later | [Asynchronous functions and callbacks](functions.md#2-asynchronous-functions) |
| Notify ready subscribers | [Events](events.md) |
| Execute a task once or on a schedule | [Jobs](jobs.md) |
| Store small state, counters or temporary artifacts | [Data Sets](data-sets.md) and [Data Files](data-files.md) |
| Reduce idle replicas or scale out under load | [Autoscaling](autoscaling.md) |
| Connect a worker over WebSocket | [Official clients](clients.md) |
| Wake consumers from Kafka lag | [Kafka connector](kafka.md) |
| Show a wake-up experience in a frontend | [Planet Saver](planet-saver.md) |

## Understand and operate SlimFaas

SlimFaas is an HTTP proxy with orchestration workers and a replicated SlimData store. It can manage Kubernetes workloads, Docker containers or native local processes. The deployment model changes; application routes stay familiar.

Read [How It Works](how-it-works.md), look up a route in the [API Reference](api-reference.md), configure the [dashboard](user-interface.md), or export telemetry with [OpenTelemetry](opentelemetry.md). For measured performance and reproducible commands, use [Benchmarks](benchmarking.md).

## Open source, built together

SlimFaas was originally created by AXA France. Contributions, reproducible bug reports and documentation improvements are welcome.

- [Source and issues](https://github.com/SlimPlanet/SlimFaas)
- [Contributing](../CONTRIBUTING.md)
- [Releases](https://github.com/SlimPlanet/SlimFaas/releases)
