import { test, expect } from './coverage.mjs';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { makeFixtures, makeObservationFixture, makeSpeechFunctions } from '../src/lib/fixtures.ts';
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
    window.__mapLabels = new Set();
    const fillText = CanvasRenderingContext2D.prototype.fillText;
    CanvasRenderingContext2D.prototype.fillText = function(text, ...args) {
      if (this.canvas.classList.contains('traffic-canvas__surface')) window.__mapLabels.add(text);
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
    } else if (path === '/' || path === '/slimfaas.svg' || /^\/assets\/[^/]+$/.test(path)) {
      const body = await readFile(resolve(root, path === '/' ? 'index.html' : path.slice(1)));
      response.writeHead(200, { 'Content-Type': path.endsWith('.css') ? 'text/css' : path.endsWith('.js') ? 'text/javascript' : path.endsWith('.svg') ? 'image/svg+xml' : 'text/html' });
      response.end(body);
    } else { response.writeHead(404); response.end(); }
  });
  servers.set(page, { server, clients });
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  await page.goto(`http://127.0.0.1:${server.address().port}/`);
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
  await page.getByRole('button', { name: 'Actors', exact: true }).click();
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
  await page.getByRole('button', { name: 'Filters', exact: true }).click();
  await page.getByRole('combobox', { name: /^Animation speed/ }).selectOption('Fast');
  await page.getByRole('button', { name: 'Filters', exact: true }).click();
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
  await page.getByRole('button', { name: 'Filters', exact: true }).click();
  await page.getByRole('combobox', { name: /^Animation speed/ }).selectOption('Slow');
  await page.getByRole('button', { name: 'Filters', exact: true }).click();
  await page.getByRole('button', { name: 'Actors', exact: true }).click();
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

test('home and Overview keep all three speech deployments and their own replicas distinct', async ({ page }, info) => {
  const data = frame();
  data.Functions = makeSpeechFunctions();
  await viewer(page, data);
  const surface = page.locator('canvas');
  const viewport = await surface.boundingBox();
  expect(viewport.width).toBe(1920);
  expect(viewport.height).toBeGreaterThan(900);
  await expect.poll(() => page.evaluate(() => ['speech', 'gemini25flash', 'piper'].every(
    suffix => [...window.__mapLabels].some(label => label.endsWith(suffix))))).toBe(true);
  await page.screenshot({ path: info.outputPath('speech-deployments.png') });
  await page.getByRole('button', { name: 'Actors', exact: true }).click();
  const camera = () => surface.evaluate(canvas => ({ ...canvas.__zoom }));
  const before = await camera();
  async function assertWorkloads() {
    for (const fn of data.Functions) {
      const trigger = page.getByRole('button', { name: fn.Name, exact: true });
      await trigger.click();
      const dialog = page.getByRole('dialog');
      await expect(dialog.getByRole('heading', { level: 2 })).toHaveText(fn.Name);
      await expect(dialog.locator('tbody tr')).toHaveCount(fn.NumberReady);
      await expect(dialog.locator('tbody tr td:first-child')).toHaveText(fn.Pods.map(p => p.Name));
      if (page.url().includes('actor=')) await page.goBack();
      else await page.keyboard.press('Escape');
      await expect(dialog).toHaveCount(0);
      await expect(trigger).toBeFocused();
    }
  }
  await assertWorkloads();
  expect(await camera()).toEqual(before);
  expect(servers.get(page).clients.size).toBe(1);
  await page.getByRole('button', { name: 'Close list' }).click();
  await page.getByRole('button', { name: 'Clear selection', exact: true }).click();
  await page.getByRole('button', { name: 'Views' }).click();
  await page.getByRole('link', { name: 'Overview' }).click();
  await assertWorkloads();
});

