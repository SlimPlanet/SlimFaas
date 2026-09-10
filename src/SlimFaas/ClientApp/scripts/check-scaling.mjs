// Production browser regression; Playwright stays outside dashboard dependencies.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { createRequire } from 'node:module';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { resolve, extname, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import { makeFixtures } from '../src/lib/fixtures.ts';

const { chromium } = createRequire(resolve(process.env.PLAYWRIGHT_ROOT ?? '.', 'package.json'))('playwright-core');
if (!process.env.CHROMIUM_EXECUTABLE) throw new Error('Set CHROMIUM_EXECUTABLE.');
const root = fileURLToPath(new URL('../../wwwroot/', import.meta.url));
const out = process.env.SCALING_BROWSER_RESULTS ?? resolve(tmpdir(), 'slimfaas-scaling-browser-results');
await mkdir(out, { recursive: true });
const fixture = makeFixtures(2, 1);
fixture.functions.push({ ...fixture.functions[0], Name: 'fibonacci2', Pods: [] });
const configuration = { ReplicaMax: 20, ScaleFromZero: true, Sources: [{ Name: 'jobs', Url: 'http://exporter/metrics' }],
  Triggers: [{ MetricName: 'pending_jobs', Query: 'sum(jobs_pending)', Source: 'jobs', MetricType: 'AverageValue', Threshold: 10 }],
  Behavior: { ScaleUp: { StabilizationWindowSeconds: 0, Policies: [{ Type: 'Pods', Value: 4, PeriodSeconds: 15 }] },
    ScaleDown: { StabilizationWindowSeconds: 300, Policies: [{ Type: 'Percent', Value: 100, PeriodSeconds: 15 }] } } };
fixture.functions.forEach(fn => { fn.Scale = configuration; });
const streams = new Set(), statusStreams = new Set(), requests = [], simulations = [];
let session = 'leader-one', fail = false, sequence = 0;
const decision = name => ({ Function: name, TimestampMs: Date.now(), CurrentReplicas: 0, ReadyReplicas: 0,
  RawTarget: 8, PolicyTarget: 4, StabilizedTarget: 4, Target: 4, Action: 'ScaleUp', Application: 'Accepted', AcceptedReplicas: 4,
  Reasons: [{ Code: 'PolicyLimited', Message: 'The default policy limits the first increase to four replicas.' }],
  Triggers: [{ Index: 0, Metric: 'pending_jobs', Source: 'jobs', Query: 'sum(jobs_pending)', MetricType: 'AverageValue', Threshold: 10,
    State: 'Valid', Value: 73, RawTarget: 8, Simulated: false, Detail: null }],
  Sources: [{ Name: 'jobs', State: 'Valid', LastSuccessMs: Date.now(), IntervalMs: 5000 }],
  LastHttpActivityMs: null, LastScheduleActivityMs: null, InactivityRemainingSeconds: 0, DependenciesReady: true, DependencyDemand: false });
