// Rasterize the motion design with the dashboard's existing, offline Playwright installation.
import { chromium } from '../../../src/SlimFaas/ClientApp/node_modules/playwright/index.mjs';
import { mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';

const directory = process.argv[2];
const { scenes, fps, observed } = JSON.parse(await readFile(path.join(directory, 'plan.json'), 'utf8'));
const board = await readFile(new URL('board.html', import.meta.url), 'utf8');
const frames = path.join(directory, 'frames');
await mkdir(frames);
const edits = {
  night: { label: 'After hours', accent: 'the night off.' },
  wake: { label: 'The first call', accent: 'you call.', metrics: [['0 → 1', 'worker'], [observed.syncResponse.result, 'HTTP result']] },
  rush: { label: 'A burst of HTTP calls', accent: 'rush in stride.', metrics: [[observed.asyncAccepted, 'calls accepted'], [observed.peakReadyWorkers, 'peak workers']] },
  quiet: { label: 'When the work is done', accent: 'Clock out.', metrics: [[observed.asyncCompletedWithResult4807526976, 'finished'], [observed.finalReadyWorkers, 'workers after idle timeout']] },
  try: { label: 'Try SlimFaas', accent: 'a five-minute try.' },
};
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1080, height: 1350 }, deviceScaleFactor: 1 });
  await page.setContent(board);
  await page.evaluate(sceneCount => {
    for (let i = 0; i < 60; i++) {
      const tick = document.createElementNS('http://www.w3.org/2000/svg', 'path');
      tick.setAttribute('d', 'M200 73 V' + (i % 5 ? 79 : 86));
      tick.setAttribute('stroke-width', i % 5 ? '2' : '5');
      tick.setAttribute('transform', 'rotate(' + (6 * i) + ' 200 200)');
      document.getElementById('ticks').append(tick);
    }
    for (let i = 0; i < sceneCount; i++) {
      document.querySelector('.film__progress').append(document.createElement('i'));
    }
  }, scenes.length);
  let movieFrame = 0;
  for (const [index, scene] of scenes.entries()) {
    const edit = edits[scene.key];
    if (!scene.headline.endsWith(edit.accent)) throw new Error('Check headline break for ' + scene.key);
    await page.evaluate(({ scene, edit, index }) => {
      const field = name => document.querySelector('.film__' + name);
      const clock = !scene.images;
      const ending = scene.key === 'try';
      document.querySelector('main').className = 'film film--' + scene.key;
      field('eyebrow').textContent = edit.label;
      field('title').querySelector('span').textContent = scene.headline.slice(0, -edit.accent.length).trim();
      field('title').querySelector('em').textContent = edit.accent;
      field('feature').hidden = !clock;
      field('panel').hidden = clock;
      if (clock) {
        field('dial-label').textContent = ending ? 'Start with' : 'On the night shift';
        field('dial-value').textContent = ending ? 'One' : '0';
        field('dial-detail').textContent = ending ? 'existing HTTP tool.' : 'workers for an idle tool.';
        field('cta').hidden = field('url').hidden = !ending;
      } else {
        field('source').textContent = scene.key === 'quiet' ? 'Kubernetes · time-lapse' : 'Kubernetes recording';
        field('metrics').replaceChildren();
        for (const [value, unit] of edit.metrics) {
          const pair = document.createElement('div');
          pair.className = 'film__metric';
          for (const [className, text] of [['number', value], ['unit', unit]]) {
            const span = document.createElement('span');
            span.className = 'film__' + className;
            span.textContent = text;
            pair.append(span);
          }
          field('metrics').append(pair);
        }
      }
      [...field('progress').children].forEach((mark, n) => mark.toggleAttribute('data-active', n <= index));
      if (field('title').getBoundingClientRect().bottom > (clock ? field('feature') : field('panel')).offsetTop - 10) {
        throw new Error('Headline overlaps the art in ' + scene.key);
      }
    }, { scene, edit, index });
    let previousImage = 0;
    for (let i = 0; i < scene.frames; i++, movieFrame++) {
      const t = i / fps;
      // Give the resolved dashboard state a short hold before the cut.
      const hold = scene.key === 'quiet' ? 2.75 : 0.5;
      const shotFrame = Math.min(scene.images, 1 + Math.floor((scene.images - 1) * i / (scene.frames - fps * hold)));
      let image;
      if (scene.images && shotFrame !== previousImage) {
        const name = String(shotFrame).padStart(4, '0') + '.jpg';
        image = (await readFile(path.join(directory, scene.key, name))).toString('base64');
        previousImage = shotFrame;
      }
      const caption = scene.cues.find(cue => cue.start <= t && t < cue.end)?.text || '';
      await page.evaluate(async ({ image, t, caption, key }) => {
        const field = name => document.querySelector('.film__' + name);
        if (image) {
          field('capture').src = 'data:image/jpeg;base64,' + image;
          await field('capture').decode();
        }
        if (key === 'night' || key === 'try') {
          const hours = key === 'night' ? 22 : 9;
          for (const [id, angle] of [['hour', hours * 30 + 5], ['minute', 60 + t * .1], ['second', 210 + t * 6]]) {
            document.getElementById(id).setAttribute('transform', 'rotate(' + angle + ' 200 200)');
          }
        }
        field('caption').textContent = caption;
      }, { image, t, caption, key: scene.key });
      await page.screenshot({ path: path.join(frames, String(movieFrame).padStart(5, '0') + '.jpg'),
        type: 'jpeg', quality: 92 });
    }
    console.log(scene.key, (scene.frames / fps).toFixed(2) + 's');
  }
} finally {
  await browser.close();
}
