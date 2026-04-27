import { useEffect, useRef, useState, useCallback } from 'react';
import type { FunctionStatusDetailed, QueueInfo, NetworkActivityEvent, StatusStreamPayload, SlimFaasNodeInfo, JobConfigurationStatus } from '../types';

const COOLDOWN_MS = 3000;
const ACTIVITY_FLUSH_MS = 100;
const ACTIVITY_STATE_LIMIT = 200;
const ACTIVITY_IMMEDIATE_FLUSH_SIZE = 200;

function pick<T = unknown>(obj: unknown, pascal: string, camel: string): T | undefined {
  if (!obj || typeof obj !== 'object') return undefined;
  const rec = obj as Record<string, unknown>;
  return (rec[pascal] ?? rec[camel]) as T | undefined;
}

function asString(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback;
}

function asNumber(value: unknown, fallback = 0): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}

function asNullableNumber(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }
  if (typeof value === 'string') {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return null;
}

function asArray<T = unknown>(value: unknown): T[] {
  return Array.isArray(value) ? value as T[] : [];
}

function normalizeScaleDownTimeout(raw: unknown): { Time: string; Value: number }[] {
  return asArray(raw).map((item) => ({
    Time: asString(pick(item, 'Time', 'time')),
    Value: asNumber(pick(item, 'Value', 'value')),
  }));
}

function normalizeFunctions(raw: unknown): FunctionStatusDetailed[] {
  return asArray(raw).map((entry) => {
    const resources = pick<Record<string, unknown>>(entry, 'Resources', 'resources');
    const schedule = pick<Record<string, unknown>>(entry, 'Schedule', 'schedule');
    const scheduleDefault = (schedule?.Default ?? schedule?.default) as Record<string, unknown> | null | undefined;
    const scale = pick<Record<string, unknown>>(entry, 'Scale', 'scale');

    return {
      Name: asString(pick(entry, 'Name', 'name')),
      NumberReady: asNumber(pick(entry, 'NumberReady', 'numberReady')),
      NumberRequested: asNumber(pick(entry, 'NumberRequested', 'numberRequested')),
      PodType: asString(pick(entry, 'PodType', 'podType')),
      Visibility: asString(pick(entry, 'Visibility', 'visibility')),
      Trust: asString(pick(entry, 'Trust', 'trust')),
      ReplicasMin: asNumber(pick(entry, 'ReplicasMin', 'replicasMin')),
      ReplicasAtStart: asNumber(pick(entry, 'ReplicasAtStart', 'replicasAtStart')),
      TimeoutSecondBeforeSetReplicasMin: asNumber(pick(entry, 'TimeoutSecondBeforeSetReplicasMin', 'timeoutSecondBeforeSetReplicasMin')),
      NumberParallelRequest: asNumber(pick(entry, 'NumberParallelRequest', 'numberParallelRequest')),
      NumberParallelRequestPerPod: asNumber(pick(entry, 'NumberParallelRequestPerPod', 'numberParallelRequestPerPod')),
      Resources: resources ? {
        CpuRequest: asString(resources.CpuRequest ?? resources.cpuRequest ?? null, '') || null,
        CpuLimit: asString(resources.CpuLimit ?? resources.cpuLimit ?? null, '') || null,
        MemoryRequest: asString(resources.MemoryRequest ?? resources.memoryRequest ?? null, '') || null,
        MemoryLimit: asString(resources.MemoryLimit ?? resources.memoryLimit ?? null, '') || null,
      } : null,
      Schedule: schedule ? {
        TimeZoneID: asString(schedule.TimeZoneID ?? schedule.timeZoneID),
        Default: scheduleDefault ? {
          WakeUp: asArray<string>(scheduleDefault.WakeUp ?? scheduleDefault.wakeUp),
          ScaleDownTimeout: normalizeScaleDownTimeout(scheduleDefault.ScaleDownTimeout ?? scheduleDefault.scaleDownTimeout),
        } : null,
      } : null,
      Scale: (scale ?? null) as FunctionStatusDetailed['Scale'],
      SubscribeEvents: asArray(pick(entry, 'SubscribeEvents', 'subscribeEvents')).map((evt) => ({
        Name: asString(pick(evt, 'Name', 'name')),
        Visibility: pick(evt, 'Visibility', 'visibility') as string | number,
      })),
      PathsStartWithVisibility: asArray(pick(entry, 'PathsStartWithVisibility', 'pathsStartWithVisibility')).map((path) => ({
        Path: asString(pick(path, 'Path', 'path')),
        Visibility: asString(pick(path, 'Visibility', 'visibility')),
      })),
      DependsOn: asArray<string>(pick(entry, 'DependsOn', 'dependsOn')),
      Pods: asArray(pick(entry, 'Pods', 'pods')).map((pod) => ({
        Name: asString(pick(pod, 'Name', 'name')),
        Status: asString(pick(pod, 'Status', 'status')),
        Ready: Boolean(pick(pod, 'Ready', 'ready')),
        Ip: asString(pick(pod, 'Ip', 'ip')),
      })),
    };
  });
}

