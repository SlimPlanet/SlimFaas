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
  Visibility: string;
}

export interface PathVisibility {
  Path: string;
  Visibility: string;
}

export interface FunctionStatusDetailed {
  Name: string;
  NumberReady: number;
  NumberRequested: number;
  PodType: string;
  Visibility: string;
  ReplicasMin: number;
  ReplicasAtStart: number;
  TimeoutSecondBeforeSetReplicasMin: number;
  NumberParallelRequest: number;
  NumberParallelRequestPerPod: number;
  Resources: ResourcesConfiguration | null;
  Schedule: ScheduleConfig | null;
  SubscribeEvents: SubscribeEvent[] | null;
  PathsStartWithVisibility: PathVisibility[] | null;
  DependsOn: string[] | null;
  Pods: PodStatus[];
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
