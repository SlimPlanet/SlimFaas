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
  resources: ResourcesConfiguration | null;
  schedule: ScheduleConfig | null;
  subscribeEvents: SubscribeEvent[] | null;
  pathsStartWithVisibility: PathVisibility[] | null;
  pods: PodStatus[];
}

