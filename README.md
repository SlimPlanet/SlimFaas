
<div align="center">
  <img src="https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/SlimFaas.png?raw=true" alt="SlimFaas" />
</div>

<div align="center">
  <h1>
    <a href="https://slimfaas.dev">Go to SlimFaas website</a>
  </h1>
</div>

# SlimFaas: The Slimmest and Simplest Function-as-a-Service [![Continuous Integration](https://github.com/SlimPlanet/SlimFaas/actions/workflows/slimfaas-ci.yaml/badge.svg)](https://github.com/SlimPlanet/SlimFaas/actions/workflows/slimfaas-ci.yaml) [![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=alert_status)](https://sonarcloud.io/dashboard?id=SlimPlanet_SlimFaas) [![Reliability](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=reliability_rating)](https://sonarcloud.io/component_measures?id=SlimPlanet_SlimFaas&metric=reliability_rating) [![Security](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=security_rating)](https://sonarcloud.io/component_measures?id=SlimPlanet_SlimFaas&metric=security_rating) [![Code Coverage](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=coverage)](https://sonarcloud.io/component_measures?id=SlimPlanet_SlimFaas&metric=Coverage) [![Docker SlimFaas](https://img.shields.io/docker/pulls/axaguildev/slimfaas.svg?label=docker+pull+slimfaas)](https://hub.docker.com/r/axaguildev/slimfaas/builds) [![Docker Image Size](https://img.shields.io/docker/image-size/axaguildev/slimfaas?label=image+size+slimfaas)](https://hub.docker.com/r/axaguildev/slimfaas/builds) [![Docker Image Version](https://img.shields.io/docker/v/axaguildev/slimfaas?sort=semver&label=latest+version+slimfaas)](https://hub.docker.com/r/axaguildev/slimfaas/builds) [![Artifact Hub](https://img.shields.io/endpoint?url=https://artifacthub.io/badge/repository/slimfaas)](https://artifacthub.io/packages/search?repo=slimfaas) [![Docker SlimFaas MCP](https://img.shields.io/docker/pulls/axaguildev/slimfaas-mcp.svg?label=docker+pull+slimfaas-mcp)](https://hub.docker.com/r/axaguildev/slimfaas-mcp/builds) [![Docker Image Size](https://img.shields.io/docker/image-size/axaguildev/slimfaas-mcp?label=image+size+slimfaas-mcp)](https://hub.docker.com/r/axaguildev/slimfaas-mcp/builds) [![Docker Image Version](https://img.shields.io/docker/v/axaguildev/slimfaas-mcp?sort=semver&label=latest+version+slimfaas-mcp)](https://hub.docker.com/r/axaguildev/slimfaas-mcp/builds) [![Artifact Hub](https://img.shields.io/endpoint?url=https://artifacthub.io/badge/repository/slimfaas-mcp)](https://artifacthub.io/packages/search?repo=slimfaas-mcp) [![FOSSA Status](https://app.fossa.com/api/projects/git%2Bgithub.com%2FSlimPlanet%2FSlimFaas.svg?type=shield&issueType=license)](https://app.fossa.com/projects/git%2Bgithub.com%2FSlimPlanet%2FSlimFaas?ref=badge_shield&issueType=license) [![FOSSA Status](https://app.fossa.com/api/projects/git%2Bgithub.com%2FSlimPlanet%2FSlimFaas.svg?type=shield&issueType=security)](https://app.fossa.com/projects/git%2Bgithub.com%2FSlimPlanet%2FSlimFaas?ref=badge_shield&issueType=security) [![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/SlimPlanet/SlimFaas/badge)](https://scorecard.dev/viewer/?uri=github.com/SlimPlanet/SlimFaas)

SlimFaas is a lightweight, plug-and-play Function-as-a-Service (FaaS) platform for Kubernetes (and beyond).
It’s designed to be **fast**, **simple**, and **extremely slim**—making it easy to deploy and manage serverless
functions with minimal overhead.

