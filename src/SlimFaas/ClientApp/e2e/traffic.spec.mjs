import { test as base, expect } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import { buildTopology, observedQueues } from '../src/lib/topology.ts';
import { startNative } from './native.mjs';
import { observeCanvas } from './canvas-observer.mjs';

const test = base.extend({
  scenario: ['cold', { option: true }],
  cluster: async ({ scenario }, use, info) => {
    const cluster = await startNative(info.outputPath('runtime.log'), scenario);
    try {
      await expect.poll(async () => {
        cluster.check();
        try { return (await fetch(`${cluster.url}/ready`)).status; } catch { return 0; }
      }, { timeout: 90_000 }).toBe(200);
      await use(cluster);
    } finally { await cluster.close(); }
  },
});

async function viewer(browser, url) {
  const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(observeCanvas);
  await page.goto(`${url}/#/live/traffic`);
  await expect.poll(() => page.evaluate(() => window.__traffic.state?.Functions?.find(fn => fn.Name === 'fibonacci4')?.NumberReady)).toBe(2);
  await page.getByRole('combobox', { name: /^Animation speed/ }).selectOption('Slow');
  await expect(page.getByRole('button', { name: 'Event journal (0)', exact: true })).toBeVisible();
  return { page, errors };
}
function geometry(state, event, events = []) {
  const usedQueues = new Set(events.filter(e => e.Type === 'enqueue' || e.Type === 'dequeue').map(e => e.Target));
  const model = buildTopology(state.Functions, state.Jobs, state.Queues.filter(q => q.Length > 0 || usedQueues.has(q.Name)),
    state.SlimFaasNodes, [], observedQueues(state.Queues, events));
  const point = id => {
    const node = model.byId.get(id);
    expect(node, id).toBeTruthy();
    return { x: node.x + (node.parent ? 0 : node.width / 2), y: node.y + (node.parent ? 0 : node.height / 2) };
  };
  return { external: point('external:external'), slim: point(`node:${event.NodeId}`),
    pod: event.TargetPod ? point(model.pods.get(`${event.Target}/${event.TargetPod}`)) : null,
    queue: event.QueueName ? point(`queue:${event.QueueName}`) : null,
    sourcePod: event.SourcePod ? point(model.sourcePods.get(event.SourcePod)) : null };
}
function onSegment(point, from, to) {
  const dx = to.x - from.x, dy = to.y - from.y;
  const length = dx * dx + dy * dy;
  const progress = ((point.x - from.x) * dx + (point.y - from.y) * dy) / length;
  const distance = Math.hypot(point.x - from.x - progress * dx, point.y - from.y - progress * dy);
  return distance < 2 && progress >= -0.01 && progress <= 1.01 ? progress : null;
}
function trip(frames, from, to, predicate) {
  const samples = frames.flatMap(frame => frame.markers.filter(predicate).flatMap(marker => {
    const progress = onSegment(marker, from, to);
    return progress !== null && progress > 0.03 && progress < 0.97 ? [{ time: frame.time, progress, marker }] : [];
  }));
  expect(samples.length).toBeGreaterThan(5);
  expect(samples[0].progress).toBeLessThan(0.2);
  expect(samples.at(-1).progress).toBeGreaterThan(0.8);
  for (let i = 1; i < samples.length; i++) expect(samples[i].progress).toBeGreaterThanOrEqual(samples[i - 1].progress - 0.01);
  // No duplicated markers on the same physical hop in any frame.
  expect(new Set(samples.map(sample => sample.time)).size).toBe(samples.length);
  expect(samples.every(sample => sample.marker.count === 1)).toBe(true);
  return { first: samples[0].time, last: samples.at(-1).time, color: samples[0].marker.color };
}
async function capture(page, info, name) {
  const observation = await page.evaluate(() => window.__traffic);
  await writeFile(info.outputPath(`${name}.json`), JSON.stringify(observation));
  await page.screenshot({ path: info.outputPath(`${name}.png`), fullPage: true });
  return observation;
}

