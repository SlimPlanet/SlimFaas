import assert from 'node:assert/strict';
import { test } from 'node:test';
import { appendActivity, paginate, ttlLabel, formatBytes, readSse } from '../src/lib/live.ts';
import { buildTopology, resolveSource, eventPath, filterNodes, selectedEvent } from '../src/lib/topology.ts';
import { makeFixtures, fixtureEvents, fixtureIdentity } from '../src/lib/fixtures.ts';

function model(replicas = 6, jobs = 4) {
  const f = makeFixtures(replicas, jobs);
  return buildTopology(f.functions, f.jobs, f.queues, f.slimFaasNodes);
}
test('20,000 instances have unique stable identities and non-overlapping reserved groups', () => {
  const topology = model(10000, 10000);
  assert.equal(topology.nodes.filter(n => n.parent && n.kind === 'job').length, 10000);
  assert.equal(topology.nodes.filter(n => n.parent && n.kind === 'function').length, 10000);
  assert.equal(topology.byId.size, topology.nodes.length);
  for (const group of topology.groups) {
    for (const child of group.children) {
      assert.ok(child.x >= group.x && child.x < group.x + group.width);
      assert.ok(child.y >= group.y && child.y < group.y + group.height);
      assert.equal(topology.byId.get(child.id), child);
    }
    for (const other of topology.groups) if (group !== other) assert.ok(group.x + group.width <= other.x || other.x + other.width <= group.x || group.y + group.height <= other.y || other.y + other.height <= group.y);
  }
  assert.deepEqual(topology.groups.map(g => [g.id, g.x, g.y]), model(10000, 10000).groups.map(g => [g.id, g.x, g.y]));
  assert.equal(filterNodes(topology, 'daily-report-slimfaas-job-09999')[0].id, 'run:daily-report-slimfaas-job-09999');
  assert.equal(filterNodes(topology, 'fibonacci1-09999')[0].id, 'pod:fibonacci1/fibonacci1-09999');
});
test('job routes retain associated functions and fall back to their configuration', () => {
  const topology = model();
  const event = fixtureEvents(3, 1)[0];
  const path = eventPath(topology, event);
  assert.deepEqual(path, ['node:slimfaas-0', 'pod:fibonacci1/fibonacci1-00003']);
  assert.deepEqual(eventPath(topology, { ...event, Type: 'request_in', Target: 'slimfaas' }), ['run:daily-report-slimfaas-job-00003', 'node:slimfaas-0']);
  assert.ok(selectedEvent(topology, { ...event, Type: 'request_in' }, 'job:daily-report'));
  assert.ok(selectedEvent(topology, event, 'function:fibonacci1'));
  assert.equal(resolveSource(topology, 'daily-report', 'daily-report-slimfaas-job-deleted'), 'job:daily-report');
  assert.ok(eventPath(topology, fixtureEvents(1, 1)[0]).includes('queue:fibonacci1'));
  assert.deepEqual(eventPath(topology, fixtureEvents(2, 1)[0]), ['queue:fibonacci1', 'pod:fibonacci1/fibonacci1-00002']);
});
test('opaque addresses resolve replicas while anonymous and unknown callers stay external', () => {
  const topology = model();
  assert.equal(resolveSource(topology, 'external', '127.0.0.1'), 'external:external');
  assert.equal(resolveSource(topology, 'external', '::1'), 'external:external');
  assert.equal(resolveSource(topology, 'external', 'unknown'), 'external:external');
  assert.equal(resolveSource(topology, 'external', null), 'external:external');
  assert.equal(resolveSource(topology, 'external', fixtureIdentity(1)), 'pod:fibonacci1/fibonacci1-00000');
  assert.equal(filterNodes(topology, fixtureIdentity(1))[0].id, 'pod:fibonacci1/fibonacci1-00000');
  assert.deepEqual(eventPath(topology, { ...fixtureEvents(3, 1)[0], SourcePod: fixtureIdentity(1), TargetPod: fixtureIdentity(2) }),
    ['node:slimfaas-0', 'pod:fibonacci1/fibonacci1-00001']);
  assert.deepEqual(eventPath(topology, { ...fixtureEvents(3, 1)[0], Type: 'request_in', SourcePod: fixtureIdentity(1), Target: 'slimfaas' }), ['pod:fibonacci1/fibonacci1-00000', 'node:slimfaas-0']);
  const refreshed = makeFixtures();
  refreshed.functions[0].Pods!.forEach((pod, i) => { pod.Identity = fixtureIdentity(i + 100); });
  const next = buildTopology(refreshed.functions, refreshed.jobs, refreshed.queues, refreshed.slimFaasNodes);
  assert.deepEqual([...next.byId.keys()], [...topology.byId.keys()], 'selection identities survive address-token rotation');
});
test('history and duplicate ids stay bounded during sustained bursts', () => {
  let history = [];
  for (let i = 0; i < 300; i++) {
    const batch = fixtureEvents(i * 1000, 1000);
    history = appendActivity(history, [...batch, ...batch]);
    assert.ok(history.length <= 5000);
    assert.equal(new Set(history.map(e => e.Id)).size, history.length);
  }
  assert.equal(history[0].Id, 'fixture-295000');
  assert.equal(history.at(-1).Id, 'fixture-299999');
});
test('pagination clamps after removals and keeps the final instance reachable', () => {
  const items = Array.from({ length: 10001 }, (_, i) => i);
  assert.deepEqual(paginate(items, 100).items, [10000]);
  assert.equal(paginate(items.slice(0, 100), 100).page, 0);
  assert.deepEqual(paginate([], 99), { items: [], page: 0, pages: 1 });
});
test('TTL and sizes distinguish permanent, expired, unknown and zero', () => {
  assert.equal(ttlLabel(null, 1000), 'Persistent');
  assert.equal(ttlLabel(999, 1000), 'Expired');
  assert.equal(ttlLabel(31000, 1000), '30s');
  assert.equal(ttlLabel(91000, 1000), '1m 30s');
  assert.equal(formatBytes(null), 'Unknown');
  assert.equal(formatBytes(0), '0 B');
  assert.equal(formatBytes(1024), '1.0 KiB');
});
test('SSE parser handles split UTF-8, CRLF and multiline data', async () => {
  const bytes = new TextEncoder().encode(': heartbeat\r\nevent: data_state\r\ndata: {"key":"é",\r\ndata: "count":1}\r\n\r\n');
  const frames = [];
  const body = new ReadableStream({ start(controller) { for (const byte of bytes) controller.enqueue(Uint8Array.of(byte)); controller.close(); } });
  await readSse(body, (event, data) => frames.push([event, JSON.parse(data)]));
  assert.deepEqual(frames, [['data_state', { key: 'é', count: 1 }]]);
});
test('SSE parser bounds multiline frames and cancels abandoned readers', async () => {
  let cancelled = false;
  const body = new ReadableStream({
    pull(controller) { controller.enqueue(new TextEncoder().encode(`data: ${'x'.repeat(100_000)}\n`)); },
    cancel() { cancelled = true; },
  });
  await assert.rejects(readSse(body, () => {}), /too large/);
  assert.equal(cancelled, true);
  assert.equal(body.locked, false);
});
