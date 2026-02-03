    # SlimFaas Jobs


SlimFaas lets you run **one‑off, batch, and scheduled (cron) jobs** either on‑demand via simple HTTP calls or automatically on a cron‑like cadence. Each job definition includes guard‑rails: cap concurrency with `NumberParallelJob`, enforce per‑pod CPU & memory limits, and decide whether the endpoint is **Public** (external) or **Private** (in‑cluster). You get a powerful REST API while keeping your cluster safe from resource spikes.

---

## 1. Why Use Jobs?

* **Short‑lived or periodic tasks** — perform a specialised computation once or on a fixed cadence and then shut down.
* **Separate scaling** — jobs can have different concurrency settings from standard functions.
* **Infrastructure safety** — cap concurrent pods with `NumberParallelJob` and enforce per‑pod CPU/memory limits to protect your cluster.
* **Secure yet open API** — trigger jobs & schedules through REST, choosing **Public** visibility for external access or **Private** for in‑cluster only.
* **Dependency handling** — automatically wait for required deployments before launching.
* **Cost optimisation** — jobs and its dependencies run on‑demand and scale to zero when finished.
* **Built‑in scheduling** — declare cron schedules next to the job definition or create them on the fly with the API.

---

## 2. Defining Jobs via Configuration

You configure SlimFaas jobs by providing a JSON configuration. The most common method is to store this JSON in a
Kubernetes `ConfigMap` and pass it to SlimFaas via the `SLIMFAAS_JOBS_CONFIGURATION` environment variable.

### Example `ConfigMap` + `StatefulSet`

Below is a comprehensive example showing **both** the job configuration and how it’s referenced in the SlimFaas `StatefulSet`:

```yaml
---
apiVersion: v1
kind: Namespace
metadata:
  name: slimfaas-demo
  labels:
    name: slimfaas
---
apiVersion: v1
kind: ConfigMap
metadata:
  name: slimfaas-config
  namespace: slimfaas-demo
data:
  SlimFaas__JobsConfiguration: |
    {
      "Configurations": {
        "fibonacci": {
          "Image": "axaguildev/fibonacci-batch:latest",
          "ImagesWhitelist": [],
          "Resources": {
            "Requests": {
              "cpu": "400m",
              "memory": "400Mi"
            },
            "Limits": {
              "cpu": "400m",
              "memory": "400Mi"
            }
          },
          "DependsOn": ["fibonacci1"],
          "Environments": [],
          "BackoffLimit": 1,
          "Visibility": "Public",
          "NumberParallelJob": 2,
          "TtlSecondsAfterFinished": 36000,
          "RestartPolicy": "Never"
        }
      },
      "Schedules": {
          "fibonacci": [
            {
              "Schedule": "0 0 * * *",  # every day at midnight
              "Args": ["42"]
            },
            {
              "Schedule": "0 0 * * 0",  # every Sunday at midnight
              "Args": ["42"],
              "DependsOn": ["fibonacci2"],
            }
          ]
        }
    }
---
apiVersion: apps/v1
kind: StatefulSet
metadata:
  name: slimfaas
  namespace: slimfaas-demo
spec:
  replicas: 1
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
      serviceAccountName: slimfaas
      containers:
        - name: slimfaas
          image: axaguildev/slimfaas:latest
          livenessProbe:
            httpGet:
              path: /health
              port: 5000
            initialDelaySeconds: 3
            periodSeconds: 10
            timeoutSeconds: 8
            terminationGracePeriodSeconds: 30
          env:
            - name: SlimFaas__JobsConfiguration
              valueFrom:
                configMapKeyRef:
                  name: slimfaas-config
                  key: SlimFaas__JobsConfiguration
          resources:
            limits:
              memory: "76Mi"
              cpu: "400m"
            requests:
              memory: "76Mi"
              cpu: "250m"
          ports:
            - containerPort: 5000
              name: slimfaas
            - containerPort: 3262
              name: slimdata
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
      port: 5000
    - name: "slimdata"
      port: 3262
```

### Explanation of Each Configuration Field

In `SLIMFAAS_JOBS_CONFIGURATION`environment variable, you define jobs under the `"Configurations"` key and their schedules under the `"Schedules"` key. Here’s a breakdown of the configuration structure:

```json
{
  "Configurations": {
    "fibonacci": {
      "Image": "axaguildev/fibonacci-batch:latest",
      "ImagesWhitelist": ["axaguildev/fibonacci-batch:*"],
      "Resources": {
        "Requests": {
          "cpu": "400m",
          "memory": "400Mi"
        },
        "Limits": {
          "cpu": "400m",
          "memory": "400Mi"
        }
      },
      "DependsOn": ["fibonacci1"],
      "Environments": [],
      "BackoffLimit": 1,
      "Visibility": "Public",
      "NumberParallelJob": 2,
      "TtlSecondsAfterFinished": 36000,
      "RestartPolicy": "Never"
    }
  },
  "Schedules": {
      "fibonacci": [
          { "Schedule": "0 0 * * *", "Args": ["43"] },  # every day at midnight
          { "Schedule": "0 0 * * 0", "Args": ["43"] }   # every Sunday at midnight
      ]
  }
}
```

#### Configurations

You can define multiple jobs definition under the `"Configurations"` key. Each job has a unique name (e.g., `"fibonacci"`).
> **Warning**: These definitions are protections about actions you can do via API calls, such as triggering a job or creating a schedule. For example, we cannot ask for more resources than the ones defined in the job configuration. The same for the number of parallel jobs, the image used, etc.

| **Field**                   | **Description**                                                                                                                                                                      |
| --------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **Image**                   | The container image used to run the job (e.g., `axaguildev/fibonacci-batch:latest`).                                                                                                 |
| **ImagesWhitelist**         | Array of approved images. If left empty, only the specified **Image** is allowed.                                                                                                    |
| **Resources**               | CPU/memory *Requests* and *Limits* for the job pods. This helps ensure the job has enough resources and respects your cluster quotas.                                                |
| **DependsOn**               | A list of deployment or statefulset names that must be ready before this job can run. The job won’t start unless the listed dependencies have their minimum replicas up and running. |
| **Environments**            | Array of environment variables to inject into the job container (e.g., `[{ "name": "MY_ENV", "value": "someValue" }]`).                                                              |
| **BackoffLimit**            | How many retries are allowed if the job fails before considering it “failed.” (Equivalent to `spec.backoffLimit` in a Kubernetes Job resource.)                                      |
| **Visibility**              | Either **Public** or **Private**. If **Public**, the job can be triggered from anywhere (outside or inside the cluster). If **Private**, only from trusted pods or internal calls.   |
| **NumberParallelJob**       | How many pods can run in parallel for this job. If you set `2`, SlimFaas can spin up to 2 concurrent pods for that job.                                                              |
| **TtlSecondsAfterFinished** | Time to keep the job resources around after completion (in seconds). For example, `36000` keeps them for 10 hours, after which Kubernetes cleans them up.                            |
| **RestartPolicy**           | The policy for restarting containers in a pod. Common values: `Never` (don’t restart), `OnFailure` (restart on failure).                                                             |


#### Schedules

You can also define **cron schedules** for jobs under the `"Schedules"` key. Each schedule entry contains:

