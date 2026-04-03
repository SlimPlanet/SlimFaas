export interface PodStatus {
  name: string;
  status: string;
  ready: boolean;
  ip: string;
}

export interface ResourcesConfiguration {
  cpuRequest: string | null;
  cpuLimit: string | null;
  memoryRequest: string | null;
  memoryLimit: string | null;
}

export interface ScaleDownTimeout {
  time: string;
  value: number;
}

export interface DefaultSchedule {
  wakeUp: string[];
  scaleDownTimeout: ScaleDownTimeout[];
}

export interface ScheduleConfig {
  timeZoneID: string;
  default: DefaultSchedule | null;
}

export interface SubscribeEvent {
  name: string;
  visibility: string;
}

export interface PathVisibility {
  path: string;
  visibility: string;
}

export interface FunctionStatusDetailed {
  name: string;
  numberReady: number;
  numberRequested: number;
  podType: string;
  visibility: string;
  replicasMin: number;
  replicasAtStart: number;
  timeoutSecondBeforeSetReplicasMin: number;
  numberParallelRequest: number;
  numberParallelRequestPerPod: number;
  resources: ResourcesConfiguration | null;
  schedule: ScheduleConfig | null;
  subscribeEvents: SubscribeEvent[] | null;
  pathsStartWithVisibility: PathVisibility[] | null;
  dependsOn: string[] | null;
  pods: PodStatus[];
}

// ---- Jobs ----

export interface CreateJobResources {
  requests: Record<string, string> | null;
  limits: Record<string, string> | null;
}

export interface RunningJobStatus {
  name: string;
  status: string;
  elementId: string;
  inQueueTimestamp: number;
  startTimestamp: number;
}

export interface ScheduledJobInfo {
  id: string;
  schedule: string;
  image: string;
  nextExecutionTimestamp: number | null;
  resources: CreateJobResources | null;
  dependsOn: string[] | null;
}

export interface JobConfigurationStatus {
  name: string;
  visibility: string;
  image: string;
  imagesWhitelist: string[];
  numberParallelJob: number;
  resources: CreateJobResources | null;
  dependsOn: string[] | null;
  schedules: ScheduledJobInfo[];
  runningJobs: RunningJobStatus[];
}

