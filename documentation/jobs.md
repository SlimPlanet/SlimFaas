# SlimFaas Jobs

SlimFaas can run **one‑off or batch jobs** triggered by HTTP calls. This is useful for tasks like data processing,
database migrations, or any operation that doesn’t fit into a standard, continuously running service.

---

## 1. Why Use Jobs?

* **Short-lived or periodic tasks**: Perform a specialized computation once and then shut down.
* **Separate scaling**: Jobs can have different concurrency than standard functions.
* **Dependency handling**: Automatically wait for certain deployments to be ready before launching.
* **Cost optimization**: Jobs run on demand and scale to zero when finished.

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
  SLIMFAAS_JOBS_CONFIGURATION: |
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
            - name: SLIMFAAS_JOBS_CONFIGURATION
              valueFrom:
                configMapKeyRef:
                  name: slimfaas-config
                  key: SLIMFAAS_JOBS_CONFIGURATION
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

In `SLIMFAAS_JOBS_CONFIGURATION`, you have one or more job entries under `"Configurations"`:

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
  }
}
```

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

## 5. Concurrency and Scaling

* `NumberParallelJob` controls how many pods the Kubernetes Job can spawn at once.
* **BackoffLimit** controls how many times the Job retries in total.
* SlimFaas internally manages job creation and concurrency, respecting your `DependsOn` conditions to ensure prerequisites are ready.

---

## 6. Cleaning Up

By default, completed Jobs and Pods remain in your cluster unless you specify a `TtlSecondsAfterFinished`. Setting this to a non‑zero value (e.g., `36000`) instructs Kubernetes to **garbage‑collect** those resources after the specified number of seconds.

The `DELETE /job/<jobName>/{id}` endpoint (see **§3.3**) lets you proactively clean up a specific job’s resources and logs once it has finished.

---

## 7. Example Workflow

1. **Deploy SlimFaas** with a `ConfigMap` containing `SLIMFAAS_JOBS_CONFIGURATION`.
2. **Confirm** the environment variable is loaded in your SlimFaas `StatefulSet` or `Deployment`.
3. **Check** that your dependencies (like `fibonacci1`) are annotated and running if you used `DependsOn`.
4. **Trigger** the job with an HTTP call:

```bash
curl -X POST http://<slimfaas>/job/fibonacci
```

5. **List** jobs in the `daisy` queue to view their current status:

```bash
curl -X GET http://<slimfaas>/job/daisy
```

6. **Delete** finished jobs to reclaim resources:

```bash
curl -X DELETE http://<slimfaas>/job/daisy/2
```

7. **Monitor** your cluster (e.g., `kubectl get jobs -n slimfaas-demo`) to see job pods starting, running, and completing.
8. **Observe** your logs or other debugging info to verify success/failure.

---

## 8. Summary

* **Jobs** in SlimFaas are powered by Kubernetes Jobs under the hood.
* They are defined via a JSON config (placed in a `ConfigMap` or other source) and referenced by `SLIMFAAS_JOBS_CONFIGURATION`.
* **Trigger** a job: `POST /job/<jobName>`.
* **List** jobs: `GET /job/<queueName>`.
* **Delete** a finished job: `DELETE /job/<jobName>/{id}` (jobs still in queue cannot be deleted yet).
* Control concurrency, environment variables, resources, TTL, dependencies, and more through your config.

Use **SlimFaas Jobs** to handle asynchronous, on‑demand, or batched workloads with minimal operational overhead. Enjoy automating your tasks!
