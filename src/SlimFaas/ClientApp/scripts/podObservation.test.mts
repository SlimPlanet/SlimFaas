import test from 'node:test';
import assert from 'node:assert/strict';
import { lifecycle, nextSchedule, requestLabel, workloadRequests } from '../src/lib/podObservation.ts';
import { makeObservationFixture } from '../src/lib/fixtures.ts';
import { buildTopology } from '../src/lib/topology.ts';
import { normalizePayload } from '../src/hooks/useStatusStream.ts';

test('workload requests include overlapping replicas and distinguish missing observations from zero', () => {
  const f = makeObservationFixture();
  const pods = f.functions[0].Pods!;
  assert.equal(requestLabel(workloadRequests(pods)), '1 CPU · 512 MiB RAM');
  pods[0].Requests = null;
  pods[2].Lifecycle = 'Succeeded';
  assert.equal(requestLabel(workloadRequests(pods)), '500m CPU · 256 MiB RAM (partial)');
  assert.equal(requestLabel(workloadRequests([])), '0m CPU · 0 B RAM');
  assert.equal(requestLabel(workloadRequests([pods[0]])), 'Unavailable');
});

test('the earliest future Unix second uses the viewing locale and time zone across a DST change', () => {
  const { Schedules } = makeObservationFixture().jobs[0];
  const start = Date.UTC(2026, 2, 29, 0, 59, 59);
  const values = [start / 1000 + 60, start / 1000 + 2, start / 1000 - 10, null];
  const schedules = values.map(NextExecutionTimestamp => ({ ...Schedules[0], NextExecutionTimestamp }));
  for (const [locale, timeZone] of [['fr-FR', 'Europe/Paris'], ['en-US', 'America/New_York']]) {
    const fmt = new Intl.DateTimeFormat(locale, { dateStyle: 'short', timeStyle: 'medium', timeZone });
    assert.equal(nextSchedule(schedules, start, fmt), 'Next run · ' + fmt.format(start + 2000));
  }
  assert.equal(nextSchedule(schedules, start + 61000), 'Next run unavailable');
  assert.equal(nextSchedule([]), undefined);
});

test('SSE preserves per-actor observations and snapshot totals in both JSON naming styles', () => {
  const f = makeObservationFixture();
  const payload = normalizePayload({ Functions: f.functions, Jobs: f.jobs, SlimFaasNodes: f.slimFaasNodes,
    requests: { cpuMillis: 2800, memoryBytes: 1543503872, knownPods: 10, unknownPods: 0 } });
  assert.deepEqual(payload.Requests, f.requests);
  assert.equal(payload.SlimFaasNodes![0].Requests!.CpuMillis, 100);
  const model = buildTopology(payload.Functions, payload.Jobs, [], payload.SlimFaasNodes!);
  assert.deepEqual(model.byId.get('function:fibonacci1')?.requestLabel, '1 CPU · 512 MiB RAM');
  assert.equal(model.groups.find(g => g.kind === 'function')?.children[2].lifecycle, 'Terminating');
  assert.equal(model.groups.find(g => g.kind === 'job')?.children[2].lifecycle, 'Finishing');
  assert.match(model.byId.get('job:daily-report')!.scheduleLabel!, /^Next run · /);
  assert.equal(lifecycle('ImagePullBackOff'), 'Crash');
  assert.equal(lifecycle('Running'), 'Ready');
  assert.equal(lifecycle('Running', null, true), 'Running');
  assert.equal(lifecycle('toString'), 'Unknown');
  const absent = normalizePayload({ Functions: [{ Name: 'worker', PodType: 'Deployment' }] });
  assert.equal(buildTopology(absent.Functions, [], [], []).byId.get('function:worker')?.requestLabel, 'Unavailable');
  assert.equal(normalizePayload({ Requests: { CpuMillis: -1 } }).Requests, undefined);
});
