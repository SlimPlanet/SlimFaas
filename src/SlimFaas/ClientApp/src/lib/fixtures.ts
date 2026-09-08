import type { FunctionStatusDetailed, JobConfigurationStatus, NetworkActivityEvent } from '../types.ts';

// Synthetic opaque tokens for visual fixtures; production tokens are keyed on the server.
export const fixtureAddressId = (index: number) => `ip_${index.toString(16).padStart(64, '0')}`;

export function makeFixtures(replicas = 6, executions = 4) {
  const functions: FunctionStatusDetailed[] = [{
    Name: 'fibonacci1', NumberReady: replicas, NumberRequested: replicas, PodType: 'Deployment', Visibility: 'Public', Trust: 'Trusted',
    ReplicasMin: 0, ReplicasAtStart: 1, TimeoutSecondBeforeSetReplicasMin: 10, NumberParallelRequest: 10000, NumberParallelRequestPerPod: 10,
    Resources: { CpuRequest: '100m', CpuLimit: '500m', MemoryRequest: '64Mi', MemoryLimit: '256Mi' },
    Schedule: null, Scale: null, Retry: null, SubscribeEvents: [], PathsStartWithVisibility: [], DependsOn: [],
    Pods: Array.from({ length: replicas }, (_, i) => ({ Name: `fibonacci1-${String(i).padStart(5, '0')}`, Ip: fixtureAddressId(i + 1), Status: 'Running', Ready: true })),
  }];
  const jobs: JobConfigurationStatus[] = [{
    Name: 'daily-report', Image: 'ghcr.io/example/report:1.0', Visibility: 'Private', ImagesWhitelist: [], NumberParallelJob: 10000,
    Resources: null, DependsOn: ['fibonacci1'], Schedules: [],
    RunningJobs: Array.from({ length: executions }, (_, i) => ({ Name: `daily-report-slimfaas-job-${String(i).padStart(5, '0')}`, Status: 'Running', ElementId: `execution-${i}`, InQueueTimestamp: 1788868800000, StartTimestamp: 1788868801000 })),
  }];
  return { functions, jobs, queues: [{ Name: 'fibonacci1', Length: 12 }], functionsWithQueueActivity: new Set(['fibonacci1']), slimFaasReplicas: 3,
    slimFaasNodes: [0, 1, 2].map(i => ({ Name: `slimfaas-${i}`, Status: 'Running' })) };
}
export function fixtureEvents(start: number, count: number, now = Date.now()): NetworkActivityEvent[] {
  return Array.from({ length: count }, (_, i) => {
    const n = start + i;
    const run = String(n % 10000).padStart(5, '0');
    return { Id: `fixture-${n}`, Type: ['request_in', 'enqueue', 'dequeue', 'request_out'][n % 4], Source: 'daily-report', Target: n % 4 === 0 ? 'slimfaas' : 'fibonacci1',
      QueueName: n % 4 === 1 || n % 4 === 2 ? 'fibonacci1' : null, TimestampMs: now, NodeId: `slimfaas-${n % 3}`,
      SourcePod: `daily-report-slimfaas-job-${run}`, TargetPod: `fibonacci1-${run}` };
  });
}
