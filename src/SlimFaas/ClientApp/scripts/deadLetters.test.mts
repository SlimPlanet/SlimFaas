import { test } from 'node:test';
import assert from 'node:assert/strict';
import { deadLetterPath, deadLetterLink, isDeadLetterSummary, readDeadLetterPreview, PREVIEW_BYTES } from '../src/lib/deadLetters.ts';
import { buildTopology, eventPath } from '../src/lib/topology.ts';
import { TrafficPlayback } from '../src/lib/traffic.ts';

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
test('retained DLQ actors survive function removal; removal events are not processing animations', () => {
  const dlq = { Function: 'billing', CaptureEnabled: false, FunctionExists: false, Count: 3,
    OldestFailedAt: null, NewestFailedAt: null, MaxAgeSeconds: 604800, MaxLength: 10000, CapturedAt: new Date().toISOString(), Revision: 8 };
  const model = buildTopology([], [], [{ Name: 'billing', Length: 0 }], [], [], [], [dlq]);
  assert.equal(model.byId.get('queue:dlq:billing')?.status, 'DLQ · 3 retained');
  const event = { Id: 'capture', Type: 'dead_lettered', Source: 'billing', Target: 'dlq:billing', QueueName: 'billing', TimestampMs: 1, NodeId: 'n', SourcePod: null, TargetPod: null };
  assert.deepEqual(eventPath(model, event), ['queue:billing', 'queue:dlq:billing']);
  assert.deepEqual(eventPath(model, { ...event, Type: 'dead_letter_requeued' }), ['queue:dlq:billing', 'queue:billing']);
  assert.equal(new TrafficPlayback().visualKind({ ...event, Type: 'dead_letter_deleted' }), null);
});
