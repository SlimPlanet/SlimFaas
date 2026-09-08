# Get Started with SlimFaas

Choose where your functions will run. Each guide opens the same live dashboard and leads into a shared, hands-on tour of functions, queues, events, jobs and data.

- [Get Started with **Kubernetes**](get-started-kubernetes.md)
  Deploy SlimFaas alongside your workloads. Start here to evaluate Kubernetes operations and a persistent three-node cluster.
- [Get Started **in Local**](get-started-local.md)
  Download the complete local demo and run real processes without installing .NET, Node.js, Docker or Kubernetes.
- [Get Started with **Docker Compose**](get-started-docker-compose.md)
  Run a container-based demonstration with the Docker orchestrator.

## Choose your environment

| | Kubernetes | Local | Docker Compose |
|---|---|---|---|
| You need | A cluster, kubectl, a default StorageClass | Release bundle, curl, unzip, SHA-256 tool | Docker Engine and Compose v2, or Podman with Compose |
| Functions run as | Kubernetes workloads | Native processes | Containers managed through the Docker API |
| SlimFaas nodes in this demo | 3, persistent volumes | 3, persistent local directory | 1, persistent Docker volumes |
| Dashboard | `http://127.0.0.1:30021` with port-forward | `http://127.0.0.1:30020` | `http://127.0.0.1:30021` |
| Typical use | Cluster evaluation and deployment | Application development and debugging | Container-based local evaluation |

Use one demo at a time: the local cluster's direct node ports overlap with the other demos. The local entrypoint distributes requests among real SlimFaas/Raft nodes; it is not a simulated Kubernetes cluster.

## What you will discover

After installation, keep the SlimFaas UI open while following the [Guided Tour](guided-tour.md). Send requests with cURL or the [downloadable Bruno collection](https://slimfaas.dev/downloads/slimfaas-demo.zip). Watch functions wake, traffic move through queues, and jobs appear.

The tour includes API responses and cleanup commands. It explains which effects are visible in the dashboard and which must be checked through the API. Find every route, including aliases and internal interfaces, in the [API Reference](api-reference.md).

## Bring your own application

A function is an HTTP application. On Kubernetes, add SlimFaas annotations to its workload; locally, declare its command and health check in a manifest; with Docker, use container labels. See [Functions](functions.md) for routing and visibility, [Local Mode](native-local-mode.md) for manifests and IDE routing, and [How It Works](how-it-works.md) for the architecture.

## Go further

Explore [WebSocket clients](clients.md), the [Kafka connector](kafka.md), and [Planet Saver](planet-saver.md) when your application needs them. These integrations have their own prerequisites and are not required for the introductory tour.

### Dashboard metadata visibility

The dashboard uses the SlimFaasSite visual theme and provides **Overview** plus **Live Stream → Traffic / Data**. Data metadata follows the data API's visibility policy by default. Operators may set `SlimFaas__ExposeDataMetadata=true` to expose only keys, expiry and file sizes to dashboard visitors while leaving the values private. The native local demo already exposes `/data` for its tutorial. See [the user interface](user-interface.md#data-inventory).
