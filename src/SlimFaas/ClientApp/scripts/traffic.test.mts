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

const incoming = (id = 'in', extra = {}) => event(id, { Type: 'request_in', Source: 'external', SourcePod: null, Target: 'slimfaas', TargetPod: null, ...extra });
const sent = (id = 'out', extra = {}) => event(id, { Type: 'request_out', Source: 'slimfaas', SourcePod: null, CorrelationId: 'in', ...extra });
const returned = (id = 'reply', extra = {}) => sent(id, { Type: 'request_end', CorrelationId: 'out', ...extra });
const ended = (extra = {}) => incoming('end', { Type: 'request_end', CorrelationId: 'in', ...extra });
const paths = player => [...player.markers.values()].map(marker => marker.path);

test('fast synchronous exchanges play four ordered hops, even in reverse batch order', () => {
  for (const reverse of [false, true]) {
    const player = new TrafficPlayback();
    const events = [incoming(), sent(), returned(), ended()];
    if (reverse) events.reverse();
    update(player, events);
    assert.deepEqual(paths(player), [['external:external', 'node:slimfaas-0']]);
    update(player, events, { now: 550 });
    assert.deepEqual(paths(player), [['node:slimfaas-0', 'pod:fibonacci1/fibonacci1-00003']]);
    update(player, events, { now: 1000 });
    assert.deepEqual(paths(player), [['pod:fibonacci1/fibonacci1-00003', 'node:slimfaas-0']]);
    assert.equal([...player.markers.values()][0].kind, 'reply');
    update(player, events, { now: 1450 });
    assert.deepEqual(paths(player), [['node:slimfaas-0', 'external:external']]);
    update(player, events, { now: 1900 });
    assert.equal(player.markers.size, 0);
  }
});
test('cold start waits on SlimFaas; readiness metadata never dispatches a message', () => {
  const player = new TrafficPlayback();
  const wait = sent('wait', { Type: 'request_waiting', TargetPod: null });
  const ready = sent('ready', { Type: 'request_started', TargetPod: null, ReceivedAt: 600 });
  update(player, [incoming(), wait]);
  update(player, [incoming(), wait], { now: 550 });
  assert.deepEqual(paths(player), [['node:slimfaas-0']]);
  assert.ok([...player.markers.values()][0].waiting);
  update(player, [incoming(), wait, ready], { now: 600 });
  assert.deepEqual(paths(player), [['node:slimfaas-0']]);
  update(player, [incoming(), wait, ready, sent('out', { ReceivedAt: 650 })], { now: 650 });
  assert.deepEqual(paths(player), [['node:slimfaas-0', 'pod:fibonacci1/fibonacci1-00003']]);
});
test('a dispatch before the inventory update retains its exact replica destination', () => {
  const player = new TrafficPlayback();
  const empty = buildTopology(fixture.functions.map(fn => ({ ...fn, Pods: [], NumberReady: 0 })), fixture.jobs, fixture.queues, fixture.slimFaasNodes);
  const events = [incoming(), sent()];
  update(player, events, { topology: empty });
  update(player, events, { topology: empty, now: 550 });
  assert.equal(player.markers.size, 0);
  update(player, events, { topology: model, now: 1000 });
  assert.deepEqual(paths(player), [['node:slimfaas-0', 'pod:fibonacci1/fibonacci1-00003']]);
});
test('publication ingress and transport produce one arrival and one delivery per replica', () => {
  for (const reverse of [false, true]) {
    const player = new TrafficPlayback();
    const events = [incoming(), incoming('publication', { Type: 'event_publish', CorrelationId: 'in' }), ended()];
    for (let i = 0; i < 2; i++) {
      const TargetPod = fixture.functions[0].Pods[i].Name;
      events.push(sent(`delivery-${i}`, { Type: 'event_publish', CorrelationId: 'publication', TargetPod }),
        sent(`transport-${i}`, { CorrelationId: `delivery-${i}`, TargetPod }),
        returned(`done-${i}`, { CorrelationId: `transport-${i}`, TargetPod }));
    }
    if (reverse) events.reverse();
    update(player, events);
    assert.deepEqual(paths(player), [['external:external', 'node:slimfaas-0']]);
    assert.equal([...player.markers.values()][0].kind, 'publication');
    update(player, events, { now: 550 });
    assert.deepEqual(paths(player).sort(), [0, 1].map(i => ['node:slimfaas-0', `pod:fibonacci1/fibonacci1-0000${i}`]));
    assert.ok([...player.markers.values()].every(m => m.kind === 'publication' && m.count === 1));
    update(player, events, { now: 1000 });
    assert.equal(player.markers.size, 0);
  }
});
test('an error before dispatch returns only from SlimFaas and clears waiting', () => {
  const player = new TrafficPlayback();
  const events = [incoming(), sent('wait', { Type: 'request_waiting', TargetPod: null })];
  update(player, events); update(player, events, { now: 550 });
  update(player, [...events, ended({ ReceivedAt: 600 })], { now: 600 });
  assert.deepEqual(paths(player), [['node:slimfaas-0', 'external:external']]);
});
test('late parents order children without fabricating absent request legs', () => {
  const player = new TrafficPlayback();
  update(player, [sent()]);
  assert.equal(player.markers.size, 0);
  const events = [sent(), incoming('in', { ReceivedAt: 200 })];
  update(player, events, { now: 200 });
  assert.deepEqual(paths(player), [['external:external', 'node:slimfaas-0']]);
  update(player, events, { now: 650 });
  assert.deepEqual(paths(player), [['node:slimfaas-0', 'pod:fibonacci1/fibonacci1-00003']]);
  const orphan = new TrafficPlayback();
  update(orphan, [returned()]); update(orphan, [returned()], { now: 6000 });
  assert.equal(orphan.markers.size, 0);
});
test('pause, filtering and new sessions discard scheduled legs as well as active markers', () => {
  for (const change of [{ paused: true }, { type: 'event_publish' }, { session: 2 }]) {
    const player = new TrafficPlayback();
    const events = [incoming(), sent(), returned(), ended()];
    update(player, events);
    update(player, change.session ? [] : events, { ...change, now: 200 });
    update(player, change.session ? [] : events, { ...change, now: 3000 });
    assert.equal(player.markers.size, 0);
  }
});
test('concurrent calls retain independent ordering and aggregate matching hops', () => {
  const player = new TrafficPlayback();
  const events = [incoming('one'), incoming('two'), sent('one-out', { CorrelationId: 'one' }), sent('two-out', { CorrelationId: 'two' })];
  update(player, events);
  assert.equal(count(player), 2); assert.equal(player.markers.size, 1);
  update(player, events, { now: 550 });
  assert.equal(count(player), 2); assert.equal(player.markers.size, 1);
});
test('publication-only filtering keeps its ingress without duplicate transport links', () => {
  const player = new TrafficPlayback();
  const events = [incoming(), incoming('publication', { Type: 'event_publish', CorrelationId: 'in' }),
    sent('delivery', { Type: 'event_publish', CorrelationId: 'publication' }), sent('transport', { CorrelationId: 'delivery' }), returned('done', { CorrelationId: 'transport' })];
  update(player, events, { type: 'event_publish' });
  assert.deepEqual(paths(player), [['external:external', 'node:slimfaas-0']]);
  update(player, events, { type: 'event_publish', now: 550 });
  assert.deepEqual(paths(player), [['node:slimfaas-0', 'pod:fibonacci1/fibonacci1-00003']]);
  assert.equal(player.visualKind(events[3]), null);
  assert.equal(player.visualKind(events[4]), null);
});
test('job isolation retains the caller context on downstream request and response hops', () => {
  const extra = { SourcePod: fixture.jobs[0].RunningJobs[0].Name };
  const player = new TrafficPlayback();
  const events = [incoming('in', extra), sent('out', extra), returned('reply', extra), ended(extra)];
  const settings = { selected: `job:${fixture.jobs[0].Name}`, isolate: true };
  update(player, events, settings);
  assert.equal(count(player), 1);
  update(player, events, { ...settings, now: 550 });
  assert.deepEqual(paths(player), [['node:slimfaas-0', 'pod:fibonacci1/fibonacci1-00003']]);
  update(player, events, { ...settings, now: 1000 });
  assert.deepEqual(paths(player), [['pod:fibonacci1/fibonacci1-00003', 'node:slimfaas-0']]);
});

