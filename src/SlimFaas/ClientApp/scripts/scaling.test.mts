import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createSimulationDraft, mergeScalingState, scalingValue, simulationRequest,
  type ScalingState } from '../src/lib/scaling.ts';
import type { ScaleConfig } from '../src/types.ts';

const config: ScaleConfig = { ReplicaMax: 20, ScaleFromZero: true, Sources: [{ Name: 'jobs', Url: 'http://exporter/metrics' }],
  Triggers: [{ MetricName: 'pending', Query: 'sum(jobs_pending)', MetricType: 'AverageValue', Threshold: 10, Source: 'jobs' }],
  Behavior: { ScaleUp: { StabilizationWindowSeconds: 0, Policies: [{ Type: 'Pods', Value: 20, PeriodSeconds: 15 }] },
    ScaleDown: { StabilizationWindowSeconds: 300, Policies: [] } } };
const state = (Session: string, ids: number[], time = 1_000_000): ScalingState => ({
  Session, Function: 'worker', Status: 'Live', ServerTimeMs: time, Decision: null, Configuration: config,
  ObservedReadyReplicas: 0, ObservedRequestedReplicas: 0, Truncated: false, RefreshIntervalMs: 1000,
  Events: ids.map(Id => ({ Id, TimestampMs: time, CurrentReplicas: 0, Target: 4, RawTarget: 8,
    Action: 'ScaleUp', Application: 'Accepted', Reasons: [], Signals: [] })) });

test('drafts preserve configured source scope and never send URLs or mutate configuration', () => {
  const original = JSON.stringify(config);
  const draft = createSimulationDraft(config, 0);
  draft.Triggers[0].Threshold = 20;
  const request = simulationRequest('worker', draft);
  assert.equal(request.Triggers[0].Threshold, 20);
  assert.equal(request.Triggers[0].Source, 'jobs');
  assert.equal(request.Triggers[0].Value, null);
  assert.ok(!JSON.stringify(request).includes('http://'));
  assert.equal(JSON.stringify(config), original);
});
test('zero is a simulated value, an empty input uses observations, missing values never display zero', () => {
  const draft = createSimulationDraft(config, 0);
  draft.Triggers[0].FakeValue = '0';
  assert.equal(simulationRequest('worker', draft).Triggers[0].Value, 0);
  draft.Triggers[0].FakeValue = '';
  assert.equal(simulationRequest('worker', draft).Triggers[0].Value, null);
  assert.equal(scalingValue(null), '—'); assert.equal(scalingValue(0), '0');
});
test('invalid inputs fail before sending a simulation and clearing the maximum is explicit', () => {
  const draft = createSimulationDraft(config, 0);
  draft.ReplicaMax = '';
  assert.equal(simulationRequest('worker', draft).ClearReplicaMax, true);
  draft.CurrentReplicas = '-1'; assert.throws(() => simulationRequest('worker', draft), /non-negative/);
  draft.CurrentReplicas = '0'; draft.Triggers[0].FakeValue = 'Infinity'; assert.throws(() => simulationRequest('worker', draft), /finite/);
  draft.Triggers[0].FakeValue = ''; draft.Behavior = '[]'; assert.throws(() => simulationRequest('worker', draft), /ScaleUp/);
});
test('SSE updates deduplicate ordered history and leadership changes reset the cursor history', () => {
  const first = state('leader-one', [1, 2]);
  const next = mergeScalingState(first, state('leader-one', [2, 3]));
  assert.deepEqual(next.Events.map(e => e.Id), [1, 2, 3]);
  assert.deepEqual(mergeScalingState(next, state('leader-two', [1])).Events.map(e => e.Id), [1]);
  assert.deepEqual(mergeScalingState(next, { ...state('leader-one', [4]), Function: 'other' }).Events.map(e => e.Id), [4]);
});
test('browser history remains bounded by count and fifteen minutes', () => {
  const next = mergeScalingState(null, state('leader', Array.from({ length: 700 }, (_, i) => i + 1)));
  assert.equal(next.Events.length, 300); assert.equal(next.Events[0].Id, 401); assert.equal(next.Truncated, true);
  assert.equal(mergeScalingState(next, state('leader', [], 2_000_000)).Events.length, 0);
  assert.throws(() => mergeScalingState(null, { ...next, Session: '' }), /Invalid scaling frame/);
});
