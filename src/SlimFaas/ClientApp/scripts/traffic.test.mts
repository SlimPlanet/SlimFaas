import assert from 'node:assert/strict';
import { test } from 'node:test';
import { TrafficPlayback, SPEEDS, matchesTraffic, displayPath, markerProgress } from '../src/lib/traffic.ts';
import { functionState } from '../src/lib/live.ts';
import { buildTopology, observedFunctions, observedQueues } from '../src/lib/topology.ts';
import { makeFixtures, fixtureEvents } from '../src/lib/fixtures.ts';
import { normalizeSlimFaasNodes } from '../src/hooks/useStatusStream.ts';

const fixture = makeFixtures();
const model = buildTopology(fixture.functions, fixture.jobs, fixture.queues, fixture.slimFaasNodes);
const event = (id, overrides = {}) => ({ ...fixtureEvents(3, 1)[0], Id: id, ReceivedAt: 100, ...overrides });
const count = playback => [...playback.markers.values()].reduce((sum, marker) => sum + marker.count, 0);
test('SSE normalization preserves changing leader roles and handles old or unknown snapshots', () => {
  for (const Role of ['Leader', 'Follower', 'Unknown', undefined, 'invalid']) {
    const nodes = normalizeSlimFaasNodes([{ Name: 'node', Status: 'Running', Role }]);
    assert.equal(nodes[0].Role, Role === 'Leader' || Role === 'Follower' ? Role : 'Unknown');
  }
  assert.equal(normalizeSlimFaasNodes([{ name: 'node', status: 'Starting', role: 'Leader' }])[0].Role, 'Leader');
});
function update(playback, events, { now = 100, paused = false, session = 1, speed = 'Fast', type = 'all', selected = null, isolate = false, topology = model } = {}) {
  playback.update(events, session, paused, now, topology, speed, type, selected, isolate);
}
test('the first isolated receipt is animated even when its server clock is old or ahead', () => {
  const player = new TrafficPlayback();
  update(player, []);
  const first = event('first', { TimestampMs: -86400000 });
  update(player, [first]);
  assert.equal(count(player), 1);
  update(player, [first, event('future', { TimestampMs: Date.now() + 86400000 })]);
  assert.equal(count(player), 2);
});
test('duplicates never create extra markers or inflate route counts', () => {
  const player = new TrafficPlayback(), first = event('first');
  update(player, [first, first]);
  update(player, [first, first, event('second')]);
  assert.equal(count(player), 2);
  assert.equal(player.markers.size, 1);
});
test('filters consume the global stream and never replay previously hidden events', () => {
  const player = new TrafficPlayback(), first = event('first');
  update(player, [first], { type: 'event_publish' });
  assert.equal(count(player), 0);
  update(player, [first], { selected: 'function:fibonacci1' });
  assert.equal(count(player), 0);
  update(player, [first, event('next')], { selected: 'function:fibonacci1' });
  assert.equal(count(player), 1);
  assert.ok(matchesTraffic(model, first, 'all', 'external:external', false));
  assert.ok(!matchesTraffic(model, first, 'all', 'external:external', true));
});
test('pause consumes arrivals, resume skips backlog, and reconnect clears the old session', () => {
  const player = new TrafficPlayback(), a = event('a'), b = event('b'), c = event('c');
  update(player, [a]);
  update(player, [a, b], { paused: true });
  update(player, [a, b, c]);
  assert.equal(count(player), 0);
  update(player, [a, b, c, event('live', { ReceivedAt: 101 })], { now: 101 });
  assert.equal(count(player), 1);
  update(player, [], { session: 2 });
  assert.equal(count(player), 0);
  update(player, [event('new-session')], { session: 2 });
  assert.equal(count(player), 1);
});
test('speed sets the complete trip duration and a background tab returns to current receipts', () => {
  for (const speed of Object.keys(SPEEDS)) {
    const player = new TrafficPlayback(), events = [event(speed)];
    update(player, events, { speed });
    assert.equal([...player.markers.values()][0].duration, SPEEDS[speed]);
    update(player, events, { speed, now: 100 + SPEEDS[speed] });
    assert.equal(count(player), 0);
    update(player, [...events, event('backlog')], { speed, now: 10000 });
    assert.equal(count(player), 0);
    update(player, [...events, event('current', { ReceivedAt: 10000 })], { speed, now: 10000 });
    assert.equal(count(player), 1);
  }
});
test('different message types stay separate while route counts aggregate without extending lifetime', () => {
  const player = new TrafficPlayback();
  update(player, [event('request'), event('publish', { Type: 'event_publish' }), event('reply', { Type: 'request_end' })]);
  assert.deepEqual(new Set([...player.markers.values()].map(m => m.kind)), new Set(['request', 'publication', 'reply']));
  const start = [...player.markers.values()][0].start;
  update(player, [event('request'), event('second')], { now: 200 });
  assert.equal([...player.markers.values()][0].start, start);
});
test('more than 200 distinct routes are bounded and omitted animation counts are explicit', () => {
  const fixtures = makeFixtures(1000, 0);
  const topology = buildTopology(fixtures.functions, fixtures.jobs, fixtures.queues, fixtures.slimFaasNodes);
  const events = Array.from({ length: 1000 }, (_, i) => event(`event-${i}`, { Source: 'external', SourcePod: null, Target: 'fibonacci1', TargetPod: fixtures.functions[0].Pods[i].Name }));
  const player = new TrafficPlayback();
  update(player, events, { topology, selected: 'function:fibonacci1' });
  assert.equal(player.markers.size, 200);
  assert.equal(player.omitted, 800);
  assert.equal(count(player) + player.omitted, 1000);
});
test('function power states distinguish zero capacity, startup, partial capacity and explicit errors', () => {
  const fn = { ...fixture.functions[0], Pods: [] };
  assert.equal(functionState({ ...fn, NumberReady: 0, NumberRequested: 0 }), 'Sleeping');
  assert.equal(functionState({ ...fn, NumberReady: 0, NumberRequested: 2 }), 'Starting');
  assert.equal(functionState({ ...fn, NumberReady: 1, NumberRequested: 2 }), 'Scaling');
  assert.equal(functionState({ ...fn, NumberReady: 2, NumberRequested: 2 }), 'Ready');
  assert.equal(functionState({ ...fn, NumberReady: 0, NumberRequested: 2, Pods: [{ Status: 'Error' }] }), 'Error');
});
test('SlimFaas dispatch does not introduce an extra self-hop and idle queues remain present', () => {
  const dispatch = event('dispatch', { Source: 'slimfaas', SourcePod: null });
  assert.deepEqual(displayPath(model, dispatch, null), ['node:slimfaas-0', 'pod:fibonacci1/fibonacci1-00003']);
  const empty = buildTopology(fixture.functions, fixture.jobs, [{ Name: 'fibonacci1', Length: 0 }], fixture.slimFaasNodes);
  assert.equal(empty.byId.get('queue:fibonacci1').status, '0 queued');
});