test('cold HTTP call waits at SlimFaas, then sends and returns in order on local and peer viewers', async ({ browser, request, cluster }, info) => {
  const viewers = await Promise.all([viewer(browser, cluster.url), viewer(browser, cluster.peerUrl)]);
  let pending;
  try {
    for (const { page } of viewers) expect(await page.evaluate(() => window.__traffic.state.Functions.find(fn => fn.Name === 'fibonacci1').NumberReady)).toBe(0);
    // Send once: only readiness and observation are polled, never the POST.
    pending = request.post(`${cluster.url}/function/fibonacci1/fibonacci`, { data: { input: 10 }, timeout: 90_000 });
    pending.catch(() => {});
    for (const { page } of viewers) await expect.poll(() => page.evaluate(() => window.__traffic.frames.some(frame => frame.waiting))).toBe(true);
    for (const { page } of viewers) {
      const observation = await page.evaluate(() => window.__traffic);
      expect(observation.events.some(event => event.Type === 'request_out')).toBe(false);
      await capture(page, info, page === viewers[0].page ? 'waiting-local' : 'waiting-peer');
    }
    cluster.release();
    expect((await pending).status()).toBe(200);
    for (const [index, { page, errors }] of viewers.entries()) {
      await expect.poll(() => page.evaluate(() => window.__traffic.events.filter(event => event.Type === 'request_end').length)).toBe(2);
      await expect(page.locator('.traffic-canvas__activity')).toContainText('0 events / 0 markers');
      const { frames, events, state } = await capture(page, info, `sync-${index}`);
      const ingress = events.find(e => e.Type === 'request_in');
      const dispatch = events.find(e => e.Type === 'request_out');
      const response = events.find(e => e.Type === 'request_end' && e.Target === 'fibonacci1');
      const completion = events.find(e => e.Type === 'request_end' && e.Target === 'slimfaas');
      expect(dispatch.CorrelationId).toBe(ingress.Id);
      expect(response.CorrelationId).toBe(dispatch.Id);
      expect(completion.CorrelationId).toBe(ingress.Id);
      const { external, slim, pod } = geometry(state, dispatch);
      const outbound = marker => marker.shape === 'circle' && !marker.outlined;
      const reply = marker => marker.shape === 'circle' && marker.outlined;
      const enter = trip(frames, external, slim, outbound), send = trip(frames, slim, pod, outbound);
      const receive = trip(frames, pod, slim, reply), exit = trip(frames, slim, external, reply);
      expect(send.first).toBeGreaterThan(enter.last);
      expect(receive.first).toBeGreaterThan(send.last);
      expect(exit.first).toBeGreaterThan(receive.last);
      expect(enter.color).toBe('#0000ff'); expect(receive.color).not.toBe(send.color);
      expect(errors).toEqual([]);
    }
  } finally {
    cluster.release();
    if (pending) await pending.catch(() => {});
    for (const { page } of viewers) await page.close();
  }
});

