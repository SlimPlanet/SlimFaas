import { test, expect } from '@playwright/test';
import { startNative } from './native.mjs';
import { observeCanvas } from './canvas-observer.mjs';
import { buildTopology, jobQueues } from '../src/lib/topology.ts';

const turquoise = '#00897b';
test('native Data calls originate at the exact function and job, with ordered turquoise store hops', async ({ page, request }, info) => {
  const cluster = await startNative(info.outputPath('runtime.log'), 'data');
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    await page.addInitScript(observeCanvas);
    await page.addInitScript(() => {
      window.__dataCounterTexts = [];
      const original = CanvasRenderingContext2D.prototype.fillText;
      CanvasRenderingContext2D.prototype.fillText = function(text, ...args) {
        if (/^\d+ items · /.test(text)) window.__dataCounterTexts = [...new Set([...window.__dataCounterTexts, text])].slice(-20);
        return original.call(this, text, ...args);
      };
    });
    await page.goto(`${cluster.peerUrl}/#/live/traffic`);
    await expect.poll(() => page.evaluate(() => window.__traffic.state?.Functions?.find(fn => fn.Name === 'fibonacci1')?.NumberReady)).toBe(1);
    await expect(page.getByRole('button', { name: 'Event journal (0)', exact: true })).toBeVisible();
    await page.getByRole('combobox', { name: 'Animation speed', exact: true }).selectOption('Slow');
    const response = await request.post(`${cluster.url}/function/fibonacci1/call`, { data: { path: '/data/sets/function-kept', method: 'POST', body: 'function-data' } });
    expect(await response.json()).toEqual({ status: 200 });
    await expect.poll(() => page.evaluate(() => window.__traffic.events.filter(e => e.DataKind).length)).toBe(2);
    await expect.poll(() => page.evaluate(() => window.__traffic.frames.filter(frame => frame.markers.some(marker => marker.shape === 'data')).length)).toBeGreaterThan(10);
    const events = await page.evaluate(() => window.__traffic.events.filter(event => event.DataKind));
    expect(events[0].Source).toBe('fibonacci1');
    expect(events[0].SourcePod).toMatch(/^fibonacci1-/);
    expect(events[1].CorrelationId).toBe(events[0].Id);
    await expect.poll(() => page.evaluate(() => window.__traffic.frames.at(-1)?.markers.length)).toBe(0);
    const state = await page.evaluate(() => window.__traffic.state);
    const topology = buildTopology(state.Functions, state.Jobs, jobQueues(state.Jobs), state.SlimFaasNodes, [], [], [], [], state.DataSummary);
    const source = topology.byId.get(topology.sourcePods.get(events[0].SourcePod));
    const slim = topology.byId.get(`node:${events[0].NodeId}`);
    const store = topology.byId.get('sets:Set');
    const center = { x: store.x + store.width / 2, y: store.y + store.height / 2 };
    const frames = await page.evaluate(() => window.__traffic.frames);
    const segment = (marker, from, to) => {
      const dx = to.x - from.x, dy = to.y - from.y, length = dx * dx + dy * dy;
      const progress = ((marker.x - from.x) * dx + (marker.y - from.y) * dy) / length;
      return progress > .05 && progress < .95 && Math.hypot(marker.x - from.x - progress * dx, marker.y - from.y - progress * dy) < 2;
    };
    const times = (a, b) => frames.filter(frame => frame.markers.some(marker => marker.shape === 'data' && marker.color === turquoise && segment(marker, a, b))).map(frame => frame.time);
    const first = times(source, slim), second = times(slim, center);
    expect(first.length).toBeGreaterThan(3); expect(second.length).toBeGreaterThan(3);
    expect(Math.max(...first)).toBeLessThan(Math.min(...second));

    // Real operation coverage; a failed read remains in the journal without a deposit.
    for (const call of [
      { path: '/data/sets/function-kept', method: 'GET' }, { path: '/data/sets', method: 'GET' },
      { path: '/data/sets/number', method: 'POST', body: '1' },
      ...['incr', 'incrby?by=2', 'decr', 'decrby?by=1', 'incrbyfloat?by=1.5'].map(op => ({ path: `/data/sets/number/${op}`, method: 'POST' })),
      { path: '/data/sets/number', method: 'DELETE' },
      { path: '/data/files?id=function-kept', method: 'POST', body: 'file-data' },
      { path: '/data/files/function-kept', method: 'GET' }, { path: '/data/files', method: 'GET' },
      { path: '/data/files?id=deleted', method: 'POST', body: 'removed' }, { path: '/data/files/deleted', method: 'DELETE' },
      { path: '/data/sets/missing', method: 'GET' },
    ]) {
      const result = await request.post(`${cluster.url}/function/fibonacci1/call`, { data: call });
      expect(result.ok()).toBeTruthy();
      expect((await result.json()).status, call.path).toBe(call.path.endsWith('/missing') ? 404 : call.method === 'DELETE' ? 204 : 200);
    }
    const job = await request.post(`${cluster.url}/job/data-job`, { data: { Args: [] } });
    expect(job.ok()).toBeTruthy();
    await expect.poll(() => page.evaluate(() => window.__traffic.state?.Jobs?.find(job => job.Name === 'data-job')?.RunningJobs?.length ?? 0)).toBeGreaterThan(0);
    cluster.release();
    await expect.poll(() => page.evaluate(() => window.__traffic.events.filter(e => e.DataKind && e.Source === 'data-job').length)).toBe(4);
    const jobEvents = await page.evaluate(() => window.__traffic.events.filter(e => e.DataKind && e.Source === 'data-job'));
    expect(jobEvents.every(event => event.SourcePod.startsWith('data-job-slimfaas-job-'))).toBeTruthy();
    await expect.poll(() => page.evaluate(() => window.__traffic.state?.DataSummary?.Files)).toBe(2);
    await expect.poll(() => page.evaluate(() => window.__traffic.state?.DataSummary?.SetBytes)).toBe(21);
    const queued = await request.post(`${cluster.url}/async-function/fibonacci1/call`, { data: { path: '/data/sets/function-kept' } });
    expect(queued.status()).toBe(202);
    const publication = await request.post(`${cluster.url}/publish-stream/pedagogy`, { data: { example: true } });
    expect(publication.status()).toBe(202);
    await expect.poll(() => page.evaluate(() => window.__traffic.state?.Streams?.some(stream => stream.Name === 'pedagogy'))).toBe(true);
    await expect.poll(() => page.evaluate(() => window.__traffic.events.some(event => event.QueueName === 'fibonacci1' && event.Type === 'dequeue'))).toBe(true);
    await page.getByRole('button', { name: 'Fit map', exact: true }).click();
    await expect.poll(() => page.evaluate(() => window.__dataCounterTexts.includes('2 items · 21 B') && window.__dataCounterTexts.includes('2 items · 17 B'))).toBe(true);
    await page.screenshot({ path: info.outputPath('data-topology.png'), fullPage: true });
    await page.getByRole('combobox', { name: 'Event type', exact: true }).selectOption('data_access');
    await page.getByRole('button', { name: /^Event journal/ }).click();
    await expect(page.getByRole('table')).toContainText('HTTP 404');
    await expect(page.getByRole('table')).toContainText('incrbyfloat');
    await page.getByRole('button', { name: 'Show actors', exact: true }).click();
    await page.getByRole('searchbox', { name: 'Find an actor' }).fill('Files');
    await page.getByRole('button', { name: 'Files', exact: true }).click();
    await expect(page.getByRole('dialog')).toContainText('2 items');
    await page.getByRole('checkbox', { name: 'Isolate selection', exact: true }).check();
    await page.keyboard.press('Escape');
    await page.emulateMedia({ reducedMotion: 'reduce' });
    const surface = page.locator('canvas'); await surface.focus(); await page.keyboard.press('Home'); await page.keyboard.press('+');
    await page.setViewportSize({ width: 390, height: 844 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
    expect(errors).toEqual([]);
  } finally { await page.close(); await cluster.close(); }
});
