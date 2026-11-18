// Record pixels from the production dashboard and observations from the same cluster.
import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { promisify } from 'node:util';
import { createHash } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { setTimeout as sleep } from 'node:timers/promises';
import { chromium } from '../../src/SlimFaas/ClientApp/node_modules/playwright/index.mjs';

const root = fileURLToPath(new URL('../../', import.meta.url));
const work = root + 'artifacts/live-stream-video';
const stage = process.argv[2];
assert(['sync', 'async', 'subscriptions'].includes(stage));
const dir = work + '/' + stage;
await mkdir(dir, { recursive: true });
const args = ['--kubeconfig', work + '/kubeconfig', '--context', 'kind-slimfaas-film', '-n', 'slimfaas-demo'];
const run = promisify(execFile);
const kube = async (...cmd) => (await run('kubectl', [...args, ...cmd])).stdout.trim();
const url = 'http://127.0.0.1:39490';
async function post(path, body, status) {
  const r = await fetch(url + path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  const text = await r.text();
  assert.equal(r.status, status, path + ' ' + text);
  return text;
}
async function until(test, reason, seconds = 120) {
  const deadline = performance.now() + seconds * 1000;
  while (performance.now() < deadline) {
    const result = await test();
    if (result) return result;
    await sleep(250);
  }
  throw new Error('Timed out waiting for ' + reason);
}
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 704 }, deviceScaleFactor: 1 });
const evidence = {};
const marks = {};
const frames = [];
let running = true;
let recording;
const logs = new Map();
const ticks = () => Math.round(performance.now() - start);
const start = performance.now();
function mark(key) {
  marks[key] = ticks();
  console.log(stage, key, marks[key]);
}
async function observe(fn) {
  return page.evaluate(name => {
    const s = window.filmState;
    const f = s?.Functions.find(f => f.Name === name);
    return f && { ready: f.NumberReady, requested: f.NumberRequested, pods: f.Pods?.map(p => p.Name) ?? [],
      queue: s.Queues.find(q => q.Name === name)?.Length ?? 0 };
  }, fn);
}
const idle = async () => {
  const s = await observe('fibonacci1');
  return s && !s.requested && !s.ready && !s.pods.length && !s.queue;
};
async function takeFrames() {
  let lastHash;
  let file;
  while (running) {
    const t = ticks();
    const png = await page.screenshot();
    const hash = createHash('sha256').update(png).digest('hex');
    if (hash !== lastHash) {
      file = t + '.png';
      await writeFile(dir + '/' + file, png);
      lastHash = hash;
      frames.push([t, file]);
    }
    await sleep(Math.max(0, 100 - (ticks() - t)));
  }
}
async function startWorkerLogs(pods) {
  for (const pod of pods) {
    if (logs.has(pod)) continue;
    const child = spawn('kubectl', [...args, 'logs', '-f', pod, '--pod-running-timeout=90s']);
    const observer = { child, text: '' };
    logs.set(pod, observer);
    child.stdout.on('data', data => { observer.text += data; });
    child.on('close', code => { if (code && !observer.text) logs.delete(pod); });
  }
}
async function subscribers() {
  const pods = JSON.parse(await kube('get', 'pod', '-l', 'app in (fibonacci3,fibonacci4)', '-o', 'json'));
  return Promise.all(pods.items.map(async pod => ({
    name: pod.metadata.name, fn: pod.metadata.labels.app,
    value: await kube('get', '--raw', '/api/v1/namespaces/slimfaas-demo/pods/' + pod.metadata.name + ':5000/proxy/tour/observation')
  })));
}
try {
  await page.addInitScript(() => {
    const Native = window.EventSource;
    window.EventSource = class extends Native {
      constructor(...args) {
        super(...args);
        this.addEventListener('state', e => { window.filmState = JSON.parse(e.data); });
      }
    };
  });
  await page.goto(url);
  await page.waitForSelector('canvas.traffic-canvas__surface');
  await until(() => observe('fibonacci1'), 'dashboard inventory');
  if (stage === 'subscriptions') {
    await until(async () => (await observe('fibonacci3'))?.ready === 2 && (await observe('fibonacci4'))?.ready === 2, 'four ready subscribers');
  }
  await page.getByRole('button', {name: 'Fit map'}).click();
  if (stage === 'subscriptions') {
    // Zoom towards the two recipient deployments until individual pods are drawn.
    await page.mouse.move(838, 544);
    await page.mouse.wheel(0, -445);
    await sleep(350);
  }
  await page.mouse.move(0, 0);
  recording = takeFrames();
  if (stage === 'sync') {
    await until(idle, 'cold baseline');
    mark('idle');
    await sleep(9000);
    mark('wake');
    await sleep(650);
    const reply = JSON.parse(await post('/function/fibonacci1/fibonacci', { input: 10 }, 200));
    assert.equal(reply.result ?? reply.Result, 55);
    evidence.reply = reply;
    await until(async () => (await observe('fibonacci1')).ready === 1, 'one ready pod');
    await sleep(2500);
    mark('sleep');
    await until(idle, 'return to zero');
    await sleep(2500);
  } else if (stage === 'async') {
    await until(idle, 'cold baseline');
    mark('burst');
    await sleep(950);
    const accepted = await Promise.all(Array.from({length: 30}, () => post('/async-function/fibonacci1/fibonacci', {input: 48}, 202)));
    evidence.accepted = accepted.length;
    const samples = evidence.samples = [];
    async function sample() {
      const s = await observe('fibonacci1');
      samples.push({ms: ticks(), ready: s.ready, requested: s.requested, queue: s.queue});
      await startWorkerLogs(s.pods);
      return s;
    }
    await until(async () => (await sample()).queue > 0, 'a visible backlog');
    await sleep(3500);
    mark('scale');
    await until(async () => (await sample()).ready >= 2, 'multiple ready workers');
    await sleep(6500);
    mark('drain');
    await until(async () => {
      const s = await sample();
      return !s.ready && !s.requested && !s.pods.length && !s.queue;
    }, 'the drained deployment to scale to zero', 1200);
    evidence.completed = [...logs.values()].reduce((n, log) => n + (log.text.match(/Fibonacci output: 4807526976/g) ?? []).length, 0);
    assert.equal(evidence.completed, 30, 'completed Fibonacci(48) computations');
    await sleep(3500);
  } else {
    await until(async () => {
      const r = await fetch(url + '/data/streams/agent-output/functions');
      if (!r.ok) return false;
      const rows = (await r.json()).Items;
      return rows?.length === 2 && rows.every(row => row.Active && row.AcknowledgedThrough === 0);
    }, 'fresh stream subscriptions');
    evidence.before = await subscribers();
    assert.equal(evidence.before.filter(r => r.value.endsWith('catalog-v1 pending')).length, 4);
    mark('subscribers');
    await sleep(12000);
    mark('broadcast');
    await sleep(850);
    await post('/publish-event/tool-catalog/tour/tools/invalidate', {}, 204);
    evidence.invalidated = await until(async () => {
      const r = await subscribers();
      return r.length === 4 && r.every(p => p.value.endsWith('invalidated pending')) && r;
    }, 'four invalidated caches');
    await sleep(7800);
    mark('stream');
    await sleep(8500);
    mark('ack');
    await sleep(650);
    evidence.receipt = JSON.parse(await post('/publish-stream/agent-output/tour/results/result-48', {result: 4807526976}, 202));
    evidence.cursors = await until(async () => {
      const r = await fetch(url + '/data/streams/agent-output/functions');
      const rows = (await r.json()).Items;
      return rows?.length === 2 && rows.every(s => s.Lag === 0 && s.AcknowledgedThrough > 0) && rows;
    }, 'independent stream acknowledgements');
    evidence.after = await subscribers();
    for (const fn of ['fibonacci3', 'fibonacci4']) {
      assert.equal(evidence.after.filter(p => p.fn === fn && p.value.endsWith('result-48')).length, 1);
    }
    await sleep(5000);
    await page.getByRole('button', {name: 'Views'}).click();
    await page.getByRole('link', {name: 'Streams', exact: true}).click();
    await page.getByRole('link', {name: 'agent-output', exact: true}).click();
    await page.getByRole('link', {name: 'fibonacci4', exact: true}).waitFor();
    await page.mouse.move(0, 0);
    await sleep(5200);
  }
  mark('end');
} finally {
  running = false;
  await recording;
  for (const log of logs.values()) log.child.kill();
  await writeFile(dir + '/take.json', JSON.stringify({marks, frames, evidence}, null, 2));
  await browser.close();
}
