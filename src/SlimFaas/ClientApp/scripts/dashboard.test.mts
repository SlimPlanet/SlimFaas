import test from 'node:test';
import assert from 'node:assert/strict';
import { actorLink, canWake, compactLabel, dashboardRoute, trafficHome } from '../src/lib/dashboard.ts';
import { makeFixtures, makeSpeechFunctions } from '../src/lib/fixtures.ts';
import { buildTopology, eventPath, filterNodes } from '../src/lib/topology.ts';

test('root, existing live links and actor identities round trip without splitting names', () => {
  for (const route of ['', '#/', '#/live', trafficHome]) assert.equal(dashboardRoute(route).path, trafficHome);
  for (const route of ['#/overview', '#/streams/orders', '#/dead-letters/worker', '#/live/scaling?function=worker']) {
    assert.equal(dashboardRoute(route).path, route.split('?')[0]);
  }
  const id = 'pod:worker/child+&?';
  assert.equal(dashboardRoute(actorLink(id)).params.get('actor'), id);
});

test('only sleeping orchestrator workloads can be woken', () => {
  const sleeping = makeFixtures(0).functions[0];
  for (const PodType of ['Deployment', 'StatefulSet']) assert.ok(canWake({ ...sleeping, PodType }));
  for (const changed of [{ PodType: 'WebSocket' }, { PodType: '' }, { NumberRequested: 1 }, { NumberReady: 1 },
    { Pods: [{ Name: 'bad', Status: 'CrashLoopBackOff', Identity: '', Ready: false }] }]) assert.equal(canWake({ ...sleeping, ...changed }), false);
});

test('three overlapping deployment names retain distinct labels, groups, pods and traffic destinations (#535)', () => {
  const functions = makeSpeechFunctions();
  const topology = buildTopology(functions, [], [], []);
  assert.equal(new Set(functions.map(fn => compactLabel(fn.Name, 16))).size, 3);
  assert.match(compactLabel(functions[2].Name, 21), /piper$/);
  assert.equal(compactLabel('short', 16), 'short');
  for (const fn of functions) {
    const group = topology.groups.find(group => group.id === 'function:' + fn.Name)!;
    assert.deepEqual(group.children.map(node => node.label), fn.Pods!.map(pod => pod.Name));
    for (const pod of fn.Pods!) {
      const path = eventPath(topology, { Id: 'request', Source: 'slimfaas', SourcePod: null, Target: fn.Name,
        TargetPod: pod.Identity, Type: 'request_out', NodeId: 'local', QueueName: null, TimestampMs: 0 });
      assert.equal(topology.byId.get(path.at(-1)!)?.parent, group.id);
    }
  }
});


test('exact workload matches stay on the first page among thousands of partial matches', () => {
  const fixture = makeFixtures(1, 1000);
  const topology = buildTopology(fixture.functions, fixture.jobs, fixture.queues, fixture.slimFaasNodes);
  assert.equal(filterNodes(topology, 'SLIMFAAS')[0].id, 'slimfaas:slimfaas');
});
