// Node 24, production build, external Playwright Core: see docs/dashboard-validation.md.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { createRequire } from 'node:module';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { resolve, extname, sep } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { makeFixtures } from '../src/lib/fixtures.ts';
import { buildTopology } from '../src/lib/topology.ts';

const { chromium } = createRequire(resolve(process.env.PLAYWRIGHT_ROOT ?? '.', 'package.json'))('playwright-core');
if (!process.env.CHROMIUM_EXECUTABLE) throw new Error('Set CHROMIUM_EXECUTABLE to your Chromium executable.');
const root = fileURLToPath(new URL('../../wwwroot/', import.meta.url));
const out = process.env.DASHBOARD_DRAWER_RESULTS ?? resolve(tmpdir(), 'slimfaas-dashboard-drawer');
await mkdir(out, { recursive: true });
let fixture = makeFixtures(3, 2), activeLogs = 0, logOpens = 0, sourceRequests = 0, trafficOpens = 0;
let failLogs = false, unavailable = false;
const stateStreams = new Set();
const snapshot = () => ({ Functions: fixture.functions, Jobs: fixture.jobs, Queues: fixture.queues,
  SlimFaasNodes: fixture.slimFaasNodes, SlimFaasReplicas: 3, FrontEnabled: true });
const send = (response, event, value) => response.write(`event: ${event}\ndata: ${JSON.stringify(value)}\n\n`);
const sendState = () => { for (const response of stateStreams) send(response, 'state', snapshot()); };
const server = createServer(async (request, response) => {
  const url = new URL(request.url, 'http://localhost');
  if (url.pathname === '/status-functions-stream') {
    trafficOpens++; response.writeHead(200, { 'Content-Type': 'text/event-stream' });
    stateStreams.add(response); send(response, 'state', snapshot());
    response.on('close', () => stateStreams.delete(response)); return;
  }
  if (url.pathname === '/status-log-sources') {
    sourceRequests++;
    response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify({ Status: 'Available',
      Sources: unavailable ? [] : ['app', 'sidecar'].map(Container => ({ Id: `${url.searchParams.get('replica')}/${Container}`, Name: url.searchParams.get('replica'), Container })) }));
    return;
  }
  if (url.pathname === '/status-logs-stream') {
    logOpens++;
    if (failLogs) { response.writeHead(503).end(); return; }
    activeLogs++; response.writeHead(200, { 'Content-Type': 'text/event-stream' });
    send(response, 'log_state', { Status: 'Live', Session: `session-${logOpens}`, DroppedLines: 0, MaxLines: 10000, MaxBytes: 8388608 });
    const line = Id => ({ Id, Text: `${Id % 9 ? 'Information' : 'Warning'}: ${url.searchParams.get('source')} request ${Id} <script>plain text</script>`, TimestampMs: Date.now(), Truncated: false });
    for (let i = 0; i < 10; i++) send(response, 'log_batch', { Lines: Array.from({ length: 1000 }, (_, j) => line(i * 1000 + j + 1)) });
    let next = 10000;
    const interval = setInterval(() => send(response, 'log_batch', { Lines: [line(++next)] }), 100);
    response.on('close', () => { activeLogs--; clearInterval(interval); }); return;
  }
  const path = resolve(root, `.${url.pathname === '/' ? '/index.html' : url.pathname}`);
  if (!path.startsWith(root.endsWith(sep) ? root : root + sep)) { response.writeHead(403).end(); return; }
  try {
    const content = await readFile(path);
    response.writeHead(200, { 'Content-Type': ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml' })[extname(path)] ?? 'application/octet-stream' }).end(content);
  } catch { response.writeHead(404).end(); }
});
await new Promise(done => server.listen(0, '127.0.0.1', done));
let browser;
const until = async predicate => {
  for (let i = 0; i < 100; i++) { if (await predicate()) return; await new Promise(done => setTimeout(done, 50)); }
  assert.fail('Timed out waiting for a drawer assertion');
};
try {
  browser = await chromium.launch({ executablePath: process.env.CHROMIUM_EXECUTABLE, headless: true });
  const page = await browser.newPage({ viewport: { width: 1600, height: 1100 }, locale: 'en-US' });
  const errors = []; page.on('pageerror', error => errors.push(String(error)));
  const base = `http://127.0.0.1:${server.address().port}`;
  await page.goto(`${base}/#/overview`);
  const functionButton = page.getByRole('button', { name: 'fibonacci1', exact: true });
  await functionButton.click();
  const dialog = page.getByRole('dialog');
  assert.equal(await dialog.evaluate(element => element.matches(':modal')), true);
  assert.equal(Math.round((await dialog.boundingBox()).width), 760);
  await page.getByRole('searchbox', { name: 'Find an instance' }).fill('fibonacci1-00002');
  assert.equal(await dialog.locator('tbody tr').count(), 1);
  await page.keyboard.press('Escape');
  await until(async () => await dialog.count() === 0);
  assert.equal(await functionButton.evaluate(element => element === document.activeElement), true);
  await page.getByRole('button', { name: 'daily-report', exact: true }).click();
  await page.getByRole('button', { name: 'Close details' }).click();
  await page.goto(`${base}/#/live/traffic`);
  const search = page.getByRole('searchbox', { name: 'Find an actor' });
  await search.fill('fibonacci1');
  const replica = page.getByRole('button', { name: 'fibonacci1-00000', exact: true });
  await replica.click();
  const camera = () => page.locator('canvas').evaluate(element => ({ ...element.__zoom }));
  const selectedCamera = await camera();
  await page.keyboard.press('Escape'); await until(async () => await dialog.count() === 0);
  assert.deepEqual(await camera(), selectedCamera);
  const point = buildTopology(fixture.functions, fixture.jobs, fixture.queues, fixture.slimFaasNodes).byId.get('pod:fibonacci1/fibonacci1-00000');
  const position = await page.locator('canvas').evaluate((element, point) => ({ x: point.x * element.__zoom.k + element.__zoom.x, y: point.y * element.__zoom.k + element.__zoom.y }), { x: point.x, y: point.y });
  await page.locator('canvas').click({ position });
  await dialog.waitFor();
  await page.keyboard.press('Escape'); await until(async () => await dialog.count() === 0);
  assert.equal(await page.locator('canvas').evaluate(element => element === document.activeElement), true);
  await replica.click();
  const trafficConnections = trafficOpens;
  assert.equal(sourceRequests, 0);
  assert.equal(await page.getByRole('tab', { name: 'Details', exact: true }).getAttribute('aria-selected'), 'true');
  await page.getByRole('checkbox', { name: 'Isolate selection' }).check();
  await page.getByRole('tab', { name: 'Details', exact: true }).focus();
  await page.keyboard.press('ArrowRight');
  await until(() => activeLogs === 1);
  await page.waitForFunction(() => document.querySelector('.log-view__summary')?.textContent.includes('10,000 retained'));
  const viewport = page.locator('.log-view__viewport');
  const checkWindow = async () => {
    const height = await viewport.evaluate(element => element.clientHeight);
    assert.ok(await page.locator('.log-view__line').count() <= Math.ceil(height / 24) + 11);
    await until(() => viewport.evaluate(element => element.scrollHeight - element.scrollTop - element.clientHeight < 25));
  };
  await checkWindow();
  await page.getByRole('searchbox', { name: 'Find in logs' }).fill('Warning');
  await page.getByRole('searchbox', { name: 'Exclude text' }).fill('request 99');
  await page.getByRole('checkbox', { name: 'Case sensitive' }).check();
  assert.ok((await page.locator('.log-view__lines').innerText()).includes('Warning'));
  await page.getByRole('searchbox', { name: 'Find in logs' }).fill('');
  await page.getByRole('searchbox', { name: 'Exclude text' }).fill('');
  await page.getByRole('button', { name: 'Pause scrolling' }).click();
  await viewport.focus(); await page.keyboard.press('Home');
  await page.getByRole('button', { name: /Follow latest \(\d+ new\)/ }).waitFor();
  await page.getByRole('button', { name: /Follow latest/ }).click();
  await checkWindow();
  await page.screenshot({ path: resolve(out, 'drawer-logs.png') });
  await page.setViewportSize({ width: 1600, height: 1800 });
  await until(async () => await viewport.evaluate(element => element.clientHeight) > 1000);
  await checkWindow();
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.setViewportSize({ width: 390, height: 844 });
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
  assert.equal(Math.round((await dialog.boundingBox()).width), 390);
  await page.screenshot({ path: resolve(out, 'drawer-logs-mobile.png') });
  await page.getByRole('button', { name: 'Close details' }).click();
  await until(() => activeLogs === 0);
  assert.equal(await replica.evaluate(element => document.activeElement === element), true);
  assert.ok((await page.locator('.traffic__filters').innerText()).includes('selected actor only'));
  assert.equal(await page.locator('.table__row--selected').count(), 1);
  await page.setViewportSize({ width: 1600, height: 1100 });
  await page.getByRole('button', { name: /Open details/ }).click();
  await until(() => activeLogs === 1);
  const opened = logOpens;
  await page.getByRole('combobox', { name: /Log source/ }).selectOption('fibonacci1-00000/sidecar');
  await until(() => activeLogs === 1 && logOpens === opened + 1);
  await page.waitForFunction(() => document.querySelector('.log-view__line')?.textContent.includes('/sidecar'));
  await page.getByRole('tab', { name: 'Details', exact: true }).click();
  await until(() => activeLogs === 0);
  await page.mouse.click(20, 100);
  await until(async () => await dialog.count() === 0);
  assert.equal(await dialog.count(), 0);
  await page.getByRole('button', { name: 'fibonacci1-00001', exact: true }).click();
  assert.equal(await page.getByRole('tab', { name: 'Details', exact: true }).getAttribute('aria-selected'), 'true');
  await page.getByRole('tab', { name: 'Logs', exact: true }).click();
  await page.waitForFunction(() => document.querySelector('.log-view__line')?.textContent.includes('fibonacci1-00001'));
  assert.ok(!(await page.locator('.log-view__lines').innerText()).includes('fibonacci1-00000'));
  await page.keyboard.press('Escape'); await until(() => activeLogs === 0);
  await page.getByRole('button', { name: 'Pause', exact: true }).click();
  await page.getByRole('button', { name: /Open details/ }).click();
  await until(() => activeLogs === 1);
  fixture.functions[0].Pods = fixture.functions[0].Pods.filter(pod => pod.Name !== 'fibonacci1-00001'); sendState();
  await page.getByText('The latest snapshot no longer contains this instance. Logs are unavailable.').waitFor();
  await until(() => activeLogs === 0);
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Resume live', exact: true }).click();
  await page.getByRole('button', { name: 'Clear selection', exact: true }).click();
  assert.equal(await page.locator('.traffic__filters').count(), 0);
  unavailable = true;
  await replica.click(); await page.getByRole('tab', { name: 'Logs', exact: true }).click();
  await page.getByText('Logs unavailable', { exact: true }).first().waitFor();
  await page.keyboard.press('Escape'); unavailable = false; failLogs = true;
  await page.getByRole('button', { name: /Open details/ }).click();
  await page.getByText(/Logs unavailable · reconnecting/).first().waitFor();
  await page.keyboard.press('Escape'); const attempts = logOpens;
  await new Promise(done => setTimeout(done, 1200)); assert.equal(logOpens, attempts);
  failLogs = false;
  await replica.click(); await until(() => activeLogs === 1);
  fixture.functions[0].Pods = fixture.functions[0].Pods.filter(pod => pod.Name !== 'fibonacci1-00000'); sendState();
  await page.getByText('The latest snapshot no longer contains this instance. Logs are unavailable.').waitFor();
  await until(() => activeLogs === 0);
  await page.getByRole('button', { name: 'Close details' }).click();
  await until(async () => await dialog.count() === 0);
  assert.equal(await page.locator('canvas').evaluate(element => element === document.activeElement), true);
  assert.equal(trafficOpens, trafficConnections);
  assert.deepEqual(errors, []);
  await writeFile(resolve(out, 'checks.json'), JSON.stringify({ overview: true, keyboardFocus: true, canvasFocusFallback: true, preservedCamera: true, modal: true, mobile: true,
    resizedVirtualization: true, filtersAndFollow: true, onDemandStreams: true, closeAndTabCancellation: true,
    sourceAndInstanceSwitch: true, removedWhileTrafficPaused: true, reconnectCancellation: true, unchangedTrafficConnection: true,
    logOpens, sourceRequests, activeLogs, errors }, null, 2));
  console.log(`Drawer browser checks passed: ${out}`);
} finally { await browser?.close(); server.closeAllConnections(); await new Promise(done => server.close(done)); }
