import type { Meta, StoryObj } from '@storybook/react';
import React from 'react';
import NetworkMap from '../components/NetworkMap';
import type { FunctionStatusDetailed, QueueInfo, NetworkActivityEvent } from '../types';

const FUNCTIONS: FunctionStatusDetailed[] = [
  {
    Name: 'fibonacci1', NumberReady: 2, NumberRequested: 2,
    PodType: 'Deployment', Visibility: 'Public', Trust: 'Trusted',
    ReplicasMin: 0, ReplicasAtStart: 2, TimeoutSecondBeforeSetReplicasMin: 10,
    NumberParallelRequest: 10, NumberParallelRequestPerPod: 10,
    Resources: { CpuRequest: '10m', CpuLimit: '50m', MemoryRequest: '96Mi', MemoryLimit: '96Mi' },
    Schedule: null, Scale: null, SubscribeEvents: [], PathsStartWithVisibility: [],
    DependsOn: [], Pods: [
      { Name: 'fib1-pod-0', Status: 'Running', Ready: true, Ip: '10.0.0.1' },
      { Name: 'fib1-pod-1', Status: 'Running', Ready: true, Ip: '10.0.0.2' },
    ],
  },
  {
    Name: 'fibonacci2', NumberReady: 1, NumberRequested: 1,
    PodType: 'Deployment', Visibility: 'Public', Trust: 'Trusted',
    ReplicasMin: 0, ReplicasAtStart: 1, TimeoutSecondBeforeSetReplicasMin: 8,
    NumberParallelRequest: 2, NumberParallelRequestPerPod: 10,
    Resources: { CpuRequest: '10m', CpuLimit: '50m', MemoryRequest: '96Mi', MemoryLimit: '96Mi' },
    Schedule: null, Scale: null, SubscribeEvents: [], PathsStartWithVisibility: [],
    DependsOn: ['fibonacci1'], Pods: [
      { Name: 'fib2-pod-0', Status: 'Running', Ready: true, Ip: '10.0.0.3' },
    ],
  },
  {
    Name: 'kafka', NumberReady: 0, NumberRequested: 0,
    PodType: 'Deployment', Visibility: 'Public', Trust: 'Trusted',
    ReplicasMin: 0, ReplicasAtStart: 1, TimeoutSecondBeforeSetReplicasMin: 300,
    NumberParallelRequest: 10, NumberParallelRequestPerPod: 10,
    Resources: null, Schedule: null, Scale: null, SubscribeEvents: [],
    PathsStartWithVisibility: [], DependsOn: [], Pods: [],
  },
  {
    Name: 'mysql', NumberReady: 1, NumberRequested: 1,
    PodType: 'StatefulSet', Visibility: 'Public', Trust: 'Trusted',
    ReplicasMin: 0, ReplicasAtStart: 1, TimeoutSecondBeforeSetReplicasMin: 8,
    NumberParallelRequest: 10, NumberParallelRequestPerPod: 10,
    Resources: { CpuRequest: '300m', CpuLimit: '600m', MemoryRequest: '512Mi', MemoryLimit: '1Gi' },
    Schedule: null, Scale: null, SubscribeEvents: [], PathsStartWithVisibility: [],
    DependsOn: [], Pods: [
      { Name: 'mysql-0', Status: 'Running', Ready: true, Ip: '10.0.0.5' },
    ],
  },
  {
    Name: 'ws-handler', NumberReady: 2, NumberRequested: 2,
    PodType: 'WebSocket', Visibility: 'Public', Trust: 'Trusted',
    ReplicasMin: 0, ReplicasAtStart: 2, TimeoutSecondBeforeSetReplicasMin: 0,
    NumberParallelRequest: 10, NumberParallelRequestPerPod: 10,
    Resources: null, Schedule: null, Scale: null,
    SubscribeEvents: [{ Name: 'chat', Visibility: 'Public' }],
    PathsStartWithVisibility: [], DependsOn: [],
    Pods: [
      { Name: 'ws-aaa', Status: 'Running', Ready: true, Ip: 'aaa' },
      { Name: 'ws-bbb', Status: 'Running', Ready: true, Ip: 'bbb' },
    ],
  },
];

const QUEUES: QueueInfo[] = [
  { Name: 'fibonacci1', Length: 3 },
  { Name: 'fibonacci2', Length: 0 },
  { Name: 'kafka', Length: 12 },
  { Name: 'mysql', Length: 0 },
  { Name: 'ws-handler', Length: 1 },
];

