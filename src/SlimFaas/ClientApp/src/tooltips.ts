/** Tooltip descriptions for every dashboard field — sourced from SlimFaas documentation. */

// ── Functions ────────────────────────────────────────────────────────────────

export const FN = {
  name:
    'Name of the Kubernetes Deployment or StatefulSet registered as a SlimFaas function (SlimFaas/Function: "true").',

  podType:
    'Kubernetes workload type: Deployment (stateless) or StatefulSet (persistent storage).',

  visibility:
    'Default visibility of the function. Public = accessible from anywhere. Private = restricted to trusted pods within the namespace.',

  trust:
    'Trust level (SlimFaas/DefaultTrust). Trusted functions can call Private endpoints of other functions. Untrusted functions are treated as external callers.',

  pathVisibility:
    'Per-path visibility override (SlimFaas/PathsStartWithVisibility). Allows mixing Public and Private routes on the same function.',

  subscribeEvents:
    'Events this function subscribes to (SlimFaas/SubscribeEvents). When an event is published via POST /publish-event/<eventName>/<path>, every subscribed replica receives it.',

  replicas:
    'Current ready pods / requested pods for this function.',

  replicasMin:
    'Minimum replicas allowed after inactivity timeout (SlimFaas/ReplicasMin). Set to 0 for scale-to-zero.',

  replicasAtStart:
    'Number of replicas to start when the function wakes up from zero (SlimFaas/ReplicasAtStart).',

  scaleDown:
    'Inactivity timeout in seconds before scaling down to ReplicasMin (SlimFaas/TimeoutSecondBeforeSetReplicasMin).',

  parallelReq:
    'Maximum concurrent requests across all replicas (SlimFaas/NumberParallelRequest). Extra requests are queued.',

  parallelReqPerPod:
    'Maximum concurrent requests per individual pod (SlimFaas/NumberParallelRequestPerPod).',

  schedule:
    'Scheduled wake-up times (SlimFaas/Schedule). The function is automatically woken at these times each day.',

  dependsOn:
    'Functions or services that must be ready before this function can scale up (SlimFaas/DependsOn).',

  maxReplicas:
    'Upper bound for the Prometheus-based N→M autoscaler (SlimFaas/Scale → ReplicaMax).',

  triggers:
    'Prometheus metrics triggers for N→M autoscaling (SlimFaas/Scale → Triggers). Each trigger defines a metric, a threshold, and a metric type (AverageValue or Value).',

  behavior:
    'Scale-up and scale-down stabilization and rate-limiting policies (SlimFaas/Scale → Behavior).',

  cpuResources:
    'Kubernetes CPU request / limit for this pod.',

  memResources:
    'Kubernetes Memory request / limit for this pod.',

  syncUrl:
    'Synchronous call — blocks until the function responds.\nGET/POST http://<slimfaas>/function/<name>/<path>',

  asyncUrl:
    'Asynchronous call — returns 202 immediately, processed in background.\nGET/POST http://<slimfaas>/async-function/<name>/<path>',
} as const;

// ── Jobs ─────────────────────────────────────────────────────────────────────

export const JOB = {
  name:
    'Name of the Job configuration registered via SlimFaas/Job annotation.',

  visibility:
    'Visibility of the job. Public = accessible from anywhere. Private = restricted to trusted pods within the namespace.',

  image:
    'Default container image used when creating job instances.',

  whitelist:
    'Allowed images that can be requested when creating a job (SlimFaas/JobImagesWhitelist).',

  parallel:
    'Maximum number of job instances that can run simultaneously (SlimFaas/NumberParallelJob).',

  running:
    'Number of job instances currently running.',

  schedules:
    'Cron-based schedules that automatically trigger job execution (SlimFaas/Schedules).',

  dependsOn:
    'Functions or services that must be ready before this job can start.',

  cpuResources:
    'Kubernetes CPU request / limit for this job.',

  memResources:
    'Kubernetes Memory request / limit for this job.',
} as const;

