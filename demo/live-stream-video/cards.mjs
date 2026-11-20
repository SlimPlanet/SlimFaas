// Rasterize the editorial cards offline with the same Chromium used for the live takes.
import { chromium } from 'playwright-core';
import { mkdir, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const here = new URL('./', import.meta.url);
const work = new URL('artifacts/', here);
const destination = new URL('cards/', work);
await mkdir(destination, { recursive: true });
const scenes = JSON.parse(await readFile(new URL('scenes.json', here), 'utf8'));
const css = await readFile(new URL('cards.css', here), 'utf8');
const take = JSON.parse(await readFile(new URL('async/take.json', work), 'utf8'));
const peak = take.evidence.samples.find(sample => sample.ready === 4);
const docs = {
  'sync-title': 'functions', 'queue-title': 'async-functions', 'broadcast-title': 'events',
  'stream-title': 'streams', jobs: 'jobs', sets: 'data-sets', files: 'data-files',
};

const flows = {
  'sync-title': ['HTTP caller', [['Fibonacci', 1, 1]]],
  'queue-title': ['Work queue', [['Workers', 4, 4]]],
  'broadcast-title': ['Catalog event', [['Evaluator', 2, 2], ['Audit', 2, 2]]],
  'stream-title': ['Agent result', [['Evaluator', 1, 2], ['Audit', 1, 2]]],
  jobs: ['Job queue', [['Running slots', 2, 2]]],
  sets: ['Tool result', [['Cache key', 0, 0]]],
  files: ['Artifact bytes', [['File ID', 0, 0]]],
};
function escape(value) {
  return value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
}
function diagram(key) {
  const [origin, targets] = flows[key];
  const branches = targets.map(([label, active, count], index) => {
    const y = targets.length === 1 ? 83 : 42 + 82 * index;
    let dots = '';
    for (let i = 0; i < count; i++) {
      dots += `<circle cx="${545 + i * 19}" cy="${y+12}" r="5.8" fill="${i < active ? '#23ad80' : '#d5dce6'}"/>`;
    }
    return `<path data-flow d="M221 83 C326 83 325 ${y} 447 ${y}" fill="none" stroke="#9aa7ef" stroke-width="3"/>
      <rect x="446" y="${y-33}" width="220" height="66" rx="12" fill="#fff" stroke="#c4cede" stroke-width="2"/>
      <text x="556" y="${y + (count ? -2 : 7)}">${label}</text>${dots}
      <circle data-pulse r="6" fill="#2827f4"/> `;
  }).join('');
  return `<svg viewBox="0 0 680 166" class="film-card__diagram"><rect x="13" y="48" width="208" height="70" rx="12" fill="#e8eaff"/>
    <text x="117" y="90">${origin}</text>${branches}</svg>`;
}
function content(scene) {
  const brand = Boolean(scene.art);
  const [first, second, third] = scene.card.map(escape);
  const section = brand ? first
    : (['jobs', 'sets', 'files'].includes(scene.key) ? 'More over HTTP' : 'Over HTTP');
  const recap = `<aside class="film-card__panel"><div class="film-card__panel-head">More over HTTP</div>
    <dl class="film-card__summary"><dt>Jobs</dt><dd>Queue and schedule batch runs</dd>
    <dt>Sets</dt><dd>Cache small values by key</dd><dt>Files</dt><dd>Stream temporary artifacts by key</dd></dl>
    <div class="film-card__panel-foot">${third}</div></aside>`;
  const panel = scene.art === 'recap' ? recap : brand
    ? `<aside class="film-card__panel"><div class="film-card__panel-head"><span class="film-card__signal"></span>Live Stream dashboard</div>
       <div class="film-card__capture"></div>
       <div class="film-card__panel-foot">Fibonacci deployment · Recorded on Kubernetes</div></aside>`
    : `<aside class="film-card__panel"><div class="film-card__panel-head">HTTP request</div>
       <code class="film-card__route">${first.replaceAll('/', '<wbr>/')}</code>
       <div class="film-card__body">${second}</div>${diagram(scene.key)}
       <div class="film-card__panel-foot">${third}</div></aside>`;
  const leftFoot = brand ? `<div class="film-card__aside">${second}${scene.art === 'dashboard' ? '<br>' + third : ''}</div>` : '';
  return `<main class="film-card${brand ? ' film-card--brand' : ''}">
    <header class="film-card__header"><span class="film-card__wordmark">SlimFaas</span><span>Live Stream dashboard</span></header>
    <div class="film-card__columns"><section><div class="film-card__section">${section}</div>
      <h1 class="film-card__title">${escape(scene.title)}</h1><p class="film-card__caption">${escape(scene.caption)}</p>${leftFoot}</section>${panel}</div>
    <footer class="film-card__footer">${brand ? 'slimfaas.dev' : 'slimfaas.dev/' + docs[scene.key]}</footer>
    </main>`;
}

const browser = await chromium.launch();
try {
  const page = await browser.newPage({ viewport: {width: 1920, height: 1080} });
  for (const scene of scenes.filter(s => s.card)) {
    await page.setContent('<meta charset="utf-8"><style>' + css + '</style>' + content(scene));
    const hasCapture = scene.art === 'dashboard';
    const start = scene.key === 'observability' ? take.marks.scale : peak.ms;
    const count = hasCapture ? Math.ceil((scene.voiceSamples / 24000 + 1) * 10) : scene.art ? 1 : 24;
    let lastImage;
    for (let i = 0; i < count; i++) {
      if (hasCapture) {
        const [, imageFile] = take.frames.findLast(([time]) => time <= start + i * 100);
        if (imageFile !== lastImage) {
          const data = await readFile(new URL('async/' + imageFile, work));
          await page.evaluate(async base64 => {
            const image = new Image();
            image.src = 'data:image/png;base64,' + base64;
            await image.decode();
            document.querySelector('.film-card__capture').style.backgroundImage = `url("${image.src}")`;
          }, data.toString('base64'));
          lastImage = imageFile;
        }
      }
      await page.evaluate(progress => {
        const paths = document.querySelectorAll('[data-flow]');
        document.querySelectorAll('[data-pulse]').forEach((dot, index) => {
          const path = paths[index];
          const point = path.getPointAtLength(progress * path.getTotalLength());
          dot.setAttribute('cx', point.x);
          dot.setAttribute('cy', point.y);
          dot.style.opacity = Math.min(1, progress * 12, (1-progress) * 12);
        });
      }, i / count);
      await page.screenshot({path: fileURLToPath(new URL(scene.key + '-' + String(i).padStart(2, '0') + '.png', destination))});
    }
  }
} finally {
  await browser.close();
}