function normalizeJobs(raw: unknown): JobConfigurationStatus[] {
  return asArray(raw).map((entry) => ({
    Name: asString(pick(entry, 'Name', 'name')),
    Visibility: asString(pick(entry, 'Visibility', 'visibility')),
    Image: asString(pick(entry, 'Image', 'image')),
    ImagesWhitelist: asArray<string>(pick(entry, 'ImagesWhitelist', 'imagesWhitelist')),
    NumberParallelJob: asNumber(pick(entry, 'NumberParallelJob', 'numberParallelJob')),
    Resources: (pick(entry, 'Resources', 'resources') ?? null) as JobConfigurationStatus['Resources'],
    DependsOn: asArray<string>(pick(entry, 'DependsOn', 'dependsOn')),
    Schedules: asArray(pick(entry, 'Schedules', 'schedules')).map((s) => ({
      Id: asString(pick(s, 'Id', 'id')),
      Schedule: asString(pick(s, 'Schedule', 'schedule')),
      Image: asString(pick(s, 'Image', 'image')),
      NextExecutionTimestamp: asNullableNumber(pick(s, 'NextExecutionTimestamp', 'nextExecutionTimestamp')),
      Resources: (pick(s, 'Resources', 'resources') ?? null) as JobConfigurationStatus['Schedules'][number]['Resources'],
      DependsOn: asArray<string>(pick(s, 'DependsOn', 'dependsOn')),
    })),
    RunningJobs: asArray(pick(entry, 'RunningJobs', 'runningJobs')).map((rj) => ({
      Name: asString(pick(rj, 'Name', 'name')),
      Status: asString(pick(rj, 'Status', 'status')),
      ElementId: asString(pick(rj, 'ElementId', 'elementId')),
      InQueueTimestamp: asNumber(pick(rj, 'InQueueTimestamp', 'inQueueTimestamp')),
      StartTimestamp: asNumber(pick(rj, 'StartTimestamp', 'startTimestamp')),
    })),
  }));
}

function normalizeQueues(raw: unknown): QueueInfo[] {
  return asArray(raw).map((entry) => ({
    Name: asString(pick(entry, 'Name', 'name')),
    Length: asNumber(pick(entry, 'Length', 'length')),
  }));
}

function normalizeActivity(raw: unknown): NetworkActivityEvent[] {
  return asArray(raw).map((entry) => ({
    Id: asString(pick(entry, 'Id', 'id')),
    Type: asString(pick(entry, 'Type', 'type')),
    Source: asString(pick(entry, 'Source', 'source')),
    Target: asString(pick(entry, 'Target', 'target')),
    QueueName: asString(pick(entry, 'QueueName', 'queueName'), '') || null,
    TimestampMs: asNumber(pick(entry, 'TimestampMs', 'timestampMs')),
    NodeId: asString(pick(entry, 'NodeId', 'nodeId')),
    SourcePod: asString(pick(entry, 'SourcePod', 'sourcePod'), '') || null,
    TargetPod: asString(pick(entry, 'TargetPod', 'targetPod'), '') || null,
    CorrelationId: asString(pick(entry, 'CorrelationId', 'correlationId'), '') || null,
  }));
}