test('a buffered receipt from the paused interval is not animated after resume', () => {
  const player = new TrafficPlayback();
  update(player, [], { paused: true, now: 100 });
  update(player, [], { now: 200 });
  update(player, [event('buffered', { ReceivedAt: 150 })], { now: 250 });
  assert.equal(count(player), 0);
  update(player, [event('buffered', { ReceivedAt: 150 }), event('live', { ReceivedAt: 251 })], { now: 251 });
  assert.equal(count(player), 1);
});

test('a frame timestamp preceding a layout-effect receipt keeps the marker at the source', () => {
  assert.equal(markerProgress(99, 100, 450), 0);
  assert.equal(markerProgress(100, 100, 450), 0);
  assert.equal(markerProgress(325, 100, 450), 0.5);
  assert.ok(markerProgress(1000, 100, 450) < 1);
});

test('a function first seen in a peer event has a selectable route without inventing ready replicas', () => {
  const first = event('remote', { Target: 'new-websocket', TargetPod: 'opaque-connection', Source: 'external', SourcePod: null });
  const observed = observedFunctions(fixture.functions, [first, first]);
  assert.deepEqual(observed, [{ name: 'new-websocket', replicas: ['opaque-connection'] }]);
  const topology = buildTopology(fixture.functions, fixture.jobs, fixture.queues, fixture.slimFaasNodes, observed);
  const target = topology.byId.get('function:new-websocket');
  assert.equal(target.status, 'Observed traffic · inventory unknown');
  assert.equal(target.state, undefined);
  assert.equal(target.children.length, 1);
  const playback = new TrafficPlayback();
  update(playback, [first], { topology });
  assert.equal(count(playback), 1);
  assert.ok([...playback.markers.values()][0].path.includes('observed:new-websocket/opaque-connection'));
  assert.deepEqual(observedFunctions([{ ...fixture.functions[0], Name: 'new-websocket' }], [first]), []);
});