test('recursive requests enter from their known caller pod and return to that same pod', () => {
  const caller = fixture.functions[0].Pods[0].Name;
  const source = { Source: 'fibonacci1', SourcePod: caller };
  const events = [incoming(), sent(), returned(), ended(),
    incoming('inner', source), sent('inner-out', { ...source, CorrelationId: 'inner' }),
    returned('inner-reply', { ...source, CorrelationId: 'inner-out' }),
    incoming('inner-end', { ...source, Type: 'request_end', CorrelationId: 'inner' })];
  const player = new TrafficPlayback();
  update(player, events);
  assert.deepEqual(paths(player).sort(), [['external:external', 'node:slimfaas-0'],
    [`pod:fibonacci1/${caller}`, 'node:slimfaas-0']].sort());
  update(player, events, { now: 550 });
  update(player, events, { now: 1000 });
  update(player, events, { now: 1450 });
  assert.deepEqual(paths(player).sort(), [['node:slimfaas-0', 'external:external'],
    ['node:slimfaas-0', `pod:fibonacci1/${caller}`]].sort());
  assert.ok([...player.markers.values()].every(marker => marker.count === 1));
});

const queueDispatch = (id = 'take', extra = {}) => sent(id, { Type: 'dequeue', QueueName: 'fibonacci1', CorrelationId: null, ...extra });
const queueEnd = (id = 'queue-end', extra = {}) => incoming(id, { Type: 'request_end', QueueName: 'fibonacci1',
  Source: 'fibonacci1', SourcePod: fixture.functions[0].Pods[3].Name, CorrelationId: 'take', ...extra });