> **Looking for MCP integration?** Check out **[SlimFaas MCP](https://slimfaas.dev/mcp)** -the companion runtime that converts *any* OpenAPI definition into MCP‑ready tools on the fly.

## Why Use SlimFaas?

- **Scale**
    - Scale to zero after a period of inactivity.
    - Scale from zero to any number of replicas on demand (supporting standard HTTP triggers).
    - Compatible with Horizontal Pod Autoscalers (HPA), KEDA, and Prometheus metrics.
    - (Coming soon) SlimFaas-integrated autonomous scale-up.

- **Synchronous and Asynchronous Functions**
    - Simple HTTP endpoints for both sync and async calls.
    - Async mode lets you limit the number of parallel requests and configure retry patterns.

- **Jobs**
    - Run one-off jobs via HTTP calls, with configurable concurrency and visibility (public/private).

- **Private/Public Functions and Jobs**
    - Keep internal functions private, accessible only from within the cluster or via trusted pods.

- **Publish/Subscribe Internal Events**
    - Synchronously send events to every replica of selected functions (with no extra event-bus dependency).

- **“Mind Changer” (Status & Wake-up API)**
    - A built-in REST API to monitor your functions and wake them up on demand.

- **Plug and Play**
    - Deploy SlimFaas as a standard pod/StatefulSet with minimal configuration.
    - Just add annotations to your existing pods to integrate them into SlimFaas scaling logic.

- **Slim & Fast**
    - Written in .NET with a focus on performance and minimal resource usage.

<div align="center">
  <img src="https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/slim-faas-ram-cpu.png?raw=true" alt="SlimFaas CPU RAM" />
</div>


## Ready to Get Started?

Check out:

- [Get Started](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/get-started.md) – Learn how to deploy SlimFaas on Kubernetes or Docker Compose.
- [Functions](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/functions.md) – See how to call functions synchronously or asynchronously.
- [Events](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/events.md) – Explore how to use internal synchronous publish/subscribe events.
- [Jobs](https://github.com/SlimPlanet/blob/main/SlimFaas/documentation/jobs.md) – Learn how to define and run one-off jobs.
- [How It Works](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/how-it-works.md) – Dive into SlimFaas’s architecture and design.
- [Planet Saver](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/planet-saver.md) – See how to start and monitor replicas from a JavaScript frontend.
- [MCP](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/mcp.md) – Discover how to convert *any* OpenAPI definition into MCP‑ready tools on the fly.

We hope SlimFaas helps you streamline serverless development!

---

### Community & Governance

- **CNCF Project**
  SlimFaas is proud to be part of the [Cloud Native Computing Foundation (CNCF) landscape](https://landscape.cncf.io).

  <div align="center">
    <img alt="CNCF logo" src="https://www.cncf.io/wp-content/uploads/2022/07/cncf-stacked-color-bg.svg" width="200"/>
</div>

- **Community Meeting**
  Join us through our [Community Meeting Calendar](https://calendar.google.com/calendar/embed?src=be1dd72d18650490580a7d5d96a45a6eebe0fc4c9fe8adce630754cbb6121cca%40group.calendar.google.com&ctz=Europe%2FParis)
    - [ICS file](https://calendar.google.com/calendar/ical/be1dd72d18650490580a7d5d96a45a6eebe0fc4c9fe8adce630754cbb6121cca%40group.calendar.google.com/public/basic.ics)

- **Slack Channel**
  Join our channel on the [CNCF Slack](https://cloud-native.slack.com/archives/C08CRC77VDE) to connect with other SlimFaas users.

- **Code of Conduct**
  SlimFaas follows the [CNCF Code of Conduct](https://github.com/cncf/foundation/blob/main/code-of-conduct.md).

Enjoy SlimFaas!


## Adopters

List of organizations using this project in production or at stages of testing.

<div align="center">
  <img src="https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/adopters_logo/AXA.png?raw=true" alt="AXA" />
</div>

---
Add your logo via a pull request:
- Logo must be at PNG format, `100 px width, and 100 px height`.
- Add your logo to the `documentation/adopters` folder.
