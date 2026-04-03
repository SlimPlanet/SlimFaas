import type { Meta, StoryObj } from '@storybook/react';
import JobTable from '../components/JobTable';
import type { JobConfigurationStatus } from '../types';

// ── Données réelles issues de l'API ──────────────────────────────────────────

const JOBS_IDLE: JobConfigurationStatus[] = [
  {
    Name: 'fibonacci',
    Visibility: 'Public',
    Image: 'axaguildev/fibonacci-batch:0.66.0-pr.244535',
    ImagesWhitelist: [],
    NumberParallelJob: 2,
    Resources: {
      Requests: { cpu: '400m', memory: '400Mi' },
      Limits: { cpu: '400m', memory: '400Mi' },
    },
    DependsOn: ['fibonacci1'],
    Schedules: [
      {
        Id: 'e2adcf09',
        Schedule: '0 0 * * *',
        Image: '',
        NextExecutionTimestamp: 1775260800,
        Resources: null,
        DependsOn: null,
      },
    ],
    RunningJobs: [],
  },
  {
    Name: 'Default',
    Visibility: 'Private',
    Image: '',
    ImagesWhitelist: [],
    NumberParallelJob: 1,
    Resources: {
      Requests: { cpu: '100m', memory: '100Mi' },
      Limits: { cpu: '100m', memory: '100Mi' },
    },
    DependsOn: null,
    Schedules: [],
    RunningJobs: [],
  },
];

const JOBS_WITH_RUNNING: JobConfigurationStatus[] = [
  {
    ...JOBS_IDLE[0],
    RunningJobs: [
      {
        Name: 'fibonacci-slimfaas-job-abc123',
        Status: 'Running',
        ElementId: 'elem-001',
        InQueueTimestamp: 1775257200,
        StartTimestamp: 1775257260,
      },
      {
        Name: 'fibonacci-slimfaas-job-def456',
        Status: 'Running',
        ElementId: 'elem-002',
        InQueueTimestamp: 1775257300,
        StartTimestamp: 1775257350,
      },
    ],
  },
  JOBS_IDLE[1],
];

// ── Meta ─────────────────────────────────────────────────────────────────────

const meta: Meta<typeof JobTable> = {
  title: 'Dashboard/JobTable',
  component: JobTable,
  parameters: { layout: 'fullscreen' },
};
export default meta;

type Story = StoryObj<typeof JobTable>;

// ── Stories ──────────────────────────────────────────────────────────────────

export const Idle: Story = {
  name: 'Jobs idle (no running jobs)',
  args: { jobs: JOBS_IDLE },
};

export const WithRunningJobs: Story = {
  name: 'Jobs with running instances',
  args: { jobs: JOBS_WITH_RUNNING },
};

export const Empty: Story = {
  name: 'No job configurations',
  args: { jobs: [] },
};

