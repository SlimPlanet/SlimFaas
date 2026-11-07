import { test, expect } from '@playwright/test';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { makeFixtures } from '../src/lib/fixtures.ts';
import { buildTopology, observedFunctions } from '../src/lib/topology.ts';
import { observeCanvas } from './canvas-observer.mjs';

const servers = new Map();
test.afterEach(async ({ page }) => {
  const live = servers.get(page);
  if (!live) return;
  for (const response of live.clients) response.end();
  live.server.closeAllConnections();
  await new Promise((resolve, reject) => live.server.close(error => error ? reject(error) : resolve()));
  servers.delete(page);
});

async function viewer(page, frame) {
  const root = fileURLToPath(new URL('../../wwwroot/', import.meta.url));
  await page.addInitScript(observeCanvas);
  await page.addInitScript(() => {
    window.__queueCounters = [];
    const fillText = CanvasRenderingContext2D.prototype.fillText;
    CanvasRenderingContext2D.prototype.fillText = function(text, ...args) {
      if (this.canvas.classList.contains('traffic-canvas__surface') && /^(Async|DLQ) · /.test(text)) {
        window.__queueCounters.push({ text, x: args[0], y: args[1], scale: this.getTransform().a / devicePixelRatio });
        window.__queueCounters = window.__queueCounters.slice(-20);
      }
      return fillText.call(this, text, ...args);
    };
  });
  const clients = new Set();
  const server = createServer(async (request, response) => {
    const path = new URL(request.url, 'http://localhost').pathname;
    if (path === '/status-functions-stream') {
      response.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache' });
      response.write('event: state\ndata: ' + JSON.stringify(frame) + '\n\n');
      clients.add(response); response.on('close', () => clients.delete(response));
    } else if (path === '/' || /^\/assets\/[^/]+$/.test(path)) {
      const body = await readFile(resolve(root, path === '/' ? 'index.html' : path.slice(1)));
      response.writeHead(200, { 'Content-Type': path.endsWith('.css') ? 'text/css' : path.endsWith('.js') ? 'text/javascript' : 'text/html' });
      response.end(body);
    } else { response.writeHead(404); response.end(); }
  });
  servers.set(page, { server, clients });
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  await page.goto(`http://127.0.0.1:${server.address().port}/#/live/traffic`);
  await expect(page.getByRole('button', { name: 'Event journal (0)', exact: true })).toBeVisible();
}

function frame() {
  const fixture = makeFixtures(1, 0);
  return { Functions: fixture.functions, Jobs: [], Queues: [], SlimFaasNodes: fixture.slimFaasNodes,
    RecentActivity: [], FrontEnabled: true, DeadLetters: [], DeadLettersFrontEnabled: true, DeadLettersAccessible: true,
    Streams: [], StreamSubscriptions: [], StreamsAvailable: true, StreamsFrontEnabled: true, StreamsAccessible: true };
}
function send(page, type, data) {
  for (const response of servers.get(page).clients) response.write(`event: ${type}\ndata: ${JSON.stringify(data)}\n\n`);
}
async function state(page, frame) {
  send(page, 'state', frame);
}