test('wake controls share cooldown and show HTTP errors, then follow live state while paused', async ({ page }) => {
  const data = frame();
  const fn = { ...data.Functions[0], Name: 'sleeping', PodType: 'StatefulSet', NumberReady: 0, NumberRequested: 0, Pods: [] };
  data.Functions = [fn];
  const calls = [];
  await page.route(/\/wake-functions?$|\/wake-function\/sleeping$/, route => {
    calls.push([route.request().method(), new URL(route.request().url()).pathname]);
    return route.fulfill({ status: route.request().url().endsWith('/wake-functions') ? 204 : 503 });
  });
  await viewer(page, data);
  const action = page.getByRole('button', { name: 'Wake up sleeping' });
  await action.dblclick();
  await expect(page.getByRole('alert')).toContainText('HTTP 503');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.getByRole('button', { name: 'Wake all functions' }).dblclick();
  expect(calls).toEqual([['POST', '/wake-function/sleeping'], ['POST', '/wake-functions']]);
  await page.getByRole('button', { name: 'Pause', exact: true }).click();
  await page.getByRole('button', { name: 'Actors', exact: true }).click();
  await page.getByRole('button', { name: 'sleeping', exact: true }).click();
  await expect(page.getByRole('dialog').getByRole('button', { name: 'Waking…' })).toBeDisabled();
  fn.NumberRequested = 1;
  await state(page, data);
  await expect(page.getByRole('dialog').getByRole('button', { name: /wak/i })).toHaveCount(0);
  await page.keyboard.press('Escape');
  await expect(action).toHaveCount(0);
});

test('the map shows pod requests at detail zoom, local job schedules and a filter-independent total', async ({ page }, info) => {
  const f = makeObservationFixture();
  const state = { Functions: f.functions, Jobs: f.jobs, Queues: f.queues, SlimFaasNodes: f.slimFaasNodes,
    Requests: f.requests, FrontEnabled: true };
  await viewer(page, state);
  const total = page.getByText('Requests · 2.8 CPU · 1.4 GiB RAM', { exact: true });
  await expect(total).toBeVisible();
  await expect(page.getByRole('link', { name: 'SlimFaas on GitHub' })).toHaveAttribute('href', 'https://github.com/SlimPlanet/SlimFaas');
  await page.getByRole('button', { name: 'Actors', exact: true }).click();
  await page.getByRole('button', { name: 'daily-report', exact: true }).first().click();
  const dialog = page.getByRole('dialog');
  const scheduled = await page.evaluate(seconds => new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'short', day: 'numeric',
    hour: 'numeric', minute: '2-digit', second: '2-digit', timeZoneName: 'short' }).format(seconds * 1000),
    f.jobs[0].Schedules[1].NextExecutionTimestamp);
  await expect(dialog.getByText('Next run · ' + scheduled)).toBeVisible();
  await dialog.getByRole('button', { name: 'Close details' }).click();
  await page.getByRole('row').filter({ has: page.getByRole('cell', { name: 'function', exact: true }) })
    .getByRole('button', { name: 'fibonacci1', exact: true }).click();
  await expect(dialog.getByText('Requests · 1 CPU · 512 MiB RAM')).toBeVisible();
  await dialog.getByRole('checkbox', { name: 'Isolate selection' }).check();
  await dialog.getByRole('button', { name: 'Close details' }).click();
  await page.getByRole('button', { name: 'Close list' }).click();
  await page.getByRole('button', { name: 'Show replicas' }).click();
  for (let i = 0; i < 2; i++) await page.getByRole('button', { name: 'Zoom in' }).click();
  await expect.poll(() => page.evaluate(() => [...window.__mapLabels])).toEqual(expect.arrayContaining([
    'Requests · 1 CPU · 512 MiB RAM', '250m CPU', '128 MiB RAM',
  ]));
  await expect(total).toBeVisible();
  await page.screenshot({ path: info.outputPath('replica-requests.png') });
  await page.setViewportSize({ width: 320, height: 700 });
  await expect(total).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(320);
});