test('queued replies return once to their queue after dispatch, regardless of batch order', () => {
  for (const reverse of [false, true]) {
    const player = new TrafficPlayback();
    const events = [queueDispatch(), sent('transport', { QueueName: 'fibonacci1', CorrelationId: 'take' }),
      returned('http-end', { QueueName: 'fibonacci1', CorrelationId: 'transport' }), queueEnd(), queueEnd()];
    if (reverse) events.reverse();
    update(player, events);
    assert.deepEqual(paths(player), [['queue:fibonacci1', 'pod:fibonacci1/fibonacci1-00003']]);
    assert.equal(count(player), 1);
    update(player, events, { now: 550 });
    assert.deepEqual(paths(player), [['pod:fibonacci1/fibonacci1-00003', 'queue:fibonacci1']]);
    assert.equal(count(player), 1);
    assert.equal([...player.markers.values()][0].kind, 'reply');
    update(player, events, { now: 1000 });
    assert.equal(count(player), 0);
    assert.equal(player.visualKind(events.find(e => e.Id === 'http-end')), null);
  }
});

test('HTTP acceptance alone never completes a queue request; its callback may arrive later', () => {
  const player = new TrafficPlayback();
  const events = [queueDispatch(), sent('transport', { QueueName: 'fibonacci1', CorrelationId: 'take' }),
    returned('accepted', { QueueName: 'fibonacci1', CorrelationId: 'transport' })];
  update(player, events); update(player, events, { now: 550 });
  assert.equal(count(player), 0);
  update(player, events, { now: 10_000 });
  assert.equal(count(player), 0);
  update(player, [...events, queueEnd('callback', { ReceivedAt: 10_001 })], { now: 10_001 });
  assert.deepEqual(paths(player), [['pod:fibonacci1/fibonacci1-00003', 'queue:fibonacci1']]);
  const orphan = new TrafficPlayback();
  update(orphan, [queueEnd()]); update(orphan, [queueEnd()], { now: 6000 });
  assert.equal(count(orphan), 0);
});

test('queue retry attempts retain separate dispatch and completion counts', () => {
  const player = new TrafficPlayback();
  const first = [queueDispatch(), queueEnd()];
  update(player, first); update(player, first, { now: 550 });
  assert.equal(count(player), 1);
  const events = [...first, queueDispatch('retry', { ReceivedAt: 1000 }),
    queueEnd('retry-end', { CorrelationId: 'retry', ReceivedAt: 1000 })];
  update(player, events, { now: 1000 });
  assert.deepEqual(paths(player), [['queue:fibonacci1', 'pod:fibonacci1/fibonacci1-00003']]);
  update(player, events, { now: 1450 });
  assert.deepEqual(paths(player), [['pod:fibonacci1/fibonacci1-00003', 'queue:fibonacci1']]);
  assert.equal(count(player), 1);
});
