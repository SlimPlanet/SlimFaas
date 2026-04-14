import type { Meta, StoryObj } from '@storybook/react';
import FunctionTable from '../components/FunctionTable';
import type { FunctionStatusDetailed } from '../types';

// ── Données réelles issues de l'API ──────────────────────────────────────────

const FUNCTIONS_ALL_DOWN: FunctionStatusDetailed[] = [
  {
    Name: 'fibonacci-kafka-listener',
    NumberReady: 0, NumberRequested: 0,
    PodType: 'Deployment', Visibility: 'Public', Trust: 'Trusted',
    ReplicasMin: 0, ReplicasAtStart: 1,
    TimeoutSecondBeforeSetReplicasMin: 10,
    NumberParallelRequest: 10, NumberParallelRequestPerPod: 10,
    Resources: { CpuRequest: '10m', CpuLimit: '50m', MemoryRequest: '64Mi', MemoryLimit: '96Mi' },
    Schedule: { TimeZoneID: 'GB', Default: { WakeUp: [], ScaleDownTimeout: [] } },
    Scale: null,
    SubscribeEvents: [], PathsStartWithVisibility: [],
    DependsOn: ['kafka'], Pods: [],
  },
  {
    Name: 'fibonacci-kafka-producer',
    NumberReady: 0, NumberRequested: 0,
    PodType: 'Deployment', Visibility: 'Public', Trust: 'Trusted',
    ReplicasMin: 0, ReplicasAtStart: 1,
    TimeoutSecondBeforeSetReplicasMin: 300,
    NumberParallelRequest: 10, NumberParallelRequestPerPod: 10,
    Resources: { CpuRequest: '10m', CpuLimit: '50m', MemoryRequest: '48Mi', MemoryLimit: '96Mi' },
    Schedule: { TimeZoneID: 'GB', Default: { WakeUp: [], ScaleDownTimeout: [] } },
    Scale: null,
    SubscribeEvents: [], PathsStartWithVisibility: [],
    DependsOn: ['kafka', 'slimfaas-kafka'], Pods: [],
  },
  {
    Name: 'fibonacci1',
    NumberReady: 0, NumberRequested: 0,
    PodType: 'Deployment', Visibility: 'Public', Trust: 'Trusted',
    ReplicasMin: 0, ReplicasAtStart: 1,
    TimeoutSecondBeforeSetReplicasMin: 10,
    NumberParallelRequest: 40, NumberParallelRequestPerPod: 1,
    Resources: { CpuRequest: '10m', CpuLimit: '50m', MemoryRequest: '96Mi', MemoryLimit: '96Mi' },
    Schedule: { TimeZoneID: 'GB', Default: { WakeUp: [], ScaleDownTimeout: [] } },
    Scale: null,
    SubscribeEvents: [], PathsStartWithVisibility: [], DependsOn: [], Pods: [],
  },
  {
    Name: 'fibonacci2',
    NumberReady: 0, NumberRequested: 0,
    PodType: 'Deployment', Visibility: 'Public', Trust: 'Trusted',
    ReplicasMin: 0, ReplicasAtStart: 1,
    TimeoutSecondBeforeSetReplicasMin: 8,
    NumberParallelRequest: 2, NumberParallelRequestPerPod: 10,
    Resources: { CpuRequest: '10m', CpuLimit: '50m', MemoryRequest: '96Mi', MemoryLimit: '96Mi' },
    Schedule: { TimeZoneID: 'GB', Default: { WakeUp: [], ScaleDownTimeout: [] } },
    Scale: null,
    SubscribeEvents: [], PathsStartWithVisibility: [],
    DependsOn: ['fibonacci1', 'mysql'], Pods: [],
  },
  {
    Name: 'fibonacci3',
    NumberReady: 0, NumberRequested: 0,
    PodType: 'Deployment', Visibility: 'Public', Trust: 'Untrusted',
    ReplicasMin: 0, ReplicasAtStart: 1,
    TimeoutSecondBeforeSetReplicasMin: 120,
    NumberParallelRequest: 10, NumberParallelRequestPerPod: 10,
    Resources: { CpuRequest: '200m', CpuLimit: '200m', MemoryRequest: '512Mi', MemoryLimit: '512Mi' },
    Schedule: { TimeZoneID: 'GB', Default: { WakeUp: ['0 8 * * 1-5'], ScaleDownTimeout: [] } },
    Scale: {
      ReplicaMax: 5,
      Triggers: [
        { MetricType: 'AverageValue', MetricName: 'http_requests_per_second', Query: '', Threshold: 100 },
      ],
      Behavior: {
        ScaleUp: { StabilizationWindowSeconds: 0, Policies: [{ Type: 'Percent', Value: 100, PeriodSeconds: 15 }, { Type: 'Pods', Value: 4, PeriodSeconds: 15 }] },
        ScaleDown: { StabilizationWindowSeconds: 300, Policies: [{ Type: 'Percent', Value: 100, PeriodSeconds: 15 }] },
      },
    },
    SubscribeEvents: [{ Name: 'fibo-public', Visibility: 0 }],
    PathsStartWithVisibility: [
      { Path: '/api/public', Visibility: 'Public' },
      { Path: '/api/admin', Visibility: 'Private' },
    ],
    DependsOn: [], Pods: [],
  },
  {
    Name: 'fibonacci4',
    NumberReady: 0, NumberRequested: 0,
    PodType: 'Deployment', Visibility: 'Private', Trust: 'Trusted',
    ReplicasMin: 0, ReplicasAtStart: 2,
    TimeoutSecondBeforeSetReplicasMin: 120,
    NumberParallelRequest: 10, NumberParallelRequestPerPod: 10,
    Resources: { CpuRequest: '10m', CpuLimit: '50m', MemoryRequest: '96Mi', MemoryLimit: '96Mi' },
    Schedule: { TimeZoneID: 'GB', Default: { WakeUp: [], ScaleDownTimeout: [] } },
    Scale: null,
    SubscribeEvents: [{ Name: 'fibo-public', Visibility: 0 }],
    PathsStartWithVisibility: [], DependsOn: [], Pods: [],
  },
  {
    Name: 'kafka',
    NumberReady: 0, NumberRequested: 0,
    PodType: 'Deployment', Visibility: 'Public', Trust: 'Trusted',
    ReplicasMin: 0, ReplicasAtStart: 1,
    TimeoutSecondBeforeSetReplicasMin: 300,
    NumberParallelRequest: 10, NumberParallelRequestPerPod: 10,
    Resources: { CpuRequest: null, CpuLimit: null, MemoryRequest: null, MemoryLimit: null },
    Schedule: { TimeZoneID: 'GB', Default: { WakeUp: [], ScaleDownTimeout: [] } },
    Scale: null,
    SubscribeEvents: [], PathsStartWithVisibility: [], DependsOn: [], Pods: [],
  },
  {
    Name: 'slimfaas-kafka',
    NumberReady: 0, NumberRequested: 0,
    PodType: 'Deployment', Visibility: 'Public', Trust: 'Trusted',
    ReplicasMin: 0, ReplicasAtStart: 1,
    TimeoutSecondBeforeSetReplicasMin: 300,
    NumberParallelRequest: 10, NumberParallelRequestPerPod: 10,
    Resources: { CpuRequest: '20m', CpuLimit: '100m', MemoryRequest: '64Mi', MemoryLimit: '128Mi' },
    Schedule: { TimeZoneID: 'GB', Default: { WakeUp: [], ScaleDownTimeout: [] } },
    Scale: null,
    SubscribeEvents: [], PathsStartWithVisibility: [],
    DependsOn: ['kafka'], Pods: [],
  },
  {
    Name: 'mysql',
    NumberReady: 0, NumberRequested: 0,
    PodType: 'StatefulSet', Visibility: 'Public', Trust: 'Trusted',
    ReplicasMin: 0, ReplicasAtStart: 1,
    TimeoutSecondBeforeSetReplicasMin: 8,
    NumberParallelRequest: 10, NumberParallelRequestPerPod: 10,
    Resources: { CpuRequest: '300m', CpuLimit: '600m', MemoryRequest: '512Mi', MemoryLimit: '1Gi' },
    Schedule: { TimeZoneID: 'GB', Default: { WakeUp: [], ScaleDownTimeout: [] } },
    Scale: null,
    SubscribeEvents: [], PathsStartWithVisibility: [], DependsOn: [], Pods: [],
  },
  {
    Name: 'ws-chat-handler',
    NumberReady: 2, NumberRequested: 2,
    PodType: 'WebSocket', Visibility: 'Public', Trust: 'Trusted',
    ReplicasMin: 0, ReplicasAtStart: 2,
    TimeoutSecondBeforeSetReplicasMin: 0,
    NumberParallelRequest: 10, NumberParallelRequestPerPod: 10,
    Resources: null,
    Schedule: null,
    Scale: null,
    SubscribeEvents: [{ Name: 'chat-msg', Visibility: 'Public' }],
    PathsStartWithVisibility: [],
    DependsOn: ['kafka'],
    Pods: [
      { Name: 'ws-abc12345', Status: 'Running', Ready: true, Ip: 'abc12345' },
      { Name: 'ws-def67890', Status: 'Running', Ready: true, Ip: 'def67890' },
    ],
  },
];