const send = (response, event, value) => response.write(`event: ${event}\ndata: ${JSON.stringify(value)}\n\n`);
const server = createServer(async (request, response) => {
  const url = new URL(request.url, 'http://localhost'); requests.push(url.pathname + url.search);
  if (url.pathname === '/status-functions-stream') {
    response.writeHead(200, { 'Content-Type': 'text/event-stream' }); statusStreams.add(response);
    send(response, 'state', { Functions: fixture.functions, Jobs: [], Queues: [], SlimFaasNodes: [], SlimFaasReplicas: 3, FrontEnabled: true });
    response.on('close', () => statusStreams.delete(response)); return;
  }
  if (url.pathname === '/status-scaling-stream') {
    if (fail) { response.writeHead(503).end(); return; }
    const name = url.searchParams.get('function');
    response.writeHead(200, { 'Content-Type': 'text/event-stream' }); streams.add(response);
    const update = () => {
      const d = decision(name), Id = ++sequence;
      send(response, 'scaling_state', { Session: session, Status: 'Live', Function: name, ServerTimeMs: Date.now(),
        Decision: d, Configuration: configuration, ObservedReadyReplicas: 0, ObservedRequestedReplicas: 4, Truncated: false,
        RefreshIntervalMs: 100, Events: [{ Id, TimestampMs: Date.now(), CurrentReplicas: 0, Target: 4,
          RawTarget: 8, Action: 'ScaleUp', Application: 'Accepted', Reasons: ['PolicyLimited'], Signals: ['jobs:Valid'] }] });
    };
    update(); const timer = setInterval(update, 100);
    response.on('close', () => { streams.delete(response); clearInterval(timer); }); return;
  }
  if (url.pathname === '/debug/scaling/simulate') {
    let body = ''; for await (const chunk of request) body += chunk;
    const input = JSON.parse(body); simulations.push(input);
    const Current = decision(input.Function), raw = Math.ceil((input.Triggers[0].Value ?? 73) / input.Triggers[0].Threshold);
    response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify({ CapturedAtMs: Date.now(), Current: { ...Current, Application: 'Preview', AcceptedReplicas: null },
      Simulated: { ...Current, RawTarget: raw, Target: Math.min(raw, 4), Application: 'Preview', AcceptedReplicas: null,
        Triggers: [{ ...Current.Triggers[0], RawTarget: raw, Value: input.Triggers[0].Value ?? 73, Simulated: input.Triggers[0].Value !== null }] },
      Limitations: ['One decision only. Production is unchanged.'] })); return;
  }
  const path = resolve(root, `.${url.pathname === '/' ? '/index.html' : url.pathname}`);
  if (!path.startsWith(root.endsWith(sep) ? root : root + sep)) { response.writeHead(403).end(); return; }
  try {
    const body = await readFile(path);
    response.writeHead(200, { 'Content-Type': ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml' })[extname(path)] ?? 'application/octet-stream' }).end(body);
  } catch { response.writeHead(404).end(); }
});
await new Promise(done => server.listen(0, '127.0.0.1', done));
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_EXECUTABLE, headless: true });
const errors = [];
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  page.on('pageerror', error => errors.push(error.message));
  const first = fixture.functions[0].Name, second = fixture.functions[1].Name;
  await page.goto(`http://127.0.0.1:${server.address().port}/#/live/scaling?function=${first}`);
  await page.getByRole('heading', { name: 'Playground', exact: true }).waitFor();
  assert.ok(requests.filter(r => r.startsWith('/status-functions-stream')).every(r => r.endsWith('activity=false')));
  await page.getByLabel('Threshold', { exact: true }).fill('20');
  await page.getByRole('button', { name: 'Simulate next decision' }).click();
  await page.getByRole('heading', { name: 'Simulated scenario' }).waitFor();
  assert.equal(simulations[0].Triggers[0].Threshold, 20);
  assert.ok(!JSON.stringify(simulations[0]).includes('http://'));
  await page.screenshot({ path: resolve(out, 'scaling-desktop.png'), fullPage: true });
  await page.getByLabel('Simulated value').fill('0');
  await page.getByRole('button', { name: 'Simulate next decision' }).click();
  await page.getByText(/Simulated value 0/).waitFor();
  assert.equal(simulations.at(-1).Triggers[0].Value, 0);
  session = 'leader-two';
  await page.getByText('The leader session changed. The recent history has restarted.').waitFor();
  await page.getByRole('combobox', { name: 'Function', exact: true }).selectOption(second);
  await page.getByRole('heading', { name: 'Playground', exact: true }).waitFor();
  await page.waitForFunction(() => !document.querySelector('.scaling-playground__result'));
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('combobox', { name: 'Function', exact: true }).focus(); await page.keyboard.press('Tab');
  assert.notEqual(await page.evaluate(() => document.activeElement?.tagName), 'BODY');
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1));
  await page.screenshot({ path: resolve(out, 'scaling-mobile.png'), fullPage: true });
  fail = true; for (const response of streams) response.end();
  await page.getByText(/Showing the last received decision/).waitFor();
  await page.getByRole('link', { name: 'Overview', exact: true }).click();
  await page.getByRole('heading', { name: 'Overview', exact: true }).waitFor();
  await page.waitForFunction(() => !document.querySelector('.scaling-explorer'));
  await page.close();
  assert.equal(streams.size, 0);
  assert.deepEqual(errors, []);
  const result = { browserErrors: errors, simulations: simulations.length, activeScalingStreams: streams.size,
    desktop: '1440x1000', mobile: '390x844', trafficSubscriptionActivated: requests.some(r => r === '/status-functions-stream') };
  await writeFile(resolve(out, 'result.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result));
} finally {
  await browser.close();
  for (const response of [...streams, ...statusStreams]) response.end();
  await new Promise(done => server.close(done));
}
