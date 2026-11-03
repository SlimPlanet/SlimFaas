import { test, expect } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { startNative } from './native.mjs';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

for (const mode of ['disabled', 'restricted']) {
  test(`Streams ${mode} UI hides navigation and stale live observations without requesting management APIs`, async ({ page }) => {
    let apiRequests = 0;
    const summary = { Name: 'protected-orders', Visibility: 'Public', RetainedMessageCount: 2, VisibleFunctionCount: 1,
      OldestAppendedAt: null, NewestAppendedAt: null, MaxAgeSeconds: 86400, MaxLength: 100000,
      EarliestMessageId: null, LatestMessageId: null, Generation: 'a', CapturedAt: '', Revision: 5 };
    const frame = { Functions: [], Jobs: [], Queues: [], SlimFaasNodes: [], FrontEnabled: true,
      Streams: [summary], StreamSubscriptions: [], StreamsAvailable: false,
      StreamsFrontEnabled: mode !== 'disabled', StreamsAccessible: mode !== 'restricted',
      RecentActivity: [{ Id: 'private-event', Type: 'stream_ack', Source: 'slimfaas', Target: 'stream:protected-orders',
        QueueName: null, TimestampMs: Date.now(), NodeId: 'node', StreamName: 'protected-orders' }] };
    const root = fileURLToPath(new URL('../../wwwroot/', import.meta.url));
    await page.route('http://slimfaas.test/**', async route => {
      const path = new URL(route.request().url()).pathname;
      if (path === '/status-functions-stream') return route.fulfill({ contentType: 'text/event-stream',
        body: 'event: state\ndata: ' + JSON.stringify(frame) + '\n\n' });
      if (path.startsWith('/data/streams')) { apiRequests++; return route.fulfill({ status: 404 }); }
      if (path === '/' || /^\/assets\/[^/]+$/.test(path)) return route.fulfill({
        path: resolve(root, path === '/' ? 'index.html' : path.slice(1)),
        contentType: path.endsWith('.css') ? 'text/css' : path.endsWith('.js') ? 'text/javascript' : 'text/html',
      });
      return route.fulfill({ status: 404 });
    });
    await page.goto('http://slimfaas.test/#/streams');
    await expect(page.getByText(mode === 'disabled' ? 'Streams UI is disabled.' : 'Stream management is restricted for this caller.', { exact: false })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Streams', exact: true })).toHaveCount(0);
    expect(apiRequests).toBe(0);
    await page.goto('http://slimfaas.test/#/live/traffic');
    await expect(page.getByRole('button', { name: 'Event journal (0)', exact: true })).toBeVisible();
    await expect(page.getByRole('option', { name: 'stream ack', exact: true })).toHaveCount(0);
    await expect(page.getByText('protected-orders', { exact: false })).toHaveCount(0);
    expect(apiRequests).toBe(0);
  });
}

test('mobile navigation remains visible and keyboard-accessible with DLQ and Streams enabled', async ({ page }) => {
  const root = fileURLToPath(new URL('../../wwwroot/', import.meta.url));
  const frame = { Functions: [], Jobs: [], Queues: [], SlimFaasNodes: [], RecentActivity: [], FrontEnabled: true,
    DeadLetters: [], DeadLettersFrontEnabled: true, DeadLettersAccessible: true,
    Streams: [], StreamSubscriptions: [], StreamsAvailable: true, StreamsFrontEnabled: true, StreamsAccessible: true };
  await page.route('http://slimfaas.test/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/status-functions-stream') return route.fulfill({ contentType: 'text/event-stream',
      body: 'event: state\ndata: ' + JSON.stringify(frame) + '\n\n' });
    if (path === '/' || /^\/assets\/[^/]+$/.test(path)) return route.fulfill({
      path: resolve(root, path === '/' ? 'index.html' : path.slice(1)),
      contentType: path.endsWith('.css') ? 'text/css' : path.endsWith('.js') ? 'text/javascript' : 'text/html',
    });
    return route.fulfill({ status: 404 });
  });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('http://slimfaas.test/#/live/traffic');
  await expect(page.getByRole('link', { name: 'Dead Letter Queues', exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Streams', exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  const links = page.locator('.navbar__navigation .navbar__link');
  await links.first().focus();
  for (let index = 0; index < await links.count(); index++) {
    await expect(links.nth(index)).toBeFocused();
    await expect(links.nth(index)).toBeVisible();
    await page.keyboard.press('Tab');
  }
});

async function openStreamSocket(url, name, configuration, sockets) {
  const socket = new WebSocket(url.replace(/^http/, 'ws') + '/ws'); sockets.push(socket);
  const messages = [];
  socket.addEventListener('message', event => messages.push(JSON.parse(event.data)));
  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', reject, { once: true });
  });
  socket.send(JSON.stringify({ type: 0, correlationId: randomUUID(), payload: { functionName: name, configuration } }));
  await expect.poll(() => messages.find(message => message.type === 1)).toBeTruthy();
  return { socket, messages, registration: messages.find(message => message.type === 1).payload };
}