const FUNCTIONS_SOME_UP: FunctionStatusDetailed[] = FUNCTIONS_ALL_DOWN.map((fn, i) =>
  i % 3 === 0
    ? {
        ...fn,
        NumberReady: fn.ReplicasAtStart,
        NumberRequested: fn.ReplicasAtStart,
        Pods: Array.from({ length: fn.ReplicasAtStart }, (_, j) => ({
          Name: `${fn.Name}-pod-${j}`,
          Status: 'Running',
          Ready: true,
          Ip: `10.0.0.${j + 1}`,
        })),
      }
    : fn
);

const FUNCTIONS_ALL_UP: FunctionStatusDetailed[] = FUNCTIONS_ALL_DOWN.map((fn) => ({
  ...fn,
  NumberReady: fn.ReplicasAtStart,
  NumberRequested: fn.ReplicasAtStart,
  Pods: Array.from({ length: fn.ReplicasAtStart }, (_, j) => ({
    Name: `${fn.Name}-pod-${j}`,
    Status: 'Running',
    Ready: true,
    Ip: `10.0.0.${j + 1}`,
  })),
}));

// ── Meta ─────────────────────────────────────────────────────────────────────

const meta: Meta<typeof FunctionTable> = {
  title: 'Dashboard/FunctionTable',
  component: FunctionTable,
  parameters: { layout: 'fullscreen' },
  args: { onWakeUp: (name) => console.log('Wake up:', name) },
};
export default meta;

type Story = StoryObj<typeof FunctionTable>;

// ── Stories ──────────────────────────────────────────────────────────────────

export const AllDown: Story = {
  name: 'All functions down (scaled to 0)',
  args: { functions: FUNCTIONS_ALL_DOWN },
};

export const SomeUp: Story = {
  name: 'Mixed — some up, some down',
  args: { functions: FUNCTIONS_SOME_UP },
};

export const AllUp: Story = {
  name: 'All functions up',
  args: { functions: FUNCTIONS_ALL_UP },
};

export const Empty: Story = {
  name: 'No functions',
  args: { functions: [] },
};

