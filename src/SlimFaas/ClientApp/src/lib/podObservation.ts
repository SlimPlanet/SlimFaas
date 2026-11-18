import type { PodRequests, PodStatus, ScheduledJobInfo } from '../types.ts';
import { formatBytes } from './live.ts';

const localDate = new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'short', day: 'numeric',
  hour: 'numeric', minute: '2-digit', second: '2-digit', timeZoneName: 'short' });

export function nextSchedule(schedules: ScheduledJobInfo[], now = Date.now(), formatter = localDate): string | undefined {
  if (!schedules.length) return undefined;
  let next = Infinity;
  for (const { NextExecutionTimestamp: seconds } of schedules) {
    if (seconds != null && seconds * 1000 >= now && seconds * 1000 <= 8.64e15) next = Math.min(next, seconds);
  }
  return Number.isFinite(next) ? 'Next run · ' + formatter.format(next * 1000) : 'Next run unavailable';
}

const stateColors = {
  Starting: 'starting', Ready: 'ready', Running: 'ready', Terminating: 'finishing', Finishing: 'finishing',
  Crash: 'crash', Failed: 'crash', Succeeded: 'succeeded', Unknown: 'unknown',
} as const;

const states = new Set(Object.keys(stateColors));

export function lifecycle(status: string, observed?: string | null, job = false): keyof typeof stateColors {
  const state = observed || status;
  if (state === 'Running' && !job) return 'Ready';
  if (states.has(state)) return state as keyof typeof stateColors;
  if (['Pending', 'ContainerCreating', 'PodInitializing'].includes(state)) return 'Starting';
  if (['Error', 'CrashLoopBackOff', 'OOMKilled', 'ErrImagePull', 'ImagePullBackOff'].includes(state)) return 'Crash';
  return 'Unknown';
}
export const lifecycleColor = (state: keyof typeof stateColors) => stateColors[state];

export function workloadRequests(pods: PodStatus[]): PodRequests {
  const total = { CpuMillis: 0, MemoryBytes: 0, KnownPods: 0, UnknownPods: 0 };
  for (const pod of pods) {
    if (['Failed', 'Succeeded'].includes(pod.Lifecycle ?? pod.Status)) continue;
    const request = pod.Requests;
    if (request) {
      total.CpuMillis += request.CpuMillis;
      total.MemoryBytes += request.MemoryBytes;
      total.KnownPods += request.KnownPods;
      total.UnknownPods += request.UnknownPods;
    } else total.UnknownPods++;
  }
  return total;
}

export function requestLabel(requests?: PodRequests | null): string {
  if (!requests || !requests.KnownPods && requests.UnknownPods) return 'Unavailable';
  const cpu = requests.CpuMillis >= 1000 ? String(requests.CpuMillis / 1000) : requests.CpuMillis + 'm';
  return cpu + ' CPU · ' + formatBytes(requests.MemoryBytes).replace('.0 ', ' ') + ' RAM' + (requests.UnknownPods ? ' (partial)' : '');
}