test('Streams retain history, deliver independently, render inert bodies, and resume peer WebSocket subscriptions', async ({ browser, request }, info) => {
  const external = process.env.SLIMFAAS_STREAM_URL;
  const cluster = external ? {
    url: external, peerUrl: process.env.SLIMFAAS_STREAM_PEER_URL ?? external,
    check() {}, async close() {},
  } : await startNative(info.outputPath('streams-runtime.log'), 'streams');
  const page = await browser.newPage({ viewport: { width: 900, height: 900 } });
  const errors = [];
  const sockets = [];
  page.on('pageerror', error => errors.push(error.message));
  const rows = async () => {
    const response = await request.get(`${cluster.peerUrl}/data/streams/orders/functions`);
    return response.ok() ? (await response.json()).Items : [];
  };
  const name = `stream-ws-${randomUUID()}`;
  const connect = async url => {
    const connection = await openStreamSocket(url, name, { defaultVisibility: 'Public', numberParallelRequest: 1,
      numberParallelRequestPerPod: 1, subscribeStreams: [{ name: 'orders', visibility: 'Public' }] }, sockets);
    expect(connection.registration.success).toBe(true);
    expect(connection.registration.capabilities).toContain('streams-v1');
    return connection;
  };
  try {
    await expect.poll(async () => { cluster.check(); return (await rows()).filter(row => row.Active).length; }, { timeout: 90_000 }).toBeGreaterThanOrEqual(2);
    const body = JSON.stringify({ text: '<script>window.__streamExecuted=true</script>' + 'x'.repeat(70000) });
    const appended = await request.post(`${cluster.url}/publish-stream/orders/compute`, {
      data: body, headers: { 'Content-Type': 'application/json' },
    });

    await test.step('Peer replicas share callback capacity and reject disjoint conflicting registrations', async () => {
      const sockets = [];
      const name = `stream-capacity-${randomUUID()}`;
      const stream = `capacity-${randomUUID()}`;
      const configuration = { defaultVisibility: 'Public', numberParallelRequest: 1, numberParallelRequestPerPod: 1,
        subscribeStreams: [{ name: stream, visibility: 'Public' }] };
      const connect = (url, config = configuration) => openStreamSocket(url, name, config, sockets);
      const summary = async () => {
        const response = await request.get(`${cluster.peerUrl}/data/streams/${stream}/functions/${name}`);
        expect(response.ok()).toBe(true); return response.json();
      };
      try {
        const first = await connect(cluster.url), second = await connect(cluster.peerUrl);
        expect(first.registration.success).toBe(true); expect(second.registration.success).toBe(true);
        const conflict = await connect(cluster.peerUrl, { ...configuration,
          subscribeStreams: [{ name: `other-${randomUUID()}`, visibility: 'Public' }] });
        expect(conflict.registration.success).toBe(false);
        const publications = [];
        for (let i = 0; i < 2; i++) {
          const response = await request.post(`${cluster.url}/publish-stream/${stream}/compute`, { data: { iterations: 1 } });
          expect(response.status()).toBe(201); publications.push((await response.json()).MessageId);
        }
        const deliveries = () => [first, second].flatMap(connection => connection.messages
          .filter(message => message.type === 2).map(message => ({ connection, payload: message.payload })));
        await expect.poll(() => deliveries().length).toBe(1);
        const running = deliveries()[0];
        expect(running.payload.messageId).toBe(publications[0]);
        running.connection.socket.send(JSON.stringify({ type: 3, payload: { elementId: running.payload.elementId, statusCode: 202 } }));
        await expect.poll(async () => (await summary()).WaitingForCallbackCount).toBe(1);
        expect((await summary()).Lag).toBe(2); expect(deliveries()).toHaveLength(1);
        running.connection.socket.send(JSON.stringify({ type: 3, payload: { elementId: running.payload.elementId, statusCode: 200 } }));
        await expect.poll(() => deliveries().length).toBe(2);
        expect(deliveries().map(item => item.payload.messageId).sort()).toEqual([...publications].sort());
        const next = deliveries().find(item => item.payload.elementId !== running.payload.elementId);
        next.connection.socket.send(JSON.stringify({ type: 3, payload: { elementId: next.payload.elementId, statusCode: 200 } }));
        await expect.poll(async () => (await summary()).Lag).toBe(0);
        for (const socket of sockets) socket.close();
        await expect.poll(() => sockets.every(socket => socket.readyState === WebSocket.CLOSED)).toBe(true);
        const removed = await request.delete(`${cluster.peerUrl}/data/streams/${stream}/functions/${name}`);
        expect(removed.status()).toBe(204);
        const reconnected = await connect(cluster.url);
        expect(reconnected.registration.success).toBe(true);
        expect((await summary()).Lag).toBe(0);
        expect((await summary()).StartSequence).toBe(2);
      } finally {
        for (const socket of sockets) socket.close();
      }
    });
    expect(appended.status()).toBe(201);
    const id = (await appended.json()).MessageId;
    await expect.poll(async () => (await rows()).filter(row => row.Active && row.Function !== name).every(row => row.Lag === 0),
      { timeout: 120_000 }).toBe(true);
    await page.goto(`${cluster.peerUrl}/#/streams/orders/messages/${id}`);
    await expect(page.getByRole('heading', { name: `Message ${id}`, exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Preview body (maximum 65,536 bytes)' }).click();
    await expect(page.getByText('Preview truncated at 65,536 bytes.', { exact: false })).toBeVisible();
    const text = await page.locator('.streams__preview').textContent();
    expect(Buffer.byteLength(text)).toBe(65536);
    expect(await page.evaluate(() => window.__streamExecuted)).toBeUndefined();
    await page.setViewportSize({ width: 390, height: 844 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    const first = await connect(cluster.url);
    const generation = (await rows()).find(row => row.Function === name).Generation;
    const publish = await request.post(`${cluster.url}/publish-stream/orders/compute`, { data: { iterations: 1 } });
    expect(publish.status()).toBe(201);
    await expect.poll(() => first.messages.filter(message => message.type === 2).length).toBe(1);
    const delivery = first.messages.find(message => message.type === 2).payload;
    expect(delivery.streamName).toBe('orders');
    expect(delivery.messageId).toBe((await publish.json()).MessageId);
    first.socket.send(JSON.stringify({ type: 3, payload: { elementId: delivery.elementId, statusCode: 202 } }));
    await expect.poll(async () => (await rows()).find(row => row.Function === name)?.WaitingForCallbackCount).toBe(1);
    first.socket.send(JSON.stringify({ type: 3, payload: { elementId: delivery.elementId, statusCode: 200 } }));
    await expect.poll(async () => (await rows()).find(row => row.Function === name)?.Lag).toBe(0);
    first.socket.close();
    await expect.poll(() => first.socket.readyState).toBe(WebSocket.CLOSED);
    const offline = await request.post(`${cluster.peerUrl}/publish-stream/orders/compute`, { data: { iterations: 1 } });
    expect(offline.status()).toBe(201);
    const second = await connect(cluster.peerUrl);
    await expect.poll(() => second.messages.filter(message => message.type === 2).length).toBe(1);
    const resumed = second.messages.find(message => message.type === 2).payload;
    expect(resumed.messageId).toBe((await offline.json()).MessageId);
    expect((await rows()).find(row => row.Function === name).Generation).toBe(generation);
    second.socket.send(JSON.stringify({ type: 3, payload: { elementId: resumed.elementId, statusCode: 200 } }));
    await expect.poll(async () => (await rows()).find(row => row.Function === name)?.Lag).toBe(0);
    expect(errors).toEqual([]);
    await page.screenshot({ path: info.outputPath('streams-mobile.png'), fullPage: true });
  } finally {
    for (const socket of sockets) socket.close();
    await page.close(); await cluster.close();
  }
});
