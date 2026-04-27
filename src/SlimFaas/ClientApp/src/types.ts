export interface PodStatus {
  Name: string;
  Status: string;
  Ready: boolean;
  Ip: string;
}

export interface ResourcesConfiguration {
  CpuRequest: string | null;
  CpuLimit: string | null;
  MemoryRequest: string | null;
  MemoryLimit: string | null;
}

export interface ScaleDownTimeout {
  Time: string;
  Value: number;
}

export interface DefaultSchedule {
  WakeUp: string[];
  ScaleDownTimeout: ScaleDownTimeout[];
}

export interface ScheduleConfig {
  TimeZoneID: string;
  Default: DefaultSchedule | null;
}

export interface SubscribeEvent {
  Name: string;
  Visibility: string | number;
}

export interface PathVisibility {
  Path: string;
  Visibility: string;
}

export interface ScaleTrigger {
  MetricType: string;
  MetricName: string;
  Query: string;
  Threshold: number;
}

export interface ScalePolicy {
  Type: string;
  Value: number;
  PeriodSeconds: number;
}

export interface ScaleDirectionBehavior {
  StabilizationWindowSeconds: number;
  Policies: ScalePolicy[];
}

export interface ScaleBehavior {
  ScaleUp: ScaleDirectionBehavior;
  ScaleDown: ScaleDirectionBehavior;
}

export interface ScaleConfig {
  ReplicaMax: number | null;
  Triggers: ScaleTrigger[];
  Behavior: ScaleBehavior;
}

export interface FunctionStatusDetailed {
  Name: string;
  NumberReady: number;
  NumberRequested: number;
  PodType: string;
  Visibility: string;
  Trust: string;
  ReplicasMin: number;
  ReplicasAtStart: number;
  TimeoutSecondBeforeSetReplicasMin: number;
  NumberParallelRequest: number;
  NumberParallelRequestPerPod: number;
  Resources: ResourcesConfiguration | null;
  Schedule: ScheduleConfig | null;
  Scale: ScaleConfig | null;
  SubscribeEvents: SubscribeEvent[] | null;
  PathsStartWithVisibility: PathVisibility[] | null;
  DependsOn: string[] | null;
  Pods: PodStatus[] | null;
}

// ---- Jobs ----

// ---- Network Activity / Stream ----

export interface NetworkActivityEvent {
  Id: string;
  Type: string; // "request_in", "enqueue", "dequeue", "request_out", "response", "event_publish", "request_waiting", "request_started", "request_end"
  Source: string;
  Target: string;
  QueueName: string | null;
  TimestampMs: number;
  NodeId: string;
  SourcePod: string | null;  // source pod name or IP (e.g. the caller pod)
  TargetPod: string | null;  // target pod name or IP (e.g. the downstream pod receiving the request)
  CorrelationId?: string | null; // shared id used to pair related start/end events
}

export interface QueueInfo {
  Name: string;
  Length: number;
}

export interface SlimFaasNodeInfo {
  Name: string;
  Status: string;  // "Running", "Starting", "Pending"
}

export interface StatusStreamPayload {
  Functions: FunctionStatusDetailed[];
  Queues: QueueInfo[];
  Jobs: JobConfigurationStatus[];
  RecentActivity: NetworkActivityEvent[];
  SlimFaasReplicas: number;
  SlimFaasNodes: SlimFaasNodeInfo[] | null;
  FrontEnabled?: boolean;
  FrontMessage?: string | null;
}

// ---- Jobs ----

export interface CreateJobResources {
  Requests: Record<string, string> | null;
  Limits: Record<string, string> | null;
}

export interface RunningJobStatus {
  Name: string;
  Status: string;
  ElementId: string;
  InQueueTimestamp: number;
  StartTimestamp: number;
}

export interface ScheduledJobInfo {
  Id: string;
  Schedule: string;
  Image: string;
  NextExecutionTimestamp: number | null;
  Resources: CreateJobResources | null;
  DependsOn: string[] | null;
}

export interface JobConfigurationStatus {
  Name: string;
  Visibility: string;
  Image: string;
  ImagesWhitelist: string[];
  NumberParallelJob: number;
  Resources: CreateJobResources | null;
  DependsOn: string[] | null;
  Schedules: ScheduledJobInfo[];
  RunningJobs: RunningJobStatus[];
}
