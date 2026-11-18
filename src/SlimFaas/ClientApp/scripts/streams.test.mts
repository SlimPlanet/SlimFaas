import { test } from 'node:test';
import assert from 'node:assert/strict';
import { streamPath, streamLink, isStreamSummary, isStreamFunction, newerStreamObservation, reconcileStreamCatalog } from '../src/lib/streams.ts';
import { buildTopology, eventPath, observedFunctions } from '../src/lib/topology.ts';
import { TrafficPlayback } from '../src/lib/traffic.ts';

test('Stream routes encode resource segments and guards reject incomplete counters', () => {
  assert.equal(streamPath('a/b'), '/data/streams/a%2Fb');
  assert.equal(streamLink('a/b', 'messages', 'x?y'), '#/streams/a%2Fb/messages/x%3Fy');
  assert.equal(isStreamSummary({ Name: 'orders' }), false);
  assert.equal(isStreamFunction({ Function: 'billing' }), false);
});
test('Stream refreshes cannot replace a known revision with an older one in the same generation', () => {
  const current = { Revision: 8, Generation: 'current' };
  assert.equal(newerStreamObservation(current, { Revision: 7, Generation: 'current' }), current);
  assert.deepEqual(newerStreamObservation(current, { Revision: 1, Generation: 'recreated' }),
    { Revision: 1, Generation: 'recreated' });
  assert.deepEqual(newerStreamObservation(null, current), current);
  const page = { Revision: 8, Items: ['known'] };
  assert.equal(newerStreamObservation(page, { Revision: 7, Items: [] }), page);
});
test('Catalog revisions are per Stream, so deleting the highest revision must not retain deleted rows', () => {
  const first = { Name: 'first', Generation: 'a', Revision: 8 };
  const previous = { Items: [first, { Name: 'deleted', Generation: 'b', Revision: 20 }],
    NextCursor: null, CapturedAt: '', Revision: 20 };
  const incoming = { Items: [{ ...first, Revision: 7 }], NextCursor: null, CapturedAt: '', Revision: 7 };
  const reconciled = reconcileStreamCatalog(previous, incoming);
  assert.deepEqual(reconciled.Items, [first]);
  assert.equal(reconciled.Revision, 7);
  assert.deepEqual(reconcileStreamCatalog(previous, { ...incoming, Items: [], Revision: 0 }).Items, []);
});
test('Stream actors are not queues or fallback functions; ACK never deletes retained messages', () => {
  const summary = { Name: 'orders', Visibility: 'Public', RetainedMessageCount: 2, VisibleFunctionCount: 1,
    OldestAppendedAt: null, NewestAppendedAt: null, MaxAgeSeconds: 86400, MaxLength: 100000,
    EarliestMessageId: null, LatestMessageId: null, Generation: 'a', CapturedAt: '', Revision: 5 };
  const event = { Id: 'ack', Type: 'stream_ack', Source: 'slimfaas', Target: 'stream:orders', QueueName: null,
    TimestampMs: 1, NodeId: 'n', SourcePod: null, TargetPod: null, StreamName: 'orders', StreamFunction: 'billing' };
  const topology = buildTopology([], [], [], [], [], [], [], [summary]);
  assert.equal(topology.byId.get('stream:orders')?.kind, 'stream');
  assert.equal(topology.byId.has('queue:orders'), false);
  assert.deepEqual(observedFunctions([], [event]), []);
  assert.deepEqual(eventPath(topology, event), ['slimfaas:slimfaas', 'stream:orders']);
  const playback = new TrafficPlayback();
  assert.equal(playback.visualKind(event), 'reply');
  for (const type of ['stream_waiting', 'stream_retry', 'stream_failed', 'stream_retention', 'stream_deleted'])
    assert.equal(playback.visualKind({ ...event, Type: type }), null);
});