test('one live queue box renders independent async and DLQ counters, including zero and removed access', async ({ page }) => {
  const data = frame();
  const name = data.Functions[0].Name;
  data.Queues = [{ Name: name, Length: 0 }];
  data.DeadLetters = [{ Function: name, CaptureEnabled: true, FunctionExists: true, Count: 3,
    OldestFailedAt: null, NewestFailedAt: null, MaxAgeSeconds: 604800, MaxLength: 10000, CapturedAt: '', Revision: 8 }];
  await viewer(page, data);
  const queue = page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'queue', exact: true }) });
  await expect(queue).toHaveCount(1);
  await expect(queue).toContainText('0 queued · DLQ 3 retained');
  await expect(page.getByRole('button', { name: `dlq:${name}`, exact: true })).toHaveCount(0);
  await expect.poll(() => page.evaluate(() => window.__queueCounters.slice(-2).map(item => item.text))).toEqual(['Async · 0 queued', 'DLQ · 3 retained']);
  const counters = await page.evaluate(() => window.__queueCounters.slice(-2));
  expect(counters[0].x).toBe(counters[1].x);
  expect((counters[1].y - counters[0].y) * counters[0].scale).toBeCloseTo(18);
  await queue.getByRole('button', { name, exact: true }).click();
  await expect(page.getByRole('link', { name: 'Open dead letters', exact: true })).toHaveAttribute('href', `#/dead-letters/${name}`);
  await page.keyboard.press('Escape');
  data.Queues[0].Length = 7; data.DeadLetters[0].Count = 0;
  await state(page, data);
  await expect(queue).toContainText('7 queued · DLQ 0 retained');
  data.DeadLetters = []; data.DeadLettersAccessible = false;
  await state(page, data);
  await expect(queue).toContainText('7 queued');
  await expect(queue).not.toContainText('DLQ');
});

test('a live WebSocket Stream delivery still moves to its connection when metadata arrives after a Fast trip', async ({ page }) => {
  const data = frame();
  data.Functions = [{ ...data.Functions[0], Name: 'ws-render', PodType: 'WebSocket',
    Pods: [{ Name: 'ws-connection', Identity: 'connection', Ready: true, Status: 'Running' }] }];
  await viewer(page, data);
  await page.getByRole('combobox', { name: /^Animation speed/ }).selectOption('Fast');
  const delivery = { Id: 'delivery', Type: 'stream_delivery', Source: 'stream:fresh', Target: 'ws-render',
    SourcePod: null, TargetPod: 'connection', QueueName: null, NodeId: 'slimfaas-0', TimestampMs: Date.now(),
    StreamName: 'fresh', StreamFunction: 'ws-render', CorrelationId: 'attempt', ElementId: 'attempt' };
  const ack = { ...delivery, Id: 'ack', Type: 'stream_ack', Source: 'ws-render', Target: 'stream:fresh',
    SourcePod: 'connection', TargetPod: null };
  await page.evaluate(() => { window.__receiptAt = performance.now(); });
  send(page, 'activity_batch', [delivery, ack]);
  await expect(page.getByRole('button', { name: 'Event journal (2)', exact: true })).toBeVisible();
  await expect.poll(() => page.evaluate(() => performance.now() - window.__receiptAt > 600)).toBe(true);
  expect(await page.evaluate(() => window.__traffic.frames.some(frame => frame.markers.some(marker => marker.shape === 'square')))).toBe(false);
  data.Streams = [{ Name: 'fresh', Visibility: 'Public', RetainedMessageCount: 1, VisibleFunctionCount: 1,
    OldestAppendedAt: null, NewestAppendedAt: null, MaxAgeSeconds: 86400, MaxLength: 100000,
    EarliestMessageId: null, LatestMessageId: null, Generation: 'generation', CapturedAt: '', Revision: 1 }];
  await state(page, data);
  const topology = buildTopology(data.Functions, [], [], data.SlimFaasNodes, [], [], [], data.Streams);
  const stream = topology.byId.get('stream:fresh');
  const from = { x: stream.x + stream.width / 2, y: stream.y + stream.height / 2 };
  const to = topology.byId.get('pod:ws-render/ws-connection');
  const progress = (point, start, end) => {
    const value = (point.x - start.x) / (end.x - start.x);
    return Math.abs(point.y - start.y - value * (end.y - start.y)) < 2 && value > 0.02 && value < 0.98 ? value : null;
  };
  await expect.poll(() => page.evaluate(() => window.__traffic.frames.some(frame => frame.markers.some(marker => marker.outlined)))).toBe(true);
  await expect(page.locator('.traffic-canvas__activity')).toContainText('0 events / 0 markers');
  const frames = await page.evaluate(() => window.__traffic.frames);
  for (const [start, end, predicate] of [
    [from, to, marker => marker.shape === 'square'],
    [to, from, marker => marker.shape === 'circle' && marker.outlined],
  ]) {
    const samples = frames.flatMap(frame => frame.markers.filter(predicate).map(marker => progress(marker, start, end))).filter(value => value !== null);
    expect(samples.length).toBeGreaterThan(5);
    expect(Math.max(...samples) - Math.min(...samples)).toBeGreaterThan(0.5);
  }
});