```json
{
  "Schedules": {
    "fibonacci": [
      {
        "Schedule": "0 0 * * *",  # Every day at midnight
        "Args": ["42"]
      },
      {
        "Schedule": "0 0 * * 0",  # Every Sunday at midnight
        "Args": ["42"],
        "DependsOn": ["fibonacci2"]
      }
    ]
  }
}
```
| **Field**     | **Description**                                                                                                                                                                            |
| -------------- |--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Schedule**   | A standard 5‑field cron expression (`m h dom mon dow`). See the [Kubernetes CronJob documentation](https://kubernetes.io/docs/concepts/workloads/controllers/cron-jobs/) for full details. |
| **Args**       | Arguments to pass to the job when it runs. Override default args defined in the job configuration.                                                                                         |
| **DependsOn**  | Optional list of dependencies that must be ready before the scheduled job runs. Override default dependencies defined in the job configuration.                                            |
| **Resources**   | Optional overrides for resource requests/limits, merging with the job configuration defaults.                                                                                              |
| **Environments** | Optional environment variables to inject into the job container, merging with the job configuration defaults.                                                                              |
| **BackoffLimit** | Optional override for the number of retries allowed if the job fails. Defaults to the job configuration value.                                                                             |
| **TtlSecondsAfterFinished** | Optional override for how long to keep job resources after completion. Defaults to the job configuration value.                                                                            |
| **RestartPolicy** | Optional override for the restart policy of the job pods. Defaults to the job configuration value.                                                                                         |

### Default configuration values

If you trigger a job whose name does not exist in `SLIMFAAS_JOBS_CONFIGURATION`, SlimFaas falls back to a built‑in entry called `Default`. It ships with conservative resource requests/limits (`cpu: 100m`, `memory: 100Mi`), no dependencies, and the same visibility/retry defaults. You can customise these defaults by adding or overriding the `"Default"` key in your configuration map.

---


## 3. Invoking **and Managing** Jobs

SlimFaas exposes HTTP endpoints to **trigger**, **list**, and **delete** jobs.

### 3.1 Triggering a Job

To **trigger** a job, make an HTTP request to the **job endpoint**:

```bash
POST http://<slimfaas>/job/<jobName>
```

* `<jobName>` corresponds to the name defined in `"Configurations"` (e.g., `"fibonacci"` in the example).
* `<path>` can be any arbitrary suffix you want to pass along; SlimFaas ignores it or can forward it as part of the environment or request parameters to the job container.

**Example 1** (default parameters):

```bash
curl -X POST http://localhost:30021/job/fibonacci
{
  "Args": ["42", "43"]
}
```

**Example 2** (overrides default configuration):

```bash
curl -X POST http://localhost:30021/job/fibonacci
{
  "Image": "axaguildev/fibonacci-batch:1.0.1",         # Must match ImagesWhitelist
  "Args": ["42", "43"],
  "DependsOn": ["fibonacci2"],                          # Overrides default
  "Resources": {                                         # Cannot exceed configured limits
    "Requests": { "cpu": "200m", "memory": "200Mi" },
    "Limits":   { "cpu": "200m", "memory": "200Mi" }
  },
  "Environments": [],  # Override and merge with default Environments configured
  "BackoffLimit": 1,
  "TtlSecondsAfterFinished": 100,
  "RestartPolicy": "Never"
}
```

- SlimFaas merge parameters with the default configured value in `ConfigMap`
- SlimFaas checks if the job is configured and, if so, spawns a Kubernetes Job resource in `slimfaas-demo` (or whichever namespace you deployed SlimFaas to).
- If the job is **Public**, you can call it from anywhere.
- If the job is **Private**, SlimFaas verifies that the request originates from trusted pods or is internal to the cluster.


### 3.2 Listing Jobs

To **list jobs** (queued *and* already running/finished) for a queue or job family, send a `GET` request:

```bash
GET http://<slimfaas>/job/<queueName>
```

**Example:**

```bash
curl -X GET http://localhost:30021/job/daisy
[
  { "Id": "1", "Name": "daisy-slimfaas-job-12772", "Status": "Queued",   "PositionInQueue": 1, "InQueueTimestamp": 1, "StartTimestamp": -1 },
  { "Id": "2", "Name": "daisy-slimfaas-job-12732", "Status": "Running",  "PositionInQueue": -1, , "InQueueTimestamp": 1, "StartTimestamp": 1 }
]
```

| **Field**           | **Description**                                                                                                                               |
| ------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| **Id**              | Unique identifier returned by SlimFaas when the job was triggered.                                                                            |
| **Name**            | Kubernetes job name.                                                                                                                          |
| **Status**          | Current status. Possible values:<br>`Queued`, `Pending`, `Running`, `Succeeded`, `Failed`, `ImagePullBackOff`.                                |
| **PositionInQueue** | Integer position (1‑based) if the job is still in the queue. `-1` indicates that the job has already left the queue (Pending, Running, etc.). |

### 3.3 Deleting a Job

To **delete a job** *after* it has left the queue, send a `DELETE` request with the job name and job ID:

```bash
DELETE http://<slimfaas>/job/<jobName>/{id}
```

| **Response**      | **Meaning**                                          |
|-------------------| ---------------------------------------------------- |
| **200 OK**        | Job resources and logs were successfully cleaned up. |
| **404 Not Found** | No job with the provided ID exists.                  |


---

## 4. Visibility: Public vs. Private

As with functions, you can mark jobs as **Public** or **Private**:

- **Public**: Accessible from outside the cluster (e.g., direct HTTP calls).
- **Private**: Accessible only from within the cluster or trusted pods.

Use the `Visibility` field in the job configuration:

```json
{
  "Visibility": "Public"
}
```

---


## 5 Managing Cron Schedules

SlimFaas ships with a companion API to create, list, and delete cron schedules at runtime.
The routes live under `/job‑schedules` and are **Private by default** (cluster‑internal) unless the corresponding job is explicitly marked `Public`.

| **Verb & Path**                        | **Description**                                 |
| -------------------------------------- | ----------------------------------------------- |
| `POST /job-schedules/<jobName>`        | Add a new cron schedule for `<jobName>`.        |
| `GET  /job-schedules/<jobName>`        | List every schedule configured for `<jobName>`. |
| `DELETE /job-schedules/<jobName>/{id}` | Delete schedule *Id* from `<jobName>`.          |

Request body for **`POST`**

```json
{
  "Schedule": "0 0 * * *",           // cron expression (required)
  "Args": ["42", "43"],             // optional
  "Image": "axaguildev/fibonacci-batch:1.0.1",  // optional — must be in ImagesWhitelist
  "DependsOn": ["fibonacci2"],
  "Resources": {                       // optional overrides
    "Requests": {"cpu": "200m", "memory": "200Mi"},
    "Limits":   {"cpu": "200m", "memory": "200Mi"}
  },
  "Environments": [],
  "BackoffLimit": 1,
  "TtlSecondsAfterFinished": 100,
  "RestartPolicy": "Never"
}
```

Only `Schedule` is mandatory. Any additional keys follow the same override/merge rules as a one‑off job trigger.

**Examples**

```bash
# Run Fibonacci once a day at midnight
curl -X POST http://<slimfaas>/job-schedules/fibonacci \
     -d '{"Schedule":"0 0 * * *","Args":["42"]}'

# Add a weekly run on Sundays at 00:00
curl -X POST http://<slimfaas>/job-schedules/fibonacci \
     -d '{"Schedule":"0 0 * * 0","Args":["42"]}'

# List configured schedules
curl -X GET  http://<slimfaas>/job-schedules/fibonacci
[
  {"Id":"0","Name":"fibonacci","Schedule":"0 0 * * *","Args":["42"]},
  {"Id":"1","Name":"fibonacci","Schedule":"0 0 * * 0","Args":["42"]}
]

# Delete the first schedule (Id 0)
curl -X DELETE http://<slimfaas>/job-schedules/fibonacci/0
```

> **Behaviour**
> At the scheduled time, SlimFaas triggers the job exactly as if you had called `POST /job/<jobName>` with the provided overrides. Dependency checks, visibility rules, and concurrency limits all apply identically.

---

## 6. Visibility: Public vs. Private

As with functions, you can mark jobs **and their schedules** as **Public** or **Private**.

* **Public** — job triggers (manual *or* scheduled) can originate from outside the cluster.
* **Private** — only trusted pods or in‑cluster callers may trigger or create schedules.

Set the `Visibility` field inside `Configurations`.

---

## 7. Concurrency and Scaling

* `NumberParallelJob` controls simultaneous pods in a single job execution.
* **BackoffLimit** controls total retries.
* SlimFaas manages creation, dependency checks, and honouring cron schedules without exceeding your limits.

---

## 8. Summary

* **One‑off & batch jobs** — `POST /job/<jobName>`.
* **Cron (scheduled) jobs** — attach them inside `SLIMFAAS_JOBS_CONFIGURATION > Schedules` **or** manage at runtime via `/job‑schedules` endpoints.
* **List** jobs: `GET /job/<queueName>`; **list** schedules: `GET /job‑schedules/<jobName>`.
* **Delete** a (running, waiting, finished) job: `DELETE /job/<jobName>/{id}`; **delete** a schedule: `DELETE /job‑schedules/<jobName>/{id}`.
* Visibility, overrides, resource limits, dependency checks, retries, and TTL behave **identically** for manual and scheduled executions.

Use **SlimFaas Jobs & Cron Schedules** to handle asynchronous, on‑demand, or recurring workloads with minimal operational overhead. Happy automating! 🚀