test('peer WebSocket queue dispatches retain their queue even without a local queue inventory', () => {
  const e = event('remote-attempt', { Type: 'dequeue', QueueName: 'remote-ws', Target: 'remote-ws', TargetPod: 'connection' });
  const unknown = observedQueues(fixture.queues, [e, e]);
  const topology = buildTopology(fixture.functions, fixture.jobs, fixture.queues, fixture.slimFaasNodes, observedFunctions(fixture.functions, [e]), unknown);
  assert.deepEqual(unknown, ['remote-ws']);
  assert.equal(topology.byId.get('queue:remote-ws').status, 'Queue length unknown');
  assert.deepEqual(displayPath(topology, e, null), ['queue:remote-ws', 'observed:remote-ws/connection']);
  assert.deepEqual(observedQueues([{ Name: 'remote-ws', Length: 0 }], [e]), []);
});

test('async dequeue and dispatch represent one delivery in either order, across batches and after completion', () => {
  for (const reverse of [false, true]) {
    const player = new TrafficPlayback();
    const dequeue = event('attempt-1', { Type: 'dequeue', QueueName: 'fibonacci1' });
    const dispatch = event('dispatch-1', { Type: 'request_out', QueueName: 'fibonacci1', CorrelationId: 'attempt-1' });
    const [first, second] = reverse ? [dispatch, dequeue] : [dequeue, dispatch];
    update(player, [first]);
    const marker = [...player.markers.values()][0];
    assert.deepEqual(marker.path, ['queue:fibonacci1', 'pod:fibonacci1/fibonacci1-00003']);
    assert.equal(marker.kind, 'queue');
    update(player, [first, second]);
    assert.equal(count(player), 1);
    update(player, [first, second], { now: 1000 });
    assert.equal(count(player), 0);
    update(player, [second, first], { now: 1001 });
    assert.equal(count(player), 0);
    update(player, [first, second, event('retry', { Type: 'dequeue', QueueName: 'fibonacci1', ReceivedAt: 1002 })], { now: 1002 });
    assert.equal(count(player), 1);
  }
});
test('an async pair split by filters or Pause cannot replay when its technical companion arrives', () => {
  const a = event('attempt', { Type: 'dequeue', QueueName: 'fibonacci1' });
  const b = event('dispatch', { Type: 'request_out', QueueName: 'fibonacci1', CorrelationId: a.Id, ReceivedAt: 201 });
  for (const view of [{ type: 'event_publish' }, { paused: true }]) {
    const player = new TrafficPlayback();
    update(player, [a], view);
    update(player, [a], { now: 200 });
    update(player, [a, b], { now: 201 });
    assert.equal(count(player), 0);
  }
});
test('either technical event filter can show its async delivery once, in either arrival order', () => {
  const dequeue = event('filtered-attempt', { Type: 'dequeue', QueueName: 'fibonacci1' });
  const dispatch = event('filtered-dispatch', { Type: 'request_out', QueueName: 'fibonacci1', CorrelationId: dequeue.Id });
  for (const type of ['dequeue', 'request_out']) for (const events of [[dequeue, dispatch], [dispatch, dequeue]]) {
    const player = new TrafficPlayback();
    update(player, [events[0]], { type });
    update(player, events, { type });
    assert.equal(count(player), 1);
    assert.deepEqual([...player.markers.values()][0].path, ['queue:fibonacci1', 'pod:fibonacci1/fibonacci1-00003']);
  }
});
test('unselected destinations remain exact and a leader is searchable independently of readiness', () => {
  const player = new TrafficPlayback();
  update(player, [event('one', { TargetPod: fixture.functions[0].Pods[0].Identity }), event('two', { TargetPod: fixture.functions[0].Pods[1].Identity })]);
  assert.equal(player.markers.size, 2);
  assert.deepEqual([...player.markers.values()].map(m => m.path.at(-1)), ['pod:fibonacci1/fibonacci1-00000', 'pod:fibonacci1/fibonacci1-00001']);
  const leader = model.byId.get('node:slimfaas-1');
  assert.equal(leader.role, 'Leader');
  assert.match(leader.status, /Running.*Leader/);
  assert.match(model.byId.get('slimfaas:slimfaas').status, /slimfaas-1/);
});