test('Data sources precede inventory and revoking metadata discards active, queued and buffered Data traffic', async ({ page }) => {
  const data = frame();
  data.Functions = [];
  data.DataSummary = { Sets: 1, Files: 0, SetBytes: 4, FileBytes: 0, UnknownFileSizes: 0 };
  await viewer(page, data);
  await page.getByRole('combobox', { name: /^Animation speed/ }).selectOption('Slow');
  const ingress = { Id: 'peer-source', Type: 'data_request', Source: 'new-source', SourcePod: 'replica-before-snapshot',
    Target: 'slimfaas', TargetPod: null, QueueName: null, NodeId: data.SlimFaasNodes[0].Name, TimestampMs: Date.now(), DataKind: 'sets', DataOperation: 'write' };
  const access = { ...ingress, Id: 'peer-store', Type: 'data_access', CorrelationId: ingress.Id, DataStatusCode: 200 };
  send(page, 'activity_batch', [ingress, access]);
  const sourceRow = page.getByRole('row').filter({ has: page.getByRole('button', { name: 'new-source', exact: true }) });
  await expect(sourceRow).toContainText('Observed traffic · inventory unknown');
  await expect.poll(() => page.evaluate(() => window.__traffic.frames.some(frame => frame.markers.some(marker => marker.shape === 'data')))).toBe(true);
  const topology = buildTopology([], [], [], data.SlimFaasNodes, observedFunctions([], [ingress]));
  const source = topology.byId.get('observed:new-source/replica-before-snapshot');
  const first = await page.evaluate(() => window.__traffic.frames.flatMap(frame => frame.markers).find(marker => marker.shape === 'data'));
  expect(Math.hypot(first.x - source.x, first.y - source.y)).toBeLessThan(30);

  // Publish a receipt and revocation in one SSE delivery, before the client's flush timer.
  data.DataSummary = null;
  send(page, 'activity_batch', [{ ...ingress, Id: 'buffered-before-revoke' }]);
  await state(page, data);
  const sets = page.getByRole('row').filter({ has: page.getByRole('button', { name: 'Set', exact: true }) });
  await expect(sets).toContainText('Metadata unavailable');
  await expect(page.getByRole('button', { name: 'Event journal (0)', exact: true })).toBeVisible();
  await page.evaluate(() => { window.__dataRevokedAt = performance.now(); });
  send(page, 'activity_batch', [{ ...ingress, Id: 'late-private' }]);
  await expect.poll(() => page.evaluate(() => performance.now() - window.__dataRevokedAt > 1700)).toBe(true);
  expect(await page.evaluate(() => window.__traffic.frames.filter(frame => frame.time >= window.__dataRevokedAt)
    .some(frame => frame.markers.some(marker => marker.shape === 'data')))).toBe(false);

  data.DataSummary = { Sets: 1, Files: 0, SetBytes: 4, FileBytes: 0, UnknownFileSizes: 0 };
  await state(page, data);
  await expect(sets).toContainText('1 items · 4 B');
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await expect(page.getByRole('button', { name: 'Event journal (0)', exact: true })).toBeVisible();
  expect(await page.evaluate(() => window.__traffic.frames.at(-1).markers.some(marker => marker.shape === 'data'))).toBe(false);
  send(page, 'activity_batch', [{ ...ingress, Id: 'fresh-authorized' }]);
  await expect(page.getByRole('button', { name: 'Event journal (1)', exact: true })).toBeVisible();
  await expect.poll(() => page.evaluate(() => window.__traffic.frames.at(-1).markers.some(marker => marker.shape === 'data'))).toBe(true);
});