function normalizeSlimFaasNodes(raw: unknown): SlimFaasNodeInfo[] {
  return asArray(raw).map((entry) => ({
    Name: asString(pick(entry, 'Name', 'name')),
    Status: asString(pick(entry, 'Status', 'status')),
  }));
}

function normalizePayload(raw: unknown): StatusStreamPayload {
  return {
    Functions: normalizeFunctions(pick(raw, 'Functions', 'functions')),
    Queues: normalizeQueues(pick(raw, 'Queues', 'queues')),
    Jobs: normalizeJobs(pick(raw, 'Jobs', 'jobs')),
    RecentActivity: normalizeActivity(pick(raw, 'RecentActivity', 'recentActivity')),
    SlimFaasReplicas: asNumber(pick(raw, 'SlimFaasReplicas', 'slimFaasReplicas'), 1),
    SlimFaasNodes: normalizeSlimFaasNodes(pick(raw, 'SlimFaasNodes', 'slimFaasNodes')),
    FrontEnabled: pick(raw, 'FrontEnabled', 'frontEnabled') as boolean | undefined,
    FrontMessage: pick(raw, 'FrontMessage', 'frontMessage') as string | null | undefined,
  };
}

export function useStatusStream() {
  const [functions, setFunctions] = useState<FunctionStatusDetailed[]>([]);
  const [queues, setQueues] = useState<QueueInfo[]>([]);
  const [jobs, setJobs] = useState<JobConfigurationStatus[]>([]);
  const [activity, setActivity] = useState<NetworkActivityEvent[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [coolingDown, setCoolingDown] = useState<Set<string>>(new Set());
  const [wakeAllCooling, setWakeAllCooling] = useState(false);
  const [functionsWithQueueActivity, setFunctionsWithQueueActivity] = useState<Set<string>>(new Set());
  const [slimFaasReplicas, setSlimFaasReplicas] = useState(1);
  const [slimFaasNodes, setSlimFaasNodes] = useState<SlimFaasNodeInfo[]>([]);
  const [frontEnabled, setFrontEnabled] = useState(true);
  const [frontMessage, setFrontMessage] = useState<string | null>(null);

  const eventSourceRef = useRef<EventSource | null>(null);
  const reconnectTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const activityBufferRef = useRef<NetworkActivityEvent[]>([]);
  const activityFlushTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const flushActivityBuffer = useCallback(() => {
    if (activityFlushTimerRef.current) {
      clearTimeout(activityFlushTimerRef.current);
      activityFlushTimerRef.current = null;
    }

    const batch = activityBufferRef.current;
    if (batch.length === 0) return;
    activityBufferRef.current = [];

    setActivity(prev => {
      const next = [...prev, ...batch];
      return next.length > ACTIVITY_STATE_LIMIT ? next.slice(-ACTIVITY_STATE_LIMIT) : next;
    });

    const queueTargets = new Set<string>();
    for (const evt of batch) {
      if (evt.Type === 'enqueue' || evt.Type === 'dequeue') {
        queueTargets.add(evt.Target);
      }
    }

    if (queueTargets.size > 0) {
      setFunctionsWithQueueActivity(prev => {
        let changed = false;
        const next = new Set(prev);
        queueTargets.forEach(target => {
          if (!next.has(target)) {
            next.add(target);
            changed = true;
          }
        });
        return changed ? next : prev;
      });
    }
  }, []);

  const enqueueActivityBatch = useCallback((events: NetworkActivityEvent[]) => {
    if (events.length === 0) return;
    activityBufferRef.current.push(...events);

    if (activityBufferRef.current.length >= ACTIVITY_IMMEDIATE_FLUSH_SIZE) {
      flushActivityBuffer();
      return;
    }

    if (!activityFlushTimerRef.current) {
      activityFlushTimerRef.current = setTimeout(flushActivityBuffer, ACTIVITY_FLUSH_MS);
    }
  }, [flushActivityBuffer]);

  const connect = useCallback(() => {
    if (eventSourceRef.current) {
      eventSourceRef.current.close();
    }
    if (activityFlushTimerRef.current) {
      clearTimeout(activityFlushTimerRef.current);
      activityFlushTimerRef.current = null;
    }
    activityBufferRef.current = [];

    const es = new EventSource('/status-functions-stream');
    eventSourceRef.current = es;
    // Start each SSE session with live-only activity (no historical replay).
    setActivity([]);

    es.addEventListener('state', (e: MessageEvent) => {
      try {
        const payload = normalizePayload(JSON.parse(e.data));
        setFunctions(payload.Functions ?? []);
        setQueues(payload.Queues ?? []);
        setJobs(payload.Jobs ?? []);
        // Keep activity feed live-only from `activity` SSE events.
        // Do not hydrate from state snapshots to avoid replaying history.
        setSlimFaasReplicas(payload.SlimFaasReplicas ?? 1);
        setSlimFaasNodes(payload.SlimFaasNodes ?? []);
        setFrontEnabled(payload.FrontEnabled ?? true);
        setFrontMessage(payload.FrontMessage ?? null);
        setError(null);
        setLoading(false);

        // Detect queue usage from current queue lengths only (live view).
        const queueFns = new Set<string>();
        for (const q of (payload.Queues ?? [])) {
          if (q.Length > 0) {
            queueFns.add(q.Name);
          }
        }
        if (queueFns.size > 0) {
          setFunctionsWithQueueActivity(prev => {
            const next = new Set(prev);
            queueFns.forEach(name => next.add(name));
            return next.size !== prev.size ? next : prev;
          });
        }
      } catch (err) {
        console.warn('Unable to parse status stream state event.', err);
      }
    });

    es.addEventListener('activity', (e: MessageEvent) => {
      try {
        const evt = normalizeActivity([JSON.parse(e.data)])[0];
        if (!evt) return;
        enqueueActivityBatch([evt]);
      } catch (err) {
        console.warn('Unable to parse status stream activity event.', err);
      }
    });

    es.addEventListener('activity_batch', (e: MessageEvent) => {
      try {
        enqueueActivityBatch(normalizeActivity(JSON.parse(e.data)));
      } catch (err) {
        console.warn('Unable to parse status stream activity_batch event.', err);
      }
    });

    es.onerror = () => {
      setError('Stream disconnected, reconnecting...');
      es.close();
      eventSourceRef.current = null;
      // Reconnect after a short delay
      reconnectTimer.current = setTimeout(() => connect(), 3000);
    };
  }, []);

  useEffect(() => {
    connect();
    return () => {
      if (eventSourceRef.current) eventSourceRef.current.close();
      if (reconnectTimer.current) clearTimeout(reconnectTimer.current);
      if (activityFlushTimerRef.current) clearTimeout(activityFlushTimerRef.current);
    };
  }, [connect]);

  const startCooldown = useCallback((name: string) => {
    setCoolingDown(prev => new Set(prev).add(name));
    setTimeout(() => {
      setCoolingDown(prev => {
        const next = new Set(prev);
        next.delete(name);
        return next;
      });
    }, COOLDOWN_MS);
  }, []);

  const wakeUp = useCallback(async (functionName: string) => {
    if (coolingDown.has(functionName)) return;
    startCooldown(functionName);
    try {
      await fetch(`/wake-function/${functionName}`, { method: 'POST' });
    } catch (err) {
      console.warn(`Unable to wake function ${functionName}.`, err);
    }
  }, [coolingDown, startCooldown]);

  const wakeUpAll = useCallback(async () => {
    if (wakeAllCooling) return;
    setWakeAllCooling(true);
    setTimeout(() => setWakeAllCooling(false), COOLDOWN_MS);
    try {
      await fetch('/wake-functions', { method: 'POST' });
    } catch (err) {
      console.warn('Unable to wake all functions.', err);
    }
  }, [wakeAllCooling]);

  return {
    functions,
    queues,
    jobs,
    activity,
    loading,
    error,
    wakeUp,
    wakeUpAll,
    coolingDown,
    wakeAllCooling,
    functionsWithQueueActivity,
    slimFaasReplicas,
    slimFaasNodes,
    frontEnabled,
    frontMessage,
  };
}





