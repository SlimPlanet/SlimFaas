import { test } from 'node:test';
import assert from 'node:assert/strict';
import { deadLetterPath, deadLetterLink, isDeadLetterSummary, readDeadLetterPreview, PREVIEW_BYTES } from '../src/lib/deadLetters.ts';
import { buildTopology, eventPath, selectedEvent, filterNodes, jobQueues } from '../src/lib/topology.ts';
import { TrafficPlayback } from '../src/lib/traffic.ts';
import { makeFixtures } from '../src/lib/fixtures.ts';

test('DLQ identifiers stay in one path segment', () => {
  assert.equal(deadLetterPath('billing/test', 'a?b'), '/data/dead-letters/billing%2Ftest/messages/a%3Fb');
  assert.equal(deadLetterLink('billing'), '#/dead-letters/billing');
  assert.equal(isDeadLetterSummary({ Function: 'billing' }), false);
});
test('body preview is bounded by bytes even if a server ignores Range', async () => {
  let cancelled = false;
  const response = new Response(new ReadableStream({
    start(controller) { controller.enqueue(new TextEncoder().encode('<script>' + 'x'.repeat(PREVIEW_BYTES * 3))); },
    cancel() { cancelled = true; },
  }));
  const preview = await readDeadLetterPreview(response);
  assert.equal(new TextEncoder().encode(preview.text).length, PREVIEW_BYTES);
  assert.equal(preview.truncated, true); assert.equal(cancelled, true);
  assert.ok(preview.text.startsWith('<script>'));
});
test('ranged previews detect truncation from full length and empty bodies remain empty', async () => {
  const ranged = await readDeadLetterPreview(new Response('hello', { status: 206, headers: { 'Content-Range': 'bytes 0-4/999999' } }));
  assert.equal(ranged.text, 'hello'); assert.equal(ranged.truncated, true);
  assert.equal((await readDeadLetterPreview(new Response(''))).text, '');
});
test('one async queue actor keeps both counters and DLQ access after function removal', () => {
  const dlq = { Function: 'billing', CaptureEnabled: false, FunctionExists: false, Count: 3,
    OldestFailedAt: null, NewestFailedAt: null, MaxAgeSeconds: 604800, MaxLength: 10000, CapturedAt: new Date().toISOString(), Revision: 8 };
  const model = buildTopology([], [], [{ Name: 'billing', Length: 0 }], [], [], [], [dlq]);
  assert.equal(model.groups.filter(group => group.kind === 'queue').length, 1);
  assert.equal(model.byId.has('queue:dlq:billing'), false);
  assert.equal(model.byId.get('queue:billing')?.status, '0 queued · DLQ 3 retained');
  assert.equal(model.byId.get('queue:billing')?.queueLength, 0);
  assert.equal(model.byId.get('queue:billing')?.deadLetterCount, 3);
  assert.equal(model.actors.get('dlq:billing'), 'queue:billing');
  assert.equal(filterNodes(model, 'dead letter')[0]?.id, 'queue:billing');
  const event = { Id: 'capture', Type: 'dead_lettered', Source: 'billing', Target: 'dlq:billing', QueueName: 'billing', TimestampMs: 1, NodeId: 'n', SourcePod: null, TargetPod: null };
  assert.deepEqual(eventPath(model, event), ['queue:billing']);
  assert.deepEqual(eventPath(model, { ...event, Type: 'dead_letter_requeued' }), ['queue:billing']);
  assert.equal(selectedEvent(model, event, 'queue:billing'), true);
  assert.equal(new TrafficPlayback().visualKind({ ...event, Type: 'dead_letter_deleted' }), null);
});
test('combined queue counters remain independent, preserve job separation and omit inaccessible DLQ counts', () => {
  const fixture = makeFixtures();
  const name = fixture.functions[0].Name;
  const dlq = { Function: name, CaptureEnabled: true, FunctionExists: true, Count: 3,
    OldestFailedAt: null, NewestFailedAt: null, MaxAgeSeconds: 604800, MaxLength: 10000, CapturedAt: '', Revision: 8 };
  const job = { ...fixture.jobs[0], Name: name, QueueLength: 2 };
  for (const length of [0, 7]) for (const count of [0, 3]) {
    const model = buildTopology(fixture.functions, [job], [{ Name: name, Length: length }, ...jobQueues([job])],
      fixture.slimFaasNodes, [], [name], [{ ...dlq, Count: count }]);
    assert.equal(model.groups.filter(group => group.kind === 'queue').length, 2);
    assert.equal(model.byId.get(`queue:${name}`).status, `${length} queued · DLQ ${count} retained`);
    assert.equal(model.byId.get(`queue:job:${name}`).status, '2 queued');
    assert.equal(model.byId.get(`queue:job:${name}`).deadLetterCount, undefined);
  }
  const restricted = buildTopology(fixture.functions, [], fixture.queues, fixture.slimFaasNodes);
  assert.equal(restricted.byId.get(`queue:${name}`).deadLetterCount, undefined);
  assert.equal(restricted.byId.get(`queue:${name}`).status, '12 queued');
  const unknown = buildTopology([], [], [], [], [], [], [dlq]);
  assert.equal(unknown.byId.get(`queue:${name}`).status, 'Queue length unknown · DLQ 3 retained');
  assert.equal(unknown.groups.filter(group => group.kind === 'queue').length, 1);
});
