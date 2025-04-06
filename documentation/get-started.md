# Getting Started with SlimFaas

This guide covers two primary ways to start using SlimFaas:
1. **Kubernetes** (including local clusters via Docker Desktop)
2. **Docker Compose** (for quick local testing)
3. **Manual Installation** (for your own Kubernetes setup)

You can also find [advanced installation details](#manual-installation-on-kubernetes) below.

---

## 1. Kubernetes Quick Start

Below is an example workflow for running SlimFaas on Kubernetes locally (e.g., via Docker Desktop, kind or minikube).:

```bash
git clone https://github.com/AxaFrance/slimfaas.git
cd slimfaas/demo

# Deploy SlimFaas (StatefulSet) and related ServiceAccount
kubectl apply -f service-account-slimfaas.yml
kubectl apply -f deployment-slimfaas.yml

# Expose SlimFaas Service as NodePort or Ingress
kubectl apply -f slimfaas-nodeport.yml
# Alternatively:
# kubectl apply -f slimfaas-ingress.yml

# Deploy three sample Fibonacci functions
kubectl apply -f deployment-functions.yml

# Deploy MySQL (used by the Fibonacci functions)
kubectl apply -f deployment-mysql.yml

# (Optional) Run a single-page demo webapp on http://localhost:8000
docker run -d -p 8000:8000 --rm axaguildev/fibonacci-webapp:latest
```

### Test Synchronous Calls
If you used slimfaas-nodeport.yml, port 30021 might be exposed. You can call your functions via SlimFaas:

- GET http://localhost:30021/function/fibonacci1/hello/guillaume → HTTP 200 (OK)
- GET http://localhost:30021/function/fibonacci2/hello/elodie → HTTP 200 (OK)
- GET http://localhost:30021/function/fibonacci3/hello/julie → HTTP 200 (OK)
- GET http://localhost:30021/function/fibonacci4/hello/julie → HTTP 404 (Not Found)

### Test Asynchronous Calls
- GET http://localhost:30021/async-function/fibonacci1/hello/guillaume → HTTP 202 (Accepted)
- GET http://localhost:30021/async-function/fibonacci2/hello/elodie → HTTP 202 (Accepted)
- GET http://localhost:30021/async-function/fibonacci3/hello/julie → HTTP 202 (Accepted)
- GET http://localhost:30021/async-function/fibonacci4/hello/julie → HTTP 404 (Not Found)

### Wake Up a Function
- GET http://localhost:30021/wake-function/fibonacci1 → HTTP 204 (No Content)
- GET http://localhost:30021/wake-function/fibonacci2 → HTTP 204 (No Content)
- GET http://localhost:30021/wake-function/fibonacci3 → HTTP 204 (No Content)
- GET http://localhost:30021/wake-function/fibonacci4 → HTTP 204 (No Content)

### List All Functions
- GET http://localhost:30021/status-functions

```json
[
  {"NumberReady":1,"numberRequested":1,"PodType":"Deployment","Visibility":"Public","Name":"fibonacci1"},
  {"NumberReady":1,"numberRequested":1,"PodType":"Deployment","Visibility":"Public","Name":"fibonacci2"},
  {"NumberReady":1,"numberRequested":1,"PodType":"Deployment","Visibility":"Public","Name":"fibonacci3"},
  {"NumberReady":2,"numberRequested":2,"PodType":"Deployment","Visibility":"Private","Name":"fibonacci4"}
]
```

### Single Page WebApp Demo
If you ran the Fibonacci webapp container above:

Browse to http://localhost:8000

---

## 2. Docker Compose Quick Start

```bash
git clone https://github.com/AxaFrance/slimfaas.git
cd slimfaas
docker-compose up
```

When it’s ready:

- GET http://slimfaas/function/fibonacci/hello/guillaume

Enjoy SlimFaas!

---

## 3. Manual Installation on Kubernetes

You can also set up SlimFaas manually by adapting the sample manifests below. The key steps are:

1. **Deploy SlimFaas** (as a StatefulSet or Deployment).
2. **Expose SlimFaas** on an internal or external route (NodePort, Ingress, etc.).
3. **Annotate** your function pods/Deployments with SlimFaas annotations to enable auto-scaling and routing.

Example partial YAML (from *service-account-slimfaas.yml*):

```yaml
apiVersion: v1
kind: ServiceAccount
metadata:
    name: slimfaas
    namespace: slimfaas-demo
---
apiVersion: rbac.authorization.k8s.io/v1
kind: Role
metadata:
    name: deployment-statefulset-manager
    namespace: slimfaas-demo
rules:
    # On ajoute ici le droit de lister/voir les pods dans ce namespace
    - apiGroups: [""]
      resources: ["pods"]
      verbs: ["get", "list", "watch"]
    - apiGroups: ["apps"]
      resources: ["deployments", "statefulsets"]
      verbs: ["get", "list", "watch", "create", "update", "patch", "delete"]
    - apiGroups: ["apps"]
      resources: ["deployments/scale", "statefulsets/scale"]
      verbs: ["get", "update", "patch"]
    - apiGroups: ["batch"]
      resources: ["jobs"]
      verbs: ["get", "list", "watch", "create", "update", "patch", "delete"]
---
apiVersion: rbac.authorization.k8s.io/v1
kind: RoleBinding
metadata:
    name: slimfaas-deployment-statefulset-manager
    namespace: slimfaas-demo
subjects:
    - kind: ServiceAccount
      name: slimfaas
      namespace: slimfaas-demo
roleRef:
    kind: Role
    name: deployment-statefulset-manager
    apiGroup: rbac.authorization.k8s.io
---
apiVersion: rbac.authorization.k8s.io/v1
kind: Role
metadata:
    name: endpoints-viewer
    namespace: slimfaas-demo
rules:
    - apiGroups: [""]
      resources: ["endpoints"]
      verbs: ["get", "list", "watch"]
---
apiVersion: rbac.authorization.k8s.io/v1
kind: RoleBinding
metadata:
    name: slimfaas-endpoints-viewer
    namespace: slimfaas-demo
subjects:
    - kind: ServiceAccount
      name: slimfaas
      namespace: slimfaas-demo
roleRef:
    kind: Role
    name: endpoints-viewer
    apiGroup: rbac.authorization.k8s.io

```

Example partial YAML (from *deployment-slimfaas.yml*):

```yaml
apiVersion: apps/v1
kind: StatefulSet
metadata:
  name: slimfaas
  namespace: slimfaas-demo
spec:
  replicas: 3
  selector:
    matchLabels:
      app: slimfaas
  serviceName: slimfaas
  template:
    metadata:
      labels:
        app: slimfaas
    spec:
      # ...
      containers:
        - name: slimfaas
          image: docker.io/axaguildev/slimfaas:latest
          ports:
            - containerPort: 5000    # SlimFaas main port
            - containerPort: 3262    # SlimData port
          # ...
```
Example annotation for a function Deployment:
```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: fibonacci1
  namespace: slimfaas-demo
spec:
  template:
    metadata:
      annotations:
        SlimFaas/Function: "true" # Enable SlimFaas
        SlimFaas/ReplicasMin: "0"
        SlimFaas/ReplicasAtStart: "1"
        SlimFaas/TimeoutSecondBeforeSetReplicasMin: "300"
        SlimFaas/NumberParallelRequest: "10"
        SlimFaas/DependsOn: "mysql,fibonacci2"
        SlimFaas/SubscribeEvents: "Public:my-event-name1,Private:my-event-name2,my-event-name3"
        SlimFaas/DefaultVisibility: "Public"
        # ...
    spec:
      containers:
        - name: fibonacci1
          image: axaguildev/fibonacci:latest
          # ...

```
For more details, see **How It Works** and the other documentation pages.
