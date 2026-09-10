# Get Started with Kubernetes

Deploy a persistent three-node SlimFaas cluster and example workloads, then explore them through the SlimFaas dashboard on your computer.

## Before you start

You need Git, kubectl, a running Kubernetes cluster, and permission to create resources in a demonstration namespace. A local cluster from kind, minikube or Docker Desktop works; the manifests require a **default StorageClass** capable of provisioning ReadWriteOnce volumes. SlimFaas requests a 2 GiB data volume and a 1 GiB backup volume per replica; the MySQL dependency also needs storage.

```bash
kubectl config current-context
kubectl get nodes
kubectl get storageclass
git clone https://github.com/SlimPlanet/SlimFaas.git
cd SlimFaas
```

All commands below run from the repository root. Check the selected context before applying the demo.

## Deploy the demo

```bash
kubectl apply -f demo/service-account-slimfaas.yml
kubectl apply -f demo/deployment-slimfaas.yml
kubectl apply -f demo/deployment-mysql.yml
kubectl apply -f demo/deployment-functions.yml
kubectl apply -f demo/deployment-cron.yaml
kubectl -n slimfaas-demo rollout status statefulset/slimfaas --timeout=300s
kubectl -n slimfaas-demo get pods,pvc
```

> **Note — Kubernetes watch:** SlimFaas keeps its view of the cluster up to date
> through Kubernetes **watch** streams on pods, deployments, statefulsets, jobs and
> cronjobs (the `watch` verb granted by `demo/service-account-slimfaas.yml` is
> required). Synchronization is event-driven: full LIST calls only run when
> something actually changed, with a periodic safety-net resync. The behavior is
> configurable under `SlimFaas:KubernetesWatch`:
>
> | Key | Default | Description |
> |---|---|---|
> | `Enabled` | `true` | Set to `false` to restore the legacy fixed-cadence polling |
> | `FunctionsResyncSeconds` | `30` | Safety-net resync for deployments/pods/statefulsets |
> | `JobsResyncSeconds` | `30` | Safety-net resync for jobs |
> | `JobsConfigurationResyncSeconds` | `60` | Safety-net resync for CronJob configurations |
> | `DebounceMilliseconds` | `300` | Event burst coalescing window |
> | `WatchTimeoutSeconds` | `60` | Watch stream rotation (server-side close) |
>
> If a watch stream cannot be established (for example the ServiceAccount lacks the
> `watch` verb, or the API server is temporarily unreachable), SlimFaas logs a warning
> once and automatically falls back to the legacy polling cadence for the affected
> resources until the stream is restored, so an outdated RBAC never slows
> synchronization down.
>
> **Services are not watched** (the RBAC does not grant it): a change that only
> touches a Service — such as re-pointing the selector of the Service in front of
> the SlimFaas StatefulSet or of a function — is only picked up by the periodic
> resync, so it can take up to `FunctionsResyncSeconds` (30 s by default) to
> propagate instead of a few hundred milliseconds. Lower that value if Service
> objects change frequently in your cluster.

The first manifest creates the namespace, ServiceAccount and RBAC. The SlimFaas manifest creates its configuration, StatefulSet and Service. Functions carry annotations for visibility, inactivity, concurrency, dependencies and scaling. `fibonacci2` depends on `fibonacci1` and MySQL; MySQL is included to demonstrate orchestration dependencies. The sample API itself does not query it.

The `fibonacci` job is configured by SlimFaas. The additional `fibonacci5` CronJob is suspended in Kubernetes and discovered by SlimFaas through its annotations. Functions can scale to zero before you finish these steps, so their absence from the pod list alone is not a startup failure.

Keep `podManagementPolicy: OrderedReady`: one healthy Raft member starts before the next. Image downloads and volume provisioning can make the initial startup take several minutes. The demo exposes data APIs publicly; review this choice when adapting it to your own environment.

## Open the dashboard

Start the port-forward in a terminal and leave it running:

```bash
kubectl -n slimfaas-demo port-forward service/slimfaas 30021:5000
```

Open **http://127.0.0.1:30021/**. In a second terminal:

```bash
export BASE_URL=http://127.0.0.1:30021
curl -i "$BASE_URL/ready"
curl -fsS "$BASE_URL/status-functions"
curl -fsS "$BASE_URL/function/fibonacci1/hello/kubernetes"
```

Expect `200 READY`, a function list and a greeting. Keep the network map visible during the next steps. Port-forward selects a pod; restart it if that pod is replaced. It is a convenient local access method, not a production ingress.

## Discover the features

Follow the [Guided Tour](guided-tour.md) and select **Kubernetes** in Bruno. The default HTTP base URL is the port-forward above. The source IP seen by SlimFaas depends on the access method; the tour explains how that can affect the private-function example.

The core tour does not require Kafka. To explore lag-based wake-up later, follow the [Kafka guide](kafka.md) and its demonstration manifests.

## Other access methods

For clusters exposing NodePort to the host:

```bash
kubectl apply -f demo/slimfaas-nodeport.yml
```

SlimFaas is exposed on node port `30021`. With kind or a remote cluster, the node address and host port mappings may differ from localhost. This Service uses `externalTrafficPolicy: Local` to preserve source IP; send requests to a node with a serving SlimFaas pod.

For Ingress, adapt `demo/slimfaas-ingress.yml` to your ingress controller and hostname, then apply it. Configure TLS and streaming support, including unbuffered SSE. WebSocket clients use the separate `5003` service port by default; see [Clients](clients.md).

## Adapt the installation

Use the demo as an executable starting point. For your own namespace, update every manifest's namespace, ServiceAccount bindings, service addresses and job environment URLs together. Keep the Raft port internal and preserve durable storage when restarting nodes.

Add `SlimFaas/Function: "true"` and the desired scaling annotations to your workload's pod-template metadata. Ensure its Service, HTTP port and health probes match the application. See [Functions](functions.md), [Autoscaling](autoscaling.md), [Jobs](jobs.md) and [How It Works](how-it-works.md) for complete configuration details.

## Troubleshooting

| Symptom | What to check |
|---|---|
| PVC stays Pending | Default StorageClass, provisioner, available capacity and PVC events. |
| Pod stays Pending or fails | `kubectl -n slimfaas-demo describe pod <pod>` and `kubectl -n slimfaas-demo get events --sort-by=.lastTimestamp`. |
| No cluster readiness | `kubectl -n slimfaas-demo logs slimfaas-0`; inspect storage, service discovery and RBAC. |
| Connection refused locally | Leave the port-forward running and check port `30021` is free. |
| Function does not wake | Inspect its annotations, dependencies and readiness probes; check SlimFaas logs. |
| Data API returns 404 | Check `Data__DefaultVisibility`, the caller's visibility and the actual stored ID. |

## Stop and remove

Stop port-forward with **Ctrl+C**. To remove only the demonstrated workloads:

```bash
kubectl delete -f demo/deployment-cron.yaml
kubectl delete -f demo/deployment-functions.yml
kubectl delete -f demo/deployment-mysql.yml
kubectl delete -f demo/deployment-slimfaas.yml
```

The last manifest also declares the demo namespace: deleting it removes the namespace and its remaining resources, including PVCs. Use this cleanup only for the dedicated `slimfaas-demo` environment. To preserve data while pausing SlimFaas, scale its StatefulSet to zero instead and keep the namespace and volumes.