test('publication draws one incoming trip followed by one delivery to each of two replicas', async ({ browser, request, cluster }, info) => {
  const { page, errors } = await viewer(browser, cluster.peerUrl);
  try {
    const response = await request.post(`${cluster.url}/publish-event/fibo-public/fibonacci`, { data: { input: 10 } });
    expect(response.status()).toBe(204);
    await expect.poll(() => page.evaluate(() => window.__traffic.events.filter(e => e.Type === 'request_end').length)).toBe(3);
    await expect.poll(() => page.evaluate(() => window.__traffic.frames.some(frame => frame.markers.length === 2))).toBe(true);
    await page.screenshot({ path: info.outputPath('publication-delivery.png'), fullPage: true });
    await expect(page.locator('.traffic-canvas__activity')).toContainText('0 events / 0 markers');
    const { frames, state, events } = await capture(page, info, 'publication');
    const ingress = events.find(e => e.Type === 'request_in');
    const deliveries = events.filter(e => e.Type === 'event_publish' && e.Target === 'fibonacci4');
    expect(deliveries).toHaveLength(2);
    expect(new Set(deliveries.map(e => e.TargetPod)).size).toBe(2);
    const { external, slim } = geometry(state, ingress);
    const publication = marker => marker.shape === 'diamond';
    const enter = trip(frames, external, slim, publication);
    const targets = deliveries.map(delivery => geometry(state, delivery).pod);
    const distance = (point, target) => Math.abs((target.x - slim.x) * (point.y - slim.y) - (target.y - slim.y) * (point.x - slim.x)) / Math.hypot(target.x - slim.x, target.y - slim.y);
    for (const pod of targets) {
      // Fan-out lines share their origin. Assign samples to the nearest physical
      // line instead of confusing adjacent destinations near the source.
      const recipient = marker => publication(marker) && targets.every(other => distance(marker, pod) <= distance(marker, other));
      expect(trip(frames, slim, pod, recipient).first).toBeGreaterThan(enter.last);
    }
    expect(frames.flatMap(frame => frame.markers).every(publication)).toBe(true);
    await page.locator('canvas').focus(); await page.keyboard.press('ArrowRight'); await page.keyboard.press('Home');
    await page.setViewportSize({ width: 390, height: 844 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.evaluate(() => { window.__traffic.frames = []; });
    expect((await request.post(`${cluster.url}/publish-event/fibo-public/fibonacci`, { data: { input: 10 } })).status()).toBe(204);
    await expect.poll(() => page.evaluate(() => window.__traffic.frames.filter(f => f.markers.length === 2).length)).toBeGreaterThan(2);
    const staticFrames = await page.evaluate(() => window.__traffic.frames);
    for (const marker of staticFrames.flatMap(frame => frame.markers)) {
      expect([slim, ...targets].some(target => Math.hypot(target.x - marker.x, target.y - marker.y) < 2)).toBe(true);
    }
    await page.screenshot({ path: info.outputPath('mobile.png'), fullPage: true });
    expect(errors).toEqual([]);
  } finally { await page.close(); }
});

test.describe('recursive function calls', () => {
  test.use({ scenario: 'recursive' });
  test('one external request receives one response; internal calls belong to known Fibonacci pods', async ({ browser, request, cluster }, info) => {
    const viewers = await Promise.all([viewer(browser, cluster.url), viewer(browser, cluster.peerUrl)]);
    try {
      for (const { page } of viewers) await expect.poll(() => page.evaluate(() => window.__traffic.state.Functions.find(f => f.Name === 'fibonacci3')?.NumberReady)).toBe(2);
      const response = await request.post(`${cluster.url}/function/fibonacci3/fibonacci-recursive`, { data: { input: 12 }, timeout: 60_000 });
      expect(response.status()).toBe(200);
      expect(await response.json()).toEqual({ result: 144, numberCall: 287 });
      await viewers[0].page.screenshot({ path: info.outputPath('recursive-traffic.png'), fullPage: true });
      for (const [index, { page, errors }] of viewers.entries()) {
        await expect.poll(() => page.evaluate(() => window.__traffic.events.filter(e => e.Type === 'request_end').length)).toBe(574);
        await expect.poll(() => page.evaluate(() => window.__traffic.frames.some(f => f.markers.some(m => m.outlined)))).toBe(true);
        await expect(page.locator('.traffic-canvas__activity')).toContainText('0 events / 0 markers');
        const { events, frames, state } = await capture(page, info, `recursive-${index}`);
        const incoming = events.filter(e => e.Type === 'request_in');
        const external = incoming.filter(e => e.Source === 'external');
        expect(incoming).toHaveLength(287); expect(external).toHaveLength(1);
        const pods = state.Functions.find(f => f.Name === 'fibonacci3').Pods.map(p => p.Name);
        for (const ingress of incoming.filter(e => e.Source !== 'external')) {
          expect(ingress.Source).toBe('fibonacci3'); expect(pods).toContain(ingress.SourcePod);
          const completion = events.filter(e => e.Type === 'request_end' && e.CorrelationId === ingress.Id);
          expect(completion).toHaveLength(1);
          expect(completion[0].SourcePod).toBe(ingress.SourcePod);
        }
        expect(events.filter(e => e.Type === 'request_end' && e.Source === 'external')).toHaveLength(1);
        const position = geometry(state, external[0]);
        const enter = trip(frames, position.external, position.slim, m => m.shape === 'circle' && !m.outlined);
        const exit = trip(frames, position.slim, position.external, m => m.outlined);
        expect(exit.first).toBeGreaterThan(enter.last); expect(enter.color).toBe('#0000ff'); expect(exit.color).not.toBe(enter.color);
        expect(frames.some(f => f.markers.some(m => m.count > 1))).toBe(true);
        for (const pod of pods) {
          const source = geometry(state, { ...external[0], SourcePod: pod }).sourcePod;
          expect(frames.some(f => f.markers.some(m => {
            const p = onSegment(m, source, position.slim); return p !== null && p > 0.2 && p < 0.8;
          }))).toBe(true);
        }
        expect(errors).toEqual([]);
      }
    } finally { for (const { page } of viewers) await page.close(); }
  });
});

for (const callback of [false, true]) test.describe(callback ? 'async callback' : 'async HTTP completion', () => {
  test.use({ scenario: callback ? 'async-callback' : 'async' });
  test('the actual replica returns once to its queue after logical completion', async ({ browser, request, cluster }, info) => {
    const viewers = await Promise.all([viewer(browser, cluster.url), viewer(browser, cluster.peerUrl)]);
    try {
      for (const { page } of viewers) await expect.poll(() => page.evaluate(() => window.__traffic.state.Functions.find(f => f.Name === 'fibonacci1')?.NumberReady)).toBe(1);
      const response = await request.post(`${cluster.url}/async-function/fibonacci1/${callback ? 'computeWithCallback' : 'fibonacci'}`, { data: { input: 12 } });
      expect(response.status()).toBe(202);
      if (callback) {
        await expect.poll(() => cluster.callbacksReceived).toBe(1);
        for (const { page } of viewers) {
          await expect.poll(() => page.evaluate(() => window.__traffic.events.filter(e => e.Type === 'request_end' && e.Target === 'fibonacci1').length)).toBe(1);
          await expect(page.locator('.traffic-canvas__activity')).toContainText('0 events / 0 markers');
          const before = await capture(page, info, page === viewers[0].page ? 'callback-held-local' : 'callback-held-peer');
          expect(before.events.filter(e => e.Type === 'request_end' && e.QueueName && e.Target === 'slimfaas')).toHaveLength(0);
          expect(before.frames.flatMap(f => f.markers).filter(m => m.outlined)).toHaveLength(0);
        }
        await cluster.releaseCallbacks();
      }
      for (const [index, { page, errors }] of viewers.entries()) {
        await expect.poll(() => page.evaluate(() => window.__traffic.events.filter(e => e.Type === 'request_end' && e.QueueName && e.Target === 'slimfaas').length)).toBe(1);
        await expect.poll(() => page.evaluate(() => window.__traffic.frames.some(f => f.markers.some(m => m.outlined)))).toBe(true);
        if (index === 0) await page.screenshot({ path: info.outputPath('async-queue-return.png'), fullPage: true });
        await expect(page.locator('.traffic-canvas__activity')).toContainText('0 events / 0 markers');
        const { events, frames, state } = await capture(page, info, `async-${index}`);
        const dispatches = events.filter(e => e.Type === 'dequeue'); expect(dispatches).toHaveLength(1);
        const dispatch = dispatches[0];
        const completion = events.find(e => e.Type === 'request_end' && e.Target === 'slimfaas');
        expect(completion.CorrelationId).toBe(dispatch.Id); expect(completion.SourcePod).toBe(dispatch.TargetPod);
        const { pod, queue, slim } = geometry(state, dispatch, events);
        const outward = trip(frames, queue, pod, m => m.shape === 'square');
        const home = trip(frames, pod, queue, m => m.outlined);
        expect(home.first).toBeGreaterThan(outward.last); expect(home.color).not.toBe(outward.color);
        // Every drawn reply lies on the replica-to-queue route. No technical
        // response creates another trip, including toward any SlimFaas node.
        for (const marker of frames.flatMap(f => f.markers).filter(m => m.outlined)) {
          expect(onSegment(marker, pod, queue)).not.toBeNull(); expect(marker.count).toBe(1);
        }
        expect(Math.hypot(queue.x - slim.x, queue.y - slim.y)).toBeGreaterThan(10);
        expect(errors).toEqual([]);
      }
    } finally { for (const { page } of viewers) await page.close(); }
  });
});