const now = Date.now();
const ACTIVITY: NetworkActivityEvent[] = [
  { Id: 'evt-1', Type: 'request_in', Source: 'external', Target: 'slimfaas', QueueName: null, TimestampMs: now - 5000, NodeId: 'slimfaas-0' },
  { Id: 'evt-2', Type: 'enqueue', Source: 'slimfaas', Target: 'fibonacci1', QueueName: 'fibonacci1', TimestampMs: now - 4500, NodeId: 'slimfaas-0' },
  { Id: 'evt-3', Type: 'dequeue', Source: 'slimfaas', Target: 'fibonacci1', QueueName: 'fibonacci1', TimestampMs: now - 3000, NodeId: 'slimfaas-1' },
  { Id: 'evt-4', Type: 'request_in', Source: 'external', Target: 'slimfaas', QueueName: null, TimestampMs: now - 2000, NodeId: 'slimfaas-1' },
  { Id: 'evt-5', Type: 'request_out', Source: 'slimfaas', Target: 'fibonacci2', QueueName: null, TimestampMs: now - 1500, NodeId: 'slimfaas-0' },
  { Id: 'evt-6', Type: 'event_publish', Source: 'slimfaas', Target: 'ws-handler', QueueName: null, TimestampMs: now - 500, NodeId: 'slimfaas-1' },
];

const meta: Meta<typeof NetworkMap> = {
  title: 'Dashboard/NetworkMap',
  component: NetworkMap,
  parameters: { layout: 'padded' },
};
export default meta;

type Story = StoryObj<typeof NetworkMap>;

export const Default: Story = {
  name: 'With functions, queues and activity',
  args: { functions: FUNCTIONS, queues: QUEUES, activity: ACTIVITY },
};

export const NoActivity: Story = {
  name: 'No recent activity',
  args: { functions: FUNCTIONS, queues: QUEUES, activity: [] },
};

export const Empty: Story = {
  name: 'No functions',
  args: { functions: [], queues: [], activity: [] },
};

// Story with animated messages spawning over time — demonstrates enqueue/dequeue flows
export const LiveAnimation: Story = {
  name: 'Live animation (simulated)',
  render: () => {
    const [act, setAct] = React.useState<NetworkActivityEvent[]>([]);
    const [dynamicQueues, setDynamicQueues] = React.useState<QueueInfo[]>(QUEUES);
    React.useEffect(() => {
      let counter = 100;
      const fnNames = ['fibonacci1', 'fibonacci2', 'kafka', 'mysql', 'ws-handler'];
      const qLens: Record<string, number> = { fibonacci1: 3, fibonacci2: 0, kafka: 12, mysql: 0, 'ws-handler': 1 };

      const timer = setInterval(() => {
        counter++;
        const targetFn = fnNames[counter % fnNames.length];

        // Alternate between different event types to showcase the queue flow
        let type: string;
        if (counter % 5 === 0) {
          type = 'request_in';
        } else if (counter % 5 === 1) {
          type = 'enqueue';
          qLens[targetFn] = (qLens[targetFn] || 0) + 1;
        } else if (counter % 5 === 2) {
          type = 'dequeue';
          qLens[targetFn] = Math.max(0, (qLens[targetFn] || 0) - 1);
        } else if (counter % 5 === 3) {
          type = 'request_out';
        } else {
          type = 'event_publish';
        }

        const source = type === 'request_in' ? 'external' : 'slimfaas';
        const target = type === 'request_in' ? 'slimfaas' : targetFn;

        setAct(prev => [...prev.slice(-50), {
          Id: `live-${counter}`,
          Type: type,
          Source: source,
          Target: target,
          QueueName: (type === 'enqueue' || type === 'dequeue') ? targetFn : null,
          TimestampMs: Date.now(),
          NodeId: counter % 2 === 0 ? 'slimfaas-0' : 'slimfaas-1',
        }]);

        // Update queue lengths dynamically
        setDynamicQueues(fnNames.map(n => ({ Name: n, Length: qLens[n] || 0 })));
      }, 800);
      return () => clearInterval(timer);
    }, []);
    return <NetworkMap functions={FUNCTIONS} queues={dynamicQueues} activity={act} />;
  },
};
