
![SlimFaas.png](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/SlimFaas.png?raw=true)

# SlimFaas: The Slimmest and Simplest Function-as-a-Service [![Continuous Integration](https://github.com/SlimPlanet/SlimFaas/actions/workflows/slimfaas-ci.yaml/badge.svg)](https://github.com/SlimPlanet/SlimFaas/actions/workflows/slimfaas-ci.yaml) [![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=alert_status)](https://sonarcloud.io/dashboard?id=SlimPlanet_SlimFaas) [![Reliability](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=reliability_rating)](https://sonarcloud.io/component_measures?id=SlimPlanet_SlimFaas&metric=reliability_rating) [![Security](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=security_rating)](https://sonarcloud.io/component_measures?id=SlimPlanet_SlimFaas&metric=security_rating) [![Code Coverage](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=coverage)](https://sonarcloud.io/component_measures?id=SlimPlanet_SlimFaas&metric=Coverage) [![Docker SlimFaas](https://img.shields.io/docker/pulls/axaguildev/slimfaas.svg?label=docker+pull+slimfaas)](https://hub.docker.com/r/axaguildev/slimfaas/builds) [![Docker Image Size](https://img.shields.io/docker/image-size/axaguildev/slimfaas?label=image+size+slimfaas)](https://hub.docker.com/r/axaguildev/slimfaas/builds) [![Docker Image Version](https://img.shields.io/docker/v/axaguildev/slimfaas?sort=semver&label=latest+version+slimfaas)](https://hub.docker.com/r/axaguildev/slimfaas/builds)  [![FOSSA Status](https://app.fossa.com/api/projects/git%2Bgithub.com%2FSlimPlanet%2FSlimFaas.svg?type=shield&issueType=license)](https://app.fossa.com/projects/git%2Bgithub.com%2FSlimPlanet%2FSlimFaas?ref=badge_shield&issueType=license) [![FOSSA Status](https://app.fossa.com/api/projects/git%2Bgithub.com%2FSlimPlanet%2FSlimFaas.svg?type=shield&issueType=security)](https://app.fossa.com/projects/git%2Bgithub.com%2FSlimPlanet%2FSlimFaas?ref=badge_shield&issueType=security)


SlimFaas is a lightweight, plug-and-play Function-as-a-Service (FaaS) platform for Kubernetes (and beyond).
It’s designed to be **fast**, **simple**, and **extremely slim**—making it easy to deploy and manage serverless
functions with minimal overhead.

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

![slim-faas-ram-cpu.png](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/slim-faas-ram-cpu.png?raw=true)

## Ready to Get Started?

Check out:

- [Get Started](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/get-started.md) – Learn how to deploy SlimFaas on Kubernetes or Docker Compose.
- [Functions](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/functions.md) – See how to call functions synchronously or asynchronously.
- [Events](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/events.md) – Explore how to use internal synchronous publish/subscribe events.
- [Jobs](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/jobs.md) – Learn how to define and run one-off jobs.
- [Planet Saver](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/planet-saver.md) – See how to start and monitor replicas from a JavaScript frontend.
- [How It Works](https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/how-it-works.md) – Dive into SlimFaas’s architecture and design.

We hope SlimFaas helps you streamline serverless development!

---

### Community & Governance

- **CNCF Project**
  SlimFaas is proud to be part of the [Cloud Native Computing Foundation (CNCF) landscape](https://landscape.cncf.io).

  <img alt="CNCF logo" src="https://www.cncf.io/wp-content/uploads/2022/07/cncf-stacked-color-bg.svg" width="200"/>

- **Slack Channel**
  Join our channel on the [CNCF Slack](https://cloud-native.slack.com/archives/C08CRC77VDE) to connect with other SlimFaas users.

- **Code of Conduct**
  SlimFaas follows the [CNCF Code of Conduct](https://github.com/cncf/foundation/blob/main/code-of-conduct.md).

Enjoy SlimFaas!
