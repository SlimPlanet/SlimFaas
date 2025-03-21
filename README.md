# SlimFaas : The slimmest and simplest Function As A Service [![Continuous Integration](https://github.com/SlimPlanet/SlimFaas/actions/workflows/slimfaas-ci.yaml/badge.svg)](https://github.com/SlimPlanet/SlimFaas/actions/workflows/slimfaas-ci.yaml) [![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=alert_status)](https://sonarcloud.io/dashboard?id=SlimPlanet_SlimFaas) [![Reliability](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=reliability_rating)](https://sonarcloud.io/component_measures?id=SlimPlanet_SlimFaas&metric=reliability_rating) [![Security](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=security_rating)](https://sonarcloud.io/component_measures?id=SlimPlanet_SlimFaas&metric=security_rating) [![Code Corevage](https://sonarcloud.io/api/project_badges/measure?project=SlimPlanet_SlimFaas&metric=coverage)](https://sonarcloud.io/component_measures?id=SlimPlanet_SlimFaas&metric=Coverage)
[![Docker SlimFaas](https://img.shields.io/docker/pulls/axaguildev/slimfaas.svg?label=docker+pull+slimfaas)](https://hub.docker.com/r/axaguildev/slimfaas/builds) [![Docker Image Size](https://img.shields.io/docker/image-size/axaguildev/slimfaas?label=image+size+slimfaas)](https://hub.docker.com/r/axaguildev/slimfaas/builds)
[![Docker Image Version](https://img.shields.io/docker/v/axaguildev/slimfaas?sort=semver&label=latest+version+slimfaas)](https://hub.docker.com/r/axaguildev/slimfaas/builds)

![SlimFaas.png](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/SlimFaas.png?raw=true)

Why use SlimFaas?
- Scale
  - to 0 after a period of inactivity (work with deployment and statefulset)
  - from 0 to n at start (HTTP trigger)
  - from n to n+m : compatible with HPA (Horizontal Auto Scaler) and Keda + Prometheus via /metrics routes (SlimFaas integrated autonomous Scale Up coming soon)
- Synchronous HTTP calls
- Asynchronous HTTP calls
  - Allows you to limit the number of parallel HTTP requests for each underlying function
- Retry Pattern configurable
- Run job from HTTP calls
- Private and Public functions and jobs
  - Private functions can be accessed only by internal namespace http call from pods
- Synchronous Publish/Subscribe internal events via HTTP calls to every replicas via HTTP without any use of specific drivers/libraries (**Couple your application with SlimFaas**)
- **Mind Changer**: REST API that show the status of your functions and allow to wake up your infrastructure (**Couple your application with Slimfaas**)
  - Very useful to inform end users that your infrastructure is starting
- Plug and Play: just deploy a standard pod
  - No impact on your current kubernetes manifests: just add an annotation to the pod you want to auto-scale
- Very **Slim** and very **Fast**

![slim-faas-ram-cpu.png](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/slim-faas-ram-cpu.png?raw=true)

## Getting Started with Kubernetes

To test SlimFaas on your local machine by using kubernetes with Docker Desktop, please use these commands:

```bash
git clone https://github.com/AxaFrance/slimfaas.git
cd slimfaas
cd demo
# Create slimfaas service account and pods
kubectl apply -f deployment-slimfaas.yml
# Expose SlimFaaS service as NodePort or Ingress
kubectl apply -f slimfaas-nodeport.yml
# or
# kubectl apply -f slimfaas-ingress.yml
# Install three instances of fibonacci functions
# fibonacci1, fibonacci2 and fibonacci3
kubectl apply -f deployment-functions.yml
# Install MySql
kubectl apply -f deployment-mysql.yml
# to run Single Page webapp demo (optional) on http://localhost:8000
docker run -d -p 8000:8000 --rm axaguildev/fibonacci-webapp:latest
```

Now, you can access your pod via SlimFaas proxy:

Synchronous way:
- http://localhost:30021/function/fibonacci1/hello/guillaume => HTTP 200 (OK)
- http://localhost:30021/function/fibonacci2/hello/elodie => HTTP 200 (OK)
- http://localhost:30021/function/fibonacci3/hello/julie => HTTP 200 (OK)
- http://localhost:30021/function/fibonacci4/hello/julie => HTTP 404 (Not Found)

Asynchronous way:
- http://localhost:30021/async-function/fibonacci1/hello/guillaume => HTTP 202 (Accepted)
- http://localhost:30021/async-function/fibonacci2/hello/elodie => HTTP 202 (Accepted)
- http://localhost:30021/async-function/fibonacci3/hello/julie => HTTP 202 (Accepted)
- http://localhost:30021/async-function/fibonacci3/hello/julie => HTTP 404 (Not Found)

Just wake up function:
- http://localhost:30021/wake-function/fibonacci1 => HTTP 204 (OK - No Content)
- http://localhost:30021/wake-function/fibonacci2 => HTTP 204 (OK - No Content)
- http://localhost:30021/wake-function/fibonacci3 => HTTP 204 (OK - No Content)
- http://localhost:30021/wake-function/fibonacci4 => HTTP 204 (OK - No Content)

List all function status:
- http://localhost:30021/status-functions => [{"NumberReady":1,"numberRequested":1,PodType":"Deployment","Visibility":"Public","Name":"fibonacci1"},
                                              {"NumberReady":1,"numberRequested":1,PodType":"Deployment","Visibility":"Public","Name":"fibonacci2"},
                                              {"NumberReady":1,"numberRequested":1,PodType":"Deployment","Visibility":"Public","Name":"fibonacci3"},
                                              {"NumberReady":2,"numberRequested":2,PodType":"Deployment","Visibility":"Private","Name":"fibonacci4"}]

Send event to every function replicas (which deployment subscribe to the event name) in synchronous way:

- HTTP POST http://localhost:30021/publish-event/my-event-name {"data":"my-data"} => HTTP 204 (No Content)

Single Page WebApp demo:

- http://localhost:8000

Enjoy slimfaas!!!!

## Getting Started with docker-compose

To test slimfaas on your local machine by using kubernetes with Docker Desktop, please use these commands:

```bash
git clone https://github.com/AxaFrance/slimfaas.git
cd slimfaas
docker-compose up
```

Now, you can access your pod via SlimFaas proxy:

- http://slimfaas/function/fibonacci/hello/guillaume

Enjoy slimfaas!!!!

## Getting Started with @axa-fr/slimfaas-planet-saver

[`@axa-fr/slimfaas-planet-saver readme.md`](./src/SlimFaasPlanetSaver#README.md) : A vanilla JavaScript library to start and monitor replicas from javascript frontend.

![SlimFaasPlanetSaver.gif](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/SlimfaasPlanetSaver.gif?raw=true)

## How it works

SlimFaas act as an HTTP proxy with 2 modes:

### Synchronous HTTP call

- Synchronous http://slimfaas/function/myfunction = > HTTP response function

![sync_http_call.PNG](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/sync_http_call.PNG)

### Asynchronous HTTP call

- Asynchronous http://slimfaas/async-function/myfunction => HTTP 201
  - Tail using SlimData database included in SlimFaas pod

![async_http_call.PNG](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/async_http_call.PNG)

### Wake HTTP call

- Wake http://slimfaas/wake-function/myfunction => HTTP 200
  - Wake up a function

### Synchronous Publish HTTP call (events) to every replicas

To publish the message to every replicas in "Ready" state of the function

- HTTP POST http://slimfaas/publish-event/my-event-name {"data":"my-event-data"}

![publish_sync_call.png](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/publish_sync_call.png)

## How to install

1. Add SlimFaas StatefulSet to your kubernetes cluster
2. Add SlimFaas annotations to your pods
3. Have fun!

sample-deployment.yaml
````yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: fibonacci1
  namespace: slimfaas-demo
spec:
  selector:
    matchLabels:
      app: fibonacci1
  template:
    metadata:
      labels:
        app: fibonacci1
      annotations:
        # Just add SlimFaas annotation to your pods and that's it !
        SlimFaas/Function: "true"
        SlimFaas/ReplicasMin: "0"
        SlimFaas/ReplicasAtStart: "1"
        SlimFaas/DependsOn: "mysql,fibonacci2" # comma separated list of deployment or statefulset names
        SlimFaas/TimeoutSecondBeforeSetReplicasMin: "300"
        SlimFaas/NumberParallelRequest : "10"
        SlimFaas/Schedule: |
            {"TimeZoneID":"Europe/Paris","Default":{"WakeUp":["07:00"],"ScaleDownTimeout":[{"Time":"07:00","Value":3600},{"Time":"21:00","Value":60}]}}
        SlimFaas/SubscribeEvents: "Public:my-event-name1,Private:my-event-name2,my-event-name3" # comma separated list of event names
        SlimFaas/DefaultVisibility: "Public" # Public or Private (private can be accessed only by internal namespace https call from pods)
        SlimFaas/DefaultTrusted: "Trustes" # Public or Private (private can be accessed only by internal namespace https call from pods)
        SlimFaas/UrlsPathStartWithVisibility: "Private:/mypath/subPath,Private:/mysecondpath" # Public or Private (private can be accessed only by internal namespace https call from pods)
    spec:
      serviceAccountName: default
      containers:
        - name: fibonacci1
          image: docker.io/axaguildev/fibonacci:latest
          livenessProbe:
              httpGet:
                  path: /health
                  port: 5000
              initialDelaySeconds: 5
              periodSeconds: 5
              timeoutSeconds: 5
          resources:
            limits:
              memory: "96Mi"
              cpu: "50m"
            requests:
              memory: "96Mi"
              cpu: "10m"
          ports:
            - containerPort: 50004
---
apiVersion: v1
kind: Service
metadata:
    name: fibonacci1
    namespace: slimfaas-demo
spec:
    selector:
        app: fibonacci1
    ports:
        - port: 5000
---
apiVersion: apps/v1
kind: StatefulSet
metadata:
  name: slimfaas
  namespace: slimfaas-demo
spec:
  replicas: 3
  podManagementPolicy: Parallel
  serviceName: slimfaas
  selector:
    matchLabels:
      app: slimfaas
  template:
    metadata:
      labels:
        app: slimfaas
    spec:
      volumes:
        - name: slimfaas-volume
          emptyDir:
            sizeLimit: 10Mi
      serviceAccountName: admin # Use a service account with admin role
      containers:
        - name: slimfaas
          image: docker.io/axaguildev/slimfaas:latest
          livenessProbe:
            httpGet:
              path: /health
              port: 5000
            initialDelaySeconds: 3
            periodSeconds: 10
            timeoutSeconds: 8
            terminationGracePeriodSeconds: 30
          env:
             - name: SLIMFAAS_PORTS
               value: "5000" # can be like "5000,6000,7000" if you want to expose more ports
             - name: NAMESPACE
               value: "default"
             - name: SLIMDATA_DIRECTORY
               value: "/database"
            #- name: BASE_FUNCTION_URL
            #  value: "http://{function_name}.{namespace}.svc.cluster.local:5000"
            #  value: "http://{pod_id}:{pod_port}"
            #- name: BASE_FUNCTION_POD_URL # require for publish route
            #  value: "http://{pod_ip}:{pod_port}"
            #- name: BASE_SLIMDATA_URL
            #  value: "http://{pod_name}.slimfaas.{namespace}.svc.cluster.local:3262/"  # Don't expose this port, it can also be like "http://{pod_ip}:3262/" but if you can use DNS it's better
            # If you want to send event to an url which is not a SlimFaas function, you can use this env variable
            # use comma to separate event name and url, use => to separate event name and destination url.
            # urls are separated by ;
            #- name: SLIMFAAS_SUBSCRIBE_EVENTS
            #  value: "my-event-name1=>http://localhost:5002;http://localhost:5003,my-event-name2=>http://localhost:5002"
            # If you want to use just one pod for testing purpose, you can use this env variable
            #- name: SLIMDATA_CONFIGURATION
            #  value: |
            #      {"coldStart":"true"}
            # If you are not on kubernetes for example docker-compose, you can use this env variable, but you will lose auto-scale
            #- name: MOCK_KUBERNETES_FUNCTIONS
            #  value: "{"Functions":[{"Name":"fibonacci","NumberParallelRequest":1}],"Slimfaas":[{"Name":"slimfaas-1"}]}"

            # Configure CORS allowed Origins, default is *, you can use a comma separated list example: http://localhost:3000,http://localhost:3001
            #- name: SLIMFAAS_CORS_ALLOW_ORIGIN
            # Optional, longer is the delay, less CPU and RAM is used
            #- name : HISTORY_SYNCHRONISATION_WORKER_DELAY_MILLISECONDS
            #  value : "500" # default equivalent to 0,5 seconds
            # Optional, longer is the delay, less CPU and RAM is used
            #- name : REPLICAS_SYNCHRONISATION_WORKER_DELAY_MILLISECONDS
            #  value : "2000" # default equivalent to 2 seconds
            # Optional, longer is the delay, less CPU and RAM is used
            #- name : SLIM_WORKER_DELAY_MILLISECONDS
            #  value : "50" # default equivalent to 50 milliseconds
            # Optional, longer is the delay, less CPU and RAM is used
            #- name : SCALE_REPLICAS_WORKER_DELAY_MILLISECONDS
            #  value : "1000" # default equivalent to 1 seconds
            # Optional
            # name : TIME_MAXIMUM_WAIT_FOR_AT_LEAST_ONE_POD_STARTED_FOR_SYNC_FUNCTION
            # value : "10000" # default equivalent to 10 seconds
            # Optional
            # name : POD_SCALED_UP_BY_DEFAULT_WHEN_INFRASTRUCTURE_HAS_NEVER_CALLED
            # value : "false" # default equivalent to false
            # Optional
            # name : SLIMFAAS_ALLOW_UNSECURE_SSL
            # value : "false" # default equivalent to false
            # Optional
            # name: HEALTH_WORKER_DELAY_MILLISECONDS
            # value: "1000" # default equivalent to 1 seconds
            # Optional
            # name: HEALTH_WORKER_DELAY_TO_EXIT_SECONDS
            # value: "60" # default equivalent to 10 seconds

            # name : SLIMDATA_CONFIGURATION # represent SlimData internal configuration, more documentation here: https://dotnet.github.io/dotNext/features/cluster/raft.html
            # value : | #default values
            #    {
            #      "partitioning":"false",
            #      "lowerElectionTimeout":"150",
            #      "upperElectionTimeout":"300",
            #      "requestTimeout":"00:00:00.3000000",
            #      "rpcTimeout":"00:00:00.1500000",
            #      "coldStart":"false",
            #      "requestJournal:memoryLimit":"5",
            #      "requestJournal:expiration":"00:01:00",
            #      "heartbeatThreshold":"0.5",
            #   }
            # name : SLIMFAAS_JOB_CONFIGURATION
            # value : | # represent SlimFaas internal configuration, more documentation here:
            #    {
            #          "DefaultNumberParallelRequest": 1, # Timeout in seconds
            #          "DefaultVisibility": "Private", # Retry pattern in seconds
            #          "AllowDynamicJob": false,
            #          "Jobs": {
            #              "daisy" : { "NumberParallelRequest": 1, "Visibility": "Public" }
            #          }
            #    }
          volumeMounts:
            - name: slimfaas-volume
              mountPath: /database
          resources:
            limits:
              memory: "76Mi"
              cpu: "400m"
            requests:
              memory: "76Mi"
              cpu: "250m"
          ports:
            - containerPort: 5000
            - containerPort: 3262
  # You can use this section to define a persistent volume claim
  #volumeClaimTemplates:
  #- metadata:
  #    name: slimfaas-volume
  #  spec:
  #    accessModes: [ "ReadWriteOnce" ]
  #    storageClassName: managed-csi # or any other storage class available in your cluster
  #    volumeMode: Filesystem
  #    resources:
  #      requests:
  #        storage: 10Mi
---
apiVersion: v1
kind: Service
metadata:
    name: slimfaas
    namespace: slimfaas-demo
spec:
    selector:
        app: slimfaas
    ports:
        - name: "http"
          port: 80
          targetPort: 5000
        - name: "slimdata"
          port: 3262
          targetPort: 3262
````

> [!WARNING]
> Yours **service name** must be the same as the SlimFaas **Deployment/StatefulSet name**


### SlimFaas Annotations with defaults values
- **SlimFaas/Function**: "true"
  - Activate SlimFaas on this pod, so your pod will be auto-scaled
- **SlimFaas/ReplicasMin**: "0"
  - Scale down to this value after a period of inactivity
- **SlimFaas/ReplicasAtStart**: "1"
  - Scale up to this value at start
- **SlimFaas/TimeoutSecondBeforeSetReplicasMin**: "300"
  - Scale down to SlimFaas/ReplicasMin after this period of inactivity in seconds
- **SlimFaas/NumberParallelRequest** : "10"
  - Limit the number of parallel HTTP requests for each underlying function
- **SlimFaas/DependsOn** : ""
  - Comma separated list of deployment names or statefulset names
  - Pods will be scaled up only if all pods in this list are in ready state with the minimum number of replicas superior or equal to ReplicasAtStart
  - This property is useful if you want to scale up your pods only if your database is ready for example
- **SlimFaas/SubscribeEvents** : ""
  - Comma separated list of event names to license the function to receive events. example: "Public:my-event-name1,Private:my-event-name2,my-event-name3"
  - "Public:" or "Private:" are prefix that set the event visibility, if not set, "SlimFaas/DefaultVisibility" is used
- **SlimFaas/DefaultVisibility** : "Public"
  - Public or Private (private can be accessed only by internal namespace https call from pods)
- **SlimFaas/PathsStartWithVisibility** : ""
  - Comma separated list of path prefixed by the default visibility. example: "Private:/mypath/subpath,Public:/mypath2"
  - "Public:" or "Private:" are prefix that set the path visibility, if not set, "SlimFaas/DefaultVisibility" is used
- **SlimFaas/ExcludeDeploymentsFromVisibilityPrivate** : ""
  - Comma separated list of deployment names or statefulset names
  - Message from that pods will be considered as public. It is useful if you want to exclude some pods from the private visibility, for example for a backend for frontend.
- **SlimFaas/Configuration** : json configuration default values displayed below
    - Allows you to define a configuration for your functions. For example, you can define a timeout for HTTP calls, a retry pattern for timeouts and HTTP status codes.

````bash
{
    "DefaultSync":{
        "HttpTimeout": 120, # Timeout in seconds
        "TimeoutRetries": [2,4,8] # Retry pattern in seconds
        "HttpStatusRetries": [500,502,503] # Retry only for 500,502,503 HTTP status codes
    }
    "DefaultAsync":{
        "HttpTimeout": 120, # Timeout in seconds
        "TimeoutRetries": [2,4,8] # Retry pattern in seconds
        "HttpStatusRetries": [500,502,503] # Retry only for 500,502,503 HTTP status codes
    },
    "DefaultPublish":{
        "HttpTimeout": 120, # Timeout in seconds
        "TimeoutRetries": [2,4,8] # Retry pattern in seconds
        "HttpStatusRetries": [500,502,503] # Retry only for 500,502,503 HTTP status codes
    }
}
````
- **SlimFaas/Schedule** : json configuration
  - Allows you to define a schedule for your functions. If you want to wake up your infrastructure at 07:00 or for example scale down after 60 seconds of inactivity after 07:00 and scale down after 10 seconds of inactivity after 21:00. Time zones are defined as IANA time zones. The full list is available [here](https://nodatime.org/TimeZones)


````bash
{
  "TimeZoneID":"Europe/Paris", # Time Zone ID can be found here: https://nodatime.org/TimeZones
  "Default":{
    "WakeUp":["07:00"], # Wake up your infrastructure at 07:00
    "ScaleDownTimeout":[
              {"Time":"07:00","Value":20}, # Scale down after 20 seconds of inactivity after 07:00
              {"Time":"21:00","Value":10} # Scale down after 10 seconds of inactivity after 21:00
            ]
  }
}
````
## Why SlimFaas?

We used **OpenFaas** for a long time and we love it.
But we encountered many OpenFaas issues:
- Kubernetes scripts are tightly coupled to OpenFaas syntax
- OpenFaas pro is too expensive for our projects
- OpenFaas needs to be installed on a dedicated namespace and configuration was intricate
- OpenFaas monitoring was not compatible with our monitoring solution
- It requires to configure well NATS for managing fail-over
- Queue configuration is not easy
- The aggressive removing of old images from docker.io by OpenFaas team in April 2023 got us some production issues

We would like to use **Knative** but:
- We cannot use it because of some internal constraints and security issues.

So we decided to create **SlimFaas** to have a quick and simple replacement proxy solution that can expose Prometheus metrics.
Now we have a solution not **coupled** to anything. **SlimFaas** is **simple**, **light**, **fast** and **plug and play**!

## How it works ?

Instead of creating many pods, SlimFaas use internally many workers in the same pod:

- **SlimWorker**: Manage asynchronous HTTP requests calls to underlying functions
- **SlimDataSynchronizationWorker**: Allow to make possible scale up and down SlimData Raft nodes
- **HistorySynchronisationWorker**: Manage history of HTTP requests between the pod and kubernetes
- **ReplicasSynchronizationWorker**: Manage replicas synchronization between the pod and kubernetes
- **ReplicasScaleWorker**: If master, then scale up and down kubernetes pods

**SlimData** is a simple redis like database included inside SlimFaas executable. It is based on **Raft** (https://raft.github.io/) algorithm offered by awesome dotNext library (https://github.com/dotnet/dotNext).
By default, **SlimData** use a second HTTP port 3262 to expose its API. Don't expose it and keep it internal.

SlimFaas requires at least 3 nodes in production. 2 nodes are required to keep the database in a consistent state.

![slimdata.PNG](https://github.com/AxaFrance/slimfaas/blob/main/documentation/slimdata.png?raw=true)

If you want to use just one pod for testing purpose, you can use this env variable:
- SLIMDATA_CONFIGURATION: '{"coldStart":"true"}'

This will allow to start a pod alone as a leader.
SlimFaas can to scale up and down by using classic Horizontal Pod Autoscaler (HPA).

### Build with .NET

Why .NET?
- .NET is always getting faster and faster : https://www.techempower.com/benchmarks/#section=data-r22
- ASP.NET Core allow to resolve complex use cases with few lines of codes
- .NET is always getting smaller and smaller: https://twitter.com/MStrehovsky/status/1660806238979117056?t=WPrZwi7WrIWi4tjoDUXEgg&s=19

## Videos

- French : https://www.youtube.com/watch?v=Lvd6FCuCZPI
- English: https://youtu.be/hxRfvJhWW1w?si=4LuPgHVsuEVhlhpF

## What Next?

0. Retry pattern fully controllable with dead letter queue
1. Scale up dynamically from SlimFaas in Async Mode
2. Scale up dynamically from SlimFaas in Sync Mode (Copy KEDA syntax)
3. Aggregate all swaggers from functions in one exposed by SlimFaas
4. New pod to "Wake up" from External Source (example: Kafka, etc.) using https://cloudevents.io/ as standard https://github.com/cloudevents/sdk-csharp
5. Continue Optimization

## We are a Cloud Native Computing Foundation sandbox project.
https://landscape.cncf.io

<img src="https://www.cncf.io/wp-content/uploads/2022/07/cncf-stacked-color-bg.svg" />

### SlimFaas Slack Channel

Join us on Slack:
https://cloud-native.slack.com/archives/C08CRC77VDE

### Trademark Guidelines
The Linux Foundation® (TLF) has registered trademarks and uses trademarks. For a list of TLF trademarks, see [Trademark Usage](https://www.linuxfoundation.org/legal/trademark-usage).

## Code of Conduct

SlimFaas project has adopted the [CNCF Code of Conduct](https://github.com/cncf/foundation/blob/main/code-of-conduct.md)

